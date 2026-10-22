using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace Streplay
{
    // Montage export: the replay's cameras (world, viewmodel overlays, the game's HUD) render into an off-screen
    // target of any size; several sub-frames spread over the shutter are averaged on the GPU for motion blur; the
    // frames are read back asynchronously and piped to ffmpeg (or written as PNGs). Our own UI only ever draws to
    // the window, so it can't end up in the output.
    internal sealed partial class ReplayPlayer
    {
        sealed class RenderJob
        {
            public float Start, End, Speed, Shutter; // shutter as a fraction of the frame interval
            public int W, H, Fps, Sub, Frames;
            public int Crf = -1;                      // -1 = the Crf setting
            public string Preset;                     // encoder speed preset, null = the quality default
            public int MaxSub;                        // adaptive sub-frame ceiling (0 = the setting)
            public bool Adaptive = true;

            // output frame position of the sub-frame being placed (fractional, for the effects)
            public float FramePos => Frame + (SubOf(Frame) > 1 ? ((SubIndex + 0.5f) / SubOf(Frame) - 0.5f) * Shutter : 0f);
            public int Frame, SubIndex;               // what the next Update places / the next capture records
            public RenderFormat Format;
            public FrameSink Sink;
            public DateTime Began;
            public string Root, Name;                 // output folder and base file name
            public float FrameStep => Speed / Fps;    // replay seconds per output frame (without slow-mo)
            public SlowMo Ramp;                       // slow-mo around kills, or null
            public float[] FrameT;                    // replay time of each output frame, plus one past the last
            public int[] SubN;                        // adaptive sub-frames per output frame, or null (Sub for all)
            public long SubTotal, SubDone;            // progress over all sub-frames
            public BlurWeighting Weighting;
            // where the time goes (Stopwatch ticks), logged when the render ends
            public long TUpdate, TLate, TGui, TCapture, TWall0, TWall1, TManual;
            public int ManualCount;
            public int SavedVSync, SavedTargetFps;
            public float WeightSum;                   // of the current frame's sub-frames so far

            // weight of a sub-frame by where it sits in the shutter (x from -1 at opening to 1 at closing)
            public float WeightOf(int frame, int sub)
            {
                if (Weighting == BlurWeighting.Equal) return 1f;
                int n = SubOf(frame);
                float x = (sub + 0.5f) / n * 2f - 1f, sigma = 0.45f;
                return Mathf.Exp(-0.5f * x * x / (sigma * sigma));
            }

            public float SpeedAt(float t) => Speed * (Ramp?.Factor(t) ?? 1f);
            public int SubOf(int frame) => SubN != null && frame < SubN.Length ? SubN[frame] : Sub;

            public void Plan() => (FrameT, Frames) = PlanFrames(Start, End, Speed, Fps, Ramp);

            // Sub-frames are centred on the frame's own time (spread over that frame's own step, which slow-mo
            // shortens), so with blur off frame i is exactly its planned time.
            public float TimeAt(int frame, int sub)
            {
                float t = FrameT[frame], step = FrameT[frame + 1] - t;
                int n = SubOf(frame);
                return t + step * (n > 1 ? Shutter * ((sub + 0.5f) / n - 0.5f) : 0f);
            }
        }

        // Slow motion eased in around each kill: speed factor 1 away from kills, Speed at the kill itself.
        internal sealed class SlowMo
        {
            public float Speed, Hold, Ease;           // factor at the kill; replay seconds held slow; replay seconds of easing
            public List<float> At = new List<float>();

            public float Factor(float t)
            {
                float f = 1f;
                foreach (float k in At)
                {
                    float a = Mathf.Abs(t - k), hold = Hold * 0.5f;
                    float w = a <= hold ? 1f : a >= hold + Ease ? 0f : Mathf.SmoothStep(1f, 0f, (a - hold) / Mathf.Max(1e-3f, Ease));
                    f = Mathf.Min(f, Mathf.Lerp(1f, Speed, w));
                }
                return f;
            }

            public string Key => $"{Speed:0.###}/{Hold:0.###}/{Ease:0.###}/" + string.Join(",", At.Select(a => a.ToString("0.000")));
        }

        // Replay time of every output frame: a fixed step, or with slow-mo the speed curve integrated per frame.
        static (float[] times, int frames) PlanFrames(float start, float end, float speed, int fps, SlowMo ramp)
        {
            if (ramp == null || ramp.At.Count == 0)
            {
                int n = Mathf.Max(1, Mathf.RoundToInt((end - start) / speed * fps));
                var ft = new float[n + 1];
                for (int i = 0; i <= n; i++) ft[i] = start + speed / fps * i;
                return (ft, n);
            }
            var list = new List<float> { start };
            float t = start;
            while (list.Count < 200000)
            {
                float step = 0f;
                for (int k = 0; k < 8; k++) step += speed * ramp.Factor(t + step) / fps / 8f;
                if (t + step * 0.5f >= end) break;
                t += step;
                list.Add(t);
            }
            if (list.Count < 2) list.Add(start + speed / fps);
            else list.Add(t + speed * ramp.Factor(t) / fps);
            return (list.ToArray(), list.Count - 1);
        }

        // slow-mo for a range, centred on your kills in it (none when it's off or there's no kill)
        SlowMo RampFor(float start, float end)
        {
            if (!Plugin.SlowMoKills.Value) return null;
            var ramp = new SlowMo { Speed = Mathf.Clamp(Plugin.SlowMoSpeed.Value, 0.05f, 1f), Hold = Mathf.Max(0f, Plugin.SlowMoHold.Value), Ease = Mathf.Max(0.05f, Plugin.SlowMoEase.Value) };
            foreach (var k in _localKills)
                if (k.T >= start - 0.5f && k.T <= end + 0.5f) ramp.At.Add(k.T - 0.05f); // the feed line lands just after the shot
            return ramp.At.Count > 0 ? ramp : null;
        }

        // Adaptive motion blur: N copies of the image spread over the shutter only blend into a smear when
        // neighbouring copies are a pixel or two apart, so a fast flick (hundreds of pixels per frame) needs far
        // more sub-frames than a slow pan. The recorded camera tells how far the view turns and moves during each
        // frame's shutter; that is turned into pixels at the output size.
        void PlanSubFrames(RenderJob job)
        {
            const float pxPerStep = 2f, refDistance = 3f; // translation counts as seen on things 3 m away
            int lo = job.Sub, hi = Mathf.Max(lo, job.MaxSub > 0 ? job.MaxSub : Plugin.RenderMaxSubFrames.Value);
            var n = new int[job.Frames];
            for (int i = 0; i < job.Frames; i++)
            {
                float step = job.FrameT[i + 1] - job.FrameT[i];
                float a = job.FrameT[i] - step * job.Shutter * 0.5f, b = job.FrameT[i] + step * job.Shutter * 0.5f;
                var (p0, q0, fov) = RecordedCamAt(a);
                float turn = 0f, move = 0f;
                const int samples = 8;
                for (int s = 1; s <= samples; s++)
                {
                    var (p1, q1, _) = RecordedCamAt(Mathf.Lerp(a, b, s / (float)samples));
                    turn += Quaternion.Angle(q0, q1) * Mathf.Deg2Rad;
                    move += Vector3.Distance(p0, p1);
                    p0 = p1;
                    q0 = q1;
                }
                if (fov < 1f) fov = Plugin.DefaultFov.Value;
                float pxPerRad = job.H * 0.5f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
                float px = (turn + move / refDistance) * pxPerRad;
                n[i] = Mathf.Clamp(Mathf.CeilToInt(px / pxPerStep), lo, hi);
            }
            job.SubN = n;
        }

        (Vector3 pos, Quaternion rot, float fov) RecordedCamAt(float t)
        {
            int i = FrameAt(t);
            var a = _data.Frames[i];
            var b = i + 1 < _data.Frames.Count ? _data.Frames[i + 1] : a;
            float alpha = b.T > a.T ? Mathf.Clamp01((t - a.T) / (b.T - a.T)) : 0f;
            return (Vector3.Lerp(a.CamPos, b.CamPos, alpha), Quaternion.Slerp(a.CamRot, b.CamRot, alpha), Mathf.Lerp(a.CamFov, b.CamFov, alpha));
        }

        RenderJob _job;
        RenderTexture _camRT, _finalRT, _accA, _accB;
        bool _accOnB;
        Camera _uiCam;
        PropertySheetFactory _sheets;
        PropertySheet _lerp;
        CommandBuffer _cmd;
        readonly Queue<(AsyncGPUReadbackRequest req, int index)> _readbacks = new Queue<(AsyncGPUReadbackRequest, int)>();
        readonly List<Camera> _targeted = new List<Camera>();
        Texture2D _syncReadback;
        bool _renderStepped;   // an Update placed this frame's sub-frame, so the end of the frame records it
        bool _renderHold;      // the writers are behind: the replay and game time stand still until they catch up
        float _renderDt;       // video seconds since the previous sub-frame (POV smoothing)
        FrameSink _encoding;   // the last render's output, possibly still being finished
        volatile string _renderDone;
        string _renderSummary;
        int _readbackErrors;

        static readonly int ToId = Shader.PropertyToID("_To");
        static readonly int InterpId = Shader.PropertyToID("_Interp");

        internal void TestRender(float start, float end)
        {
            _in = start;
            _out = end;
            _startRender = true;
        }
        internal string TestRenderSummary => _renderSummary;
        internal void TestCancelRender() { if (_audioPass != null) CancelAudioPass(); else StopRender(true); } // what Esc does
        internal void TestRenderPanel(bool on) => _renderPanel = on;

        internal bool RenderBusy => _rendering || _audioPass != null || (_encoding != null && !_encoding.Finished);

        // The sound can't be rendered faster than real time, so with audio on the range is first played once at
        // normal speed while AudioTap records the mix; the video pass then runs offline as usual.
        sealed class AudioPass
        {
            public RenderJob Job;
            public AudioTap Tap;
            public int Need, Wait;
            public bool Recording, Mapped;
            public float Gain = 1f;
            public float Began;
        }
        AudioPass _audioPass;

        void StartRender()
        {
            if (_rendering || _data == null) return;
            if (_encoding != null && !_encoding.Finished) { Toast("Still finishing the previous render"); return; }

            float start = _in ?? _t;
            float stop = _out ?? _data.Duration;
            if (stop <= start) stop = _data.Duration;
            if (stop <= start) { Toast("Nothing to render: set an in point before the out point"); return; }

            var job = new RenderJob
            {
                Start = start, End = stop, Speed = Speed,
                W = Mathf.Clamp(Plugin.RenderWidth.Value, 16, 8192) & ~1, // even sizes for 4:2:0 video
                H = Mathf.Clamp(Plugin.RenderHeight.Value, 16, 8192) & ~1,
                Fps = Mathf.Max(1, Plugin.RenderFps.Value),
                Sub = Plugin.RenderMotionBlur.Value ? Mathf.Clamp(Plugin.RenderSubFrames.Value, 2, 256) : 1,
                Shutter = Mathf.Clamp(Plugin.RenderShutter.Value, 1f, 360f) / 360f,
                Weighting = Plugin.RenderBlurWeighting.Value,
                Format = Plugin.RenderFormatSetting.Value,
                Began = DateTime.Now,
            };
            job.Ramp = RampFor(start, stop);
            job.Plan();
            if (job.Sub > 1 && Plugin.RenderAdaptiveSubFrames.Value && job.Adaptive) PlanSubFrames(job);
            job.SubTotal = job.SubN != null ? job.SubN.Sum(n => (long)n) : (long)job.Sub * job.Frames;
            job.Root = string.IsNullOrWhiteSpace(Plugin.RenderFolder.Value) ? Path.Combine(Plugin.ReplayFolder.Value, "renders") : Plugin.RenderFolder.Value.Trim().Trim('"');
            job.Name = $"{Path.GetFileNameWithoutExtension(_meta.ReplayFile)}_{Fmt(start).Replace(':', '-')}_{DateTime.Now:HHmmss}";
            _renderSummary = null;
            _renderDone = null;

            if (Plugin.RenderAudio.Value && _cam != null)
            {
                // another angle of the same range: reuse its sound (recording it takes real time)
                string key = AudioKey(job);
                if (key == _audioCacheKey && File.Exists(AudioCachePath))
                {
                    try
                    {
                        Directory.CreateDirectory(job.Root);
                        string wavCopy = Path.Combine(job.Root, job.Name + ".wav");
                        File.Copy(AudioCachePath, wavCopy, true);
                        Plugin.Log.LogDebug("[render] reusing the sound recorded for this range");
                        BeginVideo(job, wavCopy);
                        return;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[render] reusing the sound failed, recording it again: {e.Message}"); }
                }
                var tap = _cam.GetComponent<AudioTap>() ?? _cam.gameObject.AddComponent<AudioTap>();
                _audioPass = new AudioPass
                {
                    Job = job, Tap = tap, Wait = 3,
                    Need = Mathf.RoundToInt(job.Frames / (float)job.Fps * AudioSettings.outputSampleRate),
                };
                Seek(start);
                _playing = false;
                Plugin.Log.LogDebug($"[render] recording the sound of {Fmt(start)}-{Fmt(stop)} in real time first");
                return;
            }
            BeginVideo(job, null);
        }

        // Update, before the replay advances: start the real-time playback, and once it has covered the range,
        // save the sound and start the video pass.
        void TickAudioPass()
        {
            var p = _audioPass;
            if (!p.Recording)
            {
                if (--p.Wait > 0) return; // let the seek's hitch pass so playback starts at a steady frame rate
                p.Recording = true;
                p.Began = Time.realtimeSinceStartup;
                p.Tap.Begin();
                _playing = true;
                return;
            }
            // done when the range is covered; playback stopping at the end of the replay (the listener pauses) or
            // a stalled audio thread end it too, and the WAV is padded with silence
            float expected = p.Need / (float)Mathf.Max(1, AudioSettings.outputSampleRate);
            bool atEnd = !_playing && _t >= _data.Duration - 1e-3f;
            bool overdue = Time.realtimeSinceStartup - p.Began > expected * 1.5f + 2f;
            if (p.Tap.Frames < p.Need && !atEnd && !overdue) return;
            p.Tap.Stop();
            _playing = false;
            _audioPass = null;
            string wav;
            try
            {
                Directory.CreateDirectory(p.Job.Root);
                wav = Path.Combine(p.Job.Root, p.Job.Name + ".wav");
                if (p.Mapped)
                {
                    // output sample n is at output time n/rate = frame n/rate*fps; that frame's replay time, minus the start
                    var j = p.Job;
                    int rate = p.Tap.SampleRate, outFrames = Mathf.RoundToInt(j.Frames / (float)j.Fps * rate);
                    p.Tap.WriteWavMapped(wav, outFrames, n =>
                    {
                        double f = (double)n / rate * j.Fps;
                        int i = Math.Min(j.Frames - 1, (int)f);
                        double r = j.FrameT[i] + (j.FrameT[i + 1] - j.FrameT[i]) * Math.Min(1.0, f - i);
                        return (r - j.Start) * rate;
                    }, p.Gain);
                }
                else p.Tap.WriteWav(wav, p.Need);
                Plugin.Log.LogDebug($"[render] sound: {p.Need} samples x {p.Tap.Channels} ch @ {p.Tap.SampleRate} Hz -> {wav}");
                try { File.Copy(wav, AudioCachePath, true); _audioCacheKey = AudioKey(p.Job); } catch { _audioCacheKey = null; }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[render] could not save the sound, rendering without it: {e.Message}");
                wav = null;
            }
            BeginVideo(p.Job, wav);
        }

        // the last recorded range's sound, kept so further angles of that range don't record it again
        string _audioCacheKey;
        static string AudioCachePath => Path.Combine(Path.GetTempPath(), "streplay_render_sound.wav");
        string AudioKey(RenderJob j) => $"{_meta?.ReplayFile}|{j.Start:0.000}|{j.End:0.000}|{j.Speed}|{j.Fps}|{j.Frames}|{j.Ramp?.Key}|{j.FrameT?.Sum(x => (double)x):0.0000}";

        void CancelAudioPass()
        {
            _audioPass.Tap.Stop();
            _audioPass = null;
            _playing = false;
            _renderSummary = "Render cancelled";
            Toast(_renderSummary);
        }

        void BeginVideo(RenderJob job, string wav)
        {
            string root = job.Root, name = job.Name;
            job.Began = DateTime.Now;
            try
            {
                Directory.CreateDirectory(root);
                if (job.Format != RenderFormat.PngSequence)
                {
                    string ffmpeg = Ffmpeg.Find();
                    if (ffmpeg == null)
                    {
                        Plugin.Log.LogWarning("[render] " + Ffmpeg.NotFoundMessage);
                        job.Format = RenderFormat.PngSequence;
                    }
                    else job.Sink = new FfmpegSink(ffmpeg, Path.Combine(root, name + FfmpegSink.Extension(job.Format)), job.Format, job.W, job.H, job.Fps, job.Crf >= 0 ? job.Crf : Plugin.RenderCrf.Value, wav,
                        job.Preset ?? (job.Sub <= 1 ? "medium" : null)); // no blur: frames come fast, a quicker preset keeps up
                }
                if (job.Sink == null)
                {
                    string dir = Path.Combine(root, name);
                    Directory.CreateDirectory(dir);
                    job.Sink = new PngSink(dir, job.W, job.H);
                    if (wav != null) { string dst = Path.Combine(dir, "audio.wav"); File.Delete(dst); File.Move(wav, dst); }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[render] could not start the output: {e}");
                Toast("Render failed to start: " + e.Message);
                return;
            }

            try { SetupRenderTargets(job); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[render] could not set up the render targets: {e}");
                Toast("Render failed to start: " + e.Message);
                job.Sink.Finish(true, null);
                TeardownRenderTargets();
                return;
            }

            _job = job;
            _readbackErrors = 0;
            // each sub-frame is a whole engine frame: no vsync or frame cap may hold it back
            job.SavedVSync = QualitySettings.vSyncCount;
            job.SavedTargetFps = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            _encoding = job.Sink;
            Seek(Mathf.Clamp(job.TimeAt(0, 0), 0f, _data.Duration));
            _rendering = true;
            _playing = true;
            _renderStepped = false;
            _renderHold = false;
            AudioListener.pause = true;
            // the next frame's game time (particles, animations) moves exactly as far as the replay does
            float firstStep = job.FrameT[1] - job.FrameT[0];
            int sub0 = job.SubOf(0);
            Time.captureDeltaTime = Mathf.Max(1e-5f, firstStep * (sub0 > 1 ? job.Shutter / sub0 : 1f) / job.SpeedAt(job.Start));
            StartCoroutine(RenderCapture());
            LogFrameCost();
            string blur = job.Sub <= 1 ? "no motion blur"
                : job.SubN != null ? $"motion blur {job.SubN.Min()}-{job.SubN.Max()} sub-frames (adaptive, {job.SubTotal} in all), {job.Shutter * 360f:0}° shutter, {job.Weighting} weighting"
                : $"motion blur {job.Sub} sub-frames, {job.Shutter * 360f:0}° shutter, {job.Weighting} weighting";
            Plugin.Log.LogDebug($"[render] {Fmt(job.Start)}-{Fmt(job.End)} at {job.Speed:0.##}x: {job.Frames} frames {job.W}x{job.H} @ {job.Fps}fps, " + blur +
                               (job.Ramp != null ? $", slow-mo {job.Ramp.Speed:0.##}x at {job.Ramp.At.Count} kill(s)" : "") +
                               $", {job.Format} -> {job.Sink.OutputPath} (color space {QualitySettings.activeColorSpace}, MSAA {_camRT.antiAliasing}, window {Screen.width}x{Screen.height}, far clip {_cam.farClipPlane})");
        }

        // what else runs every frame (each sub-frame is a whole frame): cameras and behaviours by assembly
        static void LogFrameCost()
        {
            try
            {
                var cams = Camera.allCameras.Select(c => $"{c.name} (depth {c.depth}, {(c.targetTexture != null ? c.targetTexture.name : "screen")})");
                var beh = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Where(b => b.isActiveAndEnabled)
                    .GroupBy(b => b.GetType().Assembly.GetName().Name).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} {g.Count()} ({string.Join(", ", g.GroupBy(b => b.GetType().Name).OrderByDescending(t => t.Count()).Take(6).Select(t => $"{t.Key} x{t.Count()}"))})");
                Plugin.Log.LogDebug($"[render] cameras: {string.Join("; ", cams)}");
                Plugin.Log.LogDebug($"[render] enabled behaviours: {string.Join(" | ", beh)}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[render] frame cost probe failed: {e.Message}"); }
        }

        void SetupRenderTargets(RenderJob job)
        {
            _camRT = new RenderTexture(job.W, job.H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "Streplay render", antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing),
            };
            _camRT.Create();
            _finalRT = new RenderTexture(job.W, job.H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "Streplay render out" };
            _finalRT.Create();

            if (job.Sub > 1)
            {
                var res = _postFx.Resources ?? Resources.FindObjectsOfTypeAll<PostProcessResources>().FirstOrDefault();
                var shader = res != null ? res.shaders.texture2dLerp : null;
                if (shader == null || !shader.isSupported)
                {
                    Plugin.Log.LogWarning("[render] the post-processing lerp shader isn't available; rendering without motion blur");
                    job.Sub = 1;
                    job.SubN = null;
                    job.SubTotal = job.Frames;
                }
                else
                {
                    var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf;
                    _accA = new RenderTexture(job.W, job.H, 0, fmt, RenderTextureReadWrite.Linear) { name = "Streplay accum A" };
                    _accB = new RenderTexture(job.W, job.H, 0, fmt, RenderTextureReadWrite.Linear) { name = "Streplay accum B" };
                    _accA.Create();
                    _accB.Create();
                    var was = RenderTexture.active;
                    foreach (var rt in new[] { _accA, _accB }) { RenderTexture.active = rt; GL.Clear(false, true, Color.clear); }
                    RenderTexture.active = was;
                    _sheets = new PropertySheetFactory();
                    _lerp = _sheets.Get(shader);
                    _cmd = new CommandBuffer { name = "Streplay motion blur" };
                }
            }

            _cam.targetTexture = _camRT;
            _cam.ResetAspect();
            _targeted.Clear();
            float depth = _cam.depth;
            foreach (var c in _vmCams)
            {
                if (c == null) continue;
                c.targetTexture = _camRT;
                c.ResetAspect();
                _targeted.Add(c);
                depth = Mathf.Max(depth, c.depth);
            }

            // The game's HUD draws through this camera (see HudReplay.SetRenderCamera). A Screen Space - Camera
            // canvas is a real object in the world, so the camera sits far below the map where no world camera
            // looks. It is orthographic like an overlay canvas (the ammo counter is tilted in 3D and would be
            // foreshortened by a perspective camera), and its view is large so float precision stays sub-pixel.
            var go = new GameObject("Streplay Render UI Camera");
            go.transform.SetPositionAndRotation(new Vector3(0f, -100000f, 0f), Quaternion.identity);
            _uiCam = go.AddComponent<Camera>();
            _uiCam.orthographic = true;
            _uiCam.orthographicSize = 500f;
            _uiCam.clearFlags = CameraClearFlags.Depth;
            _uiCam.cullingMask = ~0;
            _uiCam.depth = depth + 10f;
            _uiCam.nearClipPlane = 1f;
            _uiCam.farClipPlane = 10000f;
            _uiCam.allowHDR = false;
            _uiCam.targetTexture = _camRT;
            _hud.SetRenderCamera(_uiCam);
            _postFx.SuppressMotionBlur(true);

        }

        void TeardownRenderTargets()
        {
            if (_cam != null && _cam.targetTexture == _camRT) { _cam.targetTexture = null; _cam.ResetAspect(); }
            foreach (var c in _targeted)
            {
                if (c == null) continue;
                c.targetTexture = null;
                c.ResetAspect();
            }
            _targeted.Clear();
            _hud.SetRenderCamera(null);
            if (_uiCam != null) Destroy(_uiCam.gameObject);
            _uiCam = null;
            _postFx.SuppressMotionBlur(false);
            _cmd?.Release();
            _cmd = null;
            _sheets?.Release();
            _sheets = null;
            _lerp = null;
            foreach (var rt in new[] { _camRT, _accA, _accB, _finalRT })
                if (rt != null) { rt.Release(); Destroy(rt); }
            _camRT = _accA = _accB = _finalRT = null;
            if (_syncReadback != null) { Destroy(_syncReadback); _syncReadback = null; }
        }

        bool WritersBehind => _job.Sink.Backlog + _readbacks.Count > _job.Sink.Workers + 2;

        // Update's part while rendering: place the replay at the current sub-frame's time.
        void StepRender()
        {
            if (_renderHold)
            {
                // this frame's game time was frozen; resuming takes effect from the next frame, which then steps
                if (!WritersBehind) { _renderHold = false; Time.timeScale = _job.SpeedAt(_job.TimeAt(_job.Frame, _job.SubIndex)); }
                return;
            }
            float target = Mathf.Clamp(_job.TimeAt(_job.Frame, _job.SubIndex), 0f, _data.Duration);
            float local = _job.SpeedAt(target);
            _renderDt = Mathf.Max(0f, target - _t) / local;
            _t = target;
            _engineT = target;
            Time.timeScale = local; // game time (particles, animations) follows the slow-mo too
            _renderStepped = true;
        }

        IEnumerator RenderCapture()
        {
            var eof = new WaitForEndOfFrame();
            while (_rendering)
            {
                yield return eof;
                if (!_rendering) yield break;
                try { CaptureSubFrame(); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[render] failed: {e}");
                    StopRender(true, e.Message);
                }
            }
        }

        void CaptureSubFrame()
        {
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CaptureSubFrameInner(); }
            finally
            {
                var pj = _job;
                if (pj != null) { long c1 = System.Diagnostics.Stopwatch.GetTimestamp(); pj.TCapture += c1 - c0; if (pj.TWall0 == 0) pj.TWall0 = c1; pj.TWall1 = c1; }
            }
        }

        float _engineT; // replay time of the last engine frame (game time steps from there)

        void CaptureSubFrameInner()
        {
            DrainReadbacks(false);
            if (_job.Sink.Error != null) { StopRender(true, _job.Sink.Error); return; }
            if (!_renderStepped) return; // started mid-frame: the cameras haven't drawn this sub-frame yet
            _renderStepped = false;
            if (Accumulate()) return;

            // More sub-frames of the same output frame without a new engine frame: re-pose the replay and redraw the
            // cameras by hand. Particles and physics stay where this engine frame left them until the next one.
            int extra = Mathf.Max(1, Plugin.RenderSubFramesPerUpdate.Value) - 1;
            for (int k = 0; k < extra && _rendering && _job.SubIndex > 0 && !WritersBehind; k++)
            {
                ManualSubFrame();
                if (Accumulate()) return;
            }

            var j = _job;
            float next = j.TimeAt(j.Frame, j.SubIndex);
            Time.captureDeltaTime = Mathf.Max(1e-5f, (next - _engineT) / j.SpeedAt(next));
            if (WritersBehind)
            {
                // freeze game time from the next frame on, so particles don't run ahead of the replay while waiting
                _renderHold = true;
                Time.timeScale = 0f;
            }
        }

        // the sub-frame the cameras just drew goes into the accumulation; true when the render is finished
        bool Accumulate()
        {
            var j = _job;
            int n = j.SubOf(j.Frame);
            j.SubDone++;
            RenderTexture latest = _camRT;
            if (n > 1)
            {
                // running weighted mean in linear float: acc = lerp(acc, sub, w / (sum of weights so far));
                // the first sub-frame copies
                var src = _accOnB ? _accB : _accA;
                var dst = _accOnB ? _accA : _accB;
                if (j.SubIndex == 0) j.WeightSum = 0f;
                float w = j.WeightOf(j.Frame, j.SubIndex);
                j.WeightSum += w;
                _cmd.Clear();
                _lerp.properties.SetTexture(ToId, _camRT);
                _lerp.properties.SetFloat(InterpId, j.SubIndex == 0 ? 1f : w / Mathf.Max(1e-6f, j.WeightSum));
                _cmd.BlitFullscreenTriangle(src, dst, _lerp, 0, false);
                Graphics.ExecuteCommandBuffer(_cmd);
                _accOnB = !_accOnB;
                latest = dst;
            }

            if (j.SubIndex >= n - 1)
            {
                Graphics.Blit(latest, _finalRT); // resolves MSAA, linear -> sRGB bytes
                RequestReadback(j.Frame);
                j.Frame++;
                j.SubIndex = 0;
            }
            else j.SubIndex++;
            RenderTexture.active = null;

            if (j.Frame >= j.Frames) { StopRender(false); return true; }
            return false;
        }

        // one sub-frame placed and drawn by hand: the same steps as Update + LateUpdate, then the cameras
        void ManualSubFrame()
        {
            var j = _job;
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float prev = _t;
            float target = Mathf.Clamp(j.TimeAt(j.Frame, j.SubIndex), 0f, _data.Duration);
            _renderDt = Mathf.Max(0f, target - _t) / j.SpeedAt(target);
            _t = target;
            if (_t > prev) FireEvents(prev, _t);
            Apply(_t);
            UpdateCamera();
            LateUpdateInner(); // bones, held items, viewmodel, HUD, post-processing, camera effects
            _cam.Render();
            foreach (var c in _targeted) if (c != null && c.enabled) c.Render();
            if (_uiCam != null) _uiCam.Render();
            j.TManual += System.Diagnostics.Stopwatch.GetTimestamp() - p0;
            j.ManualCount++;
        }

        void RequestReadback(int index)
        {
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                _readbacks.Enqueue((AsyncGPUReadback.Request(_finalRT, 0, TextureFormat.RGBA32), index));
                return;
            }
            if (_syncReadback == null) _syncReadback = new Texture2D(_finalRT.width, _finalRT.height, TextureFormat.RGBA32, false);
            var was = RenderTexture.active;
            RenderTexture.active = _finalRT;
            _syncReadback.ReadPixels(new Rect(0, 0, _finalRT.width, _finalRT.height), 0, 0, false);
            RenderTexture.active = was;
            var buf = _job.Sink.Rent();
            _syncReadback.GetRawTextureData<byte>().CopyTo(buf);
            _job.Sink.Submit(buf, index);
        }

        // hands finished readbacks to the writer in frame order; waits when asked or when too many are in flight
        void DrainReadbacks(bool wait)
        {
            while (_readbacks.Count > 0)
            {
                var (req, index) = _readbacks.Peek();
                if (!req.done)
                {
                    if (!wait && _readbacks.Count <= 3) break;
                    req.WaitForCompletion();
                }
                _readbacks.Dequeue();
                if (req.hasError)
                {
                    if (_readbackErrors++ < 5) Plugin.Log.LogWarning($"[render] reading back frame {index} failed");
                    continue;
                }
                var buf = _job.Sink.Rent();
                req.GetData<byte>().CopyTo(buf);
                _job.Sink.Submit(buf, index);
            }
        }

        void StopRender(bool cancelled = true, string error = null)
        {
            if (!_rendering) return;
            _rendering = false;
            _playing = false;
            var j = _job;
            try
            {
                AsyncGPUReadback.WaitAllRequests();
                DrainReadbacks(true);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[render] last frames lost: {e.Message}"); }
            _readbacks.Clear();
            Time.captureDeltaTime = 0f;
            Time.timeScale = 0f;
            QualitySettings.vSyncCount = j.SavedVSync;
            Application.targetFrameRate = j.SavedTargetFps;
            TeardownRenderTargets();
            if (j.SubDone > 1 && j.TWall1 > j.TWall0)
            {
                double f = System.Diagnostics.Stopwatch.Frequency, n = j.SubDone - 1;
                double ms(long t) => t / f * 1000.0 / n;
                double wall = (j.TWall1 - j.TWall0) / f;
                Plugin.Log.LogDebug($"[render] speed: {n:0} sub-frames in {wall:0.0}s = {n / wall:0} per second; per sub-frame {wall * 1000.0 / n:0.00} ms: " +
                                   $"replay update {ms(j.TUpdate):0.00}, late update/HUD {ms(j.TLate):0.00}, our UI {ms(j.TGui):0.00}, capture {ms(j.TCapture):0.00} ms, " +
                                   $"rest (engine, GPU, game scripts) {wall * 1000.0 / n - ms(j.TUpdate) - ms(j.TLate) - ms(j.TGui) - ms(j.TCapture):0.00} ms " +
                                   $"(vsync was {j.SavedVSync}, frame cap was {j.SavedTargetFps}); {j.ManualCount} drawn by hand" +
                                   (j.ManualCount > 0 ? $" at {j.TManual / f * 1000.0 / j.ManualCount:0.00} ms each" : ""));
            }

            int frames = j.Frame;
            var took = DateTime.Now - j.Began;
            string what = error != null ? $"Render failed after {frames} frames: {error}"
                : cancelled ? $"Render cancelled after {frames}/{j.Frames} frames"
                : $"Rendered {frames} frames in {took.TotalSeconds:0}s";
            Plugin.Log.LogInfo($"[render] {what} -> {j.Sink.OutputPath}");
            _renderSummary = what + (j.Format == RenderFormat.PngSequence ? "" : ", finishing the video...");
            Toast(_renderSummary);

            j.Sink.Finish(cancelled || error != null, sink =>
            {
                // runs on a worker thread: just hand a message to the main thread
                _renderDone = sink.Error != null ? $"Render output error: {sink.Error}"
                    : cancelled || error != null ? $"Saved the {frames} frames rendered before stopping: {sink.OutputPath}"
                    : $"Saved {sink.OutputPath}";
            });
        }

        // main thread: report a finished output
        void PollRenderDone()
        {
            var msg = _renderDone;
            if (msg == null) return;
            _renderDone = null;
            Plugin.Log.LogInfo($"[render] {msg}");
            _renderSummary = msg;
            Toast(msg, 8f);
        }

        void Toast(string text, float seconds = 4f)
        {
            _toast = text;
            _toastUntil = Time.unscaledTime + seconds;
        }

        // ---------------------------------------------------------------- UI

        bool _renderPanel;
        string _wText, _hText, _fpsText, _shutterText;

        void DrawAudioPassProgress()
        {
            var p = _audioPass;
            float rate = Mathf.Max(1, AudioSettings.outputSampleRate);
            float total = p.Need / rate, done = p.Recording ? p.Tap.Frames / rate : 0f;
            var r = new Rect(Ui.W / 2f - 330f, Ui.H - 140f, 660f, 104f);
            Ui.Panel(r, 14f);
            Ui.Text(new Rect(r.x + 24f, r.y + 12f, 420f, 28f), "Recording the sound".ToUpperInvariant(), 18, Ui.Bright, true);
            Ui.Text(new Rect(r.x + 24f, r.y + 40f, r.width - 48f, 20f), $"Plays the clip once in real time ({done:0.0} / {total:0.0} s), then the video renders.", 12, Ui.Dim);
            EscChip(r);
            Ui.Bar(new Rect(r.x + 24f, r.y + 72f, r.width - 48f, 8f), done / Mathf.Max(0.01f, total), Ui.Accent);
        }

        void DrawRenderProgress()
        {
            Ui.Fill(new Rect(0f, 0f, Ui.W, Ui.H), Color.black);
            var j = _job;
            float done = j.SubTotal > 0 ? (float)j.SubDone / j.SubTotal : (float)j.Frame / j.Frames;
            if (_finalRT != null && j.Frame > 0)
            {
                float a = (float)_finalRT.width / _finalRT.height;
                float w = Ui.W - 80f, h = w / a;
                if (h > Ui.H - 200f) { h = Ui.H - 200f; w = h * a; }
                var pr = new Rect((Ui.W - w) / 2f, 30f, w, h);
                if (Event.current.type == EventType.Repaint) GUI.DrawTexture(pr, _finalRT, ScaleMode.ScaleToFit, false);
                Ui.Border(pr, Ui.Wash(0.12f), 1f, 4f);
            }
            var took = DateTime.Now - j.Began;
            string eta = done > 0.01f ? $"about {TimeSpan.FromSeconds(took.TotalSeconds * (1 - done) / done):m\\:ss} left" : "estimating…";
            var r = new Rect(Ui.W / 2f - 380f, Ui.H - 140f, 760f, 112f);
            Ui.Panel(r, 14f);
            Ui.Text(new Rect(r.x + 24f, r.y + 12f, 420f, 28f), "Rendering".ToUpperInvariant(), 18, Ui.Bright, true);
            Ui.Text(new Rect(r.x + 24f, r.y + 40f, r.width - 48f, 20f),
                $"frame {j.Frame + 1} / {j.Frames}" + (j.SubOf(j.Frame) > 1 ? $"  ·  sub-frame {j.SubIndex + 1}/{j.SubOf(j.Frame)}" : "") +
                $"  ·  {j.W}×{j.H} @ {j.Fps} fps  ·  {j.Format}" + (j.Ramp != null ? "  ·  slow-mo" : ""), 12, Ui.Dim);
            EscChip(r);
            Ui.Bar(new Rect(r.x + 24f, r.y + 74f, r.width - 140f, 8f), done, Ui.Accent);
            Ui.Text(new Rect(r.xMax - 110f, r.y + 64f, 90f, 28f), $"{done * 100f:0}%", 20, Ui.Bright, true, TextAnchor.MiddleRight);
            Ui.Text(new Rect(r.x + 24f, r.y + 86f, 300f, 20f), eta, 11, Ui.Faint);
        }

        static void EscChip(Rect card)
        {
            var c = new Rect(card.xMax - 140f, card.y + 16f, 116f, 24f);
            Ui.Slant(c, Ui.Wash(0.08f));
            Ui.Text(c, "ESC CANCELS", 11, Ui.Dim, true, TextAnchor.MiddleCenter);
        }

        static string IntField(Rect r, ref string buf, ConfigEntry<int> e)
        {
            buf ??= e.Value.ToString();
            string n = Ui.Field(r, buf, 6);
            if (n != buf) { buf = n; if (int.TryParse(n, out int v) && v > 0) e.Value = v; }
            return buf;
        }

        void DrawRenderPanel()
        {
            float w = 660f, h = 748f + ((HudReplay.Parts.Length + 3) / 4 - 2) * 32f;
            var p = new Rect(16f, Ui.H - 150f - 16f - 12f - h, w, h);
            if (p.y < 12f) { h += p.y - 12f; p = new Rect(p.x, 12f, w, h); }
            Claim(p);
            Ui.Panel(p, 14f);

            // header
            Ui.Text(new Rect(p.x + 22f, p.y + 12f, 200f, 30f), "RENDER", 22, Ui.Bright, true);
            float start = _in ?? _t, stop = _out ?? _data.Duration;
            if (stop <= start) stop = _data.Duration;
            int fps = Mathf.Max(1, Plugin.RenderFps.Value);
            var ramp = RampFor(start, stop);
            int frames = PlanFrames(start, stop, Speed, fps, ramp).frames;
            Ui.Text(new Rect(p.x + 22f, p.y + 40f, w - 260f, 18f),
                $"{Fmt(start)} → {Fmt(stop)}  ·  {frames / (float)fps:0.00} s of video  ·  {frames} frames", 12, Ui.Dim);
            float sw = Ui.AutoW("Start render", Ui.IconFilm, 36f, 14);
            if (Ui.Button(new Rect(p.xMax - 52f - sw, p.y + 16f, sw, 36f), "Start render", false, Ui.Kind.Accent, Ui.IconFilm, 14,
                    "Render the clip range (or from here to the end) from the camera you're in")) _startRender = true;
            if (Ui.Button(new Rect(p.xMax - 44f, p.y + 18f, 32f, 32f), "", false, Ui.Kind.Ghost, Ui.IconClose, 12, "Close (R)")) _renderPanel = false;
            Ui.Fill(new Rect(p.x + 16f, p.y + 66f, w - 32f, 1f), Ui.Line);

            float lx = p.x + 22f, cx = p.x + 140f, y = p.y + 78f;
            const float rh = 30f, gap = 6f;
            void Label(string s) => Ui.Text(new Rect(lx, y, 116f, rh), s.ToUpperInvariant(), 12, Ui.Dim, true);
            void Section(string s) { Ui.Heading(new Rect(lx, y, 200f, 18f), s); y += 22f; }

            // ---- output
            Section("Output");
            Label("Size");
            int rw = Plugin.RenderWidth.Value, rh2 = Plugin.RenderHeight.Value;
            int sizeSel = rw == 1920 && rh2 == 1080 ? 0 : rw == 2560 && rh2 == 1440 ? 1 : rw == 3840 && rh2 == 2160 ? 2 : -1;
            int ns = Ui.Segmented(new Rect(cx, y, 230f, rh), new[] { "1080p", "1440p", "4K" }, sizeSel);
            if (ns != sizeSel)
            {
                var (pw, ph) = ns == 0 ? (1920, 1080) : ns == 1 ? (2560, 1440) : (3840, 2160);
                Plugin.RenderWidth.Value = pw; Plugin.RenderHeight.Value = ph; _wText = _hText = null;
            }
            IntField(new Rect(cx + 244f, y, 70f, rh), ref _wText, Plugin.RenderWidth);
            Ui.Text(new Rect(cx + 318f, y, 14f, rh), "×", 13, Ui.Dim, false, TextAnchor.MiddleCenter);
            IntField(new Rect(cx + 334f, y, 70f, rh), ref _hText, Plugin.RenderHeight);
            y += rh + gap;

            Label("Frame rate");
            int[] rates = { 30, 60, 120, 240 };
            int fSel = Array.IndexOf(rates, Plugin.RenderFps.Value);
            int nf = Ui.Segmented(new Rect(cx, y, 230f, rh), new[] { "30", "60", "120", "240" }, fSel);
            if (nf != fSel) { Plugin.RenderFps.Value = rates[nf]; _fpsText = null; }
            IntField(new Rect(cx + 244f, y, 70f, rh), ref _fpsText, Plugin.RenderFps);
            Ui.Text(new Rect(cx + 320f, y, 40f, rh), "fps", 12, Ui.Dim);
            y += rh + gap;

            Label("Format");
            var formats = new[] { RenderFormat.H264, RenderFormat.H265, RenderFormat.ProRes4444, RenderFormat.PngSequence };
            int fmSel = Array.IndexOf(formats, Plugin.RenderFormatSetting.Value);
            int nfm = Ui.Segmented(new Rect(cx, y, 404f, rh), new[] { "H.264", "H.265", "ProRes 4444", "PNG" }, fmSel, new[] { 1f, 1f, 1.5f, 0.9f });
            if (nfm != fmSel) Plugin.RenderFormatSetting.Value = formats[nfm];
            y += rh + gap;

            Label("Sound");
            int au = Ui.Segmented(new Rect(cx, y, 140f, rh), new[] { "Off", "On" }, Plugin.RenderAudio.Value ? 1 : 0);
            Plugin.RenderAudio.Value = au == 1;
            if (Plugin.RenderAudio.Value) Ui.Text(new Rect(cx + 152f, y, 300f, rh), "recorded in real time before the render", 11, Ui.Faint);
            y += rh + gap + 6f;

            // ---- motion
            Section("Motion");
            Label("Motion blur");
            int[] subs = { 8, 16, 32, 64 };
            int bSel = Plugin.RenderMotionBlur.Value ? Array.IndexOf(subs, Plugin.RenderSubFrames.Value) + 1 : 0;
            int nb = Ui.Segmented(new Rect(cx, y, 300f, rh), new[] { "Off", "8", "16", "32", "64" }, bSel);
            if (nb != bSel) { Plugin.RenderMotionBlur.Value = nb > 0; if (nb > 0) Plugin.RenderSubFrames.Value = subs[nb - 1]; }
            Ui.Text(new Rect(cx + 312f, y, 160f, rh), "sub-frames per frame", 11, Ui.Faint);
            y += rh + gap;

            Label("Shutter");
            float[] shutters = { 90f, 180f, 270f, 360f };
            int shSel = Array.FindIndex(shutters, s => Mathf.Approximately(s, Plugin.RenderShutter.Value));
            int nsh = Ui.Segmented(new Rect(cx, y, 300f, rh), new[] { "90°", "180°", "270°", "360°" }, shSel);
            if (nsh != shSel) { Plugin.RenderShutter.Value = shutters[nsh]; _shutterText = null; }
            _shutterText ??= Plugin.RenderShutter.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            string st = Ui.Field(new Rect(cx + 312f, y, 56f, rh), _shutterText, 3);
            if (st != _shutterText) { _shutterText = st; if (float.TryParse(st.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sv) && sv >= 1 && sv <= 360) Plugin.RenderShutter.Value = sv; }
            y += rh + gap;

            Label("Blur look");
            int lk = Ui.Segmented(new Rect(cx, y, 300f, rh), new[] { "Film", "Smooth" }, Plugin.RenderBlurWeighting.Value == BlurWeighting.Gaussian ? 1 : 0);
            var want = lk == 1 ? BlurWeighting.Gaussian : BlurWeighting.Equal;
            if (want != Plugin.RenderBlurWeighting.Value)
            {
                Plugin.RenderBlurWeighting.Value = want;
                Plugin.RenderShutter.Value = want == BlurWeighting.Gaussian ? 360f : 180f;
                _shutterText = null;
            }
            Ui.Tip(new Rect(cx, y, 300f, rh), "Film: even streaks like a camera. Smooth: soft, frame-blended blur like HLAE / RSMB (sets a 360° shutter)");
            Ui.Text(new Rect(cx + 312f, y, 200f, rh), want == BlurWeighting.Gaussian ? "HLAE / RSMB style" : "camera shutter", 11, Ui.Faint);
            y += rh + gap;

            Label("Slow-mo");
            float[] slows = { 0.2f, 0.3f, 0.5f };
            int smSel = Plugin.SlowMoKills.Value ? Array.FindIndex(slows, v => Mathf.Approximately(v, Plugin.SlowMoSpeed.Value)) + 1 : 0;
            int nsm = Ui.Segmented(new Rect(cx, y, 300f, rh), new[] { "Off", "0.2x", "0.3x", "0.5x" }, smSel);
            if (nsm != smSel) { Plugin.SlowMoKills.Value = nsm > 0; if (nsm > 0) Plugin.SlowMoSpeed.Value = slows[nsm - 1]; }
            if (Plugin.SlowMoKills.Value)
            {
                float[] holds = { 0.3f, 0.6f, 1f };
                int hSel = Array.FindIndex(holds, v => Mathf.Approximately(v, Plugin.SlowMoHold.Value));
                int nh = Ui.Segmented(new Rect(cx + 312f, y, 180f, rh), new[] { "0.3s", "0.6s", "1s" }, hSel);
                if (nh != hSel) Plugin.SlowMoHold.Value = holds[nh];
                Ui.Tip(new Rect(cx + 312f, y, 180f, rh), "How long the kill stays fully slowed (replay time)");
            }
            else Ui.Text(new Rect(cx + 312f, y, 200f, rh), "eases in and out around your kills", 11, Ui.Faint);
            y += rh + gap + 6f;

            // ---- look
            Section("Look");
            Label("Game HUD");
            for (int i = 0; i < HudReplay.Parts.Length; i++)
            {
                var e = Plugin.HudShow[i];
                var cr = new Rect(cx + (i % 4) * 124f, y + (i / 4) * 32f, 118f, 26f);
                if (Ui.Chip(cr, HudReplay.Parts[i].label, e.Value, e.Value ? "Shown: click to hide it in My view and renders" : "Hidden: click to show it")) e.Value = !e.Value;
            }
            y += (HudReplay.Parts.Length + 3) / 4 * 32f + gap;

            Label("Outlines");
            int ol = Ui.Segmented(new Rect(cx, y, 140f, rh), new[] { "Off", "On" }, Plugin.PlayerOutlines.Value ? 1 : 0);
            Plugin.PlayerOutlines.Value = ol == 1;
            Ui.Text(new Rect(cx + 152f, y, 300f, rh), "around players, in your outline colours", 11, Ui.Faint);
            y += rh + gap;

            Label("Viewmodel");
            bool kestrel = KestrelViewmodels.Installed;
            GUI.enabled = kestrel;
            int vm = Ui.Segmented(new Rect(cx, y, 200f, rh), new[] { "Default", "Kestrel" }, kestrel && Plugin.Viewmodel.Value == ViewmodelStyle.Kestrel ? 1 : 0);
            GUI.enabled = true;
            if (kestrel) Plugin.Viewmodel.Value = vm == 1 ? ViewmodelStyle.Kestrel : ViewmodelStyle.Default;
            Ui.Text(new Rect(cx + 212f, y, 300f, rh), kestrel ? "Kestrel: as set in Minimal Viewmodels Reborn" : "Kestrel needs Minimal Viewmodels Reborn", 11, Ui.Faint);
            y += rh + gap;

            Label("Folder");
            string folder = Ui.Field(new Rect(cx, y, w - 160f, rh), Plugin.RenderFolder.Value);
            if (folder != Plugin.RenderFolder.Value) Plugin.RenderFolder.Value = folder;
            if (string.IsNullOrWhiteSpace(Plugin.RenderFolder.Value))
                Ui.Text(new Rect(cx + 10f, y, w - 180f, rh), Path.Combine(Plugin.ReplayFolder.Value, "renders"), 12, Ui.Faint);
            y += rh + 10f;

            // footer: ffmpeg and the last result
            string ffmpeg = Ffmpeg.Cached;
            if (Plugin.RenderFormatSetting.Value != RenderFormat.PngSequence)
                Ui.Text(new Rect(lx, y, w - 44f, 34f), ffmpeg != null ? "ffmpeg: " + ffmpeg : Ffmpeg.NotFoundMessage, 11, ffmpeg != null ? Ui.Faint : Ui.Warn, false, TextAnchor.UpperLeft, true);
            y += 34f;
            if (_renderSummary != null) Ui.Text(new Rect(lx, y, w - 44f, 34f), _renderSummary, 11, Ui.Good, false, TextAnchor.UpperLeft, true);
        }

        // one render, or with extra angles on, one per angle over the same range (lined up for multicam editing)
        bool _startRender; // set from the GUI, started at the top of the next Update so the whole frame is a render frame
    }
}
