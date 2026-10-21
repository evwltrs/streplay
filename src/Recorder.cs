using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FishNet;
using FishNet.Component.Transforming;
using FishNet.Object;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

namespace Streplay
{
    // Records every round the local client sees: transforms + animator state of every networked
    // object, each player's aim and held items, the local camera, and visual RPC events.
    internal sealed class Recorder : MonoBehaviour
    {
        public static Recorder Instance;
        public bool IsRecording => _w != null;
        public float CurrentTime => Time.unscaledTime - _startTime;
        internal string CurrentMetaPath => IsRecording ? _meta?.MetaPath : null;

        BinaryWriter _w;
        ReplayMeta _meta;
        string _scene;
        float _startTime;
        float _nextSample;
        float _nextFlush;
        string _sessionId;
        int _roundIndex;
        bool _clientWasStarted;
        float _lastMarkerTime = -10f;
        int _take = -1;                 // ScoreManager take index this file started in

        // the local player's latest kill, so the marker key can flag it for a few seconds afterwards, even after
        // the next take (and file) has started
        sealed class KillMark
        {
            public ReplayMeta Meta;
            public ReplayMeta.MetaMarker Marker;
            public float At;              // Time.unscaledTime of the kill
            public bool Flagged;
        }
        KillMark _lastKill;

        string _toast;
        float _toastUntil;

        sealed class Tracked
        {
            public NetworkObject Nob;
            public int Id;
            public bool Visual;
            public Transform[] Children;
            public Animator[] Anims;
            public RuntimeAnimatorController[] AnimControllers;
            public AnimatorControllerParameter[][] AnimParams;
            // players
            public bool IsPlayer;
            public bool PlayerInfoWritten;
            public FirstPersonController Fpc;
            public PlayerHealth Health;
            public PlayerPickup Pickup;
            public PlayerValues Values;
            public PlayerSetup Setup;
            public Transform[] Vm = Array.Empty<Transform>();   // local player's first-person arms
            public Transform[] Bones = Array.Empty<Transform>(); // third-person skeleton
            public bool[] BoneIsRoot = Array.Empty<bool>();
            public (int mat, int cig, GameObject hat) Cosmetics = (-1, -1, null);
            public bool CosmeticsWritten;
        }

        // networked AudioSources that had Play() called on them; their clip/volume/playing state is
        // sampled every frame so slide and fall loops fade exactly like they did
        sealed class TrackedSource
        {
            public int Id;
            public AudioSource Src;
            public int PlayCount;
            public AudioSrcState Last;
            public bool HasLast;
        }
        readonly Dictionary<AudioSource, TrackedSource> _sources = new Dictionary<AudioSource, TrackedSource>();
        readonly List<TrackedSource> _sourceChanges = new List<TrackedSource>();
        ViewState _lastView;
        bool _hasView;

        readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
        readonly List<int> _gone = new List<int>();
        readonly List<Tracked> _visual = new List<Tracked>();
        readonly HudCapture _hud = new HudCapture();
        bool _hudFailed;

        void Awake() => Instance = this;

        void Update()
        {
            var cm = InstanceFinder.ClientManager;
            bool clientStarted = cm != null && cm.Started;
            if (clientStarted && !_clientWasStarted)
            {
                _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _roundIndex = 0;
            }
            _clientWasStarted = clientStarted;

            string scene = SceneManager.GetActiveScene().name;
            bool want = Plugin.RecordEnabled.Value && clientStarted && !ReplayPlayer.Active && IsMapScene(scene);

            if (IsRecording && (!want || scene != _scene)) Stop();
            // a map is played for several takes without a scene change; each take gets its own file
            int take = CurrentTake;
            if (IsRecording && take > _take && _take >= 0 && CurrentTime > 3f)
            {
                Plugin.Log.LogInfo($"Take {take + 1} started");
                Stop();
            }
            else if (IsRecording && take != _take) _take = take; // the take index synced in after the file started
            if (scene != _failedScene) _failedScene = null;
            if (!IsRecording && want && (_failedScene == null || Time.unscaledTime >= _retryAt)) Begin(scene);

            // works between takes too (loading screen), as long as a kill was just made
            if (Plugin.MarkerKey.Value.IsDown() && !ReplayPlayer.Active && !FlagRecentKill() && IsRecording)
                AddMarker("Marker", auto: false);
        }

        // what the marker key does (AutoTest)
        internal void TestMarkerKey() { if (!FlagRecentKill() && IsRecording) AddMarker("Marker", auto: false); }

        static int CurrentTake => ScoreManager.Instance != null ? ScoreManager.Instance.TakeIndex : -1;

        // Called (observe-only) when the game plays the local player's kill shockwave, which every weapon does on a kill.
        internal void OnLocalKill()
        {
            if (!IsRecording) return;
            float t = CurrentTime;
            var marker = Plugin.AutoMarkKills.Value ? AddMarker("Kill", auto: true) : null;
            _lastKill = new KillMark { Meta = _meta, Marker = marker ?? new ReplayMeta.MetaMarker { T = t, Label = "Kill", Auto = true }, At = Time.unscaledTime };
        }

        // The marker key right after a kill flags that kill instead of the current moment (by then the next take may
        // already be on, in a new file).
        bool FlagRecentKill()
        {
            var k = _lastKill;
            if (k == null || k.Flagged || k.Meta == null || Time.unscaledTime - k.At > Plugin.MarkerKillGrace.Value) return false;
            k.Flagged = true;
            k.Marker.Auto = false;
            k.Marker.Label = k.Marker.Label.StartsWith("Kill", StringComparison.Ordinal) ? "Flagged " + char.ToLowerInvariant(k.Marker.Label[0]) + k.Marker.Label.Substring(1) : k.Marker.Label;
            if (!k.Meta.Markers.Contains(k.Marker))
            {
                k.Meta.Markers.Add(k.Marker);
                k.Meta.Markers.Sort((x, y) => x.T.CompareTo(y.T));
            }
            try { k.Meta.Save(); } catch (Exception e) { Plugin.Log.LogWarning($"Marker metadata save failed: {e.Message}"); }
            Plugin.Log.LogInfo($"Flagged the kill at {k.Marker.T:0.00}s in {k.Meta.ReplayFile}");
            ShowToast(k.Meta == _meta ? $"{k.Marker.Label} marked" : $"{k.Marker.Label} marked (previous take)");
            return true;
        }

        void LateUpdate()
        {
            if (!IsRecording) return;
            float now = Time.unscaledTime;
            if (now < _nextSample) return;
            float step = 1f / Mathf.Max(1f, Plugin.SampleRate.Value);
            _nextSample = now - _nextSample > step ? now + step : _nextSample + step;
            try
            {
                Sample();
            }
            catch (Exception e)
            {
                // the frame record may be half written: close the file without an end record (the reader keeps
                // everything up to the last complete record) and don't restart on this map straight away
                _torn = true;
                ReportFailure("Recording failed, stopping this round", e);
                Stop();
                _failedScene = _scene;
                _retryAt = Time.unscaledTime + 30f;
            }
        }

        // Recording problems (a full disk, an unwritable folder) would otherwise repeat every round: the full error
        // once per session, then a short line.
        bool _failureLogged;
        string _failedScene;
        float _retryAt;
        bool _torn;

        void ReportFailure(string what, Exception e)
        {
            if (!_failureLogged) { _failureLogged = true; Plugin.Log.LogError($"{what}: {e}"); }
            else Plugin.Log.LogWarning($"{what}: {e.Message}");
        }

        void OnApplicationQuit() => Stop();

        // FishNet briefly activates its own holder scene during scene changes.
        static readonly string[] AlwaysSkip = { "MainMenu", "MovedObjectsHolder", "EmptyScene", "VictoryScene" };

        internal static bool IsMapScene(string scene) =>
            !string.IsNullOrEmpty(scene) && !AlwaysSkip.Contains(scene)
            && !Plugin.NonMapScenes.Value.Split(',').Any(s => s.Trim() == scene);

        void Begin(string scene)
        {
            string path = null;
            _torn = false;
            try
            {
                Directory.CreateDirectory(Plugin.ReplayFolder.Value);
                var start = DateTime.UtcNow;
                string baseName = start.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + "_" + Sanitize(scene);
                path = Path.Combine(Plugin.ReplayFolder.Value, baseName + ".strp");
                // two recordings can start in the same second (a short take right after another): never overwrite
                for (int n = 2; File.Exists(path) || File.Exists(path + ".json"); n++)
                    path = Path.Combine(Plugin.ReplayFolder.Value, $"{baseName}_{n}.strp");
                baseName = Path.GetFileNameWithoutExtension(path);

                var header = new ReplayHeader
                {
                    GameVersion = Application.version,
                    PluginVersion = Plugin.Version,
                    Scene = scene,
                    StartUtcTicks = start.Ticks,
                    SessionId = _sessionId ?? "",
                    RoundIndex = _roundIndex++,
                    SampleRate = Plugin.SampleRate.Value,
                };
                _w = ReplayIO.OpenWrite(path, header);
                _meta = new ReplayMeta
                {
                    MetaPath = path + ".json",
                    ReplayFile = Path.GetFileName(path),
                    Scene = scene,
                    StartUtc = start,
                    SessionId = header.SessionId,
                    RoundIndex = header.RoundIndex,
                    GameVersion = header.GameVersion,
                    Take = CurrentTake >= 0 ? CurrentTake + 1 : 0,
                };
                _meta.Save();
                _scene = scene;
                _take = CurrentTake;
                _startTime = Time.unscaledTime;
                _nextSample = _startTime;
                _tracked.Clear();
                _sources.Clear();
                _hasView = false;
                _hud.Reset();
                _hudFailed = false;
                Plugin.Log.LogInfo($"Recording {baseName}");
            }
            catch (Exception e)
            {
                ReportFailure("Could not start recording", e);
                try { _w?.Dispose(); } catch { /* already broken */ }
                _w = null;
                try { if (path != null && File.Exists(path) && !File.Exists(path + ".json")) File.Delete(path); } catch { /* best effort */ }
                _failedScene = scene;
                _retryAt = Time.unscaledTime + 30f;
            }
        }

        void Stop()
        {
            if (_w == null) return;
            var w = _w;
            _w = null;
            float duration = Time.unscaledTime - _startTime;
            try
            {
                if (!_torn)
                {
                    w.Write(Rec.End);
                    w.Write(duration);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Error closing replay: {e.Message}");
            }
            finally
            {
                try { w.Dispose(); } catch (Exception e) { Plugin.Log.LogWarning($"Error closing replay file: {e.Message}"); }
                _torn = false;
            }
            if (duration < 3f && _meta.Markers.Count == 0 && _lastKill?.Meta != _meta)
            {
                // scene-transition blips; not worth keeping
                try { File.Delete(_meta.ReplayPath); File.Delete(_meta.MetaPath); } catch { /* ignore */ }
                _tracked.Clear();
                return;
            }
            try
            {
                _meta.Duration = duration;
                _meta.Finished = true;
                _meta.Save();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Error saving replay metadata: {e.Message}");
            }
            Plugin.Log.LogInfo($"Saved replay {_meta.ReplayFile} ({duration:0.0}s, {_meta.Markers.Count} markers)");
            _tracked.Clear();
            Retention.EnforceAsync(_meta.MetaPath);
        }

        public ReplayMeta.MetaMarker AddMarker(string label, bool auto)
        {
            if (!IsRecording) return null;
            float t = CurrentTime;
            if (auto && t - _lastMarkerTime < 1f) return null;
            _lastMarkerTime = t;
            _w.Write(Rec.Marker);
            _w.Write(t);
            _w.Write(label);
            _w.Write(auto);
            var marker = new ReplayMeta.MetaMarker { T = t, Label = label, Auto = auto };
            _meta.Markers.Add(marker);
            try { _meta.Save(); } catch (Exception e) { Plugin.Log.LogWarning($"Marker metadata save failed: {e.Message}"); }
            ShowToast(auto ? $"Marked: {label}" + (label == "Kill" ? $" ({Plugin.MarkerKey.Value} to flag it)" : "") : "Marker added");
            return marker;
        }

        void ShowToast(string text)
        {
            _toast = text;
            _toastUntil = Time.unscaledTime + 2f;
        }

        // ---------------------------------------------------------------- sampling

        void Sample()
        {
            var spawned = InstanceFinder.ClientManager.Objects.Spawned;

            foreach (var kv in spawned)
            {
                var nob = kv.Value;
                if (nob == null || nob.ParentNetworkObject != null) continue; // nested objects ride along with their root
                if (!_tracked.ContainsKey(nob.ObjectId)) Track(nob);
            }

            _gone.Clear();
            foreach (var kv in _tracked)
                if (kv.Value.Nob == null || !spawned.TryGetValue(kv.Key, out var cur) || cur != kv.Value.Nob)
                    _gone.Add(kv.Key);
            float t = CurrentTime;
            foreach (int id in _gone)
            {
                _tracked.Remove(id);
                _w.Write(Rec.Despawn);
                _w.Write(t);
                _w.Write(id);
            }

            _visual.Clear();
            foreach (var tr in _tracked.Values)
            {
                if (tr.IsPlayer && !tr.PlayerInfoWritten) TryWritePlayerInfo(tr);
                if (tr.IsPlayer) WriteCosmeticsIfChanged(tr);
                if (tr.Visual) _visual.Add(tr);
            }

            _w.Write(Rec.Frame);
            _w.Write(t);
            var cam = ViewCamera();
            if (cam != null)
            {
                _w.Write(cam.transform.position);
                _w.Write(cam.transform.rotation);
                _w.Write(cam.fieldOfView);
            }
            else
            {
                _w.Write(Vector3.zero);
                _w.Write(Quaternion.identity);
                _w.Write(60f);
            }

            int n = Math.Min(_visual.Count, ushort.MaxValue);
            _w.Write((ushort)n);
            for (int i = 0; i < n; i++) WriteState(_visual[i]);

            SampleAudioSources();
            SampleView();

            if (!_hudFailed)
            {
                try
                {
                    var local = _visual.FirstOrDefault(v => v.IsPlayer && v.Nob.IsOwner);
                    _hud.Sample(_w, local?.Nob);
                }
                catch (Exception e)
                {
                    // a HUD glitch must never cost the rest of the recording
                    _hudFailed = true;
                    Plugin.Log.LogError($"HUD capture disabled for this round: {e}");
                }
            }

            // sync-flush the compressor now and then so a crash only loses the last couple of seconds
            if (Time.unscaledTime >= _nextFlush)
            {
                _nextFlush = Time.unscaledTime + 2f;
                _w.Flush();
            }
        }

        static Camera ViewCamera()
        {
            var local = Settings.Instance != null ? Settings.Instance.localPlayer : null;
            if (local != null && local.playerCamera != null && local.playerCamera.isActiveAndEnabled)
                return local.playerCamera;
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled) return main;
            Camera best = null;
            foreach (var c in Camera.allCameras)
                if (c.targetTexture == null && (best == null || c.depth > best.depth)) best = c;
            return best;
        }

        Tracked Track(NetworkObject nob)
        {
            var tr = new Tracked { Nob = nob, Id = nob.ObjectId };
            var root = nob.transform;

            tr.Children = nob.GetComponentsInChildren<NetworkTransform>(true)
                .Where(nt => nt.transform != root && TopLevel(nt.GetComponentInParent<NetworkObject>()) == nob)
                .Select(nt => nt.transform).Distinct().ToArray();
            tr.Anims = nob.GetComponentsInChildren<Animator>(true)
                .Where(a => TopLevel(a.GetComponentInParent<NetworkObject>()) == nob)
                .Take(255).ToArray();
            tr.AnimControllers = new RuntimeAnimatorController[tr.Anims.Length];
            tr.AnimParams = new AnimatorControllerParameter[tr.Anims.Length][];
            tr.Visual = nob.GetComponentsInChildren<Renderer>(true).Length > 0 && !PuppetFactory.IsSessionObject(nob.gameObject);

            tr.Fpc = nob.GetComponent<FirstPersonController>();
            tr.IsPlayer = tr.Fpc != null;
            if (tr.IsPlayer)
            {
                tr.Health = nob.GetComponent<PlayerHealth>();
                tr.Pickup = nob.GetComponent<PlayerPickup>();
                tr.Values = nob.GetComponent<PlayerValues>();
                tr.Setup = nob.GetComponent<PlayerSetup>();
                var arms = tr.Pickup != null ? tr.Pickup.fpArms : null;
                if (nob.IsOwner && arms != null)
                    tr.Vm = arms.GetComponentsInChildren<Transform>(true).Take(255).ToArray();
                // The body's arms are bent by IK onto targets that game scripts move every frame, so we
                // store the final skeleton pose rather than trying to re-run animation + IK on playback.
                var body = tr.Health != null ? tr.Health.graphics : null;
                if (body != null)
                {
                    var set = new HashSet<Transform>();
                    tr.Bones = body.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .SelectMany(smr => smr.bones ?? Array.Empty<Transform>())
                        .Where(b => b != null && (arms == null || !b.IsChildOf(arms)) && set.Add(b))
                        .Take(1024).ToArray();
                    tr.BoneIsRoot = tr.Bones.Select(b => b.parent == null || !set.Contains(b.parent)).ToArray();
                }
            }
            _tracked[nob.ObjectId] = tr;

            _w.Write(Rec.ObjectInfo);
            _w.Write(CurrentTime);
            _w.Write(nob.ObjectId);
            _w.Write(nob.IsSceneObject);
            _w.Write(Net.SceneId(nob));
            _w.Write(nob.PrefabId);
            _w.Write(nob.SpawnableCollectionId);
            _w.Write(nob.gameObject.name ?? "");
            _w.Write(tr.IsPlayer);
            _w.Write((ushort)tr.Children.Length);
            foreach (var c in tr.Children) _w.Write(RelPath(root, c));
            _w.Write((ushort)tr.Anims.Length);
            foreach (var a in tr.Anims) _w.Write(RelPath(root, a.transform));
            _w.Write((ushort)tr.Vm.Length);
            foreach (var v in tr.Vm) _w.Write(RelPath(root, v));
            _w.Write((ushort)tr.Bones.Length);
            for (int i = 0; i < tr.Bones.Length; i++) { _w.Write(RelPath(root, tr.Bones[i])); _w.Write(tr.BoneIsRoot[i]); }
            return tr;
        }

        void TryWritePlayerInfo(Tracked tr)
        {
            var client = tr.Values != null ? tr.Values.playerClient : null;
            if (client == null) return;
            tr.PlayerInfoWritten = true;
            int mat = tr.Setup != null ? tr.Setup.mat : 0;
            _w.Write(Rec.PlayerInfo);
            _w.Write(tr.Id);
            _w.Write(client.PlayerId);
            _w.Write(client.PlayerSteamID);
            _w.Write(client.PlayerName ?? "");
            _w.Write(tr.Nob.IsOwner);
            _w.Write(mat);
            if (!_meta.Players.Any(p => p.PlayerId == client.PlayerId))
            {
                _meta.Players.Add(new ReplayMeta.MetaPlayer
                {
                    Name = client.PlayerName ?? "",
                    PlayerId = client.PlayerId,
                    SteamId = client.PlayerSteamID,
                    IsLocal = tr.Nob.IsOwner,
                });
                try { _meta.Save(); } catch { /* best effort, saved again on stop */ }
            }
        }

        void WriteState(Tracked tr)
        {
            var w = _w;
            var t = tr.Nob.transform;
            w.Write(tr.Id);
            w.Write(tr.Nob.gameObject.activeInHierarchy);
            w.Write(t.position);
            w.Write(t.rotation);
            w.Write(t.lossyScale);

            w.Write((byte)Math.Min(tr.Children.Length, 255));
            for (int i = 0; i < tr.Children.Length && i < 255; i++)
            {
                var c = tr.Children[i];
                if (c != null) { w.Write(c.localPosition); w.Write(c.localRotation); }
                else { w.Write(Vector3.zero); w.Write(Quaternion.identity); }
            }

            w.Write((byte)tr.Anims.Length);
            for (int i = 0; i < tr.Anims.Length; i++) WriteAnimator(tr, i);

            w.Write(tr.IsPlayer);
            if (tr.IsPlayer)
            {
                w.Write(tr.Health != null && tr.Health.graphics != null && tr.Health.graphics.activeSelf);
                var pov = tr.Fpc.playerCamera != null ? tr.Fpc.playerCamera.transform : t;
                w.Write(pov.position);
                w.Write(pov.rotation);
                w.Write(HeldId(tr.Pickup != null ? tr.Pickup.objInHand : null));
                w.Write(HeldId(tr.Pickup != null ? tr.Pickup.objInLeftHand : null));
                w.Write((byte)tr.Vm.Length);
                if (tr.Vm.Length > 0)
                {
                    w.Write(tr.Vm[0] != null && tr.Vm[0].gameObject.activeInHierarchy);
                    for (int i = 0; i < tr.Vm.Length; i++)
                    {
                        var v = tr.Vm[i];
                        if (v == null) { w.Write(Vector3.zero); w.Write(Quaternion.identity); }
                        else if (i == 0) { w.Write(v.position); w.Write(v.rotation); }
                        else { w.Write(v.localPosition); w.Write(v.localRotation); }
                    }
                }
                w.Write((ushort)tr.Bones.Length);
                for (int i = 0; i < tr.Bones.Length; i++)
                {
                    var b = tr.Bones[i];
                    w.WriteQuatPacked(b != null ? b.localRotation : Quaternion.identity);
                    if (tr.BoneIsRoot[i]) w.Write(b != null ? b.localPosition : Vector3.zero);
                }
            }
        }

        void WriteAnimator(Tracked tr, int i)
        {
            var w = _w;
            var a = tr.Anims[i];
            bool ok = a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null && a.isInitialized;
            w.Write(ok);
            if (!ok)
            {
                w.Write((byte)0);
                w.Write((byte)0);
                return;
            }
            int layers = Math.Min(a.layerCount, 255);
            w.Write((byte)layers);
            for (int l = 0; l < layers; l++)
            {
                var info = a.GetCurrentAnimatorStateInfo(l);
                w.Write(info.fullPathHash);
                w.Write(info.normalizedTime);
                w.Write(l == 0 ? 1f : a.GetLayerWeight(l));
            }
            if (tr.AnimControllers[i] != a.runtimeAnimatorController)
            {
                tr.AnimControllers[i] = a.runtimeAnimatorController;
                tr.AnimParams[i] = a.parameters;
            }
            var ps = tr.AnimParams[i];
            int np = Math.Min(ps.Length, 255);
            w.Write((byte)np);
            for (int p = 0; p < np; p++)
            {
                var par = ps[p];
                switch (par.type)
                {
                    case AnimatorControllerParameterType.Float: w.Write(a.GetFloat(par.nameHash)); break;
                    case AnimatorControllerParameterType.Int: w.Write((float)a.GetInteger(par.nameHash)); break;
                    case AnimatorControllerParameterType.Bool: w.Write(a.GetBool(par.nameHash) ? 1f : 0f); break;
                    default: w.Write(0f); break;
                }
            }
        }

        static int HeldId(GameObject go)
        {
            if (go == null) return -1;
            var nob = go.GetComponentInParent<NetworkObject>();
            return nob != null ? TopLevel(nob).ObjectId : -1;
        }

        // ---------------------------------------------------------------- cosmetics / view

        void WriteCosmeticsIfChanged(Tracked tr)
        {
            if (tr.Setup == null) return;
            var cur = (tr.Setup.mat, tr.Setup.cig, tr.Setup.hat);
            if (tr.CosmeticsWritten && cur.mat == tr.Cosmetics.mat && cur.cig == tr.Cosmetics.cig && cur.hat == tr.Cosmetics.hat) return;
            tr.Cosmetics = cur;
            tr.CosmeticsWritten = true;
            _w.Write(Rec.Cosmetics);
            _w.Write(tr.Id);
            _w.Write(cur.mat);
            _w.Write(cur.cig);
            _w.Write(cur.hat != null ? CloneName(cur.hat.name) : "");
        }

        internal static string CloneName(string n) => n != null && n.EndsWith("(Clone)", StringComparison.Ordinal) ? n.Substring(0, n.Length - 7).TrimEnd() : n ?? "";

        static readonly System.Reflection.FieldInfo F_SetupCameras = HarmonyLib.AccessTools.Field(typeof(PlayerSetup), "cameras");

        // Post-process values the game animates on the local camera, and the viewmodel camera FOV.
        void SampleView()
        {
            var local = _visual.FirstOrDefault(v => v.IsPlayer && v.Nob.IsOwner);
            if (local == null) return;
            var v = new ViewState();
            try
            {
                var fpc = local.Fpc;
                if (fpc.lensDistortion != null) v.Lens = fpc.lensDistortion.intensity.value;
                if (fpc.colorGrading != null) v.Saturation = fpc.colorGrading.saturation.value;
            }
            catch { /* post-process not set up yet */ }
            if (F_SetupCameras?.GetValue(local.Setup) is Camera[] cams && cams.Length > 1 && cams[1] != null) v.VmFov = cams[1].fieldOfView;
            if (_hasView && v.SameAs(_lastView)) return;
            _hasView = true;
            _lastView = v;
            _w.Write(Rec.View);
            _w.Write(v.Lens);
            _w.Write(v.Saturation);
            _w.Write(v.VmFov);
        }

        // ---------------------------------------------------------------- sounds

        public void OnSound(AudioSource src, AudioClip clip, float volumeScale, bool fromPlay, bool inRpc)
        {
            if (!IsRecording) return;
            if (Plugin.ContainsAny(clip.name, Plugin.IgnoredSoundClips.Value)) return; // e.g. the proximity voice-chat stream
            var near = src.GetComponentInParent<NetworkObject>();
            var top = TopLevel(near);
            bool networked = top != null && top.IsSpawned && top.ObjectId >= 0;
            if (networked) EnsureTracked(top);

            if (fromPlay && networked)
            {
                // state is sampled every frame from now on (see SampleAudioSources)
                if (!_sources.TryGetValue(src, out var ts))
                {
                    ts = new TrackedSource { Id = _sources.Count + 1, Src = src };
                    _sources[src] = ts;
                    _w.Write(Rec.AudioSrc);
                    _w.Write(ts.Id);
                    _w.Write(top.ObjectId);
                    _w.Write(RelPath(top.transform, src.transform));
                    _w.Write(SourceIndex(src));
                }
                ts.PlayCount++;
                return;
            }

            var s = new SoundEvent
            {
                T = CurrentTime,
                Clip = clip.name,
                VolumeScale = volumeScale,
                SourceVolume = src.volume,
                Pitch = src.pitch,
                SpatialBlend = src.spatialBlend,
                MinDistance = src.minDistance,
                MaxDistance = src.maxDistance,
                Rolloff = (byte)src.rolloffMode,
                Pos = src.transform.position,
                SourceIndex = SourceIndex(src),
            };
            if (inRpc) s.Flags |= SoundFlags.InRpc;
            if (fromPlay) s.Flags |= SoundFlags.FromPlay;
            if (networked)
            {
                s.Path = RelPath(top.transform, src.transform);
            }
            else
            {
                s.Path = ScenePath(src.transform);
                s.Flags |= src.gameObject.scene.name == "DontDestroyOnLoad" ? SoundFlags.Ddol : SoundFlags.ScenePath;
            }
            _w.Write(Rec.Sound);
            ReplayIO.WriteSound(_w, s, networked ? top.ObjectId : -1);
        }

        static byte SourceIndex(AudioSource src)
        {
            var all = src.GetComponents<AudioSource>();
            int i = Array.IndexOf(all, src);
            return (byte)Mathf.Clamp(i, 0, 255);
        }

        void SampleAudioSources()
        {
            if (_sources.Count == 0) return;
            _sourceChanges.Clear();
            foreach (var ts in _sources.Values)
            {
                var src = ts.Src;
                AudioSrcState st;
                if (src == null) st = new AudioSrcState { Clip = "" };
                else st = new AudioSrcState
                {
                    Playing = src.isPlaying,
                    Loop = src.loop,
                    Clip = src.clip != null ? src.clip.name : "",
                    Volume = src.volume,
                    Pitch = src.pitch,
                    PlayCount = ts.PlayCount,
                };
                if (ts.HasLast && st.SameAs(ts.Last)) continue;
                // while not playing only the playing flag matters
                if (ts.HasLast && !st.Playing && !ts.Last.Playing) continue;
                ts.Last = st;
                ts.HasLast = true;
                _sourceChanges.Add(ts);
            }
            if (_sourceChanges.Count == 0) return;
            _w.Write(Rec.AudioFrame);
            _w.Write((ushort)Math.Min(_sourceChanges.Count, ushort.MaxValue));
            foreach (var ts in _sourceChanges.Take(ushort.MaxValue))
            {
                _w.Write(ts.Id);
                _w.Write(ts.Last.Playing);
                _w.Write(ts.Last.Loop);
                _w.Write(ts.Last.Clip ?? "");
                _w.Write(ts.Last.Volume);
                _w.Write(ts.Last.Pitch);
                _w.Write(ts.Last.PlayCount);
            }
        }

        // ---------------------------------------------------------------- events

        public void OnRpc(NetworkBehaviour inst, string method, object[] args)
        {
            if (!IsRecording || inst == null) return;
            var nob = TopLevel(inst.NetworkObject);
            if (nob == null) return;
            EnsureTracked(nob);

            // serialize args first: they may need to emit ObjectInfo records for newly seen objects
            var buf = new MemoryStream(64);
            var aw = new BinaryWriter(buf);
            int argc = args == null ? 0 : Math.Min(args.Length, 255);
            for (int i = 0; i < argc; i++) WriteArg(aw, args[i]);
            aw.Flush();

            _w.Write(Rec.Event);
            _w.Write(CurrentTime);
            _w.Write(nob.ObjectId);
            _w.Write(RelPath(nob.transform, inst.transform));
            _w.Write(inst.GetType().FullName ?? "");
            _w.Write(method);
            _w.Write((byte)argc);
            _w.Write(buf.GetBuffer(), 0, (int)buf.Length);

            AutoMarkers(inst, nob, method, args);
        }

        void AutoMarkers(NetworkBehaviour inst, NetworkObject nob, string method, object[] args)
        {
            // the kill feed line ("<victim> was headshot with a M2000 by <you>") names the kill just marked
            if (method == "RpcSendChatLineToAllObservers" && _lastKill != null && _lastKill.Meta == _meta && Time.unscaledTime - _lastKill.At < 2f
                && _lastKill.Marker.Label == "Kill" && args != null && args.Length > 0 && args[0] is string line)
            {
                string victim = KillFeedVictim(line);
                if (victim != null)
                {
                    _lastKill.Marker.Label = $"Kill: {victim}";
                    try { _meta.Save(); } catch (Exception e) { Plugin.Log.LogWarning($"Marker metadata save failed: {e.Message}"); }
                }
            }
            else if (Plugin.AutoMarkDeaths.Value && method == "ExplodeForAll" && inst is PlayerHealth && nob.IsOwner)
            {
                AddMarker("Died", auto: true);
            }
        }

        static readonly System.Text.RegularExpressions.Regex Tags = new System.Text.RegularExpressions.Regex("<[^>]*>");

        static string KillFeedVictim(string line)
        {
            string named;
            try { named = ClientInstance.ReplaceAllPlayerNameTags(line); } catch { named = line; } // "{PLAYER_NAME}:{0}" -> name
            string plain = Tags.Replace(named, "").Trim();
            if (plain.Contains("{PLAYER_NAME}")) return null;
            int was = plain.IndexOf(" was ", StringComparison.Ordinal);
            return was > 0 && plain.IndexOf(" by ", was, StringComparison.Ordinal) > 0 ? plain.Substring(0, was).Trim() : null;
        }

        static string PlayerName(Component c)
        {
            var v = c != null ? c.GetComponent<PlayerValues>() : null;
            var client = v != null ? v.playerClient : null;
            return client != null ? client.PlayerName : null;
        }

        void EnsureTracked(NetworkObject top)
        {
            if (top != null && top.ObjectId >= 0 && !_tracked.ContainsKey(top.ObjectId) && top.IsSpawned)
                Track(top);
        }

        void WriteArg(BinaryWriter w, object a)
        {
            switch (a)
            {
                case null: w.Write(ArgTag.Null); return;
                case bool b: w.Write(ArgTag.Bool); w.Write(b); return;
                case int i: w.Write(ArgTag.Int); w.Write(i); return;
                case float f: w.Write(ArgTag.Float); w.Write(f); return;
                case string s: w.Write(ArgTag.String); w.Write(s); return;
                case Vector3 v: w.Write(ArgTag.Vec3); w.Write(v); return;
                case Quaternion q: w.Write(ArgTag.Quat); w.Write(q); return;
                case long l: w.Write(ArgTag.Long); w.Write(l); return;
                case double d: w.Write(ArgTag.Double); w.Write(d); return;
                case uint ui: w.Write(ArgTag.UInt); w.Write(ui); return;
                case byte by: w.Write(ArgTag.Byte); w.Write(by); return;
                case short sh: w.Write(ArgTag.Short); w.Write(sh); return;
                case ushort us: w.Write(ArgTag.UShort); w.Write(us); return;
                case ulong ul: w.Write(ArgTag.ULong); w.Write(ul); return;
                case Color c: w.Write(ArgTag.Color); w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); return;
                case Vector2 v2: w.Write(ArgTag.Vec2); w.Write(v2.x); w.Write(v2.y); return;
                case Enum e: w.Write(ArgTag.Int); w.Write(Convert.ToInt32(e)); return;
                case GameObject go: WriteObjRef(w, go); return;
                case Component comp: WriteObjRef(w, comp != null ? comp.gameObject : null); return;
                default: w.Write(ArgTag.Null); return; // connections and other non-visual types
            }
        }

        void WriteObjRef(BinaryWriter w, GameObject go)
        {
            if (go == null) { w.Write(ArgTag.Null); return; }
            var near = go.GetComponentInParent<NetworkObject>();
            var top = TopLevel(near);
            if (top != null && top.IsSpawned)
            {
                EnsureTracked(top);
                w.Write(ArgTag.ObjRef);
                w.Write(top.ObjectId);
                w.Write(RelPath(top.transform, go.transform));
            }
            else
            {
                w.Write(ArgTag.SceneRef);
                w.Write(ScenePath(go.transform));
            }
        }

        // ---------------------------------------------------------------- helpers

        internal static NetworkObject TopLevel(NetworkObject nob)
        {
            while (nob != null && nob.ParentNetworkObject != null) nob = nob.ParentNetworkObject;
            return nob;
        }

        internal static string RelPath(Transform root, Transform t)
        {
            if (t == root || t == null) return "";
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) parts.Add(cur.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static string ScenePath(Transform t)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null; cur = cur.parent) parts.Add(cur.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        // ---------------------------------------------------------------- HUD

        static GUIStyle _recStyle, _toastStyle;

        void OnGUI()
        {
            if (ReplayPlayer.Active) return;
            if (_recStyle == null)
            {
                _recStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
                _recStyle.normal.textColor = new Color(1f, 0.25f, 0.25f);
                _toastStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            }
            if (IsRecording && Plugin.ShowRecIndicator.Value)
                GUI.Label(new Rect(Screen.width - 70, 8, 70, 22), "● REC", _recStyle);
            if (_toast != null && Time.unscaledTime < _toastUntil)
            {
                float w = Mathf.Clamp(_toastStyle.CalcSize(new GUIContent(_toast)).x + 24, 260, Screen.width - 40);
                GUI.Box(new Rect(Screen.width / 2f - w / 2f, 40, w, 32), _toast, _toastStyle);
            }
        }
    }
}
