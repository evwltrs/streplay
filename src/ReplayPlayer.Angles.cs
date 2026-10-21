using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Streplay
{
    // Extra camera angles for clips: a third-person chase cam, a slow orbit around you, and two victim angles
    // (their eyes, and over their shoulder looking at you). Every angle is a function of the replay time (plus
    // light smoothing in replay time), so a render and its preview match and several angles of one clip line up.
    internal sealed partial class ReplayPlayer
    {
        internal enum Angle { Current, FirstPerson, Chase, Orbit, VictimPov, VictimCam, Drone, Low, Flyby, Sweep, TopDown, Static }

        internal static readonly (Angle a, string label, string file)[] Angles =
        {
            (Angle.Current, "Current view", ""), (Angle.FirstPerson, "First person", "firstperson"), (Angle.Chase, "Chase", "chase"),
            (Angle.Orbit, "Orbit", "orbit"), (Angle.VictimPov, "Victim POV", "victimpov"), (Angle.VictimCam, "Victim cam", "victimcam"),
            (Angle.Drone, "Drone", "drone"), (Angle.Low, "Low", "low"), (Angle.Flyby, "Flyby", "flyby"), (Angle.Sweep, "Sweep", "sweep"),
            (Angle.TopDown, "Top down", "topdown"), (Angle.Static, "Static", "static"),
        };


        // the local player's kills in this replay (time, victim player id), from the kill feed
        List<(float T, int Victim)> _localKills = new List<(float, int)>();

        void FindLocalKills()
        {
            try { _localKills = Highlights.LocalKills(_data, _meta); }
            catch (Exception e) { Plugin.Log.LogWarning($"Could not read kills: {e.Message}"); _localKills = new List<(float, int)>(); }
        }

        // whoever is about to die (or just died) around time t; with no kill nearby, the other player closest to
        // where you're aiming
        int VictimKeyAt(float t)
        {
            foreach (var k in _localKills) if (k.T >= t - 0.6f) return k.Victim;
            if (_localKills.Count > 0 && t - _localKills[_localKills.Count - 1].T < 3f) return _localKills[_localKills.Count - 1].Victim;
            Puppet best = null;
            float bestScore = float.MaxValue;
            var fwd = _origRot * Vector3.forward;
            foreach (var p in _puppets)
            {
                if (p == null || !p.Info.IsPlayer || p.IsLocalPlayer || !p.Seen) continue;
                var to = p.Go.transform.position + Vector3.up * 1.2f - _origPos;
                float score = Vector3.Angle(fwd, to) + to.magnitude * 0.2f;
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best != null ? PlayerKey(best) : int.MinValue;
        }

        // ---------------------------------------------------------------- third-person cameras

        Vector3 _tpPos, _tpLook;
        float _sweepT0, _sweepYaw;
        float _topYaw, _topYawWant;
        float _baseNear = 0.05f, _topNear = 0.05f; // the camera's own near plane; the top-down cut
        float _tpT = float.NegativeInfinity, _tpFov = 70f;
        readonly Dictionary<int, Vector3> _lastEye = new Dictionary<int, Vector3>();

        void UpdateAngleCamera()
        {
            Vector3 want, look;
            float fov = 75f;
            var eye = _origPos;                       // your recorded camera: exact head position and aim
            var aim = _origRot;
            var yaw = Quaternion.Euler(0f, aim.eulerAngles.y, 0f);
            var anchor = eye;                         // the point the camera must keep a clear line to

            switch (_camMode)
            {
                case CamMode.Chase:
                    want = eye + yaw * new Vector3(0.5f, 0.35f, -2.1f);
                    look = eye + aim * Vector3.forward * 12f;
                    break;

                case CamMode.Sweep:
                {
                    // an arc around you: from behind, gliding round to your side over a few seconds, tight on the action
                    float u = Mathf.Clamp01((_t - _sweepT0) / 4.5f);
                    float e = u * u * (3f - 2f * u);
                    float a = _sweepYaw + 180f - 110f * e;
                    var chest = eye - Vector3.up * 0.3f;
                    want = chest + Quaternion.Euler(0f, a, 0f) * Vector3.forward * Mathf.Lerp(3.2f, 2.4f, e) + Vector3.up * Mathf.Lerp(0.9f, 0.45f, e);
                    look = chest + yaw * Vector3.forward * 2.5f;
                    anchor = chest;
                    fov = 72f;
                    break;
                }

                case CamMode.Static:
                    want = _staticPos;
                    look = _staticLook;
                    anchor = want;
                    fov = _staticFov;
                    break;

                case CamMode.TopDown:
                {
                    // straight down over the fight, turned so you and whoever you're fighting sit left and right across
                    // the wide side of the picture, as low as fits you both
                    var feet = eye - Vector3.up * 1.5f;
                    var centre = feet;
                    float span = 0f;
                    int key = VictimKeyAt(_t);
                    var v = key != int.MinValue ? CurrentOf(key) : null;
                    if (v != null && v.Seen && v.Health != null && v.Health.graphics != null && v.Health.graphics.activeSelf) _lastEye[key] = v.PovPos;
                    if (key != int.MinValue && _lastEye.TryGetValue(key, out var vEye))
                    {
                        var vFeet = vEye - Vector3.up * 1.5f;
                        var flat = vFeet - feet;
                        flat.y = 0f;
                        if (flat.magnitude < 35f)
                        {
                            centre = Vector3.Lerp(feet, vFeet, 0.5f);
                            centre.y = feet.y;
                            span = flat.magnitude;
                            if (span > 3f)
                            {
                                // the picture's right toward them (or its left: whichever is the smaller turn)
                                float a = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg - 90f;
                                if (Mathf.Abs(Mathf.DeltaAngle(_topYaw, a + 180f)) < Mathf.Abs(Mathf.DeltaAngle(_topYaw, a))) a += 180f;
                                _topYawWant = a;
                            }
                        }
                    }
                    // high enough that both fit across (horizontal half-width 0.83 h at 50 degrees, 16:9), with a margin
                    float h = Mathf.Clamp((span * 0.5f + 2.5f) / 0.8f, 8f, 30f);
                    want = centre + Vector3.up * h;
                    look = centre;
                    anchor = want;
                    fov = 50f;
                    break;
                }

                case CamMode.Drone:
                    // high above and behind, looking down at the play ahead of you
                    want = eye + yaw * new Vector3(0f, 5.5f, -4.8f);
                    look = eye + yaw * Vector3.forward * 4f - Vector3.up * 0.8f;
                    fov = 62f;
                    break;

                case CamMode.Low:
                    // low at your right, tracking you, looking a little up
                    want = eye + yaw * new Vector3(2.4f, -1.1f, 0.4f);
                    look = eye + yaw * Vector3.forward * 3f + Vector3.up * 0.2f;
                    fov = 82f;
                    break;

                case CamMode.Orbit:
                {
                    // starts at your side (as you were facing at the in point) and circles slowly
                    float t0 = _in ?? 0f;
                    var f0 = _data.Frames[FrameAt(t0)].CamRot;
                    float angle = f0.eulerAngles.y + 90f + 20f * (_t - t0);
                    var chest = eye - Vector3.up * 0.35f;
                    want = chest + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * 3.2f + Vector3.up * 0.4f;
                    look = chest + aim * Vector3.forward * 1.5f;
                    anchor = chest;
                    break;
                }

                default: // VictimCam: behind and above the victim, both of you in frame
                {
                    int key = VictimKeyAt(_t);
                    var v = key != int.MinValue ? CurrentOf(key) : null;
                    if (v != null && v.Seen && v.Health != null && v.Health.graphics != null && v.Health.graphics.activeSelf) _lastEye[key] = v.PovPos;
                    if (!_lastEye.TryGetValue(key, out var vEye)) { goto case CamMode.Chase; } // no victim yet: chase
                    var me = eye - Vector3.up * 0.3f;
                    var vChest = vEye - Vector3.up * 0.45f;
                    // on the line from you through the victim, past them and off their shoulder: you near the middle,
                    // the victim big in a lower corner
                    var dir = me - vEye;
                    dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
                    var flat = new Vector3(dir.x, 0f, dir.z);
                    var right = flat.sqrMagnitude > 1e-4f ? Vector3.Cross(Vector3.up, flat.normalized) : Vector3.right;
                    want = VictimSpot(vEye, dir, right);
                    fov = 60f;
                    // aim at you, tilting toward the victim only as far as it takes to keep them in the picture
                    look = me;
                    for (float w = 0f; w <= 0.9f; w += 0.1f)
                    {
                        look = Vector3.Lerp(me, vChest, w);
                        if (InFrame(want, look, fov, vChest) && InFrame(want, look, fov, vEye)) break;
                    }
                    anchor = want; // already clear of walls
                    break;
                }
            }

            want = Unblocked(anchor, want);

            // smooth in replay time; snap after a seek or when switching angle
            float dt = _t - _tpT;
            if (dt < 0f || dt > 0.3f || float.IsInfinity(_tpT)) { _tpPos = want; _tpLook = look; _tpFov = fov; }
            else
            {
                float k = 1f - Mathf.Exp((_camMode == CamMode.TopDown ? -8f : -20f) * dt);
                _tpPos = Vector3.Lerp(_tpPos, want, k);
                _tpLook = Vector3.Lerp(_tpLook, look, 1f - Mathf.Exp(-28f * dt));
                _tpFov = Mathf.Lerp(_tpFov, fov, k);
            }
            _tpT = _t;
            if (_camMode == CamMode.TopDown)
            {
                // looking straight down, top of the picture where you faced at the start; everything more than a few
                // metres above your feet is cut away by the near plane, so roofs and ceilings don't hide the fight
                _topYaw = dt < 0f || dt > 0.3f ? _topYawWant : Mathf.LerpAngle(_topYaw, _topYawWant, 1f - Mathf.Exp(-2.5f * dt));
                _cam.transform.SetPositionAndRotation(_tpPos, Quaternion.Euler(90f, _topYaw, 0f));
                _cam.fieldOfView = _tpFov;
                _topNear = Mathf.Max(_baseNear, _tpPos.y - _tpLook.y - 4.5f);
                _cam.nearClipPlane = _topNear;
                return;
            }
            var dirLook = _tpLook - _tpPos;
            _cam.transform.SetPositionAndRotation(_tpPos, dirLook.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dirLook) : aim);
            _cam.fieldOfView = _tpFov;
        }

        // ---------------------------------------------------------------- flyby
        // A drone through the map along the route the recording player took (smoothed, lifted a little): a path that is
        // known to be open space. It glides at a steady speed from where the player was at the clip's start, looking
        // ahead along the path.
        List<List<Vector3>> _flyPaths;   // the route split at teleports (respawns), resampled every 0.25 m
        List<List<float>> _flyTimes;     // replay time at each point
        int _flyPath;
        float _flyD0, _flyT0;
        Vector3 _flyPos, _flyLook;
        float _flyLastT = float.NegativeInfinity;
        const float FlySpeed = 5f, FlyStep = 0.25f;

        void BuildFlyPaths()
        {
            _flyPaths = new List<List<Vector3>>();
            _flyTimes = new List<List<float>>();
            var frames = _data.Frames;
            // smoothed eye positions (±0.7 s), split where the camera jumps
            var raw = new List<(float t, Vector3 p)>();
            for (int i = 0; i < frames.Count; i++) raw.Add((frames[i].T, frames[i].CamPos));
            var seg = new List<(float t, Vector3 p)>();
            var segs = new List<List<(float t, Vector3 p)>>();
            for (int i = 0; i < raw.Count; i++)
            {
                if (seg.Count > 0 && (Vector3.Distance(seg[seg.Count - 1].p, raw[i].p) > 3f || raw[i].t - seg[seg.Count - 1].t > 1f)) { segs.Add(seg); seg = new List<(float, Vector3)>(); }
                seg.Add(raw[i]);
            }
            if (seg.Count > 0) segs.Add(seg);
            foreach (var s in segs)
            {
                if (s.Count < 20) continue;
                var smooth = new List<(float t, Vector3 p)>();
                int lo = 0, hi = 0;
                var sum = Vector3.zero;
                for (int i = 0; i < s.Count; i++)
                {
                    while (hi < s.Count && s[hi].t <= s[i].t + 0.7f) { sum += s[hi].p; hi++; }
                    while (s[lo].t < s[i].t - 0.7f) { sum -= s[lo].p; lo++; }
                    smooth.Add((s[i].t, sum / (hi - lo)));
                }
                // resample by distance
                var pts = new List<Vector3> { smooth[0].p };
                var ts = new List<float> { smooth[0].t };
                float carry = 0f;
                for (int i = 1; i < smooth.Count; i++)
                {
                    var a = smooth[i - 1].p; var b = smooth[i].p;
                    float len = Vector3.Distance(a, b);
                    float d = FlyStep - carry;
                    while (d <= len) { pts.Add(Vector3.Lerp(a, b, d / len)); ts.Add(Mathf.Lerp(smooth[i - 1].t, smooth[i].t, d / Mathf.Max(1e-5f, len))); d += FlyStep; }
                    carry = len - (d - FlyStep);
                }
                if (pts.Count * FlyStep >= 8f) { _flyPaths.Add(pts); _flyTimes.Add(ts); }
            }
        }

        void StartFlyby(float t)
        {
            if (_flyPaths == null) BuildFlyPaths();
            _flyT0 = t;
            _flyLastT = float.NegativeInfinity;
            _flyPath = -1;
            if (_flyPaths.Count == 0) return;
            // the route piece the player was on at t (or the longest), starting a little behind them
            for (int i = 0; i < _flyTimes.Count; i++) if (_flyTimes[i][0] <= t && _flyTimes[i][_flyTimes[i].Count - 1] >= t) _flyPath = i;
            if (_flyPath < 0) { int best = 0; for (int i = 1; i < _flyPaths.Count; i++) if (_flyPaths[i].Count > _flyPaths[best].Count) best = i; _flyPath = best; }
            var ts = _flyTimes[_flyPath];
            int k = 0;
            while (k < ts.Count - 1 && ts[k] < t) k++;
            _flyD0 = Mathf.Max(0f, k * FlyStep - 3f);
        }

        Vector3 FlyAt(float d)
        {
            var p = _flyPaths[_flyPath];
            float x = Mathf.Clamp(d / FlyStep, 0f, p.Count - 1.001f);
            int i = (int)x;
            return Vector3.Lerp(p[i], p[i + 1], x - i);
        }

        void UpdateFlyby()
        {
            if (_flyPaths == null || _flyPath < 0 || _flyPath >= _flyPaths.Count) { _camMode = CamMode.Chase; UpdateAngleCamera(); return; }
            float len = (_flyPaths[_flyPath].Count - 1) * FlyStep;
            float d = _flyD0 + FlySpeed * Mathf.Max(0f, _t - _flyT0);
            // past the end of the route: drift back along it (ping-pong) so the camera never stops dead
            if (len > 1f) { float m = d % (2f * len); d = m <= len ? m : 2f * len - m; }
            var pos = FlyAt(d) + Vector3.up * 0.7f;
            var look = FlyAt(d + 5f) + Vector3.up * 0.25f;
            float dt = _t - _flyLastT;
            if (dt < 0f || dt > 0.3f || float.IsInfinity(_flyLastT)) { _flyPos = pos; _flyLook = look; }
            else { _flyPos = Vector3.Lerp(_flyPos, pos, 1f - Mathf.Exp(-12f * dt)); _flyLook = Vector3.Lerp(_flyLook, look, 1f - Mathf.Exp(-5f * dt)); }
            _flyLastT = _t;
            var dir = _flyLook - _flyPos;
            _cam.transform.SetPositionAndRotation(_flyPos, dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir) : _cam.transform.rotation);
            _cam.fieldOfView = 80f;
        }

        // behind and above the victim, away from you; if a wall is in the way try the other shoulder, then higher up,
        // before settling for whichever spot keeps the most room
        static readonly Vector3[] VictimSpots =
        {
            new Vector3(0.8f, 0.45f, 2.6f), new Vector3(-0.8f, 0.45f, 2.6f), new Vector3(0.7f, 0.9f, 1.9f),
            new Vector3(-0.7f, 0.9f, 1.9f), new Vector3(0.4f, 1.4f, 1.3f),
        };

        Vector3 VictimSpot(Vector3 vEye, Vector3 dir, Vector3 right)
        {
            Vector3 best = vEye - dir * 1.2f + Vector3.up * 0.6f;
            float bestRoom = -1f;
            foreach (var o in VictimSpots)
            {
                var want = vEye + right * o.x + Vector3.up * o.y - dir * o.z;
                want.y = Mathf.Max(want.y, vEye.y - 0.3f); // a steep line to you shouldn't put the camera under the floor
                var got = Unblocked(vEye, want);
                float room = Vector3.Distance(got, vEye) / Vector3.Distance(want, vEye);
                if (room >= 0.85f) return got;
                if (room > bestRoom) { bestRoom = room; best = got; }
            }
            return best;
        }

        internal string TestVictimDiag()
        {
            int key = VictimKeyAt(_t);
            var v = key != int.MinValue ? CurrentOf(key) : null;
            var cam = _cam.transform;
            string where(Vector3 p) { var vp = _cam.WorldToViewportPoint(p); return $"vp=({vp.x:0.00},{vp.y:0.00},{vp.z:0.0})"; }
            return $"t={_t:0.00} key={key} victim={(v != null ? v.DisplayName : "none")} seen={v?.Seen} gfx={(v?.Health?.graphics != null ? v.Health.graphics.activeSelf.ToString() : "?")} " +
                   $"pov={v?.PovPos} root={v?.Go.transform.position} cam={cam.position} fov={_cam.fieldOfView:0} me={_origPos} " +
                   $"victimRoot {(v != null ? where(v.Go.transform.position + Vector3.up) : "")} victimPov {(v != null ? where(v.PovPos) : "")} me {where(_origPos)}";
        }

        // is p inside a 16:9 frame (with a small margin) for a camera at pos looking at look?
        static bool InFrame(Vector3 pos, Vector3 look, float vFov, Vector3 p)
        {
            var q = Quaternion.LookRotation(look - pos);
            var local = Quaternion.Inverse(q) * (p - pos);
            if (local.z <= 0.1f) return false;
            float halfV = vFov * 0.5f - 4f;
            float halfH = Mathf.Atan(Mathf.Tan(vFov * 0.5f * Mathf.Deg2Rad) * 16f / 9f) * Mathf.Rad2Deg - 4f;
            return Mathf.Abs(Mathf.Atan2(local.y, local.z) * Mathf.Rad2Deg) < halfV && Mathf.Abs(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg) < halfH;
        }

        readonly RaycastHit[] _hits = new RaycastHit[16];

        // pull the camera in front of the first wall between the subject and where it wants to be (players don't count)
        Vector3 Unblocked(Vector3 from, Vector3 to)
        {
            var d = to - from;
            float len = d.magnitude;
            if (len < 0.01f) return to;
            d /= len;
            int n = Physics.RaycastNonAlloc(from, d, _hits, len + 0.2f, ~0, QueryTriggerInteraction.Ignore);
            float best = len;
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider == null || IsPlayerCollider(h.collider)) continue;
                best = Mathf.Min(best, Mathf.Max(0.2f, h.distance - 0.25f));
            }
            return from + d * best;
        }

        bool IsPlayerCollider(Collider c)
        {
            var root = c.transform.root;
            foreach (var p in _puppets) if (p != null && p.Go != null && (p.Info.IsPlayer || p.Owned) && p.Go.transform.root == root) return true;
            return false;
        }

        // point the camera for a render angle; Current keeps whatever the user set up
        void SetAngle(Angle a, float clipStart, float clipEnd = -1f)
        {
            _tpT = float.NegativeInfinity;
            switch (a)
            {
                case Angle.FirstPerson: _camMode = CamMode.Original; break;
                case Angle.Chase: _camMode = CamMode.Chase; break;
                case Angle.Orbit: _camMode = CamMode.Orbit; break;
                case Angle.VictimCam: _camMode = CamMode.VictimCam; break;
                case Angle.Drone: _camMode = CamMode.Drone; break;
                case Angle.Low: _camMode = CamMode.Low; break;
                case Angle.Flyby: StartFlyby(clipStart); _camMode = CamMode.Flyby; break;
                case Angle.Static: PlanStatic(clipStart, clipEnd > clipStart ? clipEnd : clipStart + 4f); _camMode = CamMode.Static; break;
                case Angle.TopDown: _topYaw = _topYawWant = _data.Frames[FrameAt(clipStart)].CamRot.eulerAngles.y; _camMode = CamMode.TopDown; break;
                case Angle.Sweep: _sweepT0 = clipStart; _sweepYaw = _data.Frames[FrameAt(clipStart)].CamRot.eulerAngles.y; _camMode = CamMode.Sweep; break;
                case Angle.VictimPov:
                {
                    int key = VictimKeyAt(clipStart + 1.5f);
                    if (key != int.MinValue) { SetPov(key); _smoothPov = true; }
                    else _camMode = CamMode.VictimCam;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- static wide shot
        // A camera that doesn't move: every player near you stays in the picture for the whole clip (the fight, the
        // kill, the bodies falling). It looks across the way the players stand, from a little above, at the spot that
        // fits them all with a clear line to each; the best of a ring of candidate spots.
        Vector3 _staticPos, _staticLook;
        float _staticFov = 50f;

        void PlanStatic(float t0, float t1)
        {
            var pts = new List<Vector3>();
            float tKill = -1f;
            foreach (var k in _localKills) if (k.T >= t0 && k.T <= t1) { tKill = k.T; break; }
            // around the kill: from a second before it until the bodies are down (the whole clip if there's none);
            // who's in it is read a moment before it, while everyone is still standing
            if (tKill >= 0f) { t0 = Mathf.Max(t0, tKill - 1f); t1 = Mathf.Min(t1, tKill + 1.2f); }
            float tRef = tKill >= 0f ? tKill - 0.15f : (t0 + t1) * 0.5f;
            var refFrame = _data.Frames[FrameAt(tRef)];
            // the players in it: you, and everyone within 30 m of you at the reference time
            var near = new HashSet<int>();
            foreach (var o in refFrame.Objects)
                if (o != null && o.Active && o.HasPlayer && (o.PovPos - refFrame.CamPos).sqrMagnitude < 30f * 30f) near.Add(o.Obj);
            var chests = new List<Vector3>();
            for (float t = t0; t <= t1 + 1e-3f; t += 0.1f)
            {
                var f = _data.Frames[FrameAt(Mathf.Clamp(t, 0f, _data.Duration))];
                foreach (var o in f.Objects)
                {
                    if (o == null || !o.Active || !o.HasPlayer || !near.Contains(o.Obj)) continue;
                    pts.Add(o.PovPos + Vector3.up * 0.2f);
                    pts.Add(o.PovPos - Vector3.up * 1.7f); // feet, and where a body ends up
                }
            }
            foreach (var o in refFrame.Objects)
                if (o != null && o.Active && o.HasPlayer && near.Contains(o.Obj)) chests.Add(o.PovPos - Vector3.up * 0.5f);
            if (pts.Count == 0) { var e = refFrame.CamPos; pts.Add(e); pts.Add(e - Vector3.up * 1.7f); chests.Add(e - Vector3.up * 0.5f); }
            var lo = pts[0]; var hi = pts[0];
            foreach (var q in pts) { lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
            var centre = (lo + hi) * 0.5f;
            // the line the players stand along (the two furthest apart), to look across it
            Vector3 axis = Vector3.right;
            float far = 0f;
            for (int i = 0; i < chests.Count; i++)
                for (int j = i + 1; j < chests.Count; j++)
                {
                    var d = chests[j] - chests[i];
                    d.y = 0f;
                    if (d.magnitude > far) { far = d.magnitude; axis = d.normalized; }
                }

            float tanV = Mathf.Tan(_staticFov * 0.5f * Mathf.Deg2Rad) * 0.82f, tanH = tanV * 16f / 9f;
            float best = float.MinValue;
            _staticPos = centre + new Vector3(0f, 4f, -6f);
            _staticLook = centre;
            foreach (float elev in new[] { 14f, 24f, 36f })
                for (int yi = 0; yi < 24; yi++)
                {
                    var rot = Quaternion.Euler(elev, yi * 15f, 0f);
                    var dir = rot * Vector3.forward; // from the camera toward the centre
                    // back off until every point fits the picture
                    float need = 0f;
                    var inv = Quaternion.Inverse(rot);
                    foreach (var q in pts)
                    {
                        var l = inv * (q - centre);
                        need = Mathf.Max(need, Mathf.Abs(l.x) / tanH - l.z, Mathf.Abs(l.y) / tanV - l.z);
                    }
                    float dist = Mathf.Max(3.5f, need);
                    var cam = centre - dir * dist;
                    if (!Clear(centre, cam)) continue; // inside or behind a wall
                    int seen = chests.Count(c => Clear(cam, c));
                    var flat = new Vector3(dir.x, 0f, dir.z).normalized;
                    float across = 1f - Mathf.Abs(Vector3.Dot(flat, axis));
                    float score = seen * 10f + across * 4f - dist * 0.25f - Mathf.Abs(elev - 24f) * 0.04f;
                    if (score > best) { best = score; _staticPos = cam; _staticLook = centre; }
                }
            Plugin.Log.LogDebug($"[angle] static: {chests.Count} players, camera {Vector3.Distance(_staticPos, centre):0.0} m from them");
        }

        // a clear line between two points (players and triggers don't count)
        bool Clear(Vector3 a, Vector3 b)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return true;
            int n = Physics.RaycastNonAlloc(a, d / len, _hits, len, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (_hits[i].collider != null && !IsPlayerCollider(_hits[i].collider)) return false;
            return true;
        }

        string AngleFile(Angle a) => Angles.First(x => x.a == a).file;
        internal static string AngleFileOf(Angle a) => Angles.First(x => x.a == (a == Angle.Current ? Angle.FirstPerson : a)).file;
    }
}
