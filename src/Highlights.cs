using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace Streplay
{
    // Finds the moments worth a clip in a recording without anyone having to flag them: the local player's kills
    // (from the kill feed lines every recording has), chained into multi-kills and scored on what makes a montage
    // clip good. Most of the weight is on the aim itself, measured from the recorded camera at the actual shot: a
    // big, fast, straight flick that lands the shot without corrections, a one-tap, a scoped shot. Headshots, range,
    // quickscopes / no-scopes and low health add to it; movement (airborne, speed) adds a little; a multi-kill counts
    // mostly through how good its best kill was. Weapons can be weighted (the Bayshore is toned down by default).
    internal static class Highlights
    {
        // bump when the scoring changes, so stored results are recomputed
        public const int Version = 6;
        // bump when what's measured from a recording changes (then every recording is read again)
        public const int FeaturesVersion = 1;

        static readonly Regex Tag = new Regex("<[^>]*>");
        static readonly Regex Who = new Regex(@"\{PLAYER_NAME\}:\{(\d+)\}");

        // measure a recording (kills, aim, take wins; stored on the meta) and score it
        public static List<ReplayMeta.MetaHighlight> Analyze(ReplayData data, ReplayMeta meta)
        {
            Measure(data, meta);
            return Score(meta);
        }

        static void Measure(ReplayData data, ReplayMeta meta)
        {
            var players = data.Objects.Where(o => o.Player != null).ToList();
            var localInfo = players.FirstOrDefault(o => o.Player.IsLocal)?.Player;
            int local = localInfo?.PlayerId ?? meta.Players.FirstOrDefault(p => p.IsLocal)?.PlayerId ?? -1;
            string Name(int id) => players.FirstOrDefault(o => o.Player.PlayerId == id)?.Player.Name is string n && n.Length > 0
                ? Tag.Replace(n, "") : meta.Players.FirstOrDefault(p => p.PlayerId == id)?.Name is string m ? Tag.Replace(m, "") : $"player {id}";

            var ctx = new Context(data, local);
            var kills = new List<KillRec>();
            var takeWins = new List<float>();
            if (local >= 0)
            {
                foreach (var ev in data.Events)
                {
                    if (ev.Method != "RpcSendChatLineToAllObservers" || ev.Args.Length == 0 || !(ev.Args[0] is string raw)) continue;
                    string line = Tag.Replace(raw, "").Trim();
                    var ids = Who.Matches(line).Cast<Match>().Select(x => int.Parse(x.Groups[1].Value)).ToList();
                    if (line.EndsWith(" won the take", StringComparison.Ordinal))
                    {
                        if (ids.Contains(local)) takeWins.Add(ev.T);
                        continue;
                    }
                    // "{victim} was headshot with a M2000 by {killer}"
                    int by = line.LastIndexOf(" by ", StringComparison.Ordinal);
                    if (ids.Count != 2 || by < 0 || !line.Contains(" was ")) continue;
                    if (ids[1] != local || ids[0] == local) continue;
                    var k = new KillRec { T = ev.T, Victim = Name(ids[0]), Headshot = line.Contains(" headshot ") };
                    int with = line.IndexOf(" with a", StringComparison.Ordinal);
                    if (with >= 0)
                    {
                        int w0 = line.IndexOf(' ', with + 6) + 1;
                        if (w0 > 0 && w0 < by) k.Weapon = line.Substring(w0, by - w0).Trim();
                    }
                    ctx.Measure(k, ids[0]);
                    kills.Add(k);
                }
            }
            meta.KillRecs = kills;
            meta.TakeWins = takeWins;
            meta.WeaponZoom = ctx.LocalZoom;
            meta.FeaturesVersion = FeaturesVersion;
        }

        // the highlights of a measured recording: kills chained into multi-kills, scored and tagged
        public static List<ReplayMeta.MetaHighlight> Score(ReplayMeta meta)
        {
            var kills = meta.KillRecs ?? new List<KillRec>();
            var takeWins = meta.TakeWins ?? new List<float>();
            var result = new List<ReplayMeta.MetaHighlight>();
            var flags = meta.Markers.Where(m => !m.Auto).ToList();
            var usedFlags = new HashSet<ReplayMeta.MetaMarker>();
            float chainGap = Plugin.HighlightChain.Value;
            for (int i = 0; i < kills.Count;)
            {
                int j = i + 1;
                while (j < kills.Count && kills[j].T - kills[j - 1].T <= chainGap) j++;
                var chain = kills.GetRange(i, j - i);
                i = j;

                float t0 = chain[0].T, t1 = chain[chain.Count - 1].T, span = t1 - t0;
                var flagged = flags.Where(f => f.T >= t0 - 1.5f && f.T <= t1 + 1.5f).ToList();
                foreach (var f in flagged) usedFlags.Add(f);
                bool winner = takeWins.Any(w => w >= t1 && w - t1 < 3f);

                // the best kill carries the clip; the others add some, and a quick multi a bit more
                var byScore = chain.Select(k => k.Score()).OrderByDescending(x => x).ToList();
                float score = byScore[0] + 0.3f * byScore.Skip(1).Sum();
                if (chain.Count > 1) score += 0.35f * (chain.Count - 1) + 0.3f * Mathf.Clamp01((4f - span) / 4f);
                if (winner) score += 0.3f;
                if (flagged.Count > 0) score += 3f;

                int hs = chain.Count(k => k.Headshot);
                float far = chain.Max(k => k.Distance), speed = chain.Max(k => k.Speed);
                var best = chain.OrderByDescending(k => k.Score()).First();
                var tags = new List<string>();
                if (chain.Count > 1) tags.Add($"{chain.Count}K" + (span < 2f ? " fast" : ""));
                var flickKill = chain.Where(k => k.CleanFlick).OrderByDescending(k => k.FlickDeg).FirstOrDefault();
                if (flickKill != null) tags.Add($"flick {flickKill.FlickDeg:0}°");
                if (hs > 0) tags.Add(hs > 1 ? $"{hs} headshots" : "headshot");
                if (chain.Any(k => k.QuickScope)) tags.Add("quickscope");
                if (chain.Any(k => k.NoScope)) tags.Add("no-scope");
                if (chain.All(k => k.Shots == 1)) tags.Add(chain.Count > 1 ? "one-taps" : "one-tap");
                else if (chain.Any(k => k.Shots >= 5)) tags.Add("spray");
                if (far >= 25f) tags.Add($"{far:0} m");
                if (chain.Any(k => k.Airborne)) tags.Add("airborne");
                if (speed >= 12f) tags.Add($"fast {speed:0} m/s");
                var low = chain.Where(k => k.Health > 0 && k.Health <= 35).Select(k => k.Health).DefaultIfEmpty(0).Min();
                if (low > 0) tags.Add($"low hp {low}");
                if (winner) tags.Add("take winner");
                if (flagged.Count > 0) tags.Add("flagged");

                var weapons = chain.Select(k => k.Weapon).Where(w => w.Length > 0).Distinct();
                result.Add(new ReplayMeta.MetaHighlight
                {
                    T0 = t0, T1 = t1, Kills = chain.Count, Score = (float)Math.Round(score, 1), KillT = chain.Select(k => k.T).ToList(),
                    Tags = tags,
                    Detail = string.Join(", ", chain.Select(k => k.Victim).Distinct()) + (weapons.Any() ? " · " + string.Join(", ", weapons) : ""),
                });
            }

            // marker-key moments that aren't one of the kills above still deserve a clip
            foreach (var f in flags.Where(f => !usedFlags.Contains(f)))
                result.Add(new ReplayMeta.MetaHighlight { T0 = f.T, T1 = f.T, Kills = 0, Score = 3f, Tags = new List<string> { "flagged" }, Detail = f.Label });

            return result.OrderBy(h => h.T0).ToList();
        }

        // one kill as measured from the recording (stored in the .strp.json, so it can be scored again later)
        internal sealed class KillRec
        {
            public float T, ShotT;
            public string Victim = "", Weapon = "", Held = "";   // Weapon from the kill feed, Held = the held object
            public bool Headshot, Airborne, Scoped, QuickScope;
            [Newtonsoft.Json.JsonIgnore] public bool NoScope => !Scoped && IsScopeGun(Held); // a scope gun fired unzoomed
            public float Distance, Speed;
            public int Health;
            // aim, measured from the camera in the moment before the shot
            public float FlickDeg, FlickPeak, Straight = 1f, FlickGap; // size, top speed, net/path ratio, flick end → shot
            public int Corrections;                                   // direction reversals in the last 0.4 s
            public int Shots = -1;                                    // own shots in the 1.2 s up to the kill (-1 = unknown)
            [Newtonsoft.Json.JsonIgnore] public bool CleanFlick => FlickDeg >= Tuning.FlickMinDeg && FlickPeak >= Tuning.FlickMinPeak && Straight >= 0.85f && Corrections <= 1 && FlickGap <= 0.3f;

            float _score = -1f;
            public float Score()
            {
                if (_score >= 0f) return _score;
                float w = WeaponWeight(Weapon);
                float s = 1f;
                // the flick: how big and how fast, scaled by how clean (straight, no corrections, landing on the shot)
                if (FlickDeg >= Tuning.FlickMinDeg && FlickPeak >= Tuning.FlickMinPeak)
                {
                    float clean = Mathf.Clamp01((Straight - 0.6f) / 0.35f) * (Corrections == 0 ? 1f : Corrections == 1 ? 0.7f : 0.35f)
                                  * (FlickGap <= 0.3f ? 1f : FlickGap <= 0.6f ? 0.5f : 0.2f);
                    s += clean * (1.2f + Mathf.Clamp((FlickDeg - Tuning.FlickMinDeg) / 60f, 0f, 1.6f) + Mathf.Clamp((FlickPeak - 600f) / 1200f, 0f, 0.8f));
                }
                else if (Corrections >= 3) s -= 0.3f; // wobbling onto the target
                if (Shots == 1) s += 0.6f;
                else if (Shots >= 5) s -= 0.6f;
                if (Headshot) s += 0.7f;
                s += Mathf.Clamp((Distance - 20f) / 30f, 0f, 1f);
                if (QuickScope) s += 1f;
                if (NoScope) s += 0.9f;
                if (Health > 0 && Health <= 35) s += 0.5f;
                if (w >= 1f) // movement counts for little, and nothing on a gun whose launch makes it routine
                {
                    if (Airborne) s += 0.4f;
                    s += Mathf.Clamp((Speed - 10f) / 15f, 0f, 0.6f);
                }
                return _score = s * w;
            }
        }

        // thresholds for what counts as a flick (tuned on real recordings)
        static class Tuning
        {
            public const float FlickMinDeg = 35f, FlickMinPeak = 600f;
        }

        // Highlights.WeaponWeights: "Bayshore=0.5, Taser=0.8"
        static Dictionary<string, float> _weights;
        static string _weightsFrom;
        static float WeaponWeight(string weapon)
        {
            string cfg = Plugin.HighlightWeaponWeights?.Value ?? "";
            if (_weights == null || _weightsFrom != cfg)
            {
                var d = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in cfg.Split(','))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    if (float.TryParse(part.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                        d[part.Substring(0, eq).Trim()] = Mathf.Clamp(f, 0f, 5f);
                }
                _weights = d;
                _weightsFrom = cfg;
            }
            if (string.IsNullOrEmpty(weapon)) return 1f;
            foreach (var kv in _weights) if (weapon.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            return 1f;
        }

        // how far each weapon zooms (smallest FOV / resting FOV seen, any recording). Only real scopes (snipers) zoom
        // past ScopeZoom; firing one of those unzoomed is a no-scope (a rifle's iron sights don't count)
        static readonly Dictionary<string, float> Zoom = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        const float ScopeZoom = 0.35f;

        static bool IsScopeGun(string weapon)
        {
            lock (Zoom) return weapon != null && Zoom.TryGetValue(weapon, out float z) && z < ScopeZoom;
        }

        static void MergeZoom(Dictionary<string, float> zoom)
        {
            if (zoom == null) return;
            lock (Zoom)
                foreach (var kv in zoom)
                    if (!Zoom.TryGetValue(kv.Key, out float z) || kv.Value < z) Zoom[kv.Key] = kv.Value;
        }

        internal static string ZoomSummary() { lock (Zoom) return string.Join(", ", Zoom.OrderBy(z => z.Value).Select(z => $"{z.Key} {z.Value:0.00}")); }

        // what a recording knows about the local player over time: camera, body, held item and the HUD's readouts
        sealed class Context
        {
            readonly ReplayData _d;
            readonly int _local;
            readonly float _restFov;
            readonly UiElementInfo _speedHud, _healthHud;
            public readonly Dictionary<string, float> LocalZoom = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            static readonly Regex SpeedRx = new Regex(@"speed\s*:?\s*([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase);
            static readonly Regex HealthRx = new Regex(@"health\s*:?\s*([0-9]+)", RegexOptions.IgnoreCase);

            public Context(ReplayData d, int local)
            {
                _d = d;
                _local = local;
                _restFov = Median(d.Frames.Select(x => x.CamFov).Where(v => v > 1f));
                foreach (var el in d.Ui.Values)
                {
                    if (el.Kind != UiKind.Text || el.Dynamic) continue;
                    if (_speedHud == null && el.States.Any(s => s.Text != null && SpeedRx.IsMatch(s.Text))) _speedHud = el;
                    if (_healthHud == null && el.Path.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) < 0 && el.States.Any(s => s.Text != null && HealthRx.IsMatch(s.Text))) _healthHud = el;
                }
                // how far each held weapon zooms
                foreach (var f in d.Frames)
                    if (Scoped(f) && Held(f) is string w)
                    {
                        float ratio = f.CamFov / _restFov;
                        if (!LocalZoom.TryGetValue(w, out float z) || ratio < z) LocalZoom[w] = ratio;
                    }
                MergeZoom(LocalZoom);
            }

            bool Scoped(Frame f) => f.CamFov > 1f && _restFov > 1f && f.CamFov < _restFov * 0.8f;

            ObjState Me(Frame f)
            {
                foreach (var s in f.Objects)
                {
                    var info = _d.Objects[s.Obj];
                    if (info.Player != null && info.Player.PlayerId == _local && s.Active) return s;
                }
                return null;
            }

            string Held(Frame f)
            {
                var me = Me(f);
                if (me == null || me.HeldRight < 0 || me.HeldRight >= _d.Objects.Count) return null;
                string n = _d.Objects[me.HeldRight].Name;
                int clone = n.IndexOf("(Clone)", StringComparison.Ordinal);
                return clone > 0 ? n.Substring(0, clone).Trim() : n;
            }

            // The flick into the shot: the stretch of turning around the fastest moment in the 0.8 s before it (above 30%
            // of that speed), its net turn, how straight it went (net turn / path turned) and how long before the shot it
            // ended; plus corrections, the horizontal direction reversals in the last 0.4 s.
            void MeasureAim(KillRec k)
            {
                var fr = _d.Frames;
                int i1 = FrameAt(fr, k.ShotT), i0 = FrameAt(fr, k.ShotT - 0.8f);
                if (i1 - i0 < 4) return;
                int n = i1 - i0;
                var speed = new float[n];
                var yawV = new float[n];
                for (int i = 0; i < n; i++)
                {
                    var a = fr[i0 + i]; var b = fr[i0 + i + 1];
                    float dt = b.T - a.T;
                    if (dt <= 1e-4f) continue;
                    float ang = Quaternion.Angle(a.CamRot, b.CamRot);
                    if (ang / dt > 6000f) return; // a respawn or teleport, not aim
                    speed[i] = ang / dt;
                    yawV[i] = Mathf.DeltaAngle((a.CamRot * Vector3.forward).Yaw(), (b.CamRot * Vector3.forward).Yaw()) / dt;
                }
                int p = 0;
                for (int i = 1; i < n; i++) if (speed[i] > speed[p]) p = i;
                float peak = speed[p];
                int lo = p, hi = p;
                while (lo > 0 && speed[lo - 1] > 0.3f * peak) lo--;
                while (hi < n - 1 && speed[hi + 1] > 0.3f * peak) hi++;
                float path = 0f;
                for (int i = lo; i <= hi; i++) path += Quaternion.Angle(fr[i0 + i].CamRot, fr[i0 + i + 1].CamRot);
                float net = Quaternion.Angle(fr[i0 + lo].CamRot, fr[i0 + hi + 1].CamRot);
                k.FlickPeak = peak;
                k.FlickDeg = net;
                k.Straight = path > 1e-3f ? net / path : 1f;
                k.FlickGap = Mathf.Max(0f, k.ShotT - fr[i0 + hi + 1].T);
                int last = 0;
                for (int i = 0; i < n; i++)
                {
                    if (fr[i0 + i].T < k.ShotT - 0.4f) continue;
                    int sgn = yawV[i] > 40f ? 1 : yawV[i] < -40f ? -1 : 0;
                    if (sgn != 0 && last != 0 && sgn != last) k.Corrections++;
                    if (sgn != 0) last = sgn;
                }
            }

            float? Hud(UiElementInfo el, Regex rx, float t)
            {
                if (el == null) return null;
                int frame = FrameAt(_d.Frames, t), found = -1;
                for (int i = 0; i < el.Frames.Count && el.Frames[i] <= frame; i++) found = i;
                if (found < 0 || el.States[found].Text == null) return null;
                var m = rx.Match(el.States[found].Text);
                return m.Success && float.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : (float?)null;
            }

            // own shots: the held gun's shoot RPCs
            List<float> _shots;
            List<float> Shots()
            {
                if (_shots != null) return _shots;
                _shots = new List<float>();
                foreach (var ev in _d.Events)
                {
                    if (!ev.Method.StartsWith("Shoot", StringComparison.Ordinal)) continue;
                    var me = Me(_d.Frames[FrameAt(_d.Frames, ev.T)]);
                    if (me != null && (ev.Obj == me.HeldRight || ev.Obj == me.HeldLeft)) _shots.Add(ev.T);
                }
                return _shots;
            }

            public void Measure(KillRec k, int victimId)
            {
                var frames = _d.Frames;
                // the kill feed line comes a moment after the shot that made it; aim is judged at that shot
                var shots = Shots();
                float shot = shots.Where(x => x <= k.T + 0.02f && x >= k.T - 0.6f).DefaultIfEmpty(-1f).Max();
                k.ShotT = shot >= 0f ? shot : k.T - 0.03f;
                k.Shots = shots.Count > 0 ? shots.Count(x => x <= k.T + 0.02f && x >= k.T - 1.2f) : -1;
                if (k.Shots == 0) k.Shots = -1; // melee, or a gun without a shoot RPC
                MeasureAim(k);

                int at = FrameAt(frames, k.T);
                var f = frames[at];

                // zoom: scoped at the shot; quickscope = zoomed in only just before it; no-scope = a scope gun unzoomed
                k.Scoped = Scoped(f);
                if (k.Scoped)
                {
                    bool unzoomedJustBefore = false;
                    for (int i = FrameAt(frames, k.T - 0.45f); i < FrameAt(frames, k.T - 0.08f); i++) if (!Scoped(frames[i])) { unzoomedJustBefore = true; break; }
                    k.QuickScope = unzoomedJustBefore;
                }
                k.Held = Held(f) ?? Held(frames[FrameAt(frames, k.T - 0.1f)]) ?? "";

                // range to the victim just before they died
                var before = frames[FrameAt(frames, k.T - 0.05f)];
                foreach (var s in before.Objects)
                {
                    var info = _d.Objects[s.Obj];
                    if (info.Player == null || !s.Active) continue;
                    if (info.Player.PlayerId == victimId) k.Distance = Mathf.Max(k.Distance, Vector3.Distance(f.CamPos, s.Pos + Vector3.up * 1.2f));
                }

                // airborne: moving up/down fast, or well above where you stood in the last 1.5 s
                var early = frames[FrameAt(frames, k.T - 0.15f)];
                var me0 = Me(early); var me1 = Me(f);
                if (me0 != null && me1 != null && f.T - early.T > 0.05f)
                {
                    float vy = (me1.Pos.y - me0.Pos.y) / (f.T - early.T);
                    float floor = float.MaxValue;
                    for (int i = FrameAt(frames, k.T - 1.5f); i <= at; i++) { var m = Me(frames[i]); if (m != null) floor = Mathf.Min(floor, m.Pos.y); }
                    k.Airborne = Mathf.Abs(vy) > 2.5f || me1.Pos.y - floor > 1.5f;
                    // movement speed: the game's own speedometer if it was on screen, else from positions
                    var horiz = new Vector2(me1.Pos.x - me0.Pos.x, me1.Pos.z - me0.Pos.z).magnitude / (f.T - early.T);
                    k.Speed = Hud(_speedHud, SpeedRx, k.T) ?? horiz;
                }
                k.Health = Mathf.RoundToInt(Hud(_healthHud, HealthRx, k.T - 0.05f) ?? 0f);
            }
        }

        // the local player's kills (time, victim player id), for the cameras that follow the victim
        public static List<(float T, int Victim)> LocalKills(ReplayData data, ReplayMeta meta)
        {
            var list = new List<(float, int)>();
            int local = data.Objects.FirstOrDefault(o => o.Player != null && o.Player.IsLocal)?.Player.PlayerId
                        ?? meta?.Players.FirstOrDefault(p => p.IsLocal)?.PlayerId ?? -1;
            if (local < 0) return list;
            foreach (var ev in data.Events)
            {
                if (ev.Method != "RpcSendChatLineToAllObservers" || ev.Args.Length == 0 || !(ev.Args[0] is string raw)) continue;
                string line = Tag.Replace(raw, "");
                var ids = Who.Matches(line).Cast<Match>().Select(x => int.Parse(x.Groups[1].Value)).ToList();
                if (ids.Count == 2 && line.Contains(" was ") && line.Contains(" by ") && ids[1] == local && ids[0] != local) list.Add((ev.T, ids[0]));
            }
            return list;
        }

        static float Yaw(this Vector3 f) => Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;

        static int FrameAt(List<Frame> frames, float t)
        {
            int lo = 0, hi = frames.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (frames[mid].T <= t) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        static float Median(IEnumerable<float> values)
        {
            var v = values.OrderBy(x => x).ToList();
            return v.Count == 0 ? 0f : v[v.Count / 2];
        }

        // ---------------------------------------------------------------- background scan

        static Thread _thread;
        static volatile int _done, _total;
        public static bool Scanning => _thread != null && _thread.IsAlive;
        public static string Progress => Scanning ? $"Finding highlights {_done}/{_total}…" : null;

        // Scans finished recordings that have no (or outdated) highlights, saving the results into their .json.
        public static void Scan(List<ReplayMeta> metas)
        {
            if (Scanning) return;
            // unfinished rounds too (the game closed or crashed mid-round), except the one being recorded right now
            string recording = Recorder.Instance != null ? Recorder.Instance.CurrentMetaPath : null;
            var todo = metas.Where(m => m.HighlightsVersion < Version
                                        && (m.Finished || !string.Equals(m.MetaPath, recording, StringComparison.OrdinalIgnoreCase))).ToList();
            if (todo.Count == 0) return;
            // every recording's weapon zoom first, so no-scopes are judged the same whatever the order
            foreach (var m in metas) MergeZoom(m.WeaponZoom);
            _done = 0;
            _total = todo.Count;
            _thread = new Thread(() =>
            {
                int reread = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var m in todo)
                {
                    try
                    {
                        // Work on a copy read from disk: the menu and the replay player use `m` on the main thread
                        // (markers get added, lists enumerated). Only the finished highlight list is handed back.
                        var work = ReplayMeta.Load(m.MetaPath);
                        if (work == null) { m.HighlightsVersion = Version; _done++; continue; }
                        // measured before (current measurements): only the scoring changed, no need to read the recording
                        bool measured = work.FeaturesVersion == FeaturesVersion && work.KillRecs != null;
                        if (!measured || !work.Finished)
                        {
                            var data = ReplayIO.Read(work.ReplayPath);
                            if (!measured) { Measure(data, work); reread++; }
                            if (!work.Finished)
                            {
                                // a round the game didn't get to close: what was recorded still plays
                                work.Duration = data.Duration;
                                work.Finished = true;
                            }
                        }
                        var found = Score(work);
                        ReplayMeta.UpdateFile(m.MetaPath, f =>
                        {
                            f.Highlights = found;
                            f.HighlightsVersion = Version;
                            f.KillRecs = work.KillRecs;
                            f.TakeWins = work.TakeWins;
                            f.WeaponZoom = work.WeaponZoom;
                            f.FeaturesVersion = work.FeaturesVersion;
                            if (!f.Finished) { f.Duration = work.Duration; f.Finished = true; }
                        });
                        // reference swaps only: readers on the main thread see the old list or the new one
                        m.Highlights = found;
                        m.HighlightsVersion = Version;
                        if (!m.Finished) { m.Duration = work.Duration; m.Finished = true; }
                    }
                    catch (ThreadAbortException) { throw; } // the game closing: this one gets scanned next time
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"Finding highlights in {m.ReplayFile} failed: {e.Message}");
                        m.HighlightsVersion = Version; // don't retry every time
                    }
                    _done++;
                }
                Plugin.Log.LogInfo($"Highlights: scored {_total} recordings in {sw.Elapsed.TotalSeconds:0}s ({reread} read from disk, the rest from stored measurements); weapon zoom: {ZoomSummary()}");
            }) { IsBackground = true, Name = "Streplay highlights", Priority = System.Threading.ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }
}
