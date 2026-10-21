using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Streplay
{
    // The look of Streplay's own UI (replay player, render panel, browser), styled after STRAFTAT's own HUD:
    // black panels, slanted blocks, bold italic uppercase type, white for the main action and the crosshair's
    // magenta as the accent. Drawn with IMGUI, laid out for a 1080p screen and scaled up on bigger ones
    // (Begin sets GUI.matrix; W/H are the scaled screen size). Buttons size themselves from their label (AutoW)
    // so padding is the same everywhere.
    internal static class Ui
    {
        public static readonly Color Bg = new Color(0.02f, 0.02f, 0.022f, 0.96f);
        public static readonly Color Raised = new Color(0.1f, 0.1f, 0.105f, 1f);
        public static readonly Color RaisedHover = new Color(0.17f, 0.17f, 0.18f, 1f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.07f);
        public static readonly Color TextC = new Color(0.95f, 0.95f, 0.95f);
        public static readonly Color Dim = new Color(0.6f, 0.6f, 0.62f);
        public static readonly Color Faint = new Color(0.4f, 0.4f, 0.42f);
        public static readonly Color Accent = new Color(1f, 0.17f, 0.84f);
        public static readonly Color AccentSoft = new Color(1f, 0.17f, 0.84f, 0.22f);
        public static readonly Color Kill = new Color(1f, 0.58f, 0.2f);
        public static readonly Color Death = new Color(0.6f, 0.6f, 0.62f);
        public static readonly Color Good = new Color(0.45f, 0.92f, 0.55f);
        public static readonly Color Warn = new Color(1f, 0.78f, 0.3f);
        public static readonly Color Danger = new Color(1f, 0.32f, 0.32f);
        public static readonly Color OnAccent = new Color(0f, 0f, 0f);

        public const float SlantTan = 0.2126f; // tan 12°: how far every block leans
        public const float PadX = 16f;         // text padding inside buttons, each side

        public static float Scale = 1f, W = 1920f, H = 1080f;
        static Font _font;
        static bool _gameFont;
        static readonly Dictionary<int, GUIStyle> _styles = new Dictionary<int, GUIStyle>();
        static string _tip;
        static Matrix4x4 _oldMatrix;
        static GUIStyle _oldField, _field;

        public static void Begin()
        {
            EnsureFonts();
            Scale = Mathf.Max(1f, Screen.height / 1080f);
            _oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
            W = Screen.width / Scale;
            H = Screen.height / Scale;
            _tip = null;
            var skin = GUI.skin;
            _oldField = skin.textField;
            skin.textField = _field;
            skin.settings.cursorColor = Accent;
            skin.settings.selectionColor = new Color(1f, 0.17f, 0.84f, 0.35f);
        }

        public static void End()
        {
            if (_tip != null) DrawTip();
            GUI.skin.textField = _oldField;
            GUI.matrix = _oldMatrix;
        }

        // STRAFTAT's own HUD font (Century Gothic Bold Italic) when the game ships it, else Bahnschrift slanted
        static void EnsureFonts()
        {
            if (_font != null) return;
            _font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f != null && f.dynamic && f.name.IndexOf("GOTHICBI", System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (_font == null)
                foreach (var tmp in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>())
                    if (tmp != null && tmp.name.IndexOf("GOTHICBI", System.StringComparison.OrdinalIgnoreCase) >= 0 && tmp.sourceFontFile != null && tmp.sourceFontFile.dynamic)
                    { _font = tmp.sourceFontFile; break; }
            _gameFont = _font != null;
            if (_font == null) _font = Font.CreateDynamicFontFromOSFont(new[] { "Bahnschrift", "Segoe UI", "Arial" }, 14);
            Plugin.Log.LogInfo($"[ui] font: {_font.name}{(_gameFont ? " (the game's)" : "")}");
            _field = new GUIStyle { font = _font, fontSize = 13, padding = new RectOffset(10, 10, 4, 4), alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, fontStyle = _gameFont ? FontStyle.Normal : FontStyle.Italic };
            _field.normal.textColor = TextC;
            _field.focused.textColor = Color.white;
            _field.hover.textColor = Color.white;
        }

        // ---------------------------------------------------------------- text

        public static GUIStyle Style(int size, Color color, bool bold = false, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false)
        {
            int key = size * 397 ^ (bold ? 1 : 0) ^ ((int)anchor << 8) ^ (wrap ? 1 << 12 : 0) ^ color.GetHashCode() * 31;
            if (!_styles.TryGetValue(key, out var s))
            {
                EnsureFonts();
                // the game's font is already bold italic; the fallback gets its slant from the style
                var fs = _gameFont ? FontStyle.Normal : bold ? FontStyle.BoldAndItalic : FontStyle.Italic;
                s = new GUIStyle { font = _font, fontSize = size, fontStyle = fs, alignment = anchor, wordWrap = wrap, clipping = TextClipping.Clip, richText = false };
                s.normal.textColor = color;
                _styles[key] = s;
            }
            return s;
        }

        public static void Text(Rect r, string s, int size = 13, Color? color = null, bool bold = false, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = false) =>
            GUI.Label(r, s, Style(size, color ?? TextC, bold, anchor, wrap));

        public static float Measure(string s, int size = 13, bool bold = false) => Style(size, TextC, bold).CalcSize(new GUIContent(s)).x;

        public static void Heading(Rect r, string s) => Text(r, s.ToUpperInvariant(), 12, Faint, true);

        // width a button needs for its label (and icon), with the standard padding and room for the slant
        public static float AutoW(string label, Texture icon = null, float h = 34f, int size = 13)
        {
            float text = string.IsNullOrEmpty(label) ? 0f : Measure(label.ToUpperInvariant(), size, true);
            float ico = icon != null ? 16f + (text > 0f ? 8f : 0f) : 0f;
            return Mathf.Ceil(text + ico + PadX * 2f + h * SlantTan);
        }

        // ---------------------------------------------------------------- shapes

        public static void Fill(Rect r, Color c, float radius = 0f)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }

        public static void Border(Rect r, Color c, float width = 1f, float radius = 0f)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, width, radius);
        }

        // a block leaning like italic type, drawn as two anti-aliased edge wedges and a plain middle, so it lines up
        // exactly wherever it's drawn (inside groups too) and keeps the same lean at any size
        public static void Slant(Rect r, Color c, float lean = SlantTan)
        {
            if (Event.current.type != EventType.Repaint || c.a <= 0f) return;
            EnsureWedges();
            float w = Mathf.Min(r.height * lean, r.width * 0.5f);
            if (w < 0.5f) { GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, 0f, 0f); return; }
            GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), _wedgeL, ScaleMode.StretchToFill, true, 0f, c, 0f, 0f);
            if (r.width > w * 2f) GUI.DrawTexture(new Rect(r.x + w, r.y, r.width - w * 2f, r.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, 0f, 0f);
            GUI.DrawTexture(new Rect(r.xMax - w, r.y, w, r.height), _wedgeR, ScaleMode.StretchToFill, true, 0f, c, 0f, 0f);
        }

        static Texture2D _wedgeL, _wedgeR;

        // the slanted ends: left = lower-right triangle, right = upper-left triangle (texture y points up)
        static void EnsureWedges()
        {
            if (_wedgeL != null) return;
            _wedgeL = Wedge((u, v) => u >= v);
            _wedgeR = Wedge((u, v) => u <= v);
        }

        static Texture2D Wedge(System.Func<float, float, bool> inside)
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int py = 0; py < n; py++)
                for (int px = 0; px < n; px++)
                {
                    int hits = 0;
                    for (int k = 0; k < 16; k++)
                        if (inside((px + (k % 4 + 0.5f) / 4f) / n, (py + (k / 4 + 0.5f) / 4f) / n)) hits++;
                    t.SetPixel(px, py, new Color(1f, 1f, 1f, hits / 16f));
                }
            t.Apply();
            return t;
        }

        public static void Circle(Vector2 centre, float radius, Color c) => Fill(new Rect(centre.x - radius, centre.y - radius, radius * 2f, radius * 2f), c, radius);

        // black panel with a slanted magenta tab on its top-left corner
        public static void Panel(Rect r, float radius = 0f)
        {
            Fill(new Rect(r.x + 3f, r.y + 4f, r.width, r.height), new Color(0f, 0f, 0f, 0.35f));
            Fill(r, Bg);
            Border(r, Line, 1f);
            Slant(new Rect(r.x + 10f, r.y - 2f, 46f, 4f), Accent, 1f);
        }

        public static bool Hover(Rect r) => GUI.enabled && r.Contains(Event.current.mousePosition);

        public static void Tip(Rect r, string tip)
        {
            if (tip != null && Hover(r)) _tip = tip;
        }

        static void DrawTip()
        {
            var m = Event.current.mousePosition;
            float w = Measure(_tip, 12) + 24f;
            var r = new Rect(Mathf.Min(m.x + 14f, W - w - 6f), m.y + 20f, w, 26f);
            if (r.yMax > H - 4f) r.y = m.y - 32f;
            Slant(r, new Color(0f, 0f, 0f, 0.97f));
            Fill(new Rect(r.x + 4f, r.yMax - 2f, r.width - 8f, 2f), Accent);
            Text(new Rect(r.x + 12f, r.y, r.width, r.height), _tip, 12, TextC);
        }

        // ---------------------------------------------------------------- controls

        public enum Kind { Normal, Accent, Primary, Ghost, Danger }

        // A slanted button; click handling is IMGUI's (fires on mouse up inside).
        public static bool Button(Rect r, string label, bool on = false, Kind kind = Kind.Normal, Texture icon = null, int size = 13, string tip = null)
        {
            bool hover = Hover(r), down = hover && Input.GetMouseButton(0);
            Color bg, fg;
            if (!GUI.enabled) { bg = new Color(0.08f, 0.08f, 0.085f, 1f); fg = Faint; }
            else switch (kind)
            {
                case Kind.Accent:
                    bg = down ? Accent * 0.85f : hover ? Color.Lerp(Accent, Color.white, 0.2f) : Accent; bg.a = 1f; fg = OnAccent; break;
                case Kind.Primary:
                    bg = down ? new Color(0.8f, 0.8f, 0.8f) : hover ? Color.white : new Color(0.92f, 0.92f, 0.92f); fg = OnAccent; break;
                case Kind.Danger:
                    bg = hover ? new Color(0.35f, 0.08f, 0.08f, 1f) : Raised; fg = hover ? Color.white : new Color(1f, 0.55f, 0.55f); break;
                case Kind.Ghost:
                    bg = hover ? Raised : new Color(0f, 0f, 0f, 0f); fg = hover ? Color.white : Dim; break;
                default:
                    bg = on ? new Color(0.92f, 0.92f, 0.92f) : down ? new Color(0.22f, 0.22f, 0.23f) : hover ? RaisedHover : Raised;
                    fg = on ? OnAccent : TextC;
                    break;
            }
            Slant(r, bg);
            if (on && kind == Kind.Normal) Fill(new Rect(r.x + r.height * SlantTan * 0.5f + 4f, r.yMax - 3f, r.width - r.height * SlantTan - 8f, 3f), Accent);
            string text = string.IsNullOrEmpty(label) ? "" : label.ToUpperInvariant();
            if (icon != null)
            {
                float s = Mathf.Min(16f, r.height - 12f);
                float textW = text.Length == 0 ? 0f : Measure(text, size, true) + 8f;
                float x = r.x + (r.width - s - textW) / 2f;
                Icon(new Rect(x, r.y + (r.height - s) / 2f, s, s), icon, fg);
                if (textW > 0f) Text(new Rect(x + s + 8f, r.y, textW + 4f, r.height), text, size, fg, true);
            }
            else if (text.Length > 0) Text(r, text, size, fg, true, TextAnchor.MiddleCenter);
            Tip(r, tip);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // a row of options as separate slanted blocks; the widths follow the labels and fill r
        public static int Segmented(Rect r, string[] labels, int selected, float[] weights = null, int size = 12)
        {
            const float gap = 3f;
            var widths = labels.Select(l => Measure(l.ToUpperInvariant(), size, true) + 22f + r.height * SlantTan).ToArray();
            float total = widths.Sum(), free = r.width - gap * (labels.Length - 1);
            float extra = Mathf.Max(0f, free - total) / labels.Length; // spread any spare room evenly
            float scale = total > free ? free / total : 1f;
            float x = r.x;
            int result = selected;
            for (int i = 0; i < labels.Length; i++)
            {
                float w = widths[i] * scale + extra;
                var seg = new Rect(x, r.y, w, r.height);
                bool on = i == selected, hover = Hover(seg);
                Slant(seg, on ? new Color(0.92f, 0.92f, 0.92f) : hover ? RaisedHover : Raised);
                if (on) Fill(new Rect(seg.x + seg.height * SlantTan * 0.5f + 3f, seg.yMax - 3f, seg.width - seg.height * SlantTan - 6f, 3f), Accent);
                Text(seg, labels[i].ToUpperInvariant(), size, on ? OnAccent : hover ? Color.white : TextC, true, TextAnchor.MiddleCenter);
                if (GUI.Button(seg, GUIContent.none, GUIStyle.none)) result = i;
                x += w + gap;
            }
            return result;
        }

        // on/off toggle block (HUD pieces, angles): on = lit with a magenta bar, off = dark and dim
        public static bool Chip(Rect r, string label, bool on, string tip = null)
        {
            bool hover = Hover(r);
            Slant(r, on ? (hover ? RaisedHover : new Color(0.14f, 0.14f, 0.15f, 1f)) : hover ? Raised : new Color(0.06f, 0.06f, 0.065f, 1f));
            Slant(new Rect(r.x + 6f, r.y + 5f, 6f, r.height - 10f), on ? Accent : new Color(0.3f, 0.3f, 0.32f));
            Text(new Rect(r.x + 20f, r.y, r.width - 24f, r.height), label.ToUpperInvariant(), 11, on ? Color.white : Faint, true);
            Tip(r, tip);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        public static string Field(Rect r, string value, int maxLength = 200)
        {
            Slant(r, new Color(0.07f, 0.07f, 0.075f, 1f));
            Fill(new Rect(r.x + r.height * SlantTan * 0.5f, r.yMax - 2f, r.width - r.height * SlantTan, 2f), Hover(r) ? Accent : new Color(1f, 1f, 1f, 0.15f));
            return GUI.TextField(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), value ?? "", maxLength, _field);
        }

        public static void Bar(Rect r, float fraction, Color c)
        {
            Slant(r, new Color(1f, 1f, 1f, 0.08f));
            if (fraction > 0.001f) Slant(new Rect(r.x, r.y, Mathf.Max(r.height, r.width * Mathf.Clamp01(fraction)), r.height), c);
        }

        // ---------------------------------------------------------------- icons

        public static Texture2D IconPlay, IconPause, IconStepBack, IconStepFwd, IconPrev, IconNext, IconFlag, IconClose, IconFilm, IconDown, IconCheck;

        public static void Icon(Rect r, Texture icon, Color c)
        {
            if (Event.current.type != EventType.Repaint || icon == null) return;
            GUI.DrawTexture(r, icon, ScaleMode.ScaleToFit, true, 0f, c, 0f, 0f);
        }

        // little white icons drawn into textures once (tinted when drawn); shapes use y pointing down
        public static void EnsureIcons()
        {
            if (IconPlay != null) return;
            IconPlay = Draw((x, y) => Tri(x, y, 0.3f, 0.85f));
            IconPause = Draw((x, y) => Box(x, y, 0.27f, 0.42f, 0.18f, 0.82f) || Box(x, y, 0.58f, 0.73f, 0.18f, 0.82f));
            IconStepFwd = Draw((x, y) => Tri(x, y, 0.22f, 0.68f) || Box(x, y, 0.7f, 0.8f, 0.2f, 0.8f));
            IconStepBack = Draw((x, y) => Tri(1f - x, y, 0.22f, 0.68f) || Box(1f - x, y, 0.7f, 0.8f, 0.2f, 0.8f));
            IconNext = Draw((x, y) => Tri(x, y, 0.1f, 0.5f) || Tri(x, y, 0.5f, 0.9f));
            IconPrev = Draw((x, y) => Tri(1f - x, y, 0.1f, 0.5f) || Tri(1f - x, y, 0.5f, 0.9f));
            IconFlag = Draw((x, y) => Box(x, y, 0.2f, 0.3f, 0.1f, 0.92f) || (x >= 0.3f && x <= 0.85f && y >= 0.12f && y <= 0.55f && x - 0.3f <= 0.55f - Mathf.Abs(y - 0.335f) * 0.6f));
            IconClose = Draw((x, y) => Mathf.Abs(x - y) < 0.08f && x > 0.22f && x < 0.78f || Mathf.Abs(x - (1f - y)) < 0.08f && x > 0.22f && x < 0.78f);
            IconDown = Draw((x, y) => Tri(y, x, 0.3f, 0.75f));
            IconCheck = Draw((x, y) => Seg(x, y, 0.2f, 0.5f, 0.42f, 0.72f, 0.09f) || Seg(x, y, 0.42f, 0.72f, 0.82f, 0.28f, 0.09f));
            // a frame with a play triangle in it
            IconFilm = Draw((x, y) => (Box(x, y, 0.1f, 0.9f, 0.2f, 0.8f) && !Box(x, y, 0.18f, 0.82f, 0.28f, 0.72f))
                                      || (Box(x, y, 0.38f, 0.66f, 0.36f, 0.64f) && Mathf.Abs(y - 0.5f) <= 0.14f * (1f - (x - 0.38f) / 0.28f)));
        }

        static bool Box(float x, float y, float x0, float x1, float y0, float y1) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

        // within r of the segment (ax,ay)-(bx,by)
        static bool Seg(float x, float y, float ax, float ay, float bx, float by, float r)
        {
            var p = new Vector2(x, y); var a = new Vector2(ax, ay); var b = new Vector2(bx, by);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, b - a) / (b - a).sqrMagnitude);
            return (p - (a + (b - a) * t)).magnitude <= r;
        }

        // right-pointing triangle spanning x0..x1 horizontally and 0.15..0.85 vertically
        static bool Tri(float x, float y, float x0, float x1)
        {
            if (x < x0 || x > x1) return false;
            float f = (x - x0) / (x1 - x0);
            return Mathf.Abs(y - 0.5f) <= 0.35f * (1f - f);
        }

        // 4x supersampled for smooth edges
        static Texture2D Draw(System.Func<float, float, bool> inside)
        {
            const int n = 32;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int py = 0; py < n; py++)
                for (int px = 0; px < n; px++)
                {
                    int hits = 0;
                    for (int s = 0; s < 16; s++)
                    {
                        float x = (px + (s % 4 + 0.5f) / 4f) / n, y = 1f - (py + (s / 4 + 0.5f) / 4f) / n;
                        if (inside(x, y)) hits++;
                    }
                    t.SetPixel(px, py, new Color(1f, 1f, 1f, hits / 16f));
                }
            t.Apply();
            return t;
        }
    }
}
