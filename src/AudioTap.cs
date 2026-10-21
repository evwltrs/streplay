using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Streplay
{
    // Sits next to the replay camera's AudioListener and copies the final mix (everything the replay plays:
    // effect RPC sounds, recorded sounds, loops, with their 3D panning) while recording. Used for the render's
    // audio track, which is captured in a real-time pass because sound can't be rendered faster than real time.
    internal sealed class AudioTap : MonoBehaviour
    {
        readonly object _lock = new object();
        List<float> _samples = new List<float>();
        volatile bool _on;
        int _channels = 2;

        public int SampleRate { get; private set; }
        public int Channels { get { lock (_lock) return _channels; } }
        public int Frames { get { lock (_lock) return _samples.Count / Math.Max(1, _channels); } }

        public void Begin()
        {
            SampleRate = AudioSettings.outputSampleRate;
            lock (_lock) _samples = new List<float>(SampleRate * 2 * 30);
            _on = true;
        }

        public void Stop() => _on = false;

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_on) return;
            lock (_lock)
            {
                _channels = channels;
                _samples.AddRange(data);
            }
            // recorded, then silenced: the sound pass doesn't have to play through the speakers
            if (Plugin.RenderMuteSoundPass.Value) Array.Clear(data, 0, data.Length);
        }

        // 16-bit PCM WAV of `frames` output sample frames, each read from the recording at srcFrame(n) (fractional,
        // linearly interpolated): an edit segment's sound bent through its speed curve, like tape. Scaled by gain.
        public void WriteWavMapped(string path, int frames, Func<int, double> srcFrame, float gain)
        {
            float[] s;
            int ch;
            lock (_lock) { s = _samples.ToArray(); ch = Math.Max(1, _channels); }
            int have = s.Length / ch;
            using (var w = new BinaryWriter(File.Create(path)))
            {
                WriteHeader(w, frames, ch);
                for (int n = 0; n < frames; n++)
                {
                    double x = srcFrame(n);
                    int i = (int)Math.Floor(x);
                    float f = (float)(x - i);
                    for (int c = 0; c < ch; c++)
                    {
                        float a = i >= 0 && i < have ? s[i * ch + c] : 0f;
                        float b = i + 1 >= 0 && i + 1 < have ? s[(i + 1) * ch + c] : a;
                        w.Write((short)Mathf.Clamp(Mathf.RoundToInt((a + (b - a) * f) * gain * 32767f), -32768, 32767));
                    }
                }
            }
        }

        void WriteHeader(BinaryWriter w, int frames, int ch)
        {
            int bytes = frames * ch * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + bytes);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)ch);
            w.Write(SampleRate);
            w.Write(SampleRate * ch * 2);
            w.Write((short)(ch * 2));
            w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(bytes);
        }

        // 16-bit PCM WAV of exactly `frames` sample frames (cut or padded with silence)
        public void WriteWav(string path, int frames)
        {
            float[] s;
            int ch;
            lock (_lock) { s = _samples.ToArray(); ch = Math.Max(1, _channels); }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int bytes = frames * ch * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)ch);
                w.Write(SampleRate);
                w.Write(SampleRate * ch * 2);
                w.Write((short)(ch * 2));
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(bytes);
                for (int i = 0; i < frames * ch; i++)
                    w.Write(i < s.Length ? (short)Mathf.Clamp(Mathf.RoundToInt(s[i] * 32767f), -32768, 32767) : (short)0);
            }
        }
    }
}
