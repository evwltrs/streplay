using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Streplay
{
    // Drives the game's own HUD objects from a recording while watching from the recording
    // player's eyes. Everything on those canvases that wasn't recorded is hidden for the duration
    // and restored afterwards.
    internal sealed class HudReplay
    {
        // HUD pieces that can be switched off for clean renders, matched by the recorded path
        internal static readonly (string key, string label, string[] paths)[] Parts =
        {
            ("Crosshair", "Crosshair", new[] { "---USER INTERFACE---/Crosshair" }),
            ("Hitmarkers", "Hitmarkers", new[] { "HitMarker" }),
            ("KillFeed", "Kill feed", new[] { "/LogsPosition/" }),
            ("Ammo", "Ammo", new[] { "/GUN_RIGHT", "/GUN_LEFT", "Ammo" }),
            ("Health", "Health", new[] { "/HealthDisplay/text", "Health" }),
            ("Speed", "Speed", new[] { "/Speedometer" }),
            ("FpsPing", "FPS / ping", new[] { "/HealthDisplay/fps", "/HealthDisplay/ping" }),
            ("RoundText", "Round text", new[] { "--START ROUND", "--END ROUND", "/START ROUND TEXT" }),
            ("Chat", "Text chat", new[] { "/ChatText/" }),   // off by default: Steam avatars can't be restored
        };
        internal static bool PartDefault(string key) => key != "Chat";

        static int PartOf(string path)
        {
            // specific groups before the generic name matches (HealthEnemyText is round text, HealthDisplay/fps is fps)
            foreach (int i in Enumerable.Range(0, Parts.Length).OrderBy(i => Parts[i].key == "RoundText" ? 0 : Parts[i].key == "FpsPing" ? 1 : 2))
                foreach (var p in Parts[i].paths)
                    if (path.IndexOf(p, StringComparison.Ordinal) >= 0 && !(i == 0 && path.Contains("ScopeCrosshair"))) return i;
            return -1;
        }

        sealed class Bound
        {
            public int Part = -1;       // index into Parts, -1 = always shown
            public UiElementInfo Info;
            public Component C;
            public Canvas RootCanvas;   // dynamic elements only
            public string FitFor;       // rebuilt text: the text/width its font size was last fitted to
            public int LogBirth = -1;   // kill feed line pieces in pre-v7 recordings: frame the line appeared
            public Vector2 LogPos;      // and where it appeared
            public List<Transform> Collapsed; // unrecorded parents that are scaled to (almost) zero right now
            public List<UiElementInfo> Groups;  // rebuilt pieces: the recorded CanvasGroups above them (kill feed lines fade out through one)
        }

        // MatchLogs moves every visible line up 35 units when a new one arrives. Before v7 the recorder dropped
        // moves that small for spawned pieces, so every line would sit in the newest line's slot.
        const string LogPath = "/LogsPosition/LogPrefab(Clone)";
        const float LogLineStep = 35f;
        readonly List<int> _logBirths = new List<int>();

        readonly List<Bound> _bound = new List<Bound>();
        readonly List<GameObject> _created = new List<GameObject>();
        Dictionary<string, TMP_FontAsset> _fonts;
        readonly List<GameObject> _activated = new List<GameObject>();
        readonly List<Behaviour> _enabled = new List<Behaviour>();
        readonly List<Graphic> _suppressed = new List<Graphic>();

        // The HUD objects we drive are the game's own and live on after the replay (DontDestroyOnLoad), and nothing in
        // the game turns them back on, so everything Apply touches is put back exactly as it was in Hide.
        sealed class Snap
        {
            public Component C;
            public bool Enabled;
            public float Alpha;
            public Color Color;
            public Sprite Sprite;
            public float Fill;
            public string Text;
            public TMP_SpriteAsset SpriteAsset;
            public Vector2 Pos, Size;
            public Vector3 Scale;
            public Quaternion Rot;
        }
        readonly List<Snap> _snaps = new List<Snap>();

        void TakeSnapshots()
        {
            _snaps.Clear();
            foreach (var b in _bound)
            {
                if (b.C == null || b.Info.Dynamic) continue; // dynamic pieces are our own objects
                var sn = new Snap { C = b.C };
                if (b.C is CanvasGroup cg) sn.Alpha = cg.alpha;
                else if (b.C is Graphic g)
                {
                    sn.Enabled = g.enabled;
                    sn.Color = g.color;
                    if (g is Image img) { sn.Sprite = img.sprite; sn.Fill = img.fillAmount; }
                    if (g is TMP_Text tmp) { sn.Text = tmp.text; sn.SpriteAsset = tmp.spriteAsset; }
                    else if (g is Text txt) sn.Text = txt.text;
                    var rt = g.rectTransform;
                    sn.Pos = rt.anchoredPosition; sn.Size = rt.sizeDelta; sn.Scale = rt.localScale; sn.Rot = rt.localRotation;
                }
                else continue;
                _snaps.Add(sn);
            }
        }

        void RestoreSnapshots()
        {
            foreach (var sn in _snaps)
            {
                try
                {
                    if (sn.C == null) continue;
                    if (sn.C is CanvasGroup cg) { cg.alpha = sn.Alpha; continue; }
                    var g = (Graphic)sn.C;
                    g.color = sn.Color;
                    if (g is Image img) { img.sprite = sn.Sprite; img.fillAmount = sn.Fill; }
                    if (g is TMP_Text tmp) { tmp.spriteAsset = sn.SpriteAsset; if (tmp.text != sn.Text) tmp.text = sn.Text; }
                    else if (g is Text txt) txt.text = sn.Text;
                    var rt = g.rectTransform;
                    rt.anchoredPosition = sn.Pos; rt.sizeDelta = sn.Size; rt.localScale = sn.Scale; rt.localRotation = sn.Rot;
                    g.enabled = sn.Enabled;
                }
                catch (Exception e) { Plugin.Log.LogDebug($"HUD restore: {e.Message}"); }
            }
            _snaps.Clear();
        }
        readonly List<(CanvasGroup g, float a)> _groupAlpha = new List<(CanvasGroup, float)>();
        readonly List<(Transform t, Vector3 scale)> _expanded = new List<(Transform, Vector3)>();
        Dictionary<string, Sprite> _sprites;
        ReplayData _data;
        readonly List<Canvas> _roots = new List<Canvas>();
        readonly List<(Canvas c, RenderMode mode, Camera cam, float plane, CanvasScaler scaler, bool scalerOn, float scale)> _moved =
            new List<(Canvas, RenderMode, Camera, float, CanvasScaler, bool, float)>();
        Camera _renderCam;
        readonly List<Renderer> _hiddenRenderers = new List<Renderer>();

        public bool Shown { get; private set; }
        public bool UsesCanvas(Canvas c) => Shown && _roots.Contains(c);
        public bool Available => _bound.Count > 0;

        public void Bind(ReplayData data, Transform localPlayer)
        {
            _spriteAssetFor.Clear(); // a kill feed icon mod may have built its sheet since the last replay
            Hide();
            Unbind();
            _data = data;
            if (data.Ui.Count == 0) return;
            var ddolRoots = Net.DdolScene().GetRootGameObjects();
            int missing = 0, created = 0;
            var missingPaths = new List<string>();
            foreach (var el in data.Ui.Values)
            {
                // TMP's own sub-mesh for sprites/fallback fonts: the rebuilt text makes its own, and rebuilt as an
                // Image it would be a white box (kill feed icon mods)
                if (IsTmpSubMesh(el.Path)) continue;
                // the proximity voice chat's "who's talking" tags: not part of the moment (voice isn't recorded), and
                // rebuilt they lose their background and show as a white box (recordings made before the recorder skipped them)
                if (el.Path.IndexOf("/VoiceChatIndicator/", StringComparison.Ordinal) >= 0) continue;
                // and your own "transmitting voice" note, bottom right
                if (el.Path.IndexOf("--PopupWriting--/VoiceText", StringComparison.Ordinal) >= 0) continue;
                // fades of spawned pieces (a kill feed line's CanvasGroup): the rebuilt pieces take them from the recording
                // (LinkGroups). Bound by path they'd drive whatever copy the live game has left over, or, right after
                // launching the game when it has none, just count as not found.
                if (el.Kind == UiKind.Group && el.Path.IndexOf("(Clone)", StringComparison.Ordinal) >= 0) continue;
                if (el.Dynamic)
                {
                    var made = Create(el, localPlayer, ddolRoots);
                    if (made.c != null) { _bound.Add(new Bound { Info = el, C = made.c, RootCanvas = made.canvas }); created++; }
                    else missing++;
                    continue;
                }
                var t = Resolve(el.Anchor, el.Path, localPlayer, ddolRoots);
                Component c = null;
                if (t != null) c = el.Kind == UiKind.Group ? (Component)t.GetComponent<CanvasGroup>() : t.GetComponent<Graphic>();
                if (c == null) { missing++; if (missingPaths.Count < 60) missingPaths.Add(el.Path); continue; }
                _bound.Add(new Bound { Info = el, C = c });
            }
            foreach (var b in _bound) b.Part = PartOf(b.Info.Path);
            LinkGroups(data);
            _logBirths.Clear();
            if (data.Header.Version < 7)
            {
                foreach (var b in _bound)
                {
                    if (!b.Info.Dynamic || b.Info.Path.IndexOf(LogPath, StringComparison.Ordinal) < 0) continue;
                    int k = b.Info.States.FindIndex(st => st.Visible);
                    if (k < 0) continue;
                    b.LogBirth = b.Info.Frames[k];
                    b.LogPos = b.Info.States[k].Pos;
                    if (b.Info.Kind == UiKind.Graphic && b.Info.Path.EndsWith(LogPath + "/Panel (1)", StringComparison.Ordinal)) _logBirths.Add(b.LogBirth);
                }
                _logBirths.Sort();
            }
            Plugin.Log.LogDebug($"HUD: {_bound.Count} elements bound ({created} rebuilt)" + (missing > 0 ? $", {missing} not found" : "") +
                               (_logBirths.Count > 0 ? $", {_logBirths.Count} kill feed lines restacked" : ""));
            if (missingPaths.Count > 0) Plugin.Log.LogDebug("HUD not found: " + string.Join(" | ", missingPaths.Distinct()));
        }

        static Transform Resolve(byte anchor, string path, Transform localPlayer, GameObject[] ddolRoots)
        {
            if (anchor == HudCapture.AnchorLocalPlayer)
                return localPlayer != null ? (string.IsNullOrEmpty(path) ? localPlayer : localPlayer.Find(path)) : null;
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            foreach (var root in ddolRoots)
            {
                if (root.name != rootName) continue;
                var t = slash < 0 ? root.transform : root.transform.Find(path.Substring(slash + 1));
                if (t != null) return t;
            }
            return null;
        }

        // Rebuild a HUD piece the game spawned at runtime (e.g. a hitmarker) on the same canvas.
        (Component c, Canvas canvas) Create(UiElementInfo el, Transform localPlayer, GameObject[] ddolRoots)
        {
            // walk up the recorded path until something exists, then use its root canvas
            string p = el.Path;
            Transform anchor = null;
            while (anchor == null && p.Length > 0)
            {
                int cut = p.LastIndexOf('/');
                p = cut < 0 ? "" : p.Substring(0, cut);
                if (p.Length > 0 || el.Anchor == HudCapture.AnchorLocalPlayer) anchor = Resolve(el.Anchor, p, localPlayer, ddolRoots);
            }
            var canvas = anchor != null ? anchor.GetComponentInParent<Canvas>(true) : null;
            if (canvas == null) return (null, null);
            canvas = canvas.rootCanvas;

            var go = new GameObject("Streplay " + el.Path.Substring(el.Path.LastIndexOf('/') + 1), typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            go.transform.SetAsLastSibling();
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            _created.Add(go);
            Graphic g;
            if (el.Kind == UiKind.Text)
            {
                var tmp = go.AddComponent<TextMeshProUGUI>();
                var font = FindFont(el.Font);
                if (font != null) tmp.font = font;
                tmp.fontSize = el.FontSize;
                tmp.alignment = (TextAlignmentOptions)el.Alignment;
                tmp.fontStyle = (FontStyles)el.FontStyle;
                g = tmp;
            }
            else
            {
                var img = go.AddComponent<Image>();
                img.type = (Image.Type)el.ImageType;
                img.preserveAspect = el.PreserveAspect;
                g = img;
            }
            g.raycastTarget = false;
            g.enabled = false;
            return (g, canvas);
        }

        void Unbind()
        {
            foreach (var go in _created) if (go != null) UnityEngine.Object.Destroy(go);
            _created.Clear();
            _bound.Clear();
        }

        public void Dispose()
        {
            Hide();
            Unbind();
        }

        public void Show()
        {
            if (Shown || _bound.Count == 0) return;
            Shown = true;
            TakeSnapshots();
            var keep = new HashSet<Component>(_bound.Where(b => b.C != null).Select(b => b.C));
            _keep = keep;
            var roots = new HashSet<Canvas>();
            foreach (var b in _bound)
            {
                if (b.C == null) continue; // destroyed since binding (e.g. by the game's own UI code)
                for (var t = b.C.transform; t != null; t = t.parent)
                {
                    if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); _activated.Add(t.gameObject); }
                    var cv = t.GetComponent<Canvas>();
                    if (cv != null)
                    {
                        if (!cv.enabled) { cv.enabled = true; _enabled.Add(cv); }
                        if (cv.isRootCanvas) { roots.Add(cv); break; }
                    }
                }
            }
            _roots.Clear();
            _roots.AddRange(roots);
            FindCollapsedParents(keep);
            if (_renderCam != null) MoveToCamera();
            // hide everything else on those canvases (menus, popups, the other HUD variant...)
            foreach (var root in roots)
            {
                foreach (var g in root.GetComponentsInChildren<Graphic>(true))
                    if (g.enabled && !keep.Contains(g) && !(g is TMP_SubMeshUI)) { g.enabled = false; _suppressed.Add(g); }
                foreach (var cg in root.GetComponentsInChildren<CanvasGroup>(true))
                    if (!keep.Contains(cg)) _groupAlpha.Add((cg, cg.alpha));
            }
            // a parent group at alpha 0 would hide recorded elements
            foreach (var (cg, _) in _groupAlpha) if (cg != null) cg.alpha = 1f;
        }

        public void Hide()
        {
            if (!Shown) return;
            Shown = false;
            _keep = null;
            RestoreFromCamera();
            _roots.Clear();
            foreach (var (t, scale) in _expanded) if (t != null) t.localScale = scale;
            _expanded.Clear();
            foreach (var (cg, a) in _groupAlpha) if (cg != null) cg.alpha = a;
            _groupAlpha.Clear();
            foreach (var g in _suppressed) if (g != null) g.enabled = true;
            _suppressed.Clear();
            RestoreSnapshots();
            foreach (var b in _enabled) if (b != null) b.enabled = false;
            _enabled.Clear();
            for (int i = _activated.Count - 1; i >= 0; i--) if (_activated[i] != null) _activated[i].SetActive(false);
            _activated.Clear();
        }

        // Containers the recorder never saw (no Graphic of their own) can be left at a tiny scale by the game, e.g. the
        // end-of-round scoreboard after a match. Their recorded children were hidden by that live; see Apply.
        void FindCollapsedParents(HashSet<Component> keep)
        {
            foreach (var b in _bound)
            {
                b.Collapsed = null;
                if (b.C == null || b.Info.Dynamic) continue;
                for (var t = b.C.transform.parent; t != null; t = t.parent)
                {
                    if (t.GetComponent<Canvas>() is Canvas cv && cv.isRootCanvas) break;
                    if (t.GetComponent<Graphic>() is Graphic g && keep.Contains(g)) continue; // its scale is recorded
                    var s = t.localScale;
                    if (Mathf.Abs(s.x) < 0.05f || Mathf.Abs(s.y) < 0.05f) (b.Collapsed ??= new List<Transform>()).Add(t);
                }
            }
            var hit = _bound.Where(b => b.Collapsed != null).ToList();
            if (hit.Count > 0)
                Plugin.Log.LogDebug($"HUD: {hit.Count} recorded elements sit in collapsed containers (" +
                                   string.Join(", ", hit.SelectMany(b => b.Collapsed).Distinct().Take(4).Select(t => $"{t.name} scale {t.localScale.x:0.###}")) + ")" +
                                   (_data.Header.Version < 7 ? ", kept hidden" : ""));
        }

        // The game's HUD canvases are screen overlays, which never reach a render texture. While rendering they're
        // drawn by cam instead (Screen Space - Camera, scaled for its target size); null puts them back.
        public void SetRenderCamera(Camera cam)
        {
            if (cam == _renderCam) return;
            RestoreFromCamera();
            _renderCam = cam;
            if (cam != null && Shown) MoveToCamera();
        }

        static bool _materialsLogged, _renderersListed;
        internal static bool ProbeOnRender;

        // debugging: every drawn UI graphic (recorded or not) on the HUD canvases that covers the screen centre
        internal void ProbeCentre(string when)
        {
            var keep = new HashSet<Component>(_bound.Where(b => b.C != null).Select(b => b.C));
            foreach (var root in _roots)
            {
                if (root == null) continue;
                var cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
                var target = cam != null && cam.targetTexture != null ? new Vector2(cam.targetTexture.width, cam.targetTexture.height) / 2f : new Vector2(Screen.width, Screen.height) / 2f;
                foreach (var g in root.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.enabled || g.canvasRenderer.cull || g.canvasRenderer.GetInheritedAlpha() <= 0.001f || g.color.a <= 0.001f) continue;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, target, cam)) continue;
                    var tex = g.mainTexture;
                    var c = g.canvas;
                    Plugin.Log.LogDebug($"[probe {when}] {Recorder.ScenePath(g.transform)} {g.GetType().Name} recorded={keep.Contains(g)} rect={g.rectTransform.rect.size} lossy={g.transform.lossyScale.x:0.####} " +
                                       $"tex={(tex != null ? tex.name + " " + tex.width + "x" + tex.height : "-")} canvas={(c != null ? c.name + " " + c.renderMode + " order " + c.sortingOrder : "-")}");
                }
            }
        }

        void MoveToCamera()
        {
            if (ProbeOnRender) ProbeCentre("render");
            if (!_materialsLogged)
            {
                _materialsLogged = true;
                foreach (var b in _bound)
                {
                    if (!(b.C is Graphic g) || g == null) continue;
                    var m = g.materialForRendering;
                    bool custom = m != null && m != Graphic.defaultGraphicMaterial;
                    if (!custom && b.Info.Path.IndexOf("rosshair", StringComparison.Ordinal) < 0) continue;
                    var img = g as Image;
                    Plugin.Log.LogDebug($"[hud] {b.Info.Path}: material={(m != null ? m.name : "null")} shader={(m != null && m.shader != null ? m.shader.name : "null")} " +
                                       $"sprite={(img != null && img.sprite != null ? $"{img.sprite.name} tex={img.sprite.texture?.name} rect={img.sprite.textureRect} packed={img.sprite.packed}" : "-")} " +
                                       $"type={(img != null ? img.type.ToString() : "-")} size={g.rectTransform.rect.size}");
                }
            }
            var target = _renderCam.targetTexture;
            float w = target != null ? target.width : _renderCam.pixelWidth, h = target != null ? target.height : _renderCam.pixelHeight;
            foreach (var c in _roots)
            {
                if (c == null || c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                var scaler = c.GetComponent<CanvasScaler>();
                _moved.Add((c, c.renderMode, c.worldCamera, c.planeDistance, scaler, scaler != null && scaler.enabled, c.scaleFactor));
                float scale = ScaleFor(c, scaler, w, h);
                if (scaler != null) scaler.enabled = false; // it would size the canvas from the window
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = _renderCam;
                c.planeDistance = 5000f; // deep enough for HUD pieces tilted in 3D (the ammo counter) to stay inside the clip range
                c.scaleFactor = scale;
                // 3D objects in the UI hierarchy (the menus' 3D map window...) never show on an overlay canvas, but in
                // front of the render camera they would, as a tiny picture behind the crosshair
                foreach (var r in c.GetComponentsInChildren<Renderer>(false))
                    if (r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }
            }
            if (!_renderersListed)
            {
                _renderersListed = true;
                var all = _moved.SelectMany(m => m.c.GetComponentsInChildren<Renderer>(true)).ToList();
                Plugin.Log.LogDebug($"[render] 3D renderers in the HUD canvases ({all.Count}): " +
                                   string.Join(", ", all.Take(12).Select(r => $"{Recorder.ScenePath(r.transform)} {r.GetType().Name} on={r.enabled && r.gameObject.activeInHierarchy}")));
            }
            if (_hiddenRenderers.Count > 0)
                Plugin.Log.LogDebug($"[render] hid {_hiddenRenderers.Count} 3D renderers inside the HUD canvases: " +
                                   string.Join(", ", _hiddenRenderers.Take(6).Select(r => $"{r.name} ({r.GetType().Name})")));
        }

        void RestoreFromCamera()
        {
            foreach (var (c, mode, cam, plane, scaler, scalerOn, scale) in _moved)
            {
                if (c == null) continue;
                c.renderMode = mode;
                c.worldCamera = cam;
                c.planeDistance = plane;
                c.scaleFactor = scale;
                if (scaler != null) scaler.enabled = scalerOn;
            }
            _moved.Clear();
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenRenderers.Clear();
        }

        // What the canvas scaler would pick for a w x h screen, so the HUD has the same proportions as on screen.
        static float ScaleFor(Canvas c, CanvasScaler s, float w, float h)
        {
            if (s == null || !s.enabled) return c.scaleFactor * h / Mathf.Max(1, Screen.height);
            switch (s.uiScaleMode)
            {
                case CanvasScaler.ScaleMode.ScaleWithScreenSize:
                {
                    var r = s.referenceResolution;
                    switch (s.screenMatchMode)
                    {
                        case CanvasScaler.ScreenMatchMode.Expand: return Mathf.Min(w / r.x, h / r.y);
                        case CanvasScaler.ScreenMatchMode.Shrink: return Mathf.Max(w / r.x, h / r.y);
                        default:
                            float lw = Mathf.Log(w / r.x, 2), lh = Mathf.Log(h / r.y, 2);
                            return Mathf.Pow(2, Mathf.Lerp(lw, lh, s.matchWidthOrHeight));
                    }
                }
                default: // constant pixel / physical size: keep the on-screen proportion
                    return c.scaleFactor * h / Mathf.Max(1, Screen.height);
            }
        }

        float _nextProbe;

        // Called every LateUpdate while shown so the game's own HUD scripts can't override the frame.
        public void Apply(int frame)
        {
            if (!Shown) return;
            if (ProbeOnRender && _renderCam != null && Time.realtimeSinceStartup > _nextProbe)
            {
                _nextProbe = Time.realtimeSinceStartup + 1f;
                foreach (var b in _bound)
                {
                    if (!(b.C is Image im) || im == null || b.Info.Path.IndexOf("/Crosshair", StringComparison.Ordinal) < 0) continue;
                    var sp = im.sprite;
                    Plugin.Log.LogDebug($"[xhair] frame {frame} sprite={(sp != null ? $"{sp.name}#{sp.GetInstanceID()} rect={sp.rect} tex={sp.texture?.name}" : "null")} " +
                                       $"override={(im.overrideSprite != null ? im.overrideSprite.name + "#" + im.overrideSprite.GetInstanceID() : "-")} type={im.type} " +
                                       $"size={im.rectTransform.rect.size} scale={im.rectTransform.localScale} mat={im.materialForRendering?.name} canvas={im.canvas?.renderMode}");
                }
            }
            SuppressStrays(false);
            foreach (var b in _bound)
            {
                var s = StateAt(b.Info, frame);
                if (b.C == null) continue;
                if (b.Info.Kind == UiKind.Group)
                {
                    ((CanvasGroup)b.C).alpha = s != null && s.Visible ? s.Alpha : 0f;
                    continue;
                }
                var g = (Graphic)b.C;
                bool vis = s != null && s.Visible && (b.Part < 0 || Plugin.HudShow[b.Part].Value);
                if (vis && b.Collapsed != null)
                {
                    // before v7 a recorded "visible" can't be told apart from "hidden inside a collapsed container",
                    // so it stays hidden; from v7 on it really was on screen, so the container opens up
                    if (_data.Header.Version < 7) vis = false;
                    else
                        foreach (var t in b.Collapsed)
                            if (t != null && t.localScale != Vector3.one) { _expanded.Add((t, t.localScale)); t.localScale = Vector3.one; }
                }
                if (g.enabled != vis) g.enabled = vis;
                if (!vis) continue;
                Color col = s.Color;
                if (b.Groups != null)
                {
                    foreach (var ge in b.Groups)
                    {
                        var gs = StateAt(ge, frame);
                        col.a *= gs != null && gs.Visible ? gs.Alpha : 0f;
                    }
                    if (col.a <= 0.002f) { g.enabled = false; continue; }
                }
                g.color = col;
                if (g is Image img)
                {
                    if (s.Sprite >= 0 && _data.UiStrings.TryGetValue(s.Sprite, out var spriteName))
                    {
                        var sp = FindSprite(spriteName);
                        if (sp != null && img.sprite != sp) img.sprite = sp;
                    }
                    if (img.type == Image.Type.Filled) img.fillAmount = s.Fill;
                }
                if (s.Text != null)
                {
                    if (g is TMP_Text tmp)
                    {
                        if (tmp.text != s.Text)
                        {
                            // icons from kill feed mods (<sprite name=...>) need the mod's sprite sheet, which the
                            // game only gave its own line; without it TMP draws white boxes
                            if (s.Text.IndexOf("<sprite", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var sa = SpriteAssetFor(s.Text);
                                if (sa != null && tmp.spriteAsset != sa) tmp.spriteAsset = sa;
                            }
                            tmp.text = s.Text;
                        }
                    }
                    else if (g is Text txt) { if (txt.text != s.Text) txt.text = s.Text; }
                }
                var rt = g.rectTransform;
                if (b.Info.Dynamic)
                {
                    // recorded as fractions of the screen; the piece sits centred on its root canvas, so this
                    // works for the on-screen overlay and for a render target of any size
                    var canvasRect = b.RootCanvas != null ? ((RectTransform)b.RootCanvas.transform).rect : new Rect(0, 0, Screen.width, Screen.height);
                    var pos = b.LogBirth >= 0 ? b.LogPos : s.Pos;
                    rt.anchoredPosition = new Vector2((pos.x - 0.5f) * canvasRect.width, (pos.y - 0.5f) * canvasRect.height + LogLineStep * LinesAfter(b.LogBirth, frame));
                    rt.sizeDelta = s.Size * canvasRect.width;
                    rt.localScale = Vector3.one;
                    rt.localRotation = Quaternion.Euler(0f, 0f, s.RotZ);
                    if (g is TMP_Text t) FitText(b, t);
                    continue;
                }
                rt.anchoredPosition = s.Pos;
                rt.sizeDelta = s.Size;
                rt.localScale = s.Scale;
                var e = rt.localEulerAngles;
                rt.localEulerAngles = new Vector3(e.x, e.y, s.RotZ);
            }
        }

        // A rebuilt text doesn't lay out exactly like the original (font fallback, rich text), so it can come out a
        // little narrower than the box the game sized for it and, right-aligned, leave all the slack on one side.
        // Centre it and fit its size to the recorded width so the padding is even, like in the game.
        static void FitText(Bound b, TMP_Text t)
        {
            float w = t.rectTransform.rect.width;
            string key = t.text + "|" + w.ToString("0.0");
            if (b.FitFor == key || w < 1f) return;
            b.FitFor = key;
            t.alignment = (TextAlignmentOptions)(((int)t.alignment & ~0xFF) | (int)HorizontalAlignmentOptions.Center);
            t.enableWordWrapping = false;
            t.fontSize = b.Info.FontSize;
            float pw = t.GetPreferredValues(t.text).x;
            if (pw > 1f) t.fontSize = b.Info.FontSize * Mathf.Clamp(w / pw * 0.97f, 0.8f, 1.15f);
        }

        // kill feed lines that appeared after `birth`, up to `frame` (each pushed the older ones up a line)
        int LinesAfter(int birth, int frame)
        {
            if (birth < 0) return 0;
            int n = 0;
            foreach (int f in _logBirths) if (f > birth && f <= frame) n++;
            return n;
        }

        static UiState StateAt(UiElementInfo el, int frame)
        {
            var fs = el.Frames;
            int lo = 0, hi = fs.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (fs[mid] <= frame) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found >= 0 ? el.States[found] : null;
        }

        // the loaded TMP sprite asset that has every sprite a text names (mods build theirs at runtime)
        static readonly System.Text.RegularExpressions.Regex SpriteName =
            new System.Text.RegularExpressions.Regex("<sprite\\s+name\\s*=\\s*\"?([^\">]+)\"?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        readonly Dictionary<string, TMP_SpriteAsset> _spriteAssetFor = new Dictionary<string, TMP_SpriteAsset>();

        static bool _askedIconMods;

        static void LoadIconMods()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(HudReplay).Assembly) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name != "IconLoader") continue;
                    var m = t.GetMethod("EnsureLoaded", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    try { m.Invoke(null, null); Plugin.Log.LogDebug($"[hud] loaded kill feed icons from {asm.GetName().Name}"); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[hud] {asm.GetName().Name} could not load its icons: {e.InnerException?.Message ?? e.Message}"); }
                }
            }
        }

        TMP_SpriteAsset SpriteAssetFor(string text)
        {
            var names = SpriteName.Matches(text).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
            if (names.Count == 0) return null;
            string key = string.Join("|", names);
            if (_spriteAssetFor.TryGetValue(key, out var hit)) return hit;
            TMP_SpriteAsset best = null;
            int bestCount = 0;
            void Scan()
            {
                foreach (var a in Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>())
                {
                    if (a == null || a.spriteCharacterTable == null) continue;
                    var have = new HashSet<string>(a.spriteCharacterTable.Where(c => c != null && c.name != null).Select(c => c.name), StringComparer.OrdinalIgnoreCase);
                    int count = names.Count(have.Contains);
                    if (count > bestCount) { best = a; bestCount = count; }
                }
            }
            Scan();
            // KillfeedIcons builds its sheet when the first kill line of a match appears, so a replay opened
            // from the menu has none yet: ask it to load (once)
            if (best == null && !_askedIconMods) { _askedIconMods = true; LoadIconMods(); Scan(); }
            _spriteAssetFor[key] = best;
            if (best != null) Plugin.Log.LogDebug($"[hud] kill feed icons from sprite asset '{best.name}' ({bestCount}/{names.Count} of {key})");
            else Plugin.Log.LogWarning($"[hud] no loaded sprite asset has the icons {key}");
            return best;
        }

        // A rebuilt piece sits straight on the root canvas, so the CanvasGroup fades the game applied to its spawned
        // parents (a kill feed line fading out) have to be applied to it by hand. Ids are handed out in walk order,
        // so the piece's own groups are the latest recorded before it on each parent path.
        void LinkGroups(ReplayData data)
        {
            var groups = data.Ui.Values.Where(e => e.Kind == UiKind.Group && e.Path.IndexOf("(Clone)", StringComparison.Ordinal) >= 0).OrderBy(e => e.Id).ToList();
            if (groups.Count == 0) return;
            int linked = 0;
            foreach (var b in _bound)
            {
                if (!b.Info.Dynamic || b.Info.Kind == UiKind.Group) continue;
                var byPath = new Dictionary<string, UiElementInfo>();
                foreach (var ge in groups)
                {
                    if (ge.Id >= b.Info.Id) break;
                    if (b.Info.Path == ge.Path || b.Info.Path.StartsWith(ge.Path + "/", StringComparison.Ordinal)) byPath[ge.Path] = ge;
                }
                if (byPath.Count == 0) continue;
                b.Groups = byPath.Values.ToList();
                linked++;
            }
            if (linked > 0) Plugin.Log.LogDebug($"HUD: {linked} rebuilt pieces follow their recorded group fades");
        }

        // UI the game turns on while a replay plays (a kill feed line from a replayed effect or a mod, a chat message
        // or the lobby panel from the live lobby) would sit on top of the recording, so anything on the HUD canvases
        // that isn't ours is hidden again, a few times a second.
        HashSet<Component> _keep;
        float _nextStrayCheck;

        void SuppressStrays(bool now)
        {
            if (_keep == null || (!now && Time.realtimeSinceStartup < _nextStrayCheck)) return;
            _nextStrayCheck = Time.realtimeSinceStartup + 0.2f;
            int n = 0;
            string first = null;
            foreach (var root in _roots)
            {
                if (root == null) continue;
                foreach (var g in root.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.enabled || _keep.Contains(g) || g is TMP_SubMeshUI) continue; // sub-meshes follow their text
                    g.enabled = false;
                    _suppressed.Add(g);
                    if (n++ == 0) first = g.name;
                }
            }
            if (n > 0 && !now) Plugin.Log.LogDebug($"[hud] hid {n} piece(s) of UI the game showed during the replay (e.g. {first})");
        }

        internal static bool IsTmpSubMesh(string path)
        {
            // the name itself has a slash in it ("TMP UI SubObject [TextMeshPro/Sprite]")
            return path.StartsWith("TMP UI SubObject", StringComparison.Ordinal) || path.IndexOf("/TMP UI SubObject", StringComparison.Ordinal) >= 0
                || path.StartsWith("TMP SubMesh", StringComparison.Ordinal) || path.IndexOf("/TMP SubMesh", StringComparison.Ordinal) >= 0;
        }

        TMP_FontAsset FindFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_fonts == null)
            {
                _fonts = new Dictionary<string, TMP_FontAsset>();
                foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    if (f != null && !_fonts.ContainsKey(f.name)) _fonts[f.name] = f;
            }
            return _fonts.TryGetValue(name, out var font) ? font : null;
        }

        Sprite FindSprite(string name)
        {
            if (_sprites == null)
            {
                _sprites = new Dictionary<string, Sprite>();
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (sp != null && !_sprites.ContainsKey(sp.name)) _sprites[sp.name] = sp;
            }
            return _sprites.TryGetValue(name, out var s) ? s : null;
        }
    }
}
