using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Streplay
{
    // The Settings tab: the handful of config entries a player would want to change, written straight to the BepInEx
    // config file (so r2modman's editor shows the same values). Everything else stays in the file.
    internal sealed partial class ReplayBrowser
    {
        // a key binding waiting for its key; while set, the browser's own keys (F10, Esc) and the recorder's are off
        static ConfigEntry<KeyboardShortcut> _capture;
        internal static bool Capturing => _capture != null;

        static readonly float[] SizeStops = { 1, 2, 5, 10, 15, 20, 25, 30, 40, 50, 75, 100, 150, 200, 0 }; // 0 = no limit
        static readonly int[] Rates = { 30, 60, 90, 120 };
        // measured over real 2-4 player rounds: about 55 KB per second of round at 60 Hz
        const double DefaultBytesPerSecPerHz = 55.0 * 1024.0 / 60.0;

        long _usedBytes;
        int _usedRounds;
        double _bytesPerSecPerHz = DefaultBytesPerSecPerHz;

        void DrawSettings(Rect body)
        {
            HandleKeyCapture();
            const float gap = 48f;
            float colW = (body.width - gap) / 2f;
            float lx = body.x, rx = body.x + colW + gap;
            float y = body.y + 4f;

            // ---- storage
            y = Section(lx, y, colW, "Storage");
            float gb = Plugin.MaxFolderSizeGB.Value;
            int si = gb <= 0f ? SizeStops.Length - 1 : NearestStop(gb);
            Label(lx, y, "Max replay storage");
            int ns = Ui.Slider(new Rect(lx + 190f, y + 4f, colW - 190f - 92f, 28f), si, SizeStops.Length, "Older rounds are deleted to stay under this");
            Ui.Text(new Rect(lx + colW - 84f, y, 84f, 36f), SizeStops[ns] <= 0f ? "NO LIMIT" : $"{SizeStops[ns]:0} GB", 14, Ui.Bright, true, TextAnchor.MiddleRight);
            if (ns != si) Plugin.MaxFolderSizeGB.Value = SizeStops[ns];
            y += 42f;
            float hz = Mathf.Max(1f, Plugin.SampleRate.Value);
            string fill = SizeStops[ns] <= 0f
                ? "No limit: old rounds are never deleted"
                : $"About {Hours(SizeStops[ns] * 1024.0 * 1024.0 * 1024.0 / (_bytesPerSecPerHz * hz))} of rounds at {hz:0} Hz";
            Ui.Text(new Rect(lx + 190f, y, colW - 190f, 20f), fill, 13, Ui.TextC, true);
            y += 22f;
            Ui.Text(new Rect(lx + 190f, y, colW - 190f, 18f), $"Using {Size(_usedBytes)} · {N(_usedRounds, "round")}", 11, Ui.Dim);
            y += 18f;
            Ui.Text(new Rect(lx + 190f, y, colW - 190f, 18f), "Rounds with your own markers are always kept", 11, Ui.Faint);
            y += 20f;
            if (SizeStops[ns] > 0f && _usedBytes > SizeStops[ns] * 1024.0 * 1024.0 * 1024.0)
            {
                Ui.Text(new Rect(lx + 190f, y, colW - 190f, 34f), "Less than what's used now: the oldest unmarked rounds are deleted after your next round", 11, Ui.Warn, false, TextAnchor.UpperLeft, true);
                y += 34f;
            }
            y += 8f;
            Label(lx, y, "Folder");
            float ow = Ui.AutoW("Open", null, 30f, 12);
            Ui.Text(new Rect(lx + 190f, y, colW - 190f - ow - 10f, 36f), Plugin.ReplayFolder.Value, 11, Ui.Dim);
            if (Ui.Button(new Rect(lx + colW - ow, y + 3f, ow, 30f), "Open", false, Ui.Kind.Normal, null, 12, "Open the replay folder"))
                Ui.OpenFolder(Plugin.ReplayFolder.Value);
            y += 52f;

            // ---- recording
            y = Section(lx, y, colW, "Recording");
            Label(lx, y, "Record rounds");
            Toggle(new Rect(lx + 190f, y + 2f, 150f, 32f), Plugin.RecordEnabled);
            Ui.Text(new Rect(lx + 352f, y, colW - 352f, 36f), $"{Plugin.RecordToggleKey.Value} in game", 11, Ui.Faint);
            y += 44f;
            Label(lx, y, "Update rate");
            int ri = Array.IndexOf(Rates, Mathf.RoundToInt(hz));
            int nr = Ui.Segmented(new Rect(lx + 190f, y + 2f, colW - 190f, 32f), Rates.Select(r => $"{r} Hz").ToArray(), ri, null, 12);
            if (nr != ri && nr >= 0) Plugin.SampleRate.Value = Rates[nr];
            y += 40f;
            string rateNote = hz > 60f
                ? $"Smoother slow-mo, about {hz / 60f:0.#}x the disk and recording work. Never more than one per frame: at 80 fps, {hz:0} Hz records 80."
                : hz < 60f ? "Lighter on disk and CPU; fast flicks look less smooth in slow-mo." : "Smooth at normal speed and in slow-mo. The default.";
            Ui.Text(new Rect(lx + 190f, y, colW - 190f, 34f), rateNote, 11, Ui.Faint, false, TextAnchor.UpperLeft, true);
            y += 40f;
            Label(lx, y, "REC indicator");
            Toggle(new Rect(lx + 190f, y + 2f, 150f, 32f), Plugin.ShowRecIndicator);
            y += 44f;
            Label(lx, y, "Automatic markers");
            if (Ui.Chip(new Rect(lx + 190f, y + 3f, 130f, 30f), "Your kills", Plugin.AutoMarkKills.Value)) Plugin.AutoMarkKills.Value = !Plugin.AutoMarkKills.Value;
            if (Ui.Chip(new Rect(lx + 330f, y + 3f, 130f, 30f), "Your deaths", Plugin.AutoMarkDeaths.Value)) Plugin.AutoMarkDeaths.Value = !Plugin.AutoMarkDeaths.Value;

            // ---- keys
            float ry = body.y + 4f;
            ry = Section(rx, ry, colW, "Keys");
            ry = KeyRow(rx, ry, colW, "Replays window", Plugin.BrowserKey);
            ry = KeyRow(rx, ry, colW, "Mark / flag last kill", Plugin.MarkerKey);
            ry = KeyRow(rx, ry, colW, "Recording on / off", Plugin.RecordToggleKey);
            Ui.Text(new Rect(rx + 190f, ry - 4f, colW - 190f, 18f),
                Capturing ? "Press a key (hold Ctrl / Shift / Alt to combine). Esc cancels, Backspace clears." : "Click a key to change it", 11, Capturing ? Ui.Accent : Ui.Faint);
            ry += 34f;

            // ---- playback
            ry = Section(rx, ry, colW, "Free camera");
            ry = Stepped(rx, ry, colW, "Speed", Plugin.FreeCamSpeed, 1f, 40f, 1f, "0");
            ry = Stepped(rx, ry, colW, "Mouse sensitivity", Plugin.MouseSensitivity, 0.2f, 6f, 0.1f, "0.0");
            ry = Stepped(rx, ry, colW, "Field of view", Plugin.DefaultFov, 50f, 120f, 1f, "0");

            float rw = Ui.AutoW("Reset to defaults", null, 32f, 12);
            if (Ui.Button(new Rect(rx + colW - rw, body.yMax - 34f, rw, 32f), "Reset to defaults", false, Ui.Kind.Danger, null, 12, "Puts every setting on this page back"))
                ResetSettings();
        }

        static float Section(float x, float y, float w, string title)
        {
            Ui.Heading(new Rect(x, y, w, 20f), title);
            Ui.Fill(new Rect(x, y + 22f, w, 1f), Ui.Line);
            return y + 34f;
        }

        static void Label(float x, float y, string text) => Ui.Text(new Rect(x, y, 186f, 36f), text, 13, Ui.Dim, true);

        static void Toggle(Rect r, ConfigEntry<bool> e)
        {
            int v = Ui.Segmented(r, new[] { "Off", "On" }, e.Value ? 1 : 0, null, 12);
            if ((v == 1) != e.Value) e.Value = v == 1;
        }

        float KeyRow(float x, float y, float w, string label, ConfigEntry<KeyboardShortcut> e)
        {
            Label(x, y, label);
            bool waiting = _capture == e;
            string text = waiting ? "Press a key…" : e.Value.MainKey == KeyCode.None ? "None" : e.Value.ToString();
            if (Ui.Button(new Rect(x + 190f, y + 2f, Mathf.Min(220f, w - 190f), 32f), text, waiting, Ui.Kind.Normal, null, 12))
                _capture = waiting ? null : e;
            return y + 42f;
        }

        // a slider over min..max in steps (written only when the step changes) and its value
        static float Stepped(float x, float y, float w, string label, ConfigEntry<float> e, float min, float max, float step, string fmt)
        {
            Label(x, y, label);
            int count = Mathf.RoundToInt((max - min) / step) + 1;
            int i = Mathf.Clamp(Mathf.RoundToInt((e.Value - min) / step), 0, count - 1);
            int ni = Ui.Slider(new Rect(x + 190f, y + 4f, w - 190f - 64f, 28f), i, count);
            if (ni != i) e.Value = (float)Math.Round(min + ni * step, 3);
            Ui.Text(new Rect(x + w - 56f, y, 56f, 36f), e.Value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture), 14, Ui.Bright, true, TextAnchor.MiddleRight);
            return y + 42f;
        }

        void HandleKeyCapture()
        {
            var ev = Event.current;
            if (_capture == null || ev.type != EventType.KeyDown || ev.keyCode == KeyCode.None) return;
            var k = ev.keyCode;
            if (k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftShift || k == KeyCode.RightShift
                || k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand) return; // wait for the real key
            if (k == KeyCode.Backspace || k == KeyCode.Delete) _capture.Value = KeyboardShortcut.Empty;
            else if (k != KeyCode.Escape)
            {
                var mods = new System.Collections.Generic.List<KeyCode>();
                if (ev.control) mods.Add(KeyCode.LeftControl);
                if (ev.shift) mods.Add(KeyCode.LeftShift);
                if (ev.alt) mods.Add(KeyCode.LeftAlt);
                _capture.Value = new KeyboardShortcut(k, mods.ToArray());
            }
            _capture = null;
            ev.Use();
        }

        static void ResetSettings()
        {
            _capture = null;
            foreach (ConfigEntryBase e in new ConfigEntryBase[]
            {
                Plugin.MaxFolderSizeGB, Plugin.RecordEnabled, Plugin.SampleRate, Plugin.ShowRecIndicator, Plugin.AutoMarkKills, Plugin.AutoMarkDeaths,
                Plugin.BrowserKey, Plugin.MarkerKey, Plugin.RecordToggleKey, Plugin.FreeCamSpeed, Plugin.MouseSensitivity, Plugin.DefaultFov,
            })
                e.BoxedValue = e.DefaultValue;
        }

        static int NearestStop(float gb)
        {
            int best = 0;
            for (int i = 0; i < SizeStops.Length - 1; i++)
                if (Mathf.Abs(SizeStops[i] - gb) < Mathf.Abs(SizeStops[best] - gb)) best = i;
            return best;
        }

        static string Hours(double seconds)
        {
            double h = seconds / 3600.0;
            return h >= 10 ? $"{h:0} hours" : h >= 1 ? $"{h:0.#} hours" : $"{seconds / 60.0:0} minutes";
        }

        static string Size(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
    }
}
