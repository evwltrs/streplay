using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Streplay
{
    public enum RenderFormat { H264, H265, ProRes4444, PngSequence }

    // how the sub-frames of one output frame are weighted: Equal is a film camera's open shutter, Gaussian fades the
    // copies in and out at the ends of the shutter (the soft "frame blending" look of HLAE / RSMB / f0e's blur)
    public enum BlurWeighting { Equal, Gaussian }

    // Where finished RGBA frames go. Frames arrive in order on the main thread (rows bottom-up, as Unity
    // reads them back) and are written on worker threads. Submit never blocks: the PNG encoder needs Unity's
    // main thread to keep running, so the renderer holds still (see Backlog) instead of waiting here.
    internal abstract class FrameSink
    {
        protected readonly int W, H;
        readonly BlockingCollection<(byte[] data, int index)> _queue = new BlockingCollection<(byte[], int)>();
        readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        readonly List<Thread> _threads = new List<Thread>();
        volatile string _error;
        int _written, _backlog;
        public int Workers { get; private set; }
        // frames submitted but not written yet
        public int Backlog => Volatile.Read(ref _backlog);

        public string Error => _error;
        public int Written => _written;
        public string OutputPath { get; protected set; }
        public bool Finished { get; private set; }

        protected FrameSink(int w, int h) { W = w; H = h; }

        protected void StartWorkers(int count)
        {
            Workers = count;
            for (int i = 0; i < count; i++)
            {
                var t = new Thread(Work) { IsBackground = true, Name = "Streplay render writer" };
                _threads.Add(t);
                t.Start();
            }
        }

        public byte[] Rent() => _pool.TryTake(out var b) ? b : new byte[W * H * 4];

        public void Submit(byte[] rgba, int index)
        {
            if (_error != null) { _pool.Add(rgba); return; }
            Interlocked.Increment(ref _backlog);
            _queue.Add((rgba, index));
        }

        void Work()
        {
            foreach (var (data, index) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (_error == null) { Write(data, index); Interlocked.Increment(ref _written); }
                }
                catch (Exception e)
                {
                    _error ??= e.Message;
                }
                _pool.Add(data);
                Interlocked.Decrement(ref _backlog);
            }
        }

        protected abstract void Write(byte[] rgba, int index);
        protected virtual void Close(bool cancelled) { }

        // Lets queued frames drain and closes the output on a background thread; Finished turns true after.
        public void Finish(bool cancelled, Action<FrameSink> done)
        {
            _queue.CompleteAdding();
            var t = new Thread(() =>
            {
                foreach (var w in _threads) w.Join();
                try { Close(cancelled); }
                catch (Exception e) { _error ??= e.Message; }
                done?.Invoke(this);
                Finished = true;
            }) { IsBackground = true, Name = "Streplay render finish" };
            t.Start();
        }
    }

    internal sealed class PngSink : FrameSink
    {
        readonly string _dir;

        public PngSink(string dir, int w, int h) : base(w, h)
        {
            _dir = dir;
            OutputPath = dir;
            StartWorkers(Math.Max(1, Math.Min(4, SystemInfo.processorCount / 2)));
        }

        protected override void Write(byte[] rgba, int index)
        {
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255; // camera alpha is meaningless
            var png = ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_SRGB, (uint)W, (uint)H);
            File.WriteAllBytes(Path.Combine(_dir, $"frame_{index:00000}.png"), png);
        }
    }

    internal sealed class FfmpegSink : FrameSink
    {
        readonly object _enc; // the STREPLAY_FFMPEG package's encoder (see FfmpegBridge)
        readonly Stream _stdin;
        readonly string _audio;

        public static string Extension(RenderFormat f) => f == RenderFormat.ProRes4444 ? ".mov" : ".mp4";

        // audio: a WAV of the same length to mux in (deleted once the video is written), or null
        // preset: x264/x265 speed preset, or null for the quality default (previews use a fast one: the game draws a frame in
        // a few ms, a slow preset would make it wait on the encoder)
        public FfmpegSink(string ffmpeg, string output, RenderFormat format, int w, int h, int fps, int crf, string audio, string preset = null) : base(w, h)
        {
            OutputPath = output;
            _audio = audio;
            string codec;
            switch (format)
            {
                case RenderFormat.H265:
                    codec = $"-c:v libx265 -preset {preset ?? "medium"} -crf {crf} -pix_fmt yuv420p10le -tag:v hvc1 -x265-params log-level=error";
                    break;
                case RenderFormat.ProRes4444:
                    codec = "-c:v prores_ks -profile:v 4 -pix_fmt yuv444p10le -vendor apl0";
                    break;
                default:
                    codec = $"-c:v libx264 -preset {preset ?? "slow"} -crf {crf} -pix_fmt yuv420p";
                    break;
            }
            // Unity reads frames back bottom-up; RGB -> YUV with the BT.709 matrix, and tagged as such
            string sound = audio == null ? "" : $"-i \"{audio}\" -map 0:v -map 1:a " + (format == RenderFormat.ProRes4444 ? "-c:a pcm_s16le " : "-c:a aac -b:a 320k ");
            string args = $"-hide_banner -loglevel warning -y -f rawvideo -pix_fmt rgba -s {w}x{h} -framerate {fps} -i - {sound}" +
                          "-vf \"vflip,scale=out_color_matrix=bt709:out_range=tv\" " +
                          $"{codec} -colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
                          (format == RenderFormat.ProRes4444 ? "" : "-movflags +faststart ") + $"\"{output}\"";
            Plugin.Log.LogDebug($"[render] {ffmpeg} {args}");
            _enc = FfmpegBridge.Start(ffmpeg, args);
            _stdin = FfmpegBridge.Input(_enc);
            StartWorkers(1); // one writer keeps frames in order
        }

        protected override void Write(byte[] rgba, int index)
        {
            if (FfmpegBridge.HasExited(_enc)) throw new IOException("ffmpeg stopped: " + FfmpegBridge.LastLog(_enc));
            _stdin.Write(rgba, 0, rgba.Length);
        }

        protected override void Close(bool cancelled)
        {
            int code = FfmpegBridge.Finish(_enc);
            string log = FfmpegBridge.LastLog(_enc);
            if (code != 0) throw new IOException($"ffmpeg exit code {code}: {log}");
            Plugin.Log.LogDebug($"[render] ffmpeg finished {OutputPath}" + (log.Length > 0 ? $" ({log})" : ""));
            if (!cancelled && _audio != null) try { File.Delete(_audio); } catch (IOException) { }
        }
    }

    // STREPLAY starts no programs itself: ffmpeg is started by StreplayFfmpeg.dll, which comes with the STREPLAY_FFMPEG
    // package. Only the methods here name its types, and none of them is compiled
    // until it's called, so without the package nothing here loads and STREPLAY carries on with PNG frames.
    internal static class FfmpegBridge
    {
        static bool? _installed;

        public static bool Installed
        {
            get
            {
                if (_installed == null)
                {
                    try { _installed = BridgeVersion() >= 1; }
                    catch (Exception) { _installed = false; } // FileNotFoundException / TypeLoadException: not installed
                    Plugin.Log.LogInfo(_installed == true ? "STREPLAY_FFMPEG found: renders go to video files" : "STREPLAY_FFMPEG not installed: renders are PNG frames");
                }
                return _installed == true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] static int BridgeVersion() => StreplayFfmpeg.Bridge.Version;
        [MethodImpl(MethodImplOptions.NoInlining)] public static object Start(string exe, string args) => new StreplayFfmpeg.Encoder(exe, args);
        [MethodImpl(MethodImplOptions.NoInlining)] public static Stream Input(object e) => ((StreplayFfmpeg.Encoder)e).Input;
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool HasExited(object e) => ((StreplayFfmpeg.Encoder)e).HasExited;
        [MethodImpl(MethodImplOptions.NoInlining)] public static string LastLog(object e) => ((StreplayFfmpeg.Encoder)e).LastLog;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Finish(object e)
        {
            var enc = (StreplayFfmpeg.Encoder)e;
            try { return enc.Finish(); }
            finally { enc.Dispose(); }
        }
    }

    internal static class Ffmpeg
    {
        public static string DropFolder => Path.Combine(Plugin.ReplayFolder.Value, "ffmpeg");

        // Configured path (file or folder), <ReplayFolder>\ffmpeg (or its bin\), the plugin folder, then PATH. Any of
        // them needs the STREPLAY_FFMPEG package installed, which is what runs it (FfmpegBridge).
        public static string Find()
        {
            if (!FfmpegBridge.Installed) return null;
            var candidates = new List<string>();
            string cfg = Plugin.FfmpegPath.Value?.Trim().Trim('"');
            // a malformed setting or PATH entry (stray quotes, <>|) is skipped rather than thrown: this runs from the GUI
            if (!string.IsNullOrEmpty(cfg))
            {
                candidates.Add(cfg);
                try
                {
                    candidates.Add(Path.Combine(cfg, "ffmpeg.exe"));
                    candidates.Add(Path.Combine(cfg, "bin", "ffmpeg.exe"));
                }
                catch { /* not a folder path */ }
            }
            candidates.Add(Path.Combine(DropFolder, "ffmpeg.exe"));
            candidates.Add(Path.Combine(DropFolder, "bin", "ffmpeg.exe"));
            try { candidates.Add(Path.Combine(Path.GetDirectoryName(typeof(Ffmpeg).Assembly.Location) ?? "", "ffmpeg.exe")); } catch { }
            // a companion package (1nf-STREPLAY_FFMPEG, or any ffmpeg package) lands in its own plugins subfolder;
            // mod managers may nest it one level further, so look two levels down
            try
            {
                foreach (var dir in Directory.GetDirectories(BepInEx.Paths.PluginPath))
                {
                    candidates.Add(Path.Combine(dir, "ffmpeg.exe"));
                    foreach (var sub in Directory.GetDirectories(dir)) candidates.Add(Path.Combine(sub, "ffmpeg.exe"));
                }
            }
            catch { }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe")); }
                catch { /* malformed PATH entry */ }
            }
            foreach (var c in candidates)
            {
                try { if (c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(c)) return Path.GetFullPath(c); }
                catch { }
            }
            return null;
        }

        static string _cached;
        static float _checkedAt = -100f;

        // re-checked every few seconds so dropping ffmpeg in while the game runs just works
        public static string Cached
        {
            get
            {
                if (Time.unscaledTime - _checkedAt > 3f) { _cached = Find(); _checkedAt = Time.unscaledTime; }
                return _cached;
            }
        }

        public static string NotFoundMessage => !FfmpegBridge.Installed
            ? "Renders are PNG frames. For MP4/MOV, install the STREPLAY_FFMPEG package (it has ffmpeg and what STREPLAY needs to run it)."
            : "ffmpeg not found, so renders are PNG frames. Reinstall STREPLAY_FFMPEG, or put ffmpeg.exe in " +
              $"{DropFolder} (or set Render.FfmpegPath).";
    }
}
