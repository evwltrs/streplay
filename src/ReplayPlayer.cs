using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FishNet.Object;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Streplay
{
    // Loads a replay's map offline, fills it with puppets and plays the recording back with a
    // scrubbable timeline, speed control, markers and a free / POV / original camera.
    internal sealed partial class ReplayPlayer : MonoBehaviour
    {
        public static bool Active { get; private set; }
        public static ReplayPlayer Instance;

        enum Phase { Idle, Loading, Ready }
        Phase _phase = Phase.Idle;

        ReplayData _data;
        ReplayMeta _meta;
        Puppet[] _puppets = Array.Empty<Puppet>();
        int[] _nextLookup = Array.Empty<int>();
        string _loadError;

        float _t;
        bool _playing;
        int _speedIndex = 4;
        static readonly float[] Speeds = { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f, 4f };
        float Speed => Speeds[_speedIndex];
        int _nextEvent;

        Camera _cam;
        KillCam _sceneKillCam;
        readonly List<Behaviour> _hiddenUi = new List<Behaviour>();
        readonly List<GameObject> _eventSpawned = new List<GameObject>();
        readonly HudReplay _hud = new HudReplay();
        readonly ReplayAudio _audio = new ReplayAudio();
        bool _hudEnabled = true;
        int _curFrameIndex;

        // render export (ReplayPlayer.Render.cs)
        float? _in, _out;
        bool _rendering;

        // true from opening a replay until the main menu is back, see GameSuspend
        static bool _returningToMenu;
        static float _returningSince;
        // the menu should be back within seconds; never leave the game's pause menu switched off for the session
        public static bool SuspendGame => Active || (_returningToMenu && Time.unscaledTime - _returningSince < 30f);

        void Awake()
        {
            Instance = this;
            SceneManager.sceneLoaded += (scene, _) =>
            {
                if (scene.name != "MainMenu")
                {
                    if (!Active) _returningToMenu = false; // some other scene came up instead of the menu
                    return;
                }
                _returningToMenu = false;
                if (_leaveLobbyOnMenu) { _leaveLobbyOnMenu = false; StartCoroutine(LeaveLobbySoon()); }
            };
        }

        // A stray click on the menu (or a lobby invite) while a replay is open would load a match over it. A quick match
        // search is cancelled right away; an actual lobby closes the replay and is left once the menu is back.
        bool _leaveLobbyOnMenu;
        internal static string MenuNotice; // shown in the replay browser once the menu is back

        void GuardLobby()
        {
            var lobby = SteamLobby.Instance;
            if (lobby == null || _leaveLobbyOnMenu) return;
            if (lobby.inSteamLobby)
            {
                Plugin.Log.LogWarning("A lobby was joined while a replay was open; closing the replay and leaving the lobby");
                _leaveLobbyOnMenu = true;
                MenuNotice = "The replay was closed because a lobby was joined behind it (a stray click on the menu?). You've left that lobby.";
                Exit();
            }
            else if (lobby.findingQuickMatchLobby)
            {
                Plugin.Log.LogWarning("A quick match search started while a replay was open; cancelled it");
                try { lobby.CancelQuickMatch(); } catch (Exception e) { Plugin.Log.LogWarning($"Cancelling quick match failed: {e.Message}"); }
                Toast("Quick match search cancelled (a replay is open)", 5f);
            }
        }

        static IEnumerator LeaveLobbySoon()
        {
            yield return new WaitForSecondsRealtime(1f);
            try { SteamLobby.Instance?.LeaveLobby(); Plugin.Log.LogInfo("Left the lobby that was joined during the replay"); }
            catch (Exception e) { Plugin.Log.LogWarning($"Leaving the lobby failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- open / close

        // inPoint/outPoint: preset the clip range (a highlight's Watch)
        public void Open(ReplayMeta meta, float startAt, float? inPoint = null, float? outPoint = null)
        {
            if (Active) return;
            StartCoroutine(OpenRoutine(meta, startAt, inPoint, outPoint));
        }

        IEnumerator OpenRoutine(ReplayMeta meta, float startAt, float? inPoint, float? outPoint)
        {
            Active = true;
            _phase = Phase.Loading;
            _loadError = null;
            _meta = meta;
            _v1 = -1f;          // the previous replay's timeline zoom
            _uiVisible = true;  // and a UI hidden with H there
            yield return null;

            try
            {
                _data = ReplayIO.Read(meta.ReplayPath);
                if (_data.Frames.Count == 0) throw new InvalidDataException("Replay has no frames");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not read replay: {e}");
                _loadError = e.Message;
                MenuNotice = $"Couldn't open {meta.ReplayFile}: {e.Message}";
                _phase = Phase.Idle;
                Active = false;
                yield break;
            }

            string scene = _data.Header.Scene;
            if (!Application.CanStreamedLevelBeLoaded(scene))
            {
                _loadError = $"Map '{scene}' isn't available in this version of the game.";
                MenuNotice = _loadError;
                _phase = Phase.Idle;
                Active = false;
                yield break;
            }

            HideAllUi();
            SceneManager.sceneLoaded += OnSceneLoaded;
            var op = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
            while (op != null && !op.isDone) yield return null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            yield return null;
            if (!Active) yield break; // closed while loading (see GuardLobby)

            try
            {
                SetupCamera();
                BuildPuppets();
                _hudBoundTo = null; // bound to the live local player object in LateUpdate
                _audio.Bind(_data, _puppets, _cam != null ? _cam.transform : null);
                _audio.IsEffectObject = go => _eventSpawned.Contains(go) || _restored.Values.Any(l => l.Contains(go));
            }
            catch (Exception e)
            {
                // a half set-up replay would only throw every frame: back to the menu, with the reason
                Plugin.Log.LogError($"Replay setup failed: {e}");
                _phase = Phase.Ready; // so Exit tears down what was built
                Exit();
                MenuNotice = $"Couldn't play {meta.ReplayFile}: {e.Message}";
                yield break;
            }

            _phase = Phase.Ready;
            _playing = true;
            _speedIndex = 4;
            _camMode = CamMode.Original;
            _in = inPoint.HasValue ? Mathf.Clamp(inPoint.Value, 0f, _data.Duration) : (float?)null;
            _out = outPoint.HasValue ? Mathf.Clamp(outPoint.Value, 0f, _data.Duration) : (float?)null;
            FindLocalKills();
            _lastEye.Clear();
            Seek(Mathf.Clamp(startAt, 0f, _data.Duration));
            if (!string.IsNullOrEmpty(_data.Header.GameVersion) && _data.Header.GameVersion != Application.version)
                Toast($"Recorded on game version {_data.Header.GameVersion} (you have {Application.version}): some objects may be missing", 6f);
            Plugin.Log.LogDebug($"Physics autoSimulation={Physics.autoSimulation} fixedDelta={Time.fixedDeltaTime}");
            try { LogPuppetDiagnostics(); } catch (Exception e) { Plugin.Log.LogWarning($"Diagnostics failed: {e.Message}"); }
            Plugin.Log.LogInfo($"Playing {_meta.ReplayFile}: {_data.Frames.Count} frames, {_data.Objects.Count} objects, {_data.Events.Count} events{(_data.Truncated ? " (recording was cut short)" : "")}");
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Runs after Awake/OnEnable but before any Start/Update, so map scripts never get to run.
            foreach (var root in scene.GetRootGameObjects())
            {
                PuppetFactory.Neutralize(root, null);
                foreach (var c in root.GetComponentsInChildren<Canvas>(true))
                    if (c.enabled) { c.enabled = false; _hiddenUi.Add(c); }
            }
        }

        // the replay browser reopens where you were (Highlights or Rounds) once the menu is back
        internal static bool BackToBrowser;

        public void Exit()
        {
            if (!Active) return;
            BackToBrowser = true;
            Teardown();
            Active = false;
            _phase = Phase.Idle;
            foreach (var b in _hiddenUi) if (b != null) b.enabled = true;
            _hiddenUi.Clear();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _returningToMenu = true;
            _returningSince = Time.unscaledTime;
            SceneManager.LoadScene("MainMenu");
        }

        // everything the replay created or changed in the map, leaving the menu's hidden UI as it is
        void Teardown()
        {
            StopRender(true);
            if (_audioPass != null) { _audioPass.Tap.Stop(); _audioPass = null; }
            _phase = Phase.Idle;
            Time.timeScale = 1f;
            Time.captureFramerate = 0;
            ClearEventSpawned();
            ClearRestored();
            _restoreRules = null;
            _hud.Dispose();
            Plugin.Log.LogInfo($"Replay closed, {_audio.Stats}");
            _audio.Dispose();
            _postFx.Dispose();
            ClearViewmodelStyle();
            _vmItems.Clear();
            _slideBody = null;
            _slideHidden.Clear();
            foreach (var p in _puppets)
                if (p != null && p.Owned && p.Go != null) Destroy(p.Go);
            _puppets = Array.Empty<Puppet>();
            _data = null;
            _flyPaths = null;
        }

        // Screen canvases the game turns on after the replay opened (the lobby panel once Steam has made the lobby,
        // popups) are hidden like the rest of the menu; the HUD's own canvases are left to HudReplay.
        float _nextUiCheck;

        void KeepUiHidden()
        {
            if (Time.realtimeSinceStartup < _nextUiCheck) return;
            _nextUiCheck = Time.realtimeSinceStartup + 0.25f;
            foreach (var c in FindObjectsOfType<Canvas>())
            {
                if (c == null || !c.enabled || !c.isRootCanvas || c.renderMode == RenderMode.WorldSpace || _hud.UsesCanvas(c)) continue;
                c.enabled = false;
                if (_hiddenUi.Contains(c)) continue; // the game keeps switching it on: hide it quietly
                _hiddenUi.Add(c);
                Plugin.Log.LogDebug($"[ui] hid canvas '{c.name}' that came up during the replay");
            }
        }

        // adds to the list (switching replays keeps what the first open hid; Exit puts it all back)
        void HideAllUi()
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue; // skip prefabs/assets
                if (c.enabled) { c.enabled = false; _hiddenUi.Add(c); }
            }
        }

        void SetupCamera()
        {
            var mainGo = GameObject.Find("Main Camera");
            _cam = mainGo != null ? mainGo.GetComponent<Camera>() : null;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("Streplay Camera");
                _cam = go.AddComponent<Camera>();
            }
            _sceneKillCam = _cam.GetComponent<KillCam>();
            if (_sceneKillCam != null) _sceneKillCam.enabled = false;
            _cam.gameObject.SetActive(true);
            _cam.enabled = true;
            _cam.tag = "MainCamera";
            _cam.depth = 100;
            _cam.fieldOfView = Plugin.DefaultFov.Value;
            if (_cam.nearClipPlane > 0.05f) _cam.nearClipPlane = 0.05f;
            _baseNear = _cam.nearClipPlane;
            foreach (var c in Camera.allCameras)
                if (c != _cam && c.targetTexture == null) c.enabled = false;
            foreach (var l in FindObjectsOfType<AudioListener>()) l.enabled = l.gameObject == _cam.gameObject;
            if (_cam.GetComponent<AudioListener>() == null) _cam.gameObject.AddComponent<AudioListener>();
            _freePos = _data.Frames[0].CamPos;
            var e = _data.Frames[0].CamRot.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180 ? e.x - 360 : e.x;
            _fov = Plugin.DefaultFov.Value;
        }

        void BuildPuppets()
        {
            var sceneObjects = new Dictionary<ulong, NetworkObject>();
            foreach (var nob in Resources.FindObjectsOfTypeAll<NetworkObject>())
            {
                if (nob == null || !nob.gameObject.scene.IsValid() || Net.SceneId(nob) == 0) continue;
                sceneObjects[Net.SceneId(nob)] = nob;
            }

            // Only objects that were ever drawn get a puppet. Managers (ClientInstance, ScoreManager, ...)
            // register themselves as singletons in Awake, and a copy of them would hijack the real game.
            var visual = new HashSet<int>();
            foreach (var f in _data.Frames)
                foreach (var s in f.Objects) visual.Add(s.Obj);

            PuppetFactory.LoggedCosmetics.Clear();
            // recordings before v6 have no hat, but the dress RPC names the hat prefab (as an unresolvable scene path)
            foreach (var ev in _data.Events)
            {
                if (ev.Method != "ChangeDress" || ev.Obj < 0 || ev.Args.Length < 2 || !(ev.Args[1] is ArgSceneRef hat)) continue;
                var c = _data.Objects[ev.Obj].Cosmetics ??= new Cosmetics();
                if (string.IsNullOrEmpty(c.Hat) && !string.IsNullOrEmpty(hat.Path))
                    c.Hat = Recorder.CloneName(hat.Path.Substring(hat.Path.LastIndexOf('/') + 1));
            }
            _puppets = new Puppet[_data.Objects.Count];
            _nextLookup = new int[_data.Objects.Count];
            int made = 0, missing = 0;
            foreach (var info in _data.Objects)
            {
                if (!visual.Contains(info.Index)) continue;
                GameObject go = null;
                bool owned = false;
                if (info.IsSceneObject)
                {
                    if (sceneObjects.TryGetValue(info.SceneId, out var nob)) go = nob.gameObject;
                }
                else
                {
                    go = PuppetFactory.InstantiateInactive(info);
                    owned = go != null;
                    if (owned)
                    {
                        PuppetFactory.Neutralize(go, _cam);
                        if (info.IsPlayer) PuppetFactory.SetupAsRemotePlayer(go, _sceneKillCam);
                    }
                }
                if (go == null) { missing++; continue; }
                var p = new Puppet { Info = info, Go = go, Owned = owned, Teammate = IsTeammate(info) };
                p.Bind();
                if (!info.IsPlayer) p.Bodies = go.GetComponentsInChildren<Rigidbody>(true);
                if (owned && info.IsPlayer)
                {
                    try { PuppetFactory.ApplyCosmetics(p); }
                    catch (Exception e) { Plugin.Log.LogWarning($"Cosmetics failed for {info.Name}: {e.Message}"); }
                    try { PuppetFactory.ApplyOutline(p, Plugin.PlayerOutlines.Value); }
                    catch (Exception e) { Plugin.Log.LogWarning($"Outline failed for {info.Name}: {e.Message}"); }
                }
                _puppets[info.Index] = p;
                made++;
            }
            _outlines = Plugin.PlayerOutlines.Value;
            if (missing > 0) Plugin.Log.LogWarning($"{missing} recorded objects have no matching prefab/scene object and will be skipped");
            Plugin.Log.LogDebug($"Created {made} replay objects");

            // See what a player sees: the map's spectator camera may cull layers the player camera renders.
            var setup = _puppets.Where(p => p != null && p.Info.IsPlayer).Select(p => p.Go.GetComponent<PlayerSetup>()).FirstOrDefault(s => s != null);
            if (setup != null && F_HighMask?.GetValue(setup) is LayerMask mask && mask.value != 0)
            {
                Plugin.Log.LogDebug($"Camera mask {_cam.cullingMask:X8} -> player mask {mask.value:X8}");
                _cam.cullingMask = mask.value;
                _maskWorld = mask.value;
                // low-quality mode renders everything with one camera, so its mask includes the viewmodel layers
                _maskFirstPerson = mask.value | (F_LowMask?.GetValue(setup) is LayerMask low ? low.value : 0) | (1 << 8);
            }
            BuildViewmodelCameras();
            LogPostProcess();
            try { SetupPostFx(); }
            catch (Exception e) { Plugin.Log.LogWarning($"Post-processing setup failed: {e.Message}"); }
        }

        void LogPostProcess()
        {
            var ppl = _cam.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
            Plugin.Log.LogDebug($"[pp] replay cam layer={(ppl != null ? $"yes enabled={ppl.enabled} mask={ppl.volumeLayer.value:X8} trigger={(ppl.volumeTrigger != null ? ppl.volumeTrigger.name : "null")}" : "none")}");
            foreach (var v in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.PostProcessing.PostProcessVolume>())
            {
                if (v == null || !v.gameObject.scene.IsValid()) continue;
                Plugin.Log.LogDebug($"[pp] volume {Recorder.ScenePath(v.transform)} enabled={v.enabled} active={v.gameObject.activeInHierarchy} global={v.isGlobal} layer={v.gameObject.layer} prio={v.priority} weight={v.weight} profile={(v.sharedProfile != null ? v.sharedProfile.name : "null")}");
            }
            var local = _puppets.FirstOrDefault(p => p != null && p.IsLocalPlayer);
            var fpc = local?.Go.GetComponent<FirstPersonController>();
            if (fpc != null && fpc.playerCamera != null)
            {
                var l = fpc.playerCamera.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
                Plugin.Log.LogDebug($"[pp] player cam layer={(l != null ? $"yes mask={l.volumeLayer.value:X8} aa={l.antialiasingMode}" : "none")}");
            }
        }

        // on the recording player's team, by the teams saved in the .strp.json (none in older files: nobody is)
        bool IsTeammate(ObjectInfo info)
        {
            if (info.Player == null || info.Player.IsLocal || _meta == null) return false;
            var me = _meta.Players.FirstOrDefault(m => m.IsLocal && m.TeamId.HasValue);
            var them = _meta.Players.FirstOrDefault(m => m.PlayerId == info.Player.PlayerId);
            return me != null && them?.TeamId != null && them.TeamId == me.TeamId;
        }

        static readonly FieldInfo F_SetupCameras = HarmonyLib.AccessTools.Field(typeof(PlayerSetup), "cameras");
        readonly List<Camera> _vmCams = new List<Camera>();

        // In-game the arms/weapon are drawn by the player's extra cameras (own FOV, depth-only clear),
        // so they never clip into walls. Recreate them as overlays on the replay camera.
        void BuildViewmodelCameras()
        {
            _vmCams.Clear();
            _weaponCam = null;
            _styled = false;
            var local = _puppets.FirstOrDefault(p => p != null && p.IsLocalPlayer && p.Vm.Length > 0);
            var setup = local != null ? local.Go.GetComponent<PlayerSetup>() : null;
            if (setup == null || !(F_SetupCameras?.GetValue(setup) is Camera[] cams) || cams.Length < 2) return; // no first-person rig: no overlays
            for (int i = 1; i < cams.Length; i++)
            {
                var src = cams[i];
                if (src == null) continue;
                var go = new GameObject($"Streplay Viewmodel Cam {i}");
                go.transform.SetParent(_cam.transform, false);
                var c = go.AddComponent<Camera>();
                c.CopyFrom(src);
                c.targetTexture = null;
                c.clearFlags = CameraClearFlags.Depth;
                c.depth = _cam.depth + i;
                c.enabled = false;
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                _vmCams.Add(c);
                if (i == 1) { _weaponCam = c; _weaponNear = c.nearClipPlane; _weaponFar = c.farClipPlane; }
                Plugin.Log.LogDebug($"Viewmodel camera {i}: fov={c.fieldOfView} mask={c.cullingMask:X8} near={c.nearClipPlane}");
            }
            if (_vmCams.Count > 0) _maskFirstPerson = _maskWorld; // overlays draw the viewmodel layers
        }

        readonly ReplayPostFx _postFx = new ReplayPostFx();

        void SetupPostFx()
        {
            var local = _puppets.FirstOrDefault(p => p != null && p.IsLocalPlayer);
            var setup = local != null ? local.Go.GetComponent<PlayerSetup>() : null;
            if (!(F_SetupCameras?.GetValue(setup) is Camera[] cams) || cams.Length == 0) return;
            var pairs = new List<(Camera, Camera)> { (cams[0], _cam) };
            for (int i = 1; i < cams.Length; i++)
            {
                var dst = _vmCams.FirstOrDefault(c => c != null && c.name == $"Streplay Viewmodel Cam {i}");
                if (dst != null) pairs.Add((cams[i], dst));
            }
            _postFx.Setup(pairs);
        }

        ViewState? ViewAt(int frame)
        {
            var fr = _data.ViewFrames;
            int lo = 0, hi = fr.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (fr[mid] <= frame) { best = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return best >= 0 ? _data.ViewStates[best] : (ViewState?)null;
        }

        int _maskWorld, _maskFirstPerson;
        static readonly FieldInfo F_LowMask = HarmonyLib.AccessTools.Field(typeof(PlayerSetup), "lowMask");
        static readonly FieldInfo F_HighMask = HarmonyLib.AccessTools.Field(typeof(PlayerSetup), "highMask");

        // One line per kind of replay object so a broken one is easy to spot in the BepInEx log.
        void LogPuppetDiagnostics()
        {
            foreach (var g in _puppets.Where(p => p?.Go != null).GroupBy(p => p.Info.Name))
            {
                var p = g.FirstOrDefault(x => x.Go.activeInHierarchy) ?? g.First();
                var rs = p.Go.GetComponentsInChildren<Renderer>(true);
                int on = rs.Count(r => r.enabled && r.gameObject.activeInHierarchy);
                int culled = rs.Count(r => (_cam.cullingMask & (1 << r.gameObject.layer)) == 0);
                var layers = string.Join(",", rs.Select(r => r.gameObject.layer).Distinct());
                Plugin.Log.LogDebug($"[diag] {g.Key} x{g.Count()} owned={p.Owned} active={p.Go.activeInHierarchy} pos={p.Go.transform.position} " +
                                   $"scale={p.Go.transform.lossyScale} renderers={on}/{rs.Length} culledByCam={culled} layers={layers}");
            }
        }

        // ---------------------------------------------------------------- per frame

        void Update()
        {
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { UpdateInner(); }
            finally { if (_rendering && _job != null) _job.TUpdate += System.Diagnostics.Stopwatch.GetTimestamp() - p0; }
        }

        void UpdateInner()
        {
            PollRenderDone();
            if (Active) GuardLobby();
            if (_phase != Phase.Ready || _data == null) return;
            KeepUiHidden();
            HandleInput();
            if (_phase != Phase.Ready || _data == null) return; // Esc exits inside HandleInput
            if (_startRender) { _startRender = false; StartRender(); }
            if (_audioPass != null) TickAudioPass();

            float prev = _t;
            if (_rendering)
            {
                StepRender();
            }
            else if (_playing)
            {
                float sp = _audioPass?.Job.Ramp != null && _audioPass.Recording ? _audioPass.Job.SpeedAt(_t) : Speed;
                _t += Time.unscaledDeltaTime * sp;
                Time.timeScale = sp;
            }
            else
            {
                Time.timeScale = 0f;
            }

            if (!_rendering && _t >= _data.Duration)
            {
                _t = _data.Duration;
                _playing = false;
            }

            if (_t > prev)
            {
                FireEvents(prev, _t);
                if (!_rendering) _audio.Fire(prev, _t);
            }
            Apply(_t);
            AudioListener.pause = !_playing || _rendering;
            if (_playing && !_rendering) _audio.ApplySources(_curFrameIndex, _t);
            UpdateCamera();
        }

        public void Seek(float t)
        {
            if (_data == null) return;
            _t = Mathf.Clamp(t, 0f, _data.Duration);
            ClearEventSpawned();
            _audio.Seek(_t);
            try { RestoreEffects(_t); }
            catch (Exception e) { Plugin.Log.LogWarning($"Restoring effects after seek failed: {e.Message}"); }
            _nextEvent = LowerBoundEvent(_t);
            Apply(_t);
        }

        // ---------------------------------------------------------------- effects that outlive their event

        // Ragdolls (and recent bullet holes) stay in the world, so after a seek they're rebuilt as they'd be
        // at that moment: fired at the puppets' recorded positions, physics settled, particles fast-forwarded.
        // Kept per event so dragging the timeline only touches what actually changes.
        readonly Dictionary<int, List<GameObject>> _restored = new Dictionary<int, List<GameObject>>();
        List<(string name, float maxAge)> _restoreRules;
        const float SettleSeconds = 4f;

        bool RestoreRule(ReplayEvent ev, out float maxAge)
        {
            if (_restoreRules == null)
            {
                _restoreRules = new List<(string, float)>();
                foreach (var part in Plugin.SeekRestoreEffects.Value.Split(','))
                {
                    var bits = part.Trim().Split(':');
                    if (bits[0].Length == 0) continue;
                    float age = bits.Length > 1 && float.TryParse(bits[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0f;
                    _restoreRules.Add((bits[0], age));
                }
            }
            foreach (var (name, age) in _restoreRules)
                if (ev.Method == name) { maxAge = age; return true; }
            maxAge = 0f;
            return false;
        }

        bool Replayable(ReplayEvent ev) =>
            Plugin.ReplayEffects.Value && Plugin.ContainsAny(ev.Method, Plugin.EffectAllowlist.Value) && !Plugin.ContainsAny(ev.Method, Plugin.EffectDenylist.Value);

        void RestoreEffects(float t)
        {
            var evs = _data.Events;
            var want = new List<int>();
            var ragdolled = new HashSet<int>();
            for (int i = 0; i < evs.Count && evs[i].T <= t; i++)
            {
                if (!RestoreRule(evs[i], out float maxAge) || (maxAge > 0f && t - evs[i].T > maxAge) || !Replayable(evs[i])) continue;
                // the game sends the death RPC several times and only acts on the first (spawnedRagdoll)
                if (evs[i].Method == "ExplodeForAll" && !ragdolled.Add(evs[i].Obj)) continue;
                want.Add(i);
            }
            var wantSet = new HashSet<int>(want);
            foreach (var k in _restored.Keys.Where(k => !wantSet.Contains(k)).ToList())
            {
                foreach (var go in _restored[k]) if (go != null) Destroy(go);
                _restored.Remove(k);
            }
            foreach (var p in _puppets)
                if (p?.Health != null && PuppetFactory.F_SpawnedRagdoll != null)
                    PuppetFactory.F_SpawnedRagdoll.SetValue(p.Health, _restored.Keys.Any(k => evs[k].Obj == p.Info.Index && evs[k].Method == "ExplodeForAll"));

            var add = want.Where(i => !_restored.ContainsKey(i)).ToList();
            if (add.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool auto = Physics.autoSimulation;
            Physics.autoSimulation = false;
            var fresh = new List<(GameObject go, float age)>();
            try
            {
                for (int k = 0; k < add.Count; k++)
                {
                    var ev = evs[add[k]];
                    Apply(ev.T); // puppets where they were, so the effect spawns in the right place
                    var spawned = new List<GameObject>();
                    Invoke(ev, spawned);
                    _restored[add[k]] = spawned;
                    foreach (var go in spawned) fresh.Add((go, t - ev.T));
                    float until = k + 1 < add.Count ? Mathf.Min(evs[add[k + 1]].T, t) : t;
                    float sim = Mathf.Min(SettleSeconds, until - ev.T);
                    for (float done = 0f; done < sim; done += Time.fixedDeltaTime) Physics.Simulate(Time.fixedDeltaTime);
                }
            }
            finally { Physics.autoSimulation = auto; }

            foreach (var (go, age) in fresh)
            {
                if (go == null) continue;
                // one-shot bursts (blood, sparks) are long over by now; looping ones carry on
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps.transform.parent != null && ps.transform.parent.GetComponentInParent<ParticleSystem>() != null) continue;
                    ps.Simulate(Mathf.Min(age, 30f), true, false, true);
                    if (ps.IsAlive(true)) ps.Play(true);
                }
                foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) a.Stop();
            }
            if (ReplayAudio.LogEach) Plugin.Log.LogDebug($"[restore] t={t:0.00} re-fired {add.Count} effects ({string.Join(", ", add.Select(i => evs[i].Method).Distinct())}), {_restored.Count} kept, {sw.ElapsedMilliseconds} ms");
        }

        int FrameAt(float t)
        {
            var frames = _data.Frames;
            int lo = 0, hi = frames.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (frames[mid].T <= t) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        int LowerBoundEvent(float t)
        {
            var ev = _data.Events;
            int lo = 0, hi = ev.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (ev[mid].T <= t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        Frame _curFrame;

        void Apply(float t)
        {
            int i = FrameAt(t);
            _curFrameIndex = i;
            var a = _data.Frames[i];
            var b = i + 1 < _data.Frames.Count ? _data.Frames[i + 1] : a;
            float alpha = b.T > a.T ? Mathf.Clamp01((t - a.T) / (b.T - a.T)) : 0f;
            _curFrame = a;
            _origPos = Vector3.Lerp(a.CamPos, b.CamPos, alpha);
            _origRot = Quaternion.Slerp(a.CamRot, b.CamRot, alpha);
            _origFov = Mathf.Lerp(a.CamFov, b.CamFov, alpha);

            for (int k = 0; k < _nextLookup.Length; k++) _nextLookup[k] = -1;
            for (int k = 0; k < b.Objects.Length; k++) _nextLookup[b.Objects[k].Obj] = k;
            foreach (var p in _puppets) if (p != null) p.Seen = false;

            foreach (var s in a.Objects)
            {
                var p = _puppets[s.Obj];
                if (p == null || p.Go == null) continue;
                int ni = _nextLookup[s.Obj];
                var n = ni >= 0 ? b.Objects[ni] : s;
                p.Seen = true;
                ApplyState(p, s, n, alpha);
            }

            foreach (var p in _puppets)
                if (p != null && p.Owned && !p.Seen && p.Go != null && p.Go.activeSelf) p.Go.SetActive(false);

            // Other players' weapons use their recorded transform. The local player's own weapon was
            // recorded in first-person viewmodel space; LateUpdate re-seats it on the third-person hand.

            ApplyPovHiding();
        }

        void ApplyState(Puppet p, ObjState s, ObjState n, float alpha)
        {
            var go = p.Go;
            if (go.activeSelf != s.Active) go.SetActive(s.Active);
            if (!s.Active) return;

            var tr = go.transform;
            tr.SetPositionAndRotation(Vector3.Lerp(s.Pos, n.Pos, alpha), Quaternion.Slerp(s.Rot, n.Rot, alpha));
            // a replayed death or drop hands a weapon back to physics; it would fall between frames and be put back
            // by the recording every frame (a looping wobble), so recorded items never simulate
            foreach (var rb in p.Bodies) if (rb != null && !rb.isKinematic) rb.isKinematic = true;
            if (p.Owned && tr.parent == null) tr.localScale = Vector3.Lerp(s.Scale, n.Scale, alpha);

            int nc = Math.Min(p.Children.Length, s.ChildPos.Length);
            bool childLerp = n.ChildPos.Length == s.ChildPos.Length;
            for (int c = 0; c < nc; c++)
            {
                var ct = p.Children[c];
                if (ct == null || ct == tr) continue;
                ct.localPosition = childLerp ? Vector3.Lerp(s.ChildPos[c], n.ChildPos[c], alpha) : s.ChildPos[c];
                ct.localRotation = childLerp ? Quaternion.Slerp(s.ChildRot[c], n.ChildRot[c], alpha) : s.ChildRot[c];
            }

            if (s.HasPlayer && p.Health != null && p.Health.graphics != null && p.Health.graphics.activeSelf != s.GraphicsActive)
                p.Health.graphics.SetActive(s.GraphicsActive);

            int na = Math.Min(p.Anims.Length, s.Anims.Length);
            for (int k = 0; k < na; k++)
                ApplyAnimator(p, k, s.Anims[k], k < n.Anims.Length ? n.Anims[k] : null, alpha);

            p.CurS = s;
            p.CurN = n;
            p.CurAlpha = alpha;

            if (s.HasPlayer)
            {
                p.PovPos = Vector3.Lerp(s.PovPos, n.PovPos, alpha);
                p.PovRot = Quaternion.Slerp(s.PovRot, n.PovRot, alpha);
                p.HeldRight = s.HeldRight;
                p.HeldLeft = s.HeldLeft;
            }
        }

        // a disabled Animator (a body posed from the recorded skeleton) lists its parameters only while enabled
        static AnimatorControllerParameter[] AnimatorParameters(Animator a)
        {
            if (a.enabled) return a.parameters;
            a.enabled = true;
            try { return a.parameters; }
            finally { a.enabled = false; }
        }

        static void ApplyAnimator(Puppet p, int k, AnimState s, AnimState n, float alpha)
        {
            var a = p.Anims[k];
            if (a == null) return;
            // Bind runs while the copy is still inactive, when an Animator lists no parameters: take them once it's active
            var ps = p.AnimParams[k];
            if ((ps == null || ps.Length == 0) && s.Params.Length > 0 && a.gameObject.activeInHierarchy && a.runtimeAnimatorController != null)
                ps = p.AnimParams[k] = AnimatorParameters(a);
            if (!s.Enabled || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return;

            if (ps != null && ps.Length == s.Params.Length)
            {
                for (int i = 0; i < ps.Length; i++)
                {
                    float v = n != null && n.Params.Length == s.Params.Length ? Mathf.Lerp(s.Params[i], n.Params[i], alpha) : s.Params[i];
                    switch (ps[i].type)
                    {
                        case AnimatorControllerParameterType.Float: a.SetFloat(ps[i].nameHash, v); break;
                        case AnimatorControllerParameterType.Int: a.SetInteger(ps[i].nameHash, Mathf.RoundToInt(s.Params[i])); break;
                        case AnimatorControllerParameterType.Bool: a.SetBool(ps[i].nameHash, s.Params[i] > 0.5f); break;
                    }
                }
            }

            int layers = Math.Min(a.layerCount, s.Layers.Length);
            for (int l = 0; l < layers; l++)
            {
                var ls = s.Layers[l];
                if (!a.HasState(l, ls.Hash)) continue;
                float time = ls.Time;
                float weight = ls.Weight;
                if (n != null && l < n.Layers.Length)
                {
                    var ln = n.Layers[l];
                    if (ln.Hash == ls.Hash && ln.Time >= ls.Time) time = Mathf.Lerp(ls.Time, ln.Time, alpha);
                    weight = Mathf.Lerp(ls.Weight, ln.Weight, alpha);
                }
                if (l > 0) a.SetLayerWeight(l, weight);
                a.Play(ls.Hash, l, time);
            }
            a.Update(0f);
        }

        void SeatHeld(Puppet player, int itemIndex, int hand)
        {
            if (itemIndex < 0 || itemIndex >= _puppets.Length) return;
            var item = _puppets[itemIndex];
            if (item == null || item.Go == null || !item.Owned) return;
            var points = player.HandPoints;
            if (points.Length <= hand || points[hand] == null) return;
            if (!item.Go.activeSelf) item.Go.SetActive(true);
            item.Seen = true;
            item.Go.transform.SetPositionAndRotation(points[hand].position, points[hand].rotation);
            item.Go.transform.localScale = Vector3.one;
        }

        // ---------------------------------------------------------------- events

        static readonly Assembly GameAsm = typeof(FirstPersonController).Assembly;
        readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
        readonly HashSet<string> _warned = new HashSet<string>();

        void FireEvents(float from, float to)
        {
            var evs = _data.Events;
            while (_nextEvent < evs.Count && evs[_nextEvent].T <= to)
            {
                var ev = evs[_nextEvent++];
                if (ev.T <= from) continue;
                AudioCapture.ReplayClips.Clear();
                if (Replayable(ev))
                {
                    if (RestoreRule(ev, out _))
                    {
                        var spawned = new List<GameObject>();
                        Invoke(ev, spawned);
                        int idx = _nextEvent - 1;
                        if (_restored.TryGetValue(idx, out var old)) foreach (var go in old) if (go != null) Destroy(go);
                        _restored[idx] = spawned;
                    }
                    else Invoke(ev, _eventSpawned);
                }
                // sounds the replayed RPC didn't make itself (filtered, puppet hidden, owner-only branch like
                // the death sting, or state a puppet doesn't have): play what it played live instead
                if (ev.Sounds != null && !_rendering) _audio.PlayForRpc(ev, AudioCapture.ReplayClips);
            }
        }

        void Invoke(ReplayEvent ev, List<GameObject> spawnedInto)
        {
            if (ev.Obj < 0 || ev.Obj >= _puppets.Length) return;
            var p = _puppets[ev.Obj];
            if (p == null || p.Go == null || !p.Go.activeInHierarchy) return;
            var target = string.IsNullOrEmpty(ev.ChildPath) ? p.Go.transform : p.Go.transform.Find(ev.ChildPath);
            if (target == null) return;
            var type = GameAsm.GetType(ev.ComponentType);
            if (type == null) return;
            var comp = target.GetComponent(type);
            if (comp == null) return;
            var method = FindLogic(type, ev.Method, ev.Args.Length);
            if (method == null) return;

            var pars = method.GetParameters();
            var args = new object[pars.Length];
            for (int i = 0; i < pars.Length; i++) args[i] = ConvertArg(ev.Args[i], pars[i].ParameterType);

            // the ragdoll throws the player's hat; give it a fresh one each time so a seek can rebuild it
            if (ev.Method == "ExplodeForAll" && p.HatPrefab != null && p.HatRoot != null && p.Health != null
                && PuppetFactory.F_SpawnedRagdoll?.GetValue(p.Health) is bool spawned && !spawned)
            {
                var setup = p.Go.GetComponent<PlayerSetup>();
                if (setup != null)
                {
                    setup.hat = PuppetFactory.Wear(p.HatPrefab, p.HatRoot.transform, 18);
                    foreach (var rb in setup.hat.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = false; // it has to fly
                }
            }

            // effects can spawn under a transform passed as an argument (bullet holes parented to the wall
            // that was hit); remember child counts so those are tracked and cleaned up like scene roots
            List<(Transform t, int count)> parents = null;
            foreach (var a in args)
            {
                var t = a is Component c ? (c != null ? c.transform : null) : a is GameObject g && g != null ? g.transform : null;
                if (t == null) continue;
                (parents ??= new List<(Transform, int)>()).Add((t, t.childCount));
            }

            var scene = SceneManager.GetActiveScene();
            int before = scene.rootCount;
            try
            {
                method.Invoke(comp, args);
            }
            catch (TargetInvocationException e)
            {
                if (_warned.Add(ev.Method))
                    Plugin.Log.LogDebug($"Effect {ev.ComponentType}.{ev.Method} partially failed during replay: {e.InnerException?.Message}");
            }
            catch (Exception e)
            {
                if (_warned.Add(ev.Method))
                    Plugin.Log.LogDebug($"Effect {ev.ComponentType}.{ev.Method} could not be replayed: {e.Message}");
            }
            int after = scene.rootCount;
            if (after > before)
            {
                var roots = scene.GetRootGameObjects();
                for (int i = before; i < roots.Length; i++)
                    if (roots[i] != null && !IsPuppet(roots[i])) spawnedInto.Add(roots[i]);
            }
            if (parents != null)
                foreach (var (t, count) in parents)
                {
                    if (t == null) continue;
                    for (int i = count; i < t.childCount; i++)
                    {
                        var child = t.GetChild(i).gameObject;
                        if (!IsPuppet(child) && !spawnedInto.Contains(child)) spawnedInto.Add(child);
                    }
                }
        }

        bool IsPuppet(GameObject go)
        {
            foreach (var p in _puppets) if (p != null && p.Go == go) return true;
            return go == (_cam != null ? _cam.gameObject : null);
        }

        MethodInfo FindLogic(Type type, string name, int argc)
        {
            string key = type.FullName + "|" + name + "|" + argc;
            if (_methodCache.TryGetValue(key, out var mi)) return mi;
            string prefix = RpcCapture.LogicPrefix + name + "_";
            for (var t = type; t != null && mi == null; t = t.BaseType)
                mi = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name.StartsWith(prefix, StringComparison.Ordinal) && m.GetParameters().Length == argc);
            _methodCache[key] = mi;
            return mi;
        }

        object ConvertArg(object v, Type pt)
        {
            switch (v)
            {
                case null:
                    return pt.IsValueType ? Activator.CreateInstance(pt) : null;
                case ArgObjRef r:
                {
                    if (r.Obj < 0 || r.Obj >= _puppets.Length || _puppets[r.Obj]?.Go == null) return null;
                    var root = _puppets[r.Obj].Go.transform;
                    var t = string.IsNullOrEmpty(r.Path) ? root : root.Find(r.Path);
                    return FromTransform(t, pt);
                }
                case ArgSceneRef s:
                    return FromTransform(FindScenePath(s.Path), pt);
            }
            if (pt.IsInstanceOfType(v)) return v;
            try
            {
                if (pt.IsEnum) return Enum.ToObject(pt, Convert.ToInt32(v));
                return Convert.ChangeType(v, pt);
            }
            catch
            {
                return pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
        }

        static object FromTransform(Transform t, Type pt)
        {
            if (t == null) return null;
            if (pt == typeof(Transform)) return t;
            if (pt == typeof(GameObject)) return t.gameObject;
            if (typeof(Component).IsAssignableFrom(pt)) return t.GetComponent(pt);
            return null;
        }

        static Transform FindScenePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != rootName) continue;
                var t = slash < 0 ? root.transform : root.transform.Find(path.Substring(slash + 1));
                if (t != null) return t;
            }
            return null;
        }

        void ClearEventSpawned()
        {
            foreach (var go in _eventSpawned) if (go != null) Destroy(go);
            _eventSpawned.Clear();
        }

        void ClearRestored()
        {
            foreach (var list in _restored.Values) foreach (var go in list) if (go != null) Destroy(go);
            _restored.Clear();
        }

        // ---------------------------------------------------------------- markers / render

        List<ReplayMeta.MetaMarker> Markers =>
            _meta != null && _meta.Markers.Count > 0 ? _meta.Markers
            : _data?.Markers.Select(m => new ReplayMeta.MetaMarker { T = m.T, Label = m.Label, Auto = m.Auto }).ToList()
              ?? new List<ReplayMeta.MetaMarker>();

        void AddMarkerHere()
        {
            if (_meta == null) return;
            _meta.Markers.Add(new ReplayMeta.MetaMarker { T = _t, Label = "Marker" });
            _meta.Markers.Sort((x, y) => x.T.CompareTo(y.T));
            try { _meta.Save(); } catch (Exception e) { Plugin.Log.LogWarning($"Could not save marker: {e.Message}"); }
        }

        void JumpMarker(int dir)
        {
            var ms = Markers;
            if (ms.Count == 0) return;
            const float eps = 0.05f;
            float pre = Plugin.MarkerPreroll.Value;
            if (dir > 0)
            {
                var m = ms.Where(x => x.T - pre > _t + eps).OrderBy(x => x.T).FirstOrDefault();
                if (m != null) Seek(m.T - pre);
            }
            else
            {
                var m = ms.Where(x => x.T - pre < _t - eps).OrderByDescending(x => x.T).FirstOrDefault();
                if (m != null) Seek(Mathf.Max(0, m.T - pre));
            }
        }
    }
}
