using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        readonly Process _proc;
        readonly Stream _stdin;
        readonly Queue<string> _log = new Queue<string>();
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
            Plugin.Log.LogInfo($"[render] {ffmpeg} {args}");
            _proc = Run(ffmpeg, args, true);
            _stdin = _proc.StandardInput.BaseStream;
            StartWorkers(1); // one writer keeps frames in order
        }

        Process Run(string exe, string args, bool stdin)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = stdin,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                }
            };
            // ffmpeg blocks once its stderr pipe is full, so it's always drained
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) { _log.Enqueue(e.Data); while (_log.Count > 20) _log.Dequeue(); } };
            p.OutputDataReceived += (_, e) => { };
            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            _exe = exe;
            return p;
        }

        string _exe;

        protected override void Write(byte[] rgba, int index)
        {
            if (_proc.HasExited) throw new IOException("ffmpeg stopped: " + LastLog());
            _stdin.Write(rgba, 0, rgba.Length);
        }

        string LastLog() { lock (_log) return string.Join(" | ", _log); }

        protected override void Close(bool cancelled)
        {
            try { _stdin.Flush(); _stdin.Close(); } catch (IOException) { }
            _proc.WaitForExit();
            if (_proc.ExitCode != 0) throw new IOException($"ffmpeg exit code {_proc.ExitCode}: {LastLog()}");
            Plugin.Log.LogInfo($"[render] ffmpeg finished {OutputPath}" + (LastLog().Length > 0 ? $" ({LastLog()})" : ""));
            if (!cancelled && _audio != null) try { File.Delete(_audio); } catch (IOException) { }
        }
    }

    internal static class Ffmpeg
    {
        public static string DropFolder => Path.Combine(Plugin.ReplayFolder.Value, "ffmpeg");

        // Configured path (file or folder), <ReplayFolder>\ffmpeg (or its bin\), the plugin folder, then PATH.
        public static string Find()
        {
            var candidates = new List<string>();
            string cfg = Plugin.FfmpegPath.Value?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(cfg))
            {
                candidates.Add(cfg);
                candidates.Add(Path.Combine(cfg, "ffmpeg.exe"));
                candidates.Add(Path.Combine(cfg, "bin", "ffmpeg.exe"));
            }
            candidates.Add(Path.Combine(DropFolder, "ffmpeg.exe"));
            candidates.Add(Path.Combine(DropFolder, "bin", "ffmpeg.exe"));
            try { candidates.Add(Path.Combine(Path.GetDirectoryName(typeof(Ffmpeg).Assembly.Location) ?? "", "ffmpeg.exe")); } catch { }
            // a companion package (1nf-StreplayFFmpeg, or any ffmpeg package) lands in its own plugins subfolder;
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
                if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"));
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

        public static string NotFoundMessage =>
            "ffmpeg not found, so renders are PNG frames. For MP4/MOV: download a Windows build (gyan.dev → ffmpeg-release-essentials.zip), " +
            $"and put ffmpeg.exe in {DropFolder} (or set Render.FfmpegPath).";
    }
}
