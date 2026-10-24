using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Streplay
{
    // Camera control, keyboard shortcuts and the on-screen timeline for replay playback.
    internal sealed partial class ReplayPlayer
    {
        enum CamMode { Original, Free, Pov, Chase, Orbit, VictimCam, Drone, Low, Flyby, Sweep, TopDown, Static }
        CamMode _camMode = CamMode.Original;
        int _povPlayer = int.MinValue;     // PlayerKey of the POV target (players respawn as new objects)
        Puppet _hudBoundTo;
        bool _smoothPov = true;

        Vector3 _freePos;
        float _yaw, _pitch, _fov = 90f;
        float _speedMul = 1f;

        Vector3 _origPos;
        Quaternion _origRot = Quaternion.identity;
        float _origFov = 90f;

        Vector3 _smoothPos;
        Quaternion _smoothRot = Quaternion.identity;

        bool _uiVisible = true;
        string _toast;
        float _toastUntil;

        // A player gets a new networked object each time they respawn, so cameras follow a player
        // (by id) and resolve to whichever of their objects is live at the current time.
        static int PlayerKey(Puppet p) =>
            p.Info.Player != null && p.Info.Player.PlayerId >= 0 ? p.Info.Player.PlayerId : -1 - p.Info.Index;

        Puppet Current(Func<Puppet, bool> match)
        {
            Puppet seen = null, latest = null;
            foreach (var p in _puppets)
            {
                if (p == null || !p.Info.IsPlayer || !match(p)) continue;
                if (p.Seen && (seen == null || p.Info.SpawnTime > seen.Info.SpawnTime)) seen = p;
                if (p.Info.SpawnTime <= _t && (latest == null || p.Info.SpawnTime > latest.Info.SpawnTime)) latest = p;
            }
            return seen ?? latest;
        }

        Puppet CurrentLocal() => Current(p => p.IsLocalPlayer);
        Puppet CurrentOf(int key) => Current(p => PlayerKey(p) == key);

        // one entry per player, in order of first appearance
        int[] PlayerKeys => _puppets.Where(p => p != null && p.Info.IsPlayer)
            .OrderBy(p => p.Info.SpawnTime).Select(PlayerKey).Distinct().ToArray();

        // ---------------------------------------------------------------- input

        void HandleInput()
        {
            if (_rendering)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) StopRender(true);
                return;
            }
            if (_audioPass != null)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) CancelAudioPass();
                return;
            }
            // typing in a render panel field shouldn't trigger shortcuts; Esc just leaves the field
            if (GUIUtility.keyboardControl != 0)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return)) GUIUtility.keyboardControl = 0;
                return;
            }

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.Space)) TogglePlay();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) Seek(_t - (shift ? 1f : 5f));
            if (Input.GetKeyDown(KeyCode.RightArrow)) Seek(_t + (shift ? 1f : 5f));
            if (Input.GetKeyDown(KeyCode.Comma)) { _playing = false; Seek(_t - 1f / 60f); }
            if (Input.GetKeyDown(KeyCode.Period)) { _playing = false; Seek(_t + 1f / 60f); }
            if (Input.GetKeyDown(KeyCode.LeftBracket)) JumpMarker(-1);
            if (Input.GetKeyDown(KeyCode.RightBracket)) JumpMarker(+1);
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) _speedIndex = Mathf.Max(0, _speedIndex - 1);
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) _speedIndex = Mathf.Min(Speeds.Length - 1, _speedIndex + 1);
            if (Input.GetKeyDown(KeyCode.M)) AddMarkerHere();
            if (Input.GetKeyDown(KeyCode.H)) _uiVisible = !_uiVisible;
            if (Input.GetKeyDown(KeyCode.U)) _hudEnabled = !_hudEnabled;
            if (Input.GetKeyDown(KeyCode.F)) SetFree();
            if (Input.GetKeyDown(KeyCode.V)) _camMode = CamMode.Original;
            if (Input.GetKeyDown(KeyCode.G)) _smoothPov = !_smoothPov;
            if (Input.GetKeyDown(KeyCode.I)) _in = _t;
            if (Input.GetKeyDown(KeyCode.O)) _out = _t;
            if (Input.GetKeyDown(KeyCode.C)) ClipHere();
            if (Input.GetKeyDown(KeyCode.R)) _renderPanel = !_renderPanel;
            if (Input.GetKeyDown(KeyCode.Escape)) { Exit(); return; }

            var keys = PlayerKeys;
            for (int k = 0; k < 9 && k < keys.Length; k++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + k)) SetPov(keys[k]);
            if (Input.GetKeyDown(KeyCode.Tab) && keys.Length > 0)
            {
                int cur = Array.IndexOf(keys, _povPlayer);
                SetPov(keys[(cur + 1) % keys.Length]);
            }
        }

        // hooks for AutoTest
        internal bool TestIsReady => _phase == Phase.Ready;
        internal string TestAudioStats => _audio.Stats;
        internal string TestSeekEvent(string method, float offset)
        {
            var ev = _data.Events.FirstOrDefault(e => e.Method == method);
            if (ev == null) return $"no {method} event";
            Seek(ev.T + offset);
            return $"seek {ev.T + offset:0.000} ({method} at {ev.T:0.000})";
        }

        // seek to just after the first recorded lens-distortion kick (kill shockwave)
        internal string TestSeekView(float after)
        {
            for (int i = 0; i < _data.ViewStates.Count; i++)
                if (Mathf.Abs(_data.ViewStates[i].Lens) > 1f)
                {
                    float t = _data.Frames[_data.ViewFrames[i]].T + after;
                    Seek(t);
                    return $"seek {t:0.000} (lens {_data.ViewStates[i].Lens:0.0}, sat {_data.ViewStates[i].Saturation:0.0})";
                }
            return "no lens change recorded";
        }
        internal void TestSetPlaying(bool on) => _playing = on;
        internal void TestSetSpeed(int i) => _speedIndex = Mathf.Clamp(i, 0, Speeds.Length - 1);
        internal void TestSetHud(bool on) => _hudEnabled = on;
        internal void TestSetUi(bool on) => _uiVisible = on;
        internal void TestClip(float a, float b) { _in = a; _out = b; }
        internal void TestZoom(float a, float b) { if (b <= a) _v1 = -1f; else { _v0 = a; _v1 = b; } }
        internal void TestHelp(bool on) => _help = on;
        internal void TestProbe() => _hud.ProbeCentre(_rendering ? "render" : "window");
        internal void TestMoveFree(Vector3 d) => _freePos += Quaternion.Euler(_pitch, _yaw, 0f) * d;
        internal string TestTeams() => string.Join(", ", _puppets.Where(p => p != null && p.Info.IsPlayer && p.Owned)
            .Select(p => $"{p.DisplayName}{(p.IsLocalPlayer ? " (you)" : p.Teammate ? " (teammate)" : " (enemy)")}").Distinct());

        // free cam 1.5 m from a point, looking at it (matches AutoTest's livecamat)
        internal void TestCamAt(Vector3 c)
        {
            _freePos = c + new Vector3(1.5f, 0.6f, 0.45f);
            var e = Quaternion.LookRotation(c - _freePos).eulerAngles;
            _yaw = e.y; _pitch = e.x > 180 ? e.x - 360 : e.x; _fov = 60f; _camMode = CamMode.Free;
        }

        // free cam d metres from the first item of that name, looking at it
        internal void TestCamItem(string name, float d)
        {
            var p = _puppets.FirstOrDefault(q => q?.Go != null && q.Go.activeInHierarchy && q.Info.Name.StartsWith(name));
            if (p == null) return;
            var c = p.Go.transform.position;
            _freePos = c + new Vector3(d, 0.4f, d * 0.3f);
            var e = Quaternion.LookRotation(c - _freePos).eulerAngles;
            _yaw = e.y; _pitch = e.x > 180 ? e.x - 360 : e.x; _fov = 60f; _camMode = CamMode.Free;
        }

        // free cam d metres to the right of player n, looking at their chest
        internal void TestCamNear(int n, float d)
        {
            var keys = PlayerKeys;
            if (n < 1 || n > keys.Length) return;
            var p = CurrentOf(keys[n - 1]);
            if (p == null) return;
            var t = p.Go.transform;
            var chest = t.position + Vector3.up * 1.2f;
            _freePos = chest + t.right * d + Vector3.up * 0.3f;
            var rot = Quaternion.LookRotation(chest - _freePos);
            var e = rot.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180 ? e.x - 360 : e.x;
            _fov = 60f;
            _camMode = CamMode.Free;
        }

        // log how far each held item sits from the hand point it should be in
        internal void TestHeldDiag()
        {
            foreach (var p in _puppets)
            {
                if (p != null && p.Info.IsPlayer)
                    Plugin.Log.LogDebug($"[held] player obj={p.Info.ObjectId} {p.DisplayName} seen={p.Seen} heldR={p.HeldRight} heldL={p.HeldLeft} " +
                                       $"pickup={(p.Pickup != null)} points={p.HandPoints.Length} " +
                                       $"itemPuppetR={(p.HeldRight >= 0 && p.HeldRight < _puppets.Length ? (_puppets[p.HeldRight]?.Info.Name ?? "none") : "-")}");
                if (p == null || !p.Seen || p.HandPoints.Length == 0) continue;
                foreach (var (idx, hand) in new[] { (p.HeldRight, 0), (p.HeldLeft, 1) })
                {
                    if (idx < 0 || idx >= _puppets.Length || _puppets[idx]?.Go == null) continue;
                    var item = _puppets[idx].Go.transform;
                    var point = p.HandPoints.Length > hand ? p.HandPoints[hand] : null;
                    if (point == null) continue;
                    var s = _puppets[idx].CurS;
                    Plugin.Log.LogDebug($"[held] {p.DisplayName}{(p.IsLocalPlayer ? " (you)" : "")} hand{hand} {_puppets[idx].Info.Name}: " +
                                       $"item→handpoint {Vector3.Distance(item.position, point.position):0.000}m, " +
                                       $"recorded→handpoint {(s != null ? Vector3.Distance(s.Pos, point.position) : -1):0.000}m, " +
                                       $"item scale {item.lossyScale.x:0.00}, body→item {Vector3.Distance(p.Go.transform.position, item.position):0.00}m");
                }
            }
        }

        internal void TestSetCam(string mode, int n)
        {
            if (mode == "free") SetFree();
            else if (mode == "chase") { _camMode = CamMode.Chase; _tpT = float.NegativeInfinity; }
            else if (mode == "orbit") { _camMode = CamMode.Orbit; _tpT = float.NegativeInfinity; }
            else if (mode == "victim") { _camMode = CamMode.VictimCam; _tpT = float.NegativeInfinity; }
            else if (mode == "pov")
            {
                var keys = PlayerKeys;
                if (n >= 1 && n <= keys.Length) SetPov(keys[n - 1]);
            }
            else if (Enum.TryParse(mode, true, out Angle an) && an != Angle.Current) SetAngle(an, _t); // sweep, low, drone, flyby…
            else _camMode = CamMode.Original;
        }

        void TogglePlay()
        {
            if (!_playing && _t >= _data.Duration - 0.01f) Seek(0f);
            _playing = !_playing;
        }

        void SetFree()
        {
            if (_cam != null)
            {
                _freePos = _cam.transform.position;
                var e = _cam.transform.rotation.eulerAngles;
                _yaw = e.y;
                _pitch = e.x > 180 ? e.x - 360 : e.x;
                _fov = _cam.fieldOfView;
            }
            _camMode = CamMode.Free;
        }

        void SetPov(int playerKey)
        {
            _povPlayer = playerKey;
            _camMode = CamMode.Pov;
            var p = CurrentOf(playerKey);
            if (p != null) { _smoothPos = p.PovPos; _smoothRot = p.PovRot; }
        }

        // ---------------------------------------------------------------- camera

        void UpdateCamera()
        {
            if (_cam == null) return;
            float dt = Time.unscaledDeltaTime;
            if (_camMode != CamMode.TopDown) _cam.nearClipPlane = _baseNear; // the top-down cut sets its own
            bool look = !_rendering && (Input.GetMouseButton(1) || !_uiVisible);

            if (!_rendering)
            {
                Cursor.lockState = look ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !look;
            }

            switch (_camMode)
            {
                case CamMode.Original:
                    _cam.transform.SetPositionAndRotation(_origPos, _origRot);
                    _cam.fieldOfView = _origFov > 1f ? _origFov : Plugin.DefaultFov.Value;
                    if (look && (Mathf.Abs(Input.GetAxisRaw("Mouse X")) > 0.01f || Mathf.Abs(Input.GetAxisRaw("Mouse Y")) > 0.01f)) SetFree();
                    break;

                case CamMode.Free:
                {
                    if (look)
                    {
                        float sens = Plugin.MouseSensitivity.Value;
                        _yaw += Input.GetAxisRaw("Mouse X") * sens;
                        _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * sens, -89f, 89f);
                    }
                    float scroll = PointerOverUi() ? 0f : Input.mouseScrollDelta.y; // the wheel zooms the timeline there
                    if (Mathf.Abs(scroll) > 0.01f) _speedMul = Mathf.Clamp(_speedMul * (scroll > 0 ? 1.25f : 0.8f), 0.05f, 20f);
                    if (Input.GetKey(KeyCode.Z)) _fov = Mathf.Clamp(_fov - 40f * dt, 5f, 150f);
                    if (Input.GetKey(KeyCode.X)) _fov = Mathf.Clamp(_fov + 40f * dt, 5f, 150f);

                    var rot = Quaternion.Euler(_pitch, _yaw, 0f);
                    var move = Vector3.zero;
                    if (!_rendering)
                    {
                        if (Input.GetKey(KeyCode.W)) move += Vector3.forward;
                        if (Input.GetKey(KeyCode.S)) move += Vector3.back;
                        if (Input.GetKey(KeyCode.A)) move += Vector3.left;
                        if (Input.GetKey(KeyCode.D)) move += Vector3.right;
                        if (Input.GetKey(KeyCode.E)) move += Vector3.up;
                        if (Input.GetKey(KeyCode.Q)) move += Vector3.down;
                    }
                    float speed = Plugin.FreeCamSpeed.Value * _speedMul;
                    if (Input.GetKey(KeyCode.LeftShift)) speed *= 4f;
                    if (Input.GetKey(KeyCode.LeftControl)) speed *= 0.25f;
                    _freePos += rot * move * speed * dt;
                    _cam.transform.SetPositionAndRotation(_freePos, rot);
                    _cam.fieldOfView = _fov;
                    break;
                }

                case CamMode.Pov:
                {
                    var p = CurrentOf(_povPlayer);
                    if (p == null) { _camMode = CamMode.Original; break; }
                    if (p.Info.Player != null && p.Info.Player.IsLocal)
                    {
                        // our own POV: the recorded camera is exactly what we saw
                        _cam.transform.SetPositionAndRotation(_origPos, _origRot);
                        _cam.fieldOfView = _origFov > 1f ? _origFov : Plugin.DefaultFov.Value;
                        break;
                    }
                    float k = _smoothPov ? 1f - Mathf.Exp(-20f * (_rendering ? _renderDt : dt)) : 1f;
                    _smoothPos = Vector3.Lerp(_smoothPos, p.PovPos, k);
                    _smoothRot = Quaternion.Slerp(_smoothRot, p.PovRot, k);
                    _cam.transform.SetPositionAndRotation(_smoothPos, _smoothRot);
                    _cam.fieldOfView = Plugin.DefaultFov.Value;
                    break;
                }

                case CamMode.Chase:
                case CamMode.Orbit:
                case CamMode.VictimCam:
                case CamMode.Drone:
                case CamMode.Low:
                case CamMode.Sweep:
                case CamMode.TopDown:
                case CamMode.Static:
                    UpdateAngleCamera();
                    break;
                case CamMode.Flyby:
                    UpdateFlyby();
                    break;
            }
        }

        // in/out points 2.5 s either side of now: the quick way to cut a clip around a moment
        void ClipHere()
        {
            _in = Mathf.Max(0f, _t - 2.5f);
            _out = Mathf.Min(_data.Duration, _t + 2.5f);
            Toast($"Clip {Fmt(_in.Value)} - {Fmt(_out.Value)}");
        }

        // In someone's POV, hide their own body and held items so they don't block the view.
        void ApplyPovHiding()
        {
            Puppet target = null;
            if (_camMode == CamMode.Pov) target = CurrentOf(_povPlayer);
            else if (_camMode == CamMode.Original) target = CurrentLocal();
            foreach (var p in _puppets)
            {
                // like the game, nobody sees their own hat; everyone else wears it with the body
                if (p?.HatRoot == null) continue;
                bool show = p != target && (p.Health == null || p.Health.graphics == null || p.Health.graphics.activeSelf);
                if (p.HatRoot.activeSelf != show) p.HatRoot.SetActive(show);
            }
            bool sliding = target != null && target.Seen && Sliding(target);
            ShowSlideBody(sliding ? target : null);
            if (target == null || !target.Seen) return;
            if (!sliding && target.Health != null && target.Health.graphics != null) target.Health.graphics.SetActive(false);
            if (InFirstPerson(target)) return; // the held weapon is part of the recorded viewmodel
            HideItem(target.HeldRight);
            HideItem(target.HeldLeft);
        }

        // While sliding the game shows you your own torso and legs (FirstPersonController switches them on for the slide).
        // Read from the body animator's recorded parameters, which every player has, so this works in any POV: Slide, and
        // Grounded, since Slide also stays on for a moment after a slide, in the air, when the game has hidden them again.
        static readonly System.Reflection.FieldInfo F_FpcAnimator = HarmonyLib.AccessTools.Field(typeof(FirstPersonController), "animator");
        static readonly System.Reflection.FieldInfo[] F_SlideParts =
            { HarmonyLib.AccessTools.Field(typeof(FirstPersonController), "torso"), HarmonyLib.AccessTools.Field(typeof(FirstPersonController), "legLeft"),
              HarmonyLib.AccessTools.Field(typeof(FirstPersonController), "legRight") };
        static readonly System.Reflection.FieldInfo F_DisableForMe = HarmonyLib.AccessTools.Field(typeof(PlayerSetup), "gameObjectsToDisableForMe");
        static readonly int SlideParam = Animator.StringToHash("Slide"), GroundedParam = Animator.StringToHash("Grounded");
        Puppet _slideBody;
        readonly List<GameObject> _slideHidden = new List<GameObject>();

        static bool Sliding(Puppet p)
        {
            var fpc = p.Go.GetComponent<FirstPersonController>();
            if (fpc == null || p.CurS?.Anims == null || !(F_FpcAnimator?.GetValue(fpc) is Animator a)) return false;
            int k = Array.IndexOf(p.Anims, a);
            if (k < 0 || k >= p.CurS.Anims.Length) return false;
            var ps = p.AnimParams[k];
            var values = p.CurS.Anims[k].Params;
            if (ps == null || ps.Length != values.Length) return false;
            bool slide = false, grounded = false;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].nameHash == SlideParam) slide = values[i] > 0.5f;
                else if (ps[i].nameHash == GroundedParam) grounded = values[i] > 0.5f;
            }
            return slide && grounded;
        }

        // the body stays on, minus everything the game hides from its owner except the torso and legs (and no outline,
        // which the owner never has); null puts it all back
        void ShowSlideBody(Puppet p)
        {
            if (p == _slideBody)
            {
                // kept that way while it lasts (seeking re-applies the recorded body state)
                if (p == null) return;
                foreach (var go in _slideHidden) if (go != null && go.activeSelf) go.SetActive(false);
                if (_outlines) PuppetFactory.ApplyOutline(p, false);
                return;
            }
            foreach (var go in _slideHidden) if (go != null) go.SetActive(true);
            _slideHidden.Clear();
            if (_slideBody != null && _slideBody.Go != null) PuppetFactory.ApplyOutline(_slideBody, _outlines);
            _slideBody = p;
            if (p == null) return;
            var fpc = p.Go.GetComponent<FirstPersonController>();
            var keep = F_SlideParts.Select(f => f?.GetValue(fpc) as GameObject).Where(g => g != null).ToList();
            if (F_DisableForMe?.GetValue(p.Go.GetComponent<PlayerSetup>()) is GameObject[] parts)
                foreach (var go in parts)
                    if (go != null && go.activeSelf && !keep.Contains(go)) { go.SetActive(false); _slideHidden.Add(go); }
            foreach (var go in keep) if (!go.activeSelf) go.SetActive(true);
            PuppetFactory.ApplyOutline(p, false);
        }

        // Whose eyes the camera is looking through (their body/items are hidden or shown as viewmodel).
        bool IsViewTarget(Puppet p) =>
            p != null && ((_camMode == CamMode.Original && p.IsLocalPlayer) || (_camMode == CamMode.Pov && PlayerKey(p) == _povPlayer));

        // Our own POV with a recorded first-person viewmodel (v2 recordings).
        bool InFirstPerson(Puppet p) =>
            p != null && p.IsLocalPlayer && p.Vm.Length > 0
            && (_camMode == CamMode.Original || (_camMode == CamMode.Pov && PlayerKey(p) == _povPlayer));

        void LateUpdate()
        {
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { LateUpdateInner(); }
            finally { if (_rendering && _job != null) _job.TLate += System.Diagnostics.Stopwatch.GetTimestamp() - p0; }
        }

        void LateUpdateInner()
        {
            if (_phase != Phase.Ready || _data == null) return;
            foreach (var p in _puppets)
                if (p != null && p.Bones.Length > 0 && p.Seen && p.CurS?.BoneRot != null) ApplyBones(p);
            // Held items are seated on the (now posed) third-person hand, exactly where other players'
            // clients put them. This also moves the local player's weapon out of its first-person
            // viewmodel pose, and keeps guns locked to hands at any playback speed.
            foreach (var p in _puppets)
            {
                if (p == null || !p.Seen || p.Pickup == null || IsViewTarget(p)) continue;
                SeatHeld(p, p.HeldRight, 0);
                SeatHeld(p, p.HeldLeft, 1);
            }
            foreach (var p in _puppets)
            {
                if (p == null || p.Vm.Length == 0) continue;
                bool show = InFirstPerson(p) && p.Seen && p.CurS != null && p.CurS.VmPos != null && p.CurS.VmActive;
                if (show) ShowViewmodel(p);
                else if (p.VmShown) HideViewmodel(p);
            }
            if (_cam != null && _maskWorld != 0)
            {
                var local = CurrentLocal();
                bool fp = local != null && local.VmShown;
                _cam.cullingMask = fp ? _maskFirstPerson : _maskWorld;
                foreach (var c in _vmCams) if (c != null) c.enabled = fp;
                UpdateViewmodelItems(fp && _vmCams.Count > 0 ? local : null);
            }

            // the game's own HUD, as it was on screen, whenever we look through the recording player's eyes
            var me = CurrentLocal();
            if (me != _hudBoundTo && me != null)
            {
                // HUD pieces that live on the player object belong to the current life's object
                _hudBoundTo = me;
                _hud.Bind(_data, me.Go.transform);
            }
            if (_hudEnabled && me != null && me.Seen && IsViewTarget(me))
            {
                _hud.Show();
                _hud.Apply(_curFrameIndex);
            }
            else _hud.Hide();

            // recorded post-process animation (kill shockwave, desaturation) and viewmodel FOV (v6)
            var view = ViewAt(_curFrameIndex);
            _postFx.Apply(me != null && IsViewTarget(me), view);
            if (view.HasValue && view.Value.VmFov > 1f && _vmCams.Count > 0 && _vmCams[0] != null) _vmCams[0].fieldOfView = view.Value.VmFov;
            ApplyViewmodelStyle(me);

            if (_outlines != Plugin.PlayerOutlines.Value)
            {
                _outlines = Plugin.PlayerOutlines.Value;
                foreach (var p in _puppets)
                    if (p != null && p.Owned && p.Info.IsPlayer && p != _slideBody) PuppetFactory.ApplyOutline(p, _outlines);
            }
        }

        bool _outlines;
        Camera _weaponCam;   // the overlay copied from the player's weapon camera (cameras[1]), the one Kestrel's mod changes
        float _weaponNear, _weaponFar;
        bool _styled;
        Camera _mirrored;    // drawn with GL.invertCulling while it renders, so a mirrored projection keeps its front faces
        readonly List<Renderer> _hiddenArms = new List<Renderer>();
        Puppet _armsOf;
        static readonly System.Reflection.FieldInfo F_FpArms = HarmonyLib.AccessTools.Field(typeof(PlayerSetup), "fpArms");

        // The Kestrel viewmodel option: what Minimal Viewmodels Reborn does to the live weapon camera, done to the replay's
        // copy of it. The offset moves the camera (the arms are recorded where the game put them), the FOV and mirroring
        // are its own projection (the recorded viewmodel FOV doesn't apply, as in game), and the arms or the whole weapon
        // camera can be hidden. Sway and bob are already in the recorded arms. Default puts the camera back as it was.
        void ApplyViewmodelStyle(Puppet me)
        {
            var c = _weaponCam;
            if (c == null) return;
            if (Plugin.Viewmodel.Value != ViewmodelStyle.Kestrel || !KestrelViewmodels.TryGet(out var k)) { ClearViewmodelStyle(); return; }
            _styled = true;
            c.transform.localPosition = -k.Offset;
            c.fieldOfView = k.Fov;
            c.nearClipPlane = 0.1f;
            c.farClipPlane = 100f;
            c.ResetProjectionMatrix();
            if (k.Mirror) c.projectionMatrix *= Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            SetMirrored(k.Mirror ? c : null);
            if (k.HideWeapon) c.enabled = false;
            SetArmsHidden(me, k.HideArms);
        }

        void ClearViewmodelStyle()
        {
            SetMirrored(null);
            SetArmsHidden(null, false);
            if (!_styled) return;
            _styled = false;
            var c = _weaponCam;
            if (c == null) return;
            c.transform.localPosition = Vector3.zero;
            c.nearClipPlane = _weaponNear;
            c.farClipPlane = _weaponFar;
            c.ResetProjectionMatrix();
        }

        void SetArmsHidden(Puppet me, bool hide)
        {
            if (!hide) me = null;
            if (me == _armsOf) return;
            foreach (var r in _hiddenArms) if (r != null) r.enabled = true;
            _hiddenArms.Clear();
            _armsOf = me;
            if (me == null || !(F_FpArms?.GetValue(me.Go.GetComponent<PlayerSetup>()) is GameObject[] gos)) return;
            foreach (var go in gos)
                if (go != null && go.GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer r) { r.enabled = false; _hiddenArms.Add(r); }
        }

        // In first person the game draws the weapons in your hands with the weapon camera: picking one up puts it on the
        // viewmodel layer (ItemBehaviour.SetLayer), dropping it puts it back (UnsetLayer). The same here while we look
        // through the recording player's eyes, so held weapons get the viewmodel FOV (and Kestrel's settings) and don't
        // clip into walls.
        readonly List<Puppet> _vmItems = new List<Puppet>();

        void UpdateViewmodelItems(Puppet holder)
        {
            Puppet right = holder != null ? HeldItem(holder.HeldRight) : null, left = holder != null ? HeldItem(holder.HeldLeft) : null;
            for (int i = _vmItems.Count - 1; i >= 0; i--)
            {
                if (_vmItems[i] == right || _vmItems[i] == left) continue;
                SetItemLayer(_vmItems[i], 0);
                _vmItems.RemoveAt(i);
            }
            if (right != null && !_vmItems.Contains(right)) { SetItemLayer(right, 8); _vmItems.Add(right); }
            if (left != null && !_vmItems.Contains(left)) { SetItemLayer(left, 8); _vmItems.Add(left); }
        }

        Puppet HeldItem(int idx) => idx >= 0 && idx < _puppets.Length && _puppets[idx]?.Go != null && _puppets[idx].Owned ? _puppets[idx] : null;

        // like ItemBehaviour.SetLayerAllChildren, which stops at the first effect (muzzle flash, scope) so those keep their layers
        static void SetItemLayer(Puppet item, int layer)
        {
            if (item.Go == null) return;
            foreach (var t in item.Go.GetComponentsInChildren<Transform>(true))
            {
                if (t.tag == "vfx") break;
                t.gameObject.layer = layer;
            }
        }

        void SetMirrored(Camera c)
        {
            if (c == _mirrored) return;
            if (_mirrored == null) { Camera.onPreRender += MirrorPre; Camera.onPostRender += MirrorPost; }
            _mirrored = c;
            if (c == null) { Camera.onPreRender -= MirrorPre; Camera.onPostRender -= MirrorPost; GL.invertCulling = false; }
        }

        void MirrorPre(Camera c) { if (c == _mirrored) GL.invertCulling = true; }
        void MirrorPost(Camera c) { if (c == _mirrored) GL.invertCulling = false; }

        static void ApplyBones(Puppet p)
        {
            var s = p.CurS;
            var n = p.CurN != null && p.CurN.BoneRot != null && p.CurN.BoneRot.Length == s.BoneRot.Length ? p.CurN : s;
            float a = p.CurAlpha;
            var roots = p.Info.BoneIsRoot;
            int count = Math.Min(p.Bones.Length, s.BoneRot.Length);
            for (int i = 0; i < count; i++)
            {
                var b = p.Bones[i];
                if (b == null) continue;
                b.localRotation = Quaternion.Slerp(s.BoneRot[i], n.BoneRot[i], a);
                if (i < roots.Length && roots[i]) b.localPosition = Vector3.Lerp(s.BonePos[i], n.BonePos[i], a);
            }
        }

        void ShowViewmodel(Puppet p)
        {
            var root = p.Vm[0];
            if (root == null) return;
            if (!p.VmShown)
            {
                p.VmShown = true;
                Activate(p, root);
                // the arm meshes too: a puppet is set up the way other players see it, which switches off what's above them
                if (F_FpArms?.GetValue(p.Go.GetComponent<PlayerSetup>()) is GameObject[] arms)
                    foreach (var go in arms) if (go != null) Activate(p, go.transform);
                foreach (var d in p.VmDrivers) if (d != null) d.enabled = false;
            }
            var s = p.CurS;
            var n = p.CurN != null && p.CurN.VmPos != null && p.CurN.VmPos.Length == s.VmPos.Length ? p.CurN : s;
            float a = p.CurAlpha;
            int count = Math.Min(p.Vm.Length, s.VmPos.Length);
            for (int i = 0; i < count; i++)
            {
                var t = p.Vm[i];
                if (t == null) continue;
                var pos = Vector3.Lerp(s.VmPos[i], n.VmPos[i], a);
                var rot = Quaternion.Slerp(s.VmRot[i], n.VmRot[i], a);
                if (i == 0) t.SetPositionAndRotation(pos, rot);
                else { t.localPosition = pos; t.localRotation = rot; }
            }
        }

        // t and everything above it up to the puppet, remembering what was off so HideViewmodel can put it back
        static void Activate(Puppet p, Transform t)
        {
            for (; t != null && t != p.Go.transform.parent; t = t.parent)
                if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); p.VmActivated.Add(t.gameObject); }
        }

        void HideViewmodel(Puppet p)
        {
            p.VmShown = false;
            for (int i = p.VmActivated.Count - 1; i >= 0; i--)
                if (p.VmActivated[i] != null) p.VmActivated[i].SetActive(false);
            p.VmActivated.Clear();
            foreach (var d in p.VmDrivers) if (d != null) d.enabled = true;
        }

        void HideItem(int idx)
        {
            if (idx < 0 || idx >= _puppets.Length) return;
            var it = _puppets[idx];
            if (it?.Go != null && it.Owned) it.Go.SetActive(false);
        }

        // ---------------------------------------------------------------- GUI

        // where our UI is on screen (scaled units), so mouse wheel / clicks there don't also drive the free camera
        readonly List<Rect> _uiRects = new List<Rect>();
        bool _help;

        bool PointerOverUi()
        {
            if (!_uiVisible) return false;
            var m = Input.mousePosition;
            var p = new Vector2(m.x / Ui.Scale, (Screen.height - m.y) / Ui.Scale);
            foreach (var r in _uiRects) if (r.Contains(p)) return true;
            return false;
        }

        void OnGUI()
        {
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { OnGUIInner(); }
            finally { if (_rendering && _job != null) _job.TGui += System.Diagnostics.Stopwatch.GetTimestamp() - p0; }
        }

        void OnGUIInner()
        {
            if (!Active) return;
            Ui.Begin();
            Ui.EnsureIcons();
            GUI.depth = -1000;
            if (Event.current.type == EventType.Layout) _uiRects.Clear();
            try
            {
                if (_phase == Phase.Loading) { DrawLoading(); return; }
                if (_phase != Phase.Ready || _data == null) return;
                if (_rendering) { DrawRenderProgress(); return; }
                if (_audioPass != null) { DrawAudioPassProgress(); return; }
                if (!_uiVisible) return;

                DrawTimeline();
                if (!Active || _data == null) return; // Exit on the timeline closes the replay mid-frame
                DrawSidePanel();
                if (_renderPanel) DrawRenderPanel();
                if (_help) DrawHelp();
                DrawToast();
            }
            finally { Ui.End(); }
        }

        void Claim(Rect r)
        {
            if (Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint) _uiRects.Add(r);
        }

        void DrawLoading()
        {
            var r = new Rect(Ui.W / 2f - 190f, Ui.H / 2f - 46f, 380f, 92f);
            Ui.Panel(r);
            Ui.Text(new Rect(r.x + 24f, r.y + 14f, r.width - 48f, 28f), "Loading replay".ToUpperInvariant(), 18, null, true);
            Ui.Text(new Rect(r.x + 24f, r.y + 40f, r.width - 48f, 20f), _meta != null ? _meta.Scene : "", 12, Ui.Dim);
            // an indeterminate sweep
            var bar = new Rect(r.x + 24f, r.y + 68f, r.width - 48f, 4f);
            Ui.Fill(bar, Ui.Wash(0.08f));
            float f = Mathf.Repeat(Time.unscaledTime * 0.8f, 1.3f) - 0.3f;
            Ui.Fill(new Rect(bar.x + bar.width * Mathf.Clamp01(f), bar.y, bar.width * Mathf.Clamp(0.3f, 0f, 1f - Mathf.Clamp01(f)), 4f), Ui.Accent);
        }

        void DrawToast()
        {
            if (_toast == null || Time.unscaledTime >= _toastUntil) return;
            float a = Mathf.Clamp01((_toastUntil - Time.unscaledTime) * 3f);
            float w = Mathf.Min(Ui.Measure(_toast, 14, true) + 60f, Ui.W - 40f);
            var r = new Rect(Ui.W / 2f - w / 2f, 18f, w, 38f);
            var c = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            Ui.Slant(r, new Color(0f, 0f, 0f, 0.95f));
            Ui.Slant(new Rect(r.x + 10f, r.y + 10f, 7f, r.height - 20f), Ui.Accent);
            Ui.Text(new Rect(r.x + 28f, r.y, r.width - 40f, r.height), _toast, 14, Ui.Bright, true);
            GUI.color = c;
        }

        // ---------------------------------------------------------------- timeline

        enum Drag { None, Scrub, In, Out, Range, Pan }
        Drag _drag;
        float _dragAnchor;
        bool _wasPlaying;
        int _lastScrubFrame;
        float _v0, _v1 = -1f;   // visible part of the timeline (zoom); _v1 < 0 = everything

        float ViewStart => _v1 < 0f ? 0f : _v0;
        float ViewEnd => _v1 < 0f ? _data.Duration : _v1;

        void DrawTimeline()
        {
            const float h = 150f;
            var card = new Rect(16f, Ui.H - h - 16f, Ui.W - 32f, h);
            Claim(card);
            Ui.Panel(card, 14f);

            // header: time, map, camera, speed; clip range on the right
            float x = card.x + 20f, y = card.y + 10f;
            string now = Fmt(_t);
            Ui.Text(new Rect(x, y - 2f, 160f, 34f), now, 26, Ui.Bright, true);
            float tw = Ui.Measure(now, 26, true);
            Ui.Text(new Rect(x + tw + 8f, y + 6f, 100f, 24f), "/ " + Fmt(_data.Duration), 13, Ui.Dim, true);
            x += tw + 96f;
            x = HeaderChip(x, y + 4f, _data.Header.Scene + (_meta != null && _meta.Take > 0 ? $"  ·  take {_meta.Take}" : ""), Ui.Dim);
            x = HeaderChip(x, y + 4f, CamLabel(), Ui.Accent);
            x = HeaderChip(x, y + 4f, $"{Speed:0.##}x", Speed < 1f ? Ui.Warn : Ui.Dim);
            if (!_playing) HeaderChip(x, y + 4f, "paused", Ui.Faint);

            if (_in.HasValue || _out.HasValue)
            {
                float a = _in ?? 0f, b = _out ?? _data.Duration;
                string clip = $"{Fmt(a)}  →  {Fmt(b)}    {Mathf.Max(0f, b - a):0.00}S";
                float cw = Ui.Measure(clip, 13, true) + 96f;
                var cr = new Rect(card.xMax - cw - 20f, y + 2f, cw, 28f);
                Ui.Slant(cr, Ui.AccentSoft);
                Ui.Text(new Rect(cr.x + 18f, cr.y, 40f, cr.height), "CLIP", 12, Ui.Accent, true);
                Ui.Text(new Rect(cr.x + 58f, cr.y, cw - 90f, cr.height), clip, 13, Ui.Bright, true);
                if (Ui.Button(new Rect(cr.xMax - 34f, cr.y + 2f, 26f, 24f), "", false, Ui.Kind.Ghost, Ui.IconClose, 12, "Clear the clip range")) { _in = null; _out = null; }
            }

            var track = new Rect(card.x + 20f, card.y + 50f, card.width - 40f, 40f);
            DrawTrack(track);

            DrawControls(new Rect(card.x + 20f, card.y + 104f, card.width - 40f, 34f));
        }

        float HeaderChip(float x, float y, string text, Color c)
        {
            text = text.ToUpperInvariant();
            float w = Ui.Measure(text, 12, true) + 24f + 22f * Ui.SlantTan;
            var r = new Rect(x, y, w, 22f);
            Ui.Slant(r, new Color(c.r, c.g, c.b, 0.16f));
            Ui.Text(r, text, 12, c, true, TextAnchor.MiddleCenter);
            return x + w + 6f;
        }

        void DrawTrack(Rect tr)
        {
            float dur = Mathf.Max(0.01f, _data.Duration);
            float v0 = ViewStart, v1 = Mathf.Max(ViewStart + 0.5f, ViewEnd);
            float X(float t) => tr.x + (t - v0) / (v1 - v0) * tr.width;
            float T(float px) => Mathf.Clamp(v0 + (px - tr.x) / tr.width * (v1 - v0), 0f, dur);
            bool zoomed = _v1 >= 0f;

            // follow the playhead while playing zoomed in
            if (zoomed && _playing && _drag == Drag.None && (_t > v1 || _t < v0))
            {
                float span = v1 - v0;
                _v0 = Mathf.Clamp(_t - span * 0.1f, 0f, dur - span);
                _v1 = _v0 + span;
            }

            var ev = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var hit = new Rect(tr.x - 8f, tr.y - 14f, tr.width + 16f, tr.height + 22f);
            var markers = Markers;
            var kills = _localKills.Where(k => !markers.Any(m => Mathf.Abs(m.T - k.T) < 0.4f)).ToList();

            // ---- drawing
            Ui.Slant(tr, Ui.Wash(0.06f), 0.12f);
            GUI.BeginGroup(new Rect(tr.x, tr.y - 16f, tr.width, tr.height + 32f));
            var local = new Rect(0f, 16f, tr.width, tr.height);
            float LX(float t) => X(t) - tr.x;

            // ruler
            float[] steps = { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 15f, 30f, 60f, 120f };
            float step = steps.FirstOrDefault(s => s / (v1 - v0) * tr.width >= 80f);
            if (step <= 0f) step = 120f;
            for (float t = Mathf.Ceil(v0 / step) * step; t <= v1; t += step)
            {
                float lx = LX(t);
                Ui.Fill(new Rect(lx, local.yMax - 9f, 1f, 9f), Ui.Wash(0.2f));
                Ui.Text(new Rect(lx + 4f, local.y + 2f, 60f, 14f), step < 1f ? $"{t:0.0#}" : Fmt(t).Split('.')[0], 10, Ui.Faint);
                for (int k = 1; k < 5; k++)
                {
                    float mx = LX(t + step * k / 5f);
                    if (mx < local.width) Ui.Fill(new Rect(mx, local.yMax - 4f, 1f, 4f), Ui.Wash(0.1f));
                }
            }

            // played part, clip range
            Ui.Fill(new Rect(0f, local.y, Mathf.Clamp(LX(_t), 0f, local.width), local.height), Ui.Wash(0.05f));
            if (_in.HasValue || _out.HasValue)
            {
                float a = LX(_in ?? 0f), b = LX(_out ?? dur);
                var range = new Rect(a, local.y, Mathf.Max(1f, b - a), local.height);
                Ui.Fill(range, Ui.WithA(Ui.Accent, 0.22f));
                Ui.Fill(new Rect(range.x, range.y, range.width, 2f), Ui.Accent);
                Ui.Fill(new Rect(range.x, range.yMax - 2f, range.width, 2f), Ui.Accent);
            }

            // kills (from the kill feed) and markers
            foreach (var k in kills)
            {
                float kx = LX(k.T);
                Ui.Slant(new Rect(kx - 3f, local.yMax - 14f, 6f, 14f), Ui.Kill);
            }
            foreach (var m in markers)
            {
                float mx = LX(m.T);
                Ui.Slant(new Rect(mx - 5f, local.y + 2f, 10f, 10f), MarkerColor(m));
            }

            // in/out handles
            if (_in.HasValue) Handle(LX(_in.Value), local, _drag == Drag.In);
            if (_out.HasValue) Handle(LX(_out.Value), local, _drag == Drag.Out);

            // playhead
            float px = LX(_t);
            if (px >= -2f && px <= local.width + 2f)
            {
                Ui.Fill(new Rect(px - 1.5f, local.y - 2f, 3f, local.height + 4f), Ui.Bright);
                Ui.Slant(new Rect(px - 9f, local.y - 14f, 18f, 12f), Ui.Bright);
                Ui.Slant(new Rect(px - 4f, local.y - 11f, 8f, 6f), Ui.Accent);
            }
            GUI.EndGroup();

            // hover readout and tooltips
            var mouse = ev.mousePosition;
            if (hit.Contains(mouse) || _drag != Drag.None)
            {
                float ht = T(mouse.x);
                if (_drag == Drag.None)
                {
                    Ui.Fill(new Rect(Mathf.Clamp(mouse.x, tr.x, tr.xMax), tr.y, 1f, tr.height), Ui.Wash(0.35f));
                    string label = Fmt(ht);
                    foreach (var m in markers) if (Mathf.Abs(X(m.T) - mouse.x) < 7f && mouse.y < tr.y + 16f) label = $"{Fmt(m.T)}   {m.Label}";
                    foreach (var k in kills) if (Mathf.Abs(X(k.T) - mouse.x) < 5f && mouse.y > tr.yMax - 16f) label = $"{Fmt(k.T)}   Kill: {VictimName(k.Victim)}";
                    float bw = Ui.Measure(label, 12, true) + 30f;
                    var bubble = new Rect(Mathf.Clamp(mouse.x - bw / 2f, tr.x - 10f, tr.xMax - bw + 10f), tr.y - 42f, bw, 24f);
                    Ui.Slant(bubble, new Color(0f, 0f, 0f, 0.97f));
                    Ui.Fill(new Rect(bubble.x + 4f, bubble.yMax - 2f, bubble.width - 8f, 2f), Ui.Accent);
                    Ui.Text(bubble, label, 12, Ui.Bright, true, TextAnchor.MiddleCenter);
                }
            }

            // zoom overview
            if (zoomed)
            {
                var ov = new Rect(tr.x, tr.yMax + 6f, tr.width, 4f);
                Ui.Fill(ov, Ui.Wash(0.07f));
                Ui.Fill(new Rect(ov.x + ov.width * v0 / dur, ov.y, Mathf.Max(6f, ov.width * (v1 - v0) / dur), ov.height), Ui.Wash(0.4f));
                Ui.Fill(new Rect(ov.x + ov.width * _t / dur - 1f, ov.y - 1f, 2f, 6f), Ui.Accent);
            }

            // ---- interaction
            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown when ev.button == 0 && hit.Contains(ev.mousePosition):
                {
                    float mx = ev.mousePosition.x;
                    if (ev.clickCount == 2 && !ev.shift) { _v1 = -1f; ev.Use(); break; } // double-click: show everything
                    GUIUtility.hotControl = id;
                    GUIUtility.keyboardControl = 0;
                    if (_in.HasValue && Mathf.Abs(mx - X(_in.Value)) <= 8f) _drag = Drag.In;
                    else if (_out.HasValue && Mathf.Abs(mx - X(_out.Value)) <= 8f) _drag = Drag.Out;
                    else if (ev.shift) { _drag = Drag.Range; _dragAnchor = T(mx); _in = _out = _dragAnchor; }
                    else
                    {
                        var m = ev.mousePosition.y < tr.y + 16f ? markers.FirstOrDefault(k => Mathf.Abs(X(k.T) - mx) < 7f) : null;
                        if (m != null) { Seek(Mathf.Max(0f, m.T - Plugin.MarkerPreroll.Value)); GUIUtility.hotControl = 0; ev.Use(); break; }
                        _drag = Drag.Scrub;
                        _wasPlaying = _playing;
                        _playing = false;
                        Seek(T(mx));
                    }
                    ev.Use();
                    break;
                }
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                {
                    float t = T(ev.mousePosition.x);
                    switch (_drag)
                    {
                        case Drag.In: _in = Mathf.Min(t, (_out ?? dur) - 0.05f); break;
                        case Drag.Out: _out = Mathf.Max(t, (_in ?? 0f) + 0.05f); break;
                        case Drag.Range: _in = Mathf.Min(_dragAnchor, t); _out = Mathf.Max(_dragAnchor, t); break;
                        case Drag.Scrub:
                            if (Time.frameCount != _lastScrubFrame && Mathf.Abs(t - _t) > 0.004f) { _lastScrubFrame = Time.frameCount; Seek(t); }
                            break;
                    }
                    ev.Use();
                    break;
                }
                case EventType.MouseUp when GUIUtility.hotControl == id:
                {
                    if (_drag == Drag.Scrub) { Seek(T(ev.mousePosition.x)); _playing = _wasPlaying; }
                    if (_drag == Drag.Range && _out - _in < 0.05f) { _in = null; _out = null; }
                    _drag = Drag.None;
                    GUIUtility.hotControl = 0;
                    ev.Use();
                    break;
                }
                case EventType.ScrollWheel when hit.Contains(ev.mousePosition):
                {
                    // zoom around the mouse
                    float span = v1 - v0, centre = T(ev.mousePosition.x);
                    float ns = Mathf.Clamp(span * (ev.delta.y > 0f ? 1.25f : 0.8f), Mathf.Min(1f, dur), dur);
                    if (ns >= dur - 0.001f) _v1 = -1f;
                    else
                    {
                        float nv0 = Mathf.Clamp(centre - (centre - v0) * ns / span, 0f, dur - ns);
                        _v0 = nv0;
                        _v1 = nv0 + ns;
                    }
                    ev.Use();
                    break;
                }
            }
        }

        static void Handle(float x, Rect local, bool active)
        {
            var r = new Rect(x - 5f, local.y - 6f, 10f, local.height + 12f);
            Ui.Fill(r, active ? Ui.Bright : Ui.Accent);
            Ui.Fill(new Rect(x - 1.5f, r.center.y - 6f, 1f, 12f), new Color(0f, 0f, 0f, 0.45f));
            Ui.Fill(new Rect(x + 0.5f, r.center.y - 6f, 1f, 12f), new Color(0f, 0f, 0f, 0.45f));
        }

        static Color MarkerColor(ReplayMeta.MetaMarker m) =>
            !m.Auto ? Ui.Accent : m.Label.StartsWith("Kill", StringComparison.Ordinal) ? Ui.Kill : Ui.Death;

        string VictimName(int key)
        {
            var p = CurrentOf(key);
            if (p != null) return p.DisplayName;
            var info = _data.Objects.FirstOrDefault(o => o.Player != null && o.Player.PlayerId == key);
            return info != null ? System.Text.RegularExpressions.Regex.Replace(info.Player.Name, "<[^>]*>", "") : "?";
        }

        // ---------------------------------------------------------------- control bar

        void DrawControls(Rect row)
        {
            float x = row.x, y = row.y, h = row.height;
            Rect Next(string label, Texture icon = null, float min = 0f) { float w = Mathf.Max(min, Ui.AutoW(label, icon, h)); var r = new Rect(x, y, w, h); x += w + 4f; return r; }
            void Gap(float g = 14f) => x += g;

            if (Ui.Button(Next("Exit"), "Exit", false, Ui.Kind.Danger, null, 13, "Back to the replay list (Esc)")) { Exit(); return; }
            Gap();
            if (Ui.Button(Next("", Ui.IconPrev), "", false, Ui.Kind.Normal, Ui.IconPrev, 13, "Previous marker ( [ )")) JumpMarker(-1);
            if (Ui.Button(Next("", Ui.IconStepBack), "", false, Ui.Kind.Normal, Ui.IconStepBack, 13, "Back one frame ( , )")) { _playing = false; Seek(_t - 1f / 60f); }
            string play = _playing ? "Pause" : "Play";
            if (Ui.Button(Next("Pause", Ui.IconPause), play, false, Ui.Kind.Primary, _playing ? Ui.IconPause : Ui.IconPlay, 13, "Space")) TogglePlay();
            if (Ui.Button(Next("", Ui.IconStepFwd), "", false, Ui.Kind.Normal, Ui.IconStepFwd, 13, "Forward one frame ( . )")) { _playing = false; Seek(_t + 1f / 60f); }
            if (Ui.Button(Next("", Ui.IconNext), "", false, Ui.Kind.Normal, Ui.IconNext, 13, "Next marker ( ] )")) JumpMarker(+1);
            Gap();

            // speed
            if (Ui.Button(Next("-"), "-", false, Ui.Kind.Normal, null, 15, "Slower ( - )")) _speedIndex = Mathf.Max(0, _speedIndex - 1);
            var sr = Next("0.25x");
            Ui.Slant(sr, new Color(0f, 0f, 0f, 0.6f));
            Ui.Text(sr, $"{Speed:0.##}X", 13, Speed < 1f ? Ui.Warn : Ui.Bright, true, TextAnchor.MiddleCenter);
            if (Ui.Button(Next("+"), "+", false, Ui.Kind.Normal, null, 15, "Faster ( = )")) _speedIndex = Mathf.Min(Speeds.Length - 1, _speedIndex + 1);
            Gap();

            // camera
            string[] cams = { "My view", "Free", "Chase", "Orbit", "Victim" };
            int cur = _camMode == CamMode.Original ? 0 : _camMode == CamMode.Free ? 1 : _camMode == CamMode.Chase ? 2 : _camMode == CamMode.Orbit ? 3 : _camMode == CamMode.VictimCam ? 4 : -1;
            float camW = cams.Sum(c => Ui.AutoW(c, null, h, 12)) + 3f * (cams.Length - 1);
            var cr = new Rect(x, y, camW, h);
            x += camW + 4f;
            Ui.Tip(cr, "Camera: your recorded view, free cam (right mouse + WASD), chase, orbit, or over the victim's shoulder");
            int pick = Ui.Segmented(cr, cams, cur);
            if (pick != cur)
            {
                switch (pick)
                {
                    case 0: _camMode = CamMode.Original; break;
                    case 1: SetFree(); break;
                    case 2: _camMode = CamMode.Chase; break;
                    case 3: _camMode = CamMode.Orbit; break;
                    case 4: _camMode = CamMode.VictimCam; break;
                }
                _tpT = float.NegativeInfinity;
            }

            // right side: clip and render, laid out from the right edge
            float rx = row.xMax;
            Rect Left(string label, Texture icon = null) { float w = Ui.AutoW(label, icon, h); rx -= w; var r = new Rect(rx, y, w, h); rx -= 4f; return r; }
            if (Ui.Button(Left("?"), "?", _help, Ui.Kind.Normal, null, 14, "Keyboard shortcuts")) _help = !_help;
            if (Ui.Button(Left("Render", Ui.IconFilm), "Render", _renderPanel, Ui.Kind.Accent, Ui.IconFilm, 13, "Render settings (R)")) _renderPanel = !_renderPanel;
            rx -= 10f;
            if (Ui.Button(Left("Out"), "Out", false, Ui.Kind.Normal, null, 13, "Set the clip end here (O)")) _out = Mathf.Max(_t, (_in ?? 0f) + 0.05f);
            if (Ui.Button(Left("In"), "In", false, Ui.Kind.Normal, null, 13, "Set the clip start here (I)")) _in = Mathf.Min(_t, (_out ?? _data.Duration) - 0.05f);
            if (Ui.Button(Left("Clip ±2.5s"), "Clip ±2.5s", false, Ui.Kind.Normal, null, 13, "Clip 2.5 s either side of now (C). Shift+drag on the timeline draws a range.")) ClipHere();
            if (Ui.Button(Left("Marker", Ui.IconFlag), "Marker", false, Ui.Kind.Normal, Ui.IconFlag, 13, "Add a marker here (M)")) AddMarkerHere();
        }

        void DrawHelp()
        {
            var lines = new (string key, string what)[]
            {
                ("Space", "play / pause"), ("← / →", "5 s back / forward (Shift: 1 s)"), (", / .", "one frame"), ("[ / ]", "previous / next marker"),
                ("- / =", "slower / faster"), ("M", "add a marker"), ("I / O", "clip start / end"), ("C", "clip 2.5 s around now"),
                ("Shift+drag", "draw a clip range on the timeline"), ("Wheel", "zoom the timeline (double-click: show all)"),
                ("V / F", "my view / free camera"), ("Right mouse", "look; WASD QE fly, Shift fast, Ctrl slow, Z/X zoom"),
                ("1-9 / Tab", "a player's POV"), ("G", "smooth POV"), ("R", "render panel"), ("U", "game HUD on/off"), ("H", "hide all UI"), ("Esc", "back to the menu"),
            };
            float w = 520f, h = 44f + lines.Length * 22f;
            var r = new Rect(Ui.W - w - 16f, Ui.H - 150f - 32f - h, w, h);
            Claim(r);
            Ui.Panel(r);
            Ui.Text(new Rect(r.x + 20f, r.y + 10f, 200f, 24f), "SHORTCUTS", 15, Ui.Bright, true);
            for (int i = 0; i < lines.Length; i++)
            {
                float ly = r.y + 40f + i * 22f;
                var kr = new Rect(r.x + 20f, ly, 110f, 20f);
                Ui.Text(kr, lines[i].key.ToUpperInvariant(), 12, Ui.Accent, true);
                Ui.Text(new Rect(r.x + 136f, ly, w - 150f, 20f), lines[i].what, 12, Ui.TextC);
            }
        }

        // ---------------------------------------------------------------- side panel

        Vector2 _sideScroll2;

        void DrawSidePanel()
        {
            float w = 300f;
            var keys = PlayerKeys;
            var markers = Markers;
            float rowH = 28f;
            float content = 46f + 26f + keys.Length * (rowH + 2f) + 14f + 26f + Math.Max(1, _localKills.Count) * (rowH + 2f) + 14f + 26f + Math.Max(1, markers.Count) * (rowH + 2f) + 10f;
            float maxH = Ui.H - 150f - 16f - 16f - 16f - 16f;
            if (_renderPanel || _help) maxH = Mathf.Min(maxH, 420f);
            var r = new Rect(Ui.W - w - 16f, 16f, w, Mathf.Min(content, maxH));
            Claim(r);
            Ui.Panel(r);

            Ui.Text(new Rect(r.x + 18f, r.y + 12f, 140f, 24f), "STREPLAY", 15, Ui.Bright, true);
            Ui.Text(new Rect(r.x + 110f, r.y + 12f, w - 128f, 24f), _data.Header.Scene.ToUpperInvariant(), 12, Ui.Dim, true, TextAnchor.MiddleRight);

            var view = new Rect(r.x + 10f, r.y + 42f, w - 20f, r.height - 50f);
            float inner = content - 46f;
            if (Event.current.type == EventType.ScrollWheel && view.Contains(Event.current.mousePosition))
            {
                _sideScroll2.y = Mathf.Clamp(_sideScroll2.y + Event.current.delta.y * 20f, 0f, Mathf.Max(0f, inner - view.height));
                Event.current.Use();
            }
            _sideScroll2.y = Mathf.Clamp(_sideScroll2.y, 0f, Mathf.Max(0f, inner - view.height));
            GUI.BeginGroup(view);
            float y = -_sideScroll2.y;
            float rw = view.width - (inner > view.height ? 8f : 0f);

            Ui.Heading(new Rect(8f, y, rw, 20f), "Players");
            y += 24f;
            for (int k = 0; k < keys.Length; k++)
            {
                var p = CurrentOf(keys[k]);
                if (p == null) continue;
                bool active = (_camMode == CamMode.Pov && _povPlayer == keys[k]) || (p.IsLocalPlayer && _camMode == CamMode.Original);
                if (ListRow(new Rect(0f, y, rw, rowH), $"{k + 1}", p.DisplayName, p.IsLocalPlayer ? "you" : "", active, p.IsLocalPlayer ? Ui.Accent : Ui.TextC))
                    SetPov(keys[k]);
                y += rowH + 2f;
            }

            y += 12f;
            Ui.Heading(new Rect(8f, y, rw, 20f), "Your kills");
            y += 24f;
            if (_localKills.Count == 0) { Ui.Text(new Rect(8f, y, rw, rowH), "None in this round", 12, Ui.Faint); y += rowH + 2f; }
            foreach (var k in _localKills)
            {
                if (ListRow(new Rect(0f, y, rw, rowH), Fmt(k.T).Split('.')[0], VictimName(k.Victim), "clip", false, Ui.Kill))
                {
                    // jump there with the clip range around the kill, like a highlight's Watch
                    _in = Mathf.Max(0f, k.T - Plugin.HighlightPreroll.Value);
                    _out = Mathf.Min(_data.Duration, k.T + Plugin.HighlightPostroll.Value);
                    Seek(_in.Value);
                }
                y += rowH + 2f;
            }

            y += 12f;
            Ui.Heading(new Rect(8f, y, rw, 20f), "Markers");
            y += 24f;
            if (markers.Count == 0) { Ui.Text(new Rect(8f, y, rw, rowH), "Press M to add one", 12, Ui.Faint); y += rowH + 2f; }
            foreach (var m in markers)
            {
                if (ListRow(new Rect(0f, y, rw, rowH), Fmt(m.T).Split('.')[0], m.Label, "", false, MarkerColor(m)))
                    Seek(Mathf.Max(0f, m.T - Plugin.MarkerPreroll.Value));
                y += rowH + 2f;
            }
            GUI.EndGroup();

            // slim scrollbar
            if (inner > view.height)
            {
                float frac = view.height / inner;
                var bar = new Rect(view.xMax - 4f, view.y + (view.height - view.height * frac) * (_sideScroll2.y / (inner - view.height)), 4f, view.height * frac);
                Ui.Fill(bar, Ui.Wash(0.2f), 2f);
            }
        }

        // a clickable list row: small lead text, main text, a faint tag on the right
        bool ListRow(Rect r, string lead, string main, string tag, bool active, Color dot)
        {
            bool hover = Ui.Hover(r);
            if (active) Ui.Slant(r, Ui.Wash(0.1f));
            else if (hover) Ui.Slant(r, Ui.Wash(0.06f));
            Ui.Slant(new Rect(r.x + 6f, r.y + 7f, 6f, r.height - 14f), active ? Ui.Accent : dot);
            Ui.Text(new Rect(r.x + 22f, r.y, 50f, r.height), lead.ToUpperInvariant(), 12, Ui.Dim, true);
            Ui.Text(new Rect(r.x + 74f, r.y, r.width - 74f - (tag.Length > 0 ? 48f : 8f), r.height), main, 13, active ? Ui.Bright : Ui.TextC, true);
            if (tag.Length > 0) Ui.Text(new Rect(r.xMax - 52f, r.y, 44f, r.height), tag.ToUpperInvariant(), 11, hover ? Ui.Accent : Ui.Faint, true, TextAnchor.MiddleRight);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        string CamLabel()
        {
            switch (_camMode)
            {
                case CamMode.Free: return "free cam";
                case CamMode.Chase: return "chase";
                case CamMode.Drone: return "drone";
                case CamMode.Low: return "low";
                case CamMode.Flyby: return "flyby";
                case CamMode.Sweep: return "sweep";
                case CamMode.TopDown: return "top down";
                case CamMode.Static: return "static";
                case CamMode.Orbit: return "orbit";
                case CamMode.VictimCam: return "victim cam";
                case CamMode.Pov:
                    var p = CurrentOf(_povPlayer);
                    return p != null ? $"POV {p.DisplayName}" + (_smoothPov ? " (smooth)" : "") : "POV";
                default: return "my view";
            }
        }

        internal static string Fmt(float t)
        {
            if (t < 0) t = 0;
            int m = (int)(t / 60);
            // invariant: callers split on '.' and the result ends up in file names
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00.00}", m, t - m * 60);
        }
    }
}
