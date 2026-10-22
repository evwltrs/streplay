using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace Streplay
{
    // Main-menu window listing recorded rounds (grouped by match) and their markers, the best moments, and settings.
    internal sealed partial class ReplayBrowser : MonoBehaviour
    {
        bool _open;
        List<ReplayMeta> _list = new List<ReplayMeta>();
        readonly HashSet<string> _collapsed = new HashSet<string>(); // match ids the user folded
        readonly HashSet<string> _unfolded = new HashSet<string>();  // older matches the user opened
        bool _markersOnly;
        string _notice;
        int _tab; // 0 rounds, 1 highlights, 2 settings
        bool _allMatches;

        bool InMenu => !ReplayPlayer.Active && SceneManager.GetActiveScene().name == "MainMenu";

        // the folder is listed on a worker thread (ReplayIndex); the result is picked up here, on the main thread
        volatile ReplayIndex.Snapshot _loaded;
        bool _loading, _reloadAfter;
        int _listVersion;
        System.Diagnostics.Stopwatch _loadWatch;

        void Start() => Refresh(); // warm the index at startup, so the first F10 is instant

        void Update()
        {
            TakeLoaded();
            // The game's UI stays off while ours is up, and a moment longer: the mouse release of the click that closed
            // the browser (or exited a replay) would otherwise land on whatever menu button is underneath.
            bool want = _open || ReplayPlayer.SuspendGame || ReplayPlayer.BackToBrowser;
            if (want) _blockUntil = Time.unscaledTime + 0.35f;
            bool held = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            BlockGameUi(want || Time.unscaledTime < _blockUntil || (_blocked.Count > 0 && held));
            if (!InMenu) { _open = false; _capture = null; return; }
            if (ReplayPlayer.MenuNotice != null) { _notice = ReplayPlayer.MenuNotice; ReplayPlayer.MenuNotice = null; _open = true; Refresh(); }
            // back from a replay: straight to the list you came from (same tab, same scroll), not the bare menu
            if (ReplayPlayer.BackToBrowser)
            {
                ReplayPlayer.BackToBrowser = false;
                if (!_open) { _open = true; Refresh(); }
            }
            if (Capturing) return; // the key being bound is the settings tab's, not ours
            if (Plugin.BrowserKey.Value.IsDown()) Toggle();
            else if (_open && Input.GetKeyDown(KeyCode.Escape)) _open = false;
        }

        // IMGUI clicks also reach the game's own UI underneath (Quick match!), so its input is switched off while
        // the browser is open and while a replay loads or plays.
        readonly List<EventSystem> _blocked = new List<EventSystem>();
        float _blockUntil;

        void BlockGameUi(bool block)
        {
            if (block)
            {
                foreach (var es in FindObjectsOfType<EventSystem>())
                    if (es.enabled) { es.enabled = false; _blocked.Add(es); }
            }
            else if (_blocked.Count > 0)
            {
                foreach (var es in _blocked) if (es != null) es.enabled = true;
                _blocked.Clear();
            }
        }

        internal static ReplayBrowser Instance;
        void Awake() => Instance = this;
        internal static bool TestHideButton, TestHover; // AutoTest: clean screenshots of the menu / the lit button

#if STREPLAY_AUTOTEST
        internal void TestSetOpen(bool on) { if (_open != on) Toggle(); }
        internal void TestHighlightsTab(bool all) { _tab = 1; _allMatches = all; }
        internal void TestRoundsTab() => _tab = 0;
        internal void TestSettingsTab() => _tab = 2;
        internal string TestTopHighlights(int n) => string.Join("\n", AllHighlights(true).Take(n).Select(x =>
            $"  {x.h.Score:0.0} {x.m.Scene} take {x.m.Take} @{x.h.T0:0.00}-{x.h.T1:0.00} [{string.Join(", ", x.h.Tags)}] {x.h.Detail}"));
        internal void TestWatchHighlight(int index)
        {
            var all = AllHighlights(true);
            if (index >= 0 && index < all.Count) WatchHighlight(all[index].m, all[index].h);
        }
#endif

        List<(ReplayMeta m, ReplayMeta.MetaHighlight h)> AllHighlights(bool allMatches)
        {
            string latest = _list.Count > 0 ? MatchId(_list[0]) : null;
            return _list.Where(m => allMatches || MatchId(m) == latest)
                .SelectMany(m => (m.Highlights ?? new List<ReplayMeta.MetaHighlight>()).Select(h => (m, h)))
                .OrderByDescending(x => x.h.Score).ThenByDescending(x => x.m.StartUtc).ToList();
        }

        void Toggle()
        {
            _open = !_open;
            _capture = null;
            if (_open) Refresh();
        }

        // Lists the folder in the background; the window keeps showing the last list until the new one is in.
        void Refresh()
        {
            if (_loading) { _reloadAfter = true; return; }
            _loading = true;
            _loadWatch = System.Diagnostics.Stopwatch.StartNew();
            string dir = Plugin.ReplayFolder.Value;
            new System.Threading.Thread(() =>
            {
                ReplayIndex.Snapshot s;
                try { s = ReplayIndex.Load(dir); }
                catch (Exception e) { Plugin.Log.LogError($"Could not list replays: {e.Message}"); s = new ReplayIndex.Snapshot(); }
                _loaded = s;
            }) { IsBackground = true, Name = "Streplay list", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        void TakeLoaded()
        {
            var s = _loaded;
            if (s == null) return;
            _loaded = null;
            _loading = false;
            _list = s.List;
            _listVersion++;
            _usedBytes = s.Bytes;
            _freeBytes = Disk.Free(Plugin.ReplayFolder.Value);
            _usedRounds = s.List.Count;
            // ten minutes of rounds at 60 Hz before trusting the player's own numbers over the measured default
            _bytesPerSecPerHz = s.SecondsHz >= 600.0 * 60.0 ? s.SecondsHzBytes / s.SecondsHz : DefaultBytesPerSecPerHz;
            Highlights.Scan(_list);
            Plugin.Log.LogDebug($"Listed {_list.Count} rounds in {_loadWatch?.ElapsedMilliseconds ?? 0} ms");
            if (_reloadAfter) { _reloadAfter = false; Refresh(); }
        }

        // ---------------------------------------------------------------- drawing

        void OnGUI()
        {
            if (!InMenu) return;
            Ui.Begin();
            Ui.EnsureIcons();
            GUI.depth = -1000;
            try
            {
                if (!_open)
                {
                    DrawMenuButton();
                    return;
                }
                // dim the menu behind; the window is the only thing that takes clicks
                Ui.Fill(new Rect(0f, 0f, Ui.W, Ui.H), new Color(0f, 0f, 0f, 0.72f));
                float w = Mathf.Min(1040f, Ui.W - 60f), h = Mathf.Min(860f, Ui.H - 60f);
                var win = new Rect((Ui.W - w) / 2f, (Ui.H - h) / 2f, w, h);
                Ui.Panel(win, 16f);
                DrawContents(win);
            }
            finally { Ui.End(); }
        }

        // ---------------------------------------------------------------- the menu's Replays button

        // STRAFTAT's menu palette (sampled from the game): lime-yellow headline text, mint labels, warm white when lit
        static readonly Color MenuLime = new Color(233f / 255f, 1f, 145f / 255f);
        static readonly Color MenuLit = new Color(254f / 255f, 1f, 222f / 255f);
        static readonly Color MenuMint = new Color(187f / 255f, 243f / 255f, 192f / 255f, 0.6f);
        static readonly Color MenuInk = new Color(0.04f, 0.07f, 0.05f, 0.92f);
        static readonly Color MenuRec = new Color(0.86f, 0.28f, 0.22f);

        static bool OnTitleScreen
        {
            get
            {
                var mc = MenuController.Instance;
                return mc != null && mc.startMenu != null && mc.startMenu.activeInHierarchy;
            }
        }

        static bool InLobby => SteamLobby.Instance != null && SteamLobby.Instance.inSteamLobby;

        // In the frame's empty name slot when it's free, like a part of the menu; a small plate in the same style otherwise.
        // Not on the title screen, and not in a lobby: the slot shows the lobby's name then, and replays can't open anyway.
        void DrawMenuButton()
        {
            if (TestHideButton || OnTitleScreen || InLobby) return;
            var slot = MenuSlot.ScreenRect();
            if (slot.HasValue)
            {
                var s = slot.Value;
                var r = new Rect(s.x / Ui.Scale, s.y / Ui.Scale, s.width / Ui.Scale, s.height / Ui.Scale);
                if (DrawSlotButton(r)) Toggle();
                return;
            }
            var p = new Rect(18f, 18f, 196f, 40f);
            Ui.Fill(p, MenuInk, 20f);
            Ui.Border(p, new Color(MenuMint.r, MenuMint.g, MenuMint.b, 0.35f), 1.5f, 20f);
            if (DrawSlotButton(p)) Toggle();
        }

        bool DrawSlotButton(Rect r)
        {
            bool hover = Ui.Hover(r) || TestHover;
            float h = r.height;
            if (hover)
            {
                // the slot lights up from inside, like the menu's own lit buttons
                var glow = new Rect(r.x + h * 0.12f, r.y + h * 0.14f, r.width - h * 0.24f, h * 0.72f);
                Ui.Fill(glow, new Color(MenuLime.r, MenuLime.g, MenuLime.b, 0.10f), glow.height / 2f);
            }
            int size = Mathf.Clamp(Mathf.RoundToInt(h * 0.6f), 11, 44);
            float x = r.x + h * 0.5f;
            // the REC light: on while the menu waits, brighter on hover
            float dot = Mathf.Max(3f, h * 0.11f);
            float pulse = hover ? 1f : 0.65f + 0.35f * Mathf.Sin(Time.unscaledTime * 2.4f);
            Ui.Circle(new Vector2(x + dot, r.center.y), dot * 1.9f, new Color(MenuRec.r, MenuRec.g, MenuRec.b, 0.18f * pulse));
            Ui.Circle(new Vector2(x + dot, r.center.y), dot, new Color(MenuRec.r, MenuRec.g, MenuRec.b, 0.55f + 0.45f * pulse));
            x += dot * 2f + h * 0.28f;

            // the game's lettering: bold italic, lime on a dark outline
            const string label = "REPLAYS";
            float tw = Ui.Measure(label, size, true) + 8f;
            var tr = new Rect(x, r.y, tw, h);
            float o = Mathf.Max(1f, h * 0.035f);
            foreach (var d in new[] { new Vector2(-o, 0f), new Vector2(o, 0f), new Vector2(0f, -o), new Vector2(0f, o), new Vector2(o, o) })
                Ui.Text(new Rect(tr.x + d.x, tr.y + d.y, tr.width, tr.height), label, size, MenuInk, true);
            Ui.Text(tr, label, size, hover ? MenuLit : MenuLime, true);

            // the shortcut, quietly, at the far end
            string key = Plugin.BrowserKey.Value.ToString();
            int ks = Mathf.Clamp(Mathf.RoundToInt(h * 0.3f), 9, 22);
            float kw = Ui.Measure(key, ks, true) + 6f;
            if (x + tw + kw + h * 0.3f < r.xMax - h * 0.35f)
                Ui.Text(new Rect(r.xMax - h * 0.45f - kw, r.y, kw, h), key, ks, MenuMint, true, TextAnchor.MiddleRight);

            if (hover) Ui.Tip(r, "Your recorded rounds, markers and highlights");
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        void DrawContents(Rect win)
        {
            float x = win.x + 26f, right = win.xMax - 26f, y = win.y + 20f;

            // header
            Ui.Text(new Rect(x, y - 2f, 220f, 40f), "REPLAYS", 30, Ui.Bright, true);
            string[] tabs = { "Rounds", "Highlights", "Settings" };
            float tabsW = tabs.Sum(t => Ui.AutoW(t, null, 32f, 13)) + 3f * (tabs.Length - 1);
            int tab = Ui.Segmented(new Rect(x + 180f, y + 3f, tabsW, 32f), tabs, _tab, null, 13);
            if (tab != _tab) { _tab = tab; _capture = null; }
            float bx = right;
            Rect Hb(string l) { float w = Ui.AutoW(l, null, 32f, 12); bx -= w; var r = new Rect(bx, y + 3f, w, 32f); bx -= 4f; return r; }
            if (Ui.Button(new Rect(right - 34f, y + 3f, 34f, 32f), "", false, Ui.Kind.Ghost, Ui.IconClose, 12, "Close (Esc)")) { _open = false; _capture = null; }
            bx -= 42f;
            if (Ui.Button(Hb("Folder"), "Folder", false, Ui.Kind.Normal, null, 12, "Open the replay folder")) Ui.OpenFolder(Plugin.ReplayFolder.Value);
            if (Ui.Button(Hb("Refresh"), "Refresh", false, Ui.Kind.Normal, null, 12)) Refresh();
            y += 50f;

            if (_notice != null)
            {
                var nr = new Rect(x, y, right - x, 34f);
                Ui.Slant(nr, new Color(1f, 0.75f, 0.3f, 0.14f));
                Ui.Text(new Rect(nr.x + 14f, nr.y, nr.width - 28f, nr.height), _notice, 13, Ui.Warn, true);
                y += 44f;
            }

            var body = new Rect(x, y, right - x, win.yMax - y - 20f);
            if (_tab == 1) DrawHighlights(body);
            else if (_tab == 2) DrawSettings(body);
            else DrawRounds(body);
        }

        // "1 round", "2 rounds", "3 MATCHES"
        static string N(int n, string word) =>
            n == 1 ? $"1 {word}" : $"{n} {word}" + (word.EndsWith("CH") ? "ES" : word.EndsWith("ch") ? "es" : char.IsUpper(word[word.Length - 1]) ? "S" : "s");

        // ---------------------------------------------------------------- rounds

        float _roundsScroll, _hlScrollY;

        // One match header and its rounds (oldest first), worked out once per list rather than every frame
        sealed class MatchGroup
        {
            public string Key, Title, Summary;
            public List<ReplayMeta> Rounds;
            public float OpenHeight;
        }

        List<MatchGroup> _groups = new List<MatchGroup>();
        int _itemCount, _groupsVersion = -1, _groupsHl = -1;
        bool _groupsMarked;

        List<MatchGroup> Groups()
        {
            int hl = Highlights.Changes;
            if (_groupsVersion == _listVersion && _groupsMarked == _markersOnly && _groupsHl == hl) return _groups;
            _groupsVersion = _listVersion; _groupsMarked = _markersOnly; _groupsHl = hl;
            var items = _markersOnly ? _list.Where(m => m.Markers.Count > 0).ToList() : _list;
            _itemCount = items.Count;
            _groups = items.GroupBy(MatchId).Select(g =>
            {
                var rounds = g.Reverse().ToList();
                var others = rounds.SelectMany(m => m.Players).Where(p => !p.IsLocal).Select(p => Strip(p.Name)).Distinct().Take(5).ToList();
                int kills = rounds.Sum(m => m.Highlights?.Sum(h => h.Kills) ?? 0), flagged = rounds.Sum(m => m.Markers.Count(k => !k.Auto));
                return new MatchGroup
                {
                    Key = g.Key,
                    Rounds = rounds,
                    Title = $"{rounds[0].StartUtc.ToLocalTime():ddd MMM d  ·  HH:mm}".ToUpperInvariant(),
                    Summary = N(rounds.Count, "round") + (kills > 0 ? "  ·  " + N(kills, "kill") : "") + (flagged > 0 ? $"  ·  {flagged} flagged" : "") +
                              (others.Count > 0 ? $"  ·  vs {string.Join(", ", others)}" : ""),
                    OpenHeight = rounds.Sum(RoundHeight),
                };
            }).ToList();
            return _groups;
        }

        void DrawRounds(Rect body)
        {
            var matches = Groups();
            int itemCount = _itemCount;

            if (Ui.Chip(new Rect(body.x, body.y, 150f, 28f), "Marked only", _markersOnly, "Only rounds with markers")) _markersOnly = !_markersOnly;
            Ui.Text(new Rect(body.x + 166f, body.y, 400f, 28f), N(itemCount, "ROUND") + "  ·  " + N(matches.Count, "MATCH"), 12, Ui.Dim, true);
            string banner = !Plugin.RecordEnabled.Value ? $"Recording is off: {Plugin.RecordToggleKey.Value} in a match or the Settings tab turns it on"
                : _freeBytes >= 0 && _freeBytes < Disk.Floor ? $"Disk almost full ({Disk.Gb(_freeBytes)} free on {Disk.Drive(Plugin.ReplayFolder.Value)}): recording pauses until there's {Disk.Gb(Disk.Floor)} free"
                : _freeBytes >= 0 && _freeBytes < Disk.Low ? $"Low disk space: {Disk.Gb(_freeBytes)} free on {Disk.Drive(Plugin.ReplayFolder.Value)}"
                : null;
            if (banner != null)
                Ui.Text(new Rect(body.x + 420f, body.y, body.width - 420f, 28f), banner, 12, Ui.Warn, true, TextAnchor.MiddleRight);
            var list = new Rect(body.x, body.y + 40f, body.width, body.height - 40f);

            // measure, then draw the visible part
            float content = 0f;
            for (int i = 0; i < matches.Count; i++)
            {
                content += 48f;
                if (MatchOpen(matches[i].Key, i)) content += matches[i].OpenHeight;
            }
            if (itemCount == 0) content = 60f;
            _roundsScroll = Scroll(list, content, _roundsScroll);

            GUI.BeginGroup(list);
            float y = -_roundsScroll, w = list.width - (content > list.height ? 12f : 0f);
            if (itemCount == 0)
                Ui.Text(new Rect(0f, 0f, w, 60f), _loading ? "Loading your replays…" : _markersOnly ? "No marked rounds yet." :
                    $"No replays yet. Play a match and every take is recorded. Press {Plugin.MarkerKey.Value} during a round to mark a moment, or right after a kill to flag it.", 13, Ui.Dim, false, TextAnchor.UpperLeft, true);
            for (int i = 0; i < matches.Count; i++)
            {
                var g = matches[i];
                bool open = MatchOpen(g.Key, i);
                float gh = 48f + (open ? g.OpenHeight : 0f);
                if (y + gh < 0f || y > list.height) { y += gh; continue; } // off screen: only its height counts
                var hr = new Rect(0f, y + 6f, w, 38f);
                if (Ui.Hover(hr)) Ui.Slant(hr, Ui.Wash(0.05f));
                Ui.Icon(new Rect(12f, hr.center.y - 6f, 12f, 12f), open ? Ui.IconDown : Ui.IconPlay, Ui.Accent);
                Ui.Text(new Rect(32f, hr.y, 260f, hr.height), g.Title, 16, Ui.Bright, true);
                Ui.Text(new Rect(260f, hr.y, w - 270f, hr.height), g.Summary, 12, Ui.Dim);
                if (GUI.Button(hr, GUIContent.none, GUIStyle.none))
                {
                    if (i == 0) { if (!_collapsed.Remove(g.Key)) _collapsed.Add(g.Key); }
                    else if (!_unfolded.Remove(g.Key)) _unfolded.Add(g.Key);
                }
                y += 48f;
                if (!open) continue;
                int n = 0;
                foreach (var m in g.Rounds)
                {
                    float rh = RoundHeight(m);
                    if (y + rh > 0f && y < list.height) DrawRound(new Rect(0f, y, w, rh - 4f), m, n);
                    y += rh;
                    n++;
                }
            }
            GUI.EndGroup();
        }

        bool MatchOpen(string key, int index) => index == 0 ? !_collapsed.Contains(key) : _unfolded.Contains(key);

        static float RoundHeight(ReplayMeta m) => m.Markers.Count > 0 ? 82f : 50f;

        void DrawRound(Rect r, ReplayMeta m, int index)
        {
            bool hover = Ui.Hover(r);
            Ui.Fill(r, hover ? Ui.Wash(0.07f) : index % 2 == 0 ? Ui.Wash(0.03f) : new Color(0f, 0f, 0f, 0f));
            if (hover) Ui.Fill(new Rect(r.x, r.y, 3f, r.height), Ui.Accent);
            float top = r.y + 4f;
            Ui.Text(new Rect(r.x + 14f, top, 30f, 38f), $"{m.RoundIndex + 1}", 13, Ui.Faint, true);
            Ui.Text(new Rect(r.x + 46f, top, 320f, 38f), m.Scene, 16, Ui.Bright, true);
            float tx = r.x + 46f + Mathf.Min(320f, Ui.Measure(m.Scene, 16, true)) + 12f;
            if (m.Take > 0)
            {
                string take = m.Part >= 2 ? $"TAKE {m.Take} · PART {m.Part}" : $"TAKE {m.Take}";
                var tr = new Rect(tx, top + 9f, Mathf.Max(66f, Ui.Measure(take, 11, true) + 22f), 20f);
                Ui.Slant(tr, Ui.AccentSoft);
                Ui.Text(tr, take, 11, Ui.Accent, true, TextAnchor.MiddleCenter);
            }
            Ui.Text(new Rect(r.x + 420f, top, 90f, 38f), ReplayPlayer.Fmt(m.Duration) + (m.Finished ? "" : " (incomplete)"), 12, Ui.Dim);
            int kills = m.Highlights?.Sum(h => h.Kills) ?? 0;
            if (kills > 0) Ui.Text(new Rect(r.x + 520f, top, 90f, 38f), kills == 1 ? "1 KILL" : $"{kills} KILLS", 12, Ui.Kill, true);
            float ww = Ui.AutoW("Watch", Ui.IconPlay, 30f, 12);
            if (Ui.Button(new Rect(r.xMax - ww - 10f, top + 4f, ww, 30f), "Watch", false, hover ? Ui.Kind.Primary : Ui.Kind.Normal, Ui.IconPlay, 12)) Play(m, 0f);

            if (m.Markers.Count > 0)
            {
                float cx = r.x + 46f, cy = r.y + 46f;
                int shown = 0;
                foreach (var mk in m.Markers)
                {
                    string label = $"{ReplayPlayer.Fmt(mk.T).Split('.')[0]}  {mk.Label}".ToUpperInvariant();
                    float cw = Ui.Measure(label, 11, true) + 34f;
                    if (cx + cw > r.xMax - 110f || shown == 8) { Ui.Text(new Rect(cx, cy, 120f, 24f), $"+{m.Markers.Count - shown} more", 11, Ui.Faint); break; }
                    var cr = new Rect(cx, cy, cw, 24f);
                    var c = !mk.Auto ? Ui.Accent : mk.Label.StartsWith("Kill") ? Ui.Kill : Ui.Death;
                    bool h = Ui.Hover(cr);
                    Ui.Slant(cr, new Color(c.r, c.g, c.b, h ? 0.32f : 0.16f));
                    Ui.Slant(new Rect(cr.x + 7f, cr.y + 6f, 5f, cr.height - 12f), c);
                    Ui.Text(new Rect(cr.x + 18f, cr.y, cw - 20f, cr.height), label, 11, h ? Ui.Bright : Ui.TextC, true);
                    if (GUI.Button(cr, GUIContent.none, GUIStyle.none)) Play(m, Mathf.Max(0, mk.T - Plugin.MarkerPreroll.Value));
                    cx += cw + 6f;
                    shown++;
                }
            }
        }

        // wheel scrolling for a list area (with a slim bar); returns the new offset
        static float Scroll(Rect area, float content, float offset)
        {
            float max = Mathf.Max(0f, content - area.height);
            var e = Event.current;
            if (e.type == EventType.ScrollWheel && area.Contains(e.mousePosition)) { offset += e.delta.y * 24f; e.Use(); }
            offset = Mathf.Clamp(offset, 0f, max);
            if (max > 0f)
            {
                float frac = area.height / content;
                Ui.Fill(new Rect(area.xMax - 5f, area.y + (area.height - area.height * frac) * (offset / max), 5f, area.height * frac), Ui.Wash(0.2f), 2.5f);
            }
            return offset;
        }

        static string Strip(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "");

        // ---------------------------------------------------------------- highlights

        internal static Color TagColor(string t) =>
            t.EndsWith("K") || t.Contains("K fast") || t.Contains("headshot") || t == "quickscope" || t == "no-scope" ? Ui.Kill
            : t == "flagged" ? Ui.Accent : t == "take winner" ? Ui.Good : t.StartsWith("low hp") ? Ui.Danger : Ui.Dim;

        void DrawHighlights(Rect body)
        {
            float scW = Ui.AutoW("Latest match", null, 28f, 12) + Ui.AutoW("All matches", null, 28f, 12) + 3f;
            _allMatches = Ui.Segmented(new Rect(body.x, body.y, scW, 28f), new[] { "Latest match", "All matches" }, _allMatches ? 1 : 0, null, 12) == 1;
            // what kind of clip: every filter keeps the best-first order
            float fw = 0f;
            foreach (var l in Filters) fw += Ui.AutoW(l, null, 28f, 12) + 3f;
            _filter = Ui.Segmented(new Rect(body.xMax - fw, body.y, fw, 28f), Filters, _filter, null, 12);
            var shown = ShownHighlights();
            Ui.Text(new Rect(body.x + scW + 16f, body.y, body.width - scW - fw - 28f, 28f),
                Highlights.Progress ?? $"{shown.Count} moments, best first", 12, Ui.Dim);

            const float footer = 34f, rowH = 56f;
            var list = new Rect(body.x, body.y + 40f, body.width, body.height - 40f - footer);
            float content = Math.Max(60f, shown.Count * (rowH + 4f));
            _hlScrollY = Scroll(list, content, _hlScrollY);

            GUI.BeginGroup(list);
            float y = -_hlScrollY, w = list.width - (content > list.height ? 12f : 0f);
            if (shown.Count == 0)
                Ui.Text(new Rect(0f, 0f, w, 60f), Highlights.Scanning || _loading ? "Looking through your replays…" :
                    "No kills found yet. Every kill you make in a recorded round shows up here, best moments first.", 13, Ui.Dim, false, TextAnchor.UpperLeft, true);
            for (int i = 0; i < shown.Count; i++, y += rowH + 4f)
            {
                if (y + rowH < 0f || y > list.height) continue;
                var (m, h) = shown[i];
                DrawHighlightRow(new Rect(0f, y, w, rowH), m, h);
            }
            GUI.EndGroup();
            Ui.Text(new Rect(body.x, body.yMax - footer + 8f, body.width, 22f),
                $"Watch opens the replay with the clip's in and out points set: press R to render it.  ·  {Plugin.MarkerKey.Value} right after a kill pushes it to the top.", 11, Ui.Faint);
        }

        List<(ReplayMeta m, ReplayMeta.MetaHighlight h)> _shown = new List<(ReplayMeta, ReplayMeta.MetaHighlight)>();
        int _shownVersion = -1, _shownHl = -1, _shownFilter = -1;
        bool _shownAll;

        // the highlight list for the current tab and filter, sorted once per change rather than every frame
        List<(ReplayMeta m, ReplayMeta.MetaHighlight h)> ShownHighlights()
        {
            int hl = Highlights.Changes;
            if (_shownVersion == _listVersion && _shownHl == hl && _shownFilter == _filter && _shownAll == _allMatches) return _shown;
            _shownVersion = _listVersion; _shownHl = hl; _shownFilter = _filter; _shownAll = _allMatches;
            _shown = AllHighlights(_allMatches).Where(x => Matches(x.h, _filter)).ToList();
            return _shown;
        }

        static readonly string[] Filters = { "All", "Flicks", "Snipes", "Multi-kills", "Headshots" };
        static int _filter;

        static bool Matches(ReplayMeta.MetaHighlight h, int filter)
        {
            switch (filter)
            {
                case 1: return h.Tags.Any(t => t.StartsWith("flick ", StringComparison.Ordinal));
                case 2: return h.Tags.Any(t => t == "quickscope" || t == "no-scope") || (h.Detail ?? "").IndexOf("M2000", StringComparison.OrdinalIgnoreCase) >= 0;
                case 3: return h.Kills > 1;
                case 4: return h.Tags.Any(t => t.Contains("headshot"));
                default: return true;
            }
        }

        void DrawHighlightRow(Rect r, ReplayMeta m, ReplayMeta.MetaHighlight h)
        {
            bool hover = Ui.Hover(r);
            Ui.Fill(r, hover ? Ui.Wash(0.07f) : Ui.Wash(0.03f));
            if (hover) Ui.Fill(new Rect(r.x, r.y, 3f, r.height), Ui.Accent);

            // score badge
            var sb = new Rect(r.x + 14f, r.center.y - 20f, 54f, 40f);
            var sc = h.Score >= 6f ? Ui.Kill : h.Score >= 3f ? Ui.Accent : Ui.Dim;
            Ui.Slant(sb, new Color(sc.r, sc.g, sc.b, 0.18f));
            Ui.Text(sb, h.Score.ToString("0.0"), 20, sc, true, TextAnchor.MiddleCenter);

            // tags and details
            float tx = r.x + 84f, ty = r.y + 6f;
            var tags = h.Tags.Count > 0 ? h.Tags : new List<string> { "kill" };
            foreach (var t in tags)
            {
                var c = TagColor(t);
                string tu = t.ToUpperInvariant();
                float tw = Ui.Measure(tu, 11, true) + 26f;
                if (tx + tw > r.xMax - 120f) break;
                var tr = new Rect(tx, ty, tw, 22f);
                Ui.Slant(tr, new Color(c.r, c.g, c.b, 0.2f));
                Ui.Text(tr, tu, 11, Color.Lerp(c, Ui.Bright, 0.3f), true, TextAnchor.MiddleCenter);
                tx += tw + 6f;
            }
            Ui.Text(new Rect(r.x + 84f, r.y + 30f, r.width - 200f, 22f),
                $"{h.Detail}     {m.Scene}{(m.Take > 0 ? $" take {m.Take}" : "")} at {ReplayPlayer.Fmt(h.T0).Split('.')[0]}  ·  {m.StartUtc.ToLocalTime():ddd HH:mm}", 12, Ui.Dim);

            float ww = Ui.AutoW("Watch", Ui.IconPlay, 32f, 12);
            if (Ui.Button(new Rect(r.xMax - ww - 10f, r.center.y - 16f, ww, 32f), "Watch", false, hover ? Ui.Kind.Primary : Ui.Kind.Normal, Ui.IconPlay, 12,
                    "Open the replay at this moment with the clip range set"))
                WatchHighlight(m, h);
        }

        // opens the replay a moment before the kill, with the clip's in/out points ready for R (render)
        void WatchHighlight(ReplayMeta m, ReplayMeta.MetaHighlight h)
        {
            float a = Mathf.Max(0f, h.T0 - Plugin.HighlightPreroll.Value);
            float b = h.T1 + Plugin.HighlightPostroll.Value;
            if (m.Duration > 0f) b = Mathf.Min(b, m.Duration);
            Play(m, a, a, b);
        }

        bool LobbyFree()
        {
            var cm = FishNet.InstanceFinder.ClientManager;
            if ((SteamLobby.Instance != null && (SteamLobby.Instance.inSteamLobby || SteamLobby.Instance.findingQuickMatchLobby)) || (cm != null && cm.Started))
            {
                _notice = "Leave your lobby (or stop the quick match search) first: a match starting would load over the replay.";
                return false;
            }
            return true;
        }


        static string MatchId(ReplayMeta m) =>
            string.IsNullOrEmpty(m.SessionId) ? m.StartUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : m.SessionId;

        void Play(ReplayMeta m, float t, float? inPoint = null, float? outPoint = null)
        {
            if (!LobbyFree()) return;
            _notice = null;
            _open = false;
            ReplayPlayer.Instance.Open(m, t, inPoint, outPoint);
        }
    }
}
