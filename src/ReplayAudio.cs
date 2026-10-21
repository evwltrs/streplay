using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Streplay
{
    // Plays back recorded sounds (v6): one-shots through the puppet's own AudioSource when it can
    // (same mixer group, rolloff and position as live), otherwise through a pooled stand-in source set up
    // from the recorded parameters; and looping sources (slide, fall wind) driven from their per-frame state.
    internal sealed class ReplayAudio
    {
        internal static bool LogEach;  // autotest: log every sound played
        public Func<GameObject, bool> IsEffectObject;

        ReplayData _data;
        Puppet[] _puppets;
        Transform _listener;
        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        readonly HashSet<string> _missing = new HashSet<string>();
        GameObject _root;
        readonly List<AudioSource> _pool = new List<AudioSource>();
        int _poolNext;
        int _nextSound;
        readonly List<AudioSource> _puppetSources = new List<AudioSource>();
        readonly HashSet<AudioSource> _usedSources = new HashSet<AudioSource>();  // non-puppet sources we played on

        sealed class LoopState
        {
            public AudioSource Src;
            public bool Resolved;
            public int PlayCount = -1;
            public float StartT = -1f;
        }
        readonly Dictionary<int, LoopState> _loops = new Dictionary<int, LoopState>();

        public int PlayedStandalone, PlayedForRpc, LoopsStarted;

        public void Bind(ReplayData data, Puppet[] puppets, Transform listener)
        {
            Dispose();
            _data = data;
            _puppets = puppets;
            _listener = listener;
            _root = new GameObject("Streplay Audio");
            foreach (var p in puppets)
                if (p?.Go != null && p.Owned) _puppetSources.AddRange(p.Go.GetComponentsInChildren<AudioSource>(true));
            PlayedStandalone = PlayedForRpc = LoopsStarted = 0;
            Plugin.Log.LogDebug($"Replay audio: {data.Sounds.Count} sounds ({data.Sounds.Count(s => s.Rpc != null)} tied to effect RPCs), {data.AudioSources.Count} tracked sources");
        }

        public void Dispose()
        {
            StopAll();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _pool.Clear();
            _puppetSources.Clear();
            _loops.Clear();
            _clips.Clear();
            _missing.Clear();
            AudioListener.pause = false;
        }

        public string Stats => $"sounds played: {PlayedStandalone} standalone, {PlayedForRpc} for effect RPCs, {LoopsStarted} loop starts" +
                               (_missing.Count > 0 ? $"; missing clips: {string.Join(", ", _missing.Take(10))}" : "");

        // ---------------------------------------------------------------- timeline

        public void Seek(float t)
        {
            if (_data == null) return;
            _nextSound = LowerBound(t);
            StopAll();
        }

        int LowerBound(float t)
        {
            var ev = _data.Sounds;
            int lo = 0, hi = ev.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (ev[mid].T <= t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // Sounds that weren't played by an effect RPC, between two playback times (forward only).
        public void Fire(float from, float to)
        {
            if (_data == null) return;
            var list = _data.Sounds;
            while (_nextSound < list.Count && list[_nextSound].T <= to)
            {
                var s = list[_nextSound++];
                if (s.T <= from || s.Rpc != null) continue;
                if (to - s.T > 0.5f) continue; // after a hitch, don't fire a burst of stale sounds
                if ((s.Flags & SoundFlags.ScenePath) != 0 && IsEffectObject != null)
                {
                    // played by an object an effect spawned (ragdoll impacts...): the replayed copy makes its own
                    var anchor = ResolveAnchor(s.Obj, s.Path, s.Flags);
                    if (anchor != null && IsEffectObject(anchor.root.gameObject))
                    {
                        if (LogEach) Plugin.Log.LogDebug($"[sound] t={s.T:0.00} clip={s.Clip} left to the replayed {anchor.root.name}");
                        continue;
                    }
                }
                if (Play(s)) PlayedStandalone++;
            }
        }

        // An effect RPC was replayed (or skipped): play the sounds it made live that the replay didn't make
        // itself (`madeByReplay` = clips the replayed body played, consumed one per recorded sound).
        public void PlayForRpc(ReplayEvent ev, List<string> madeByReplay)
        {
            if (ev.Sounds == null) return;
            foreach (var s in ev.Sounds)
            {
                if (madeByReplay.Remove(s.Clip)) continue;
                if (Play(s)) PlayedForRpc++;
            }
        }

        void StopAll()
        {
            foreach (var s in _pool) if (s != null) s.Stop();
            foreach (var s in _puppetSources) if (s != null && s.isPlaying) s.Stop();
            foreach (var s in _usedSources) if (s != null) s.Stop();
            _usedSources.Clear();
            foreach (var l in _loops.Values) { l.PlayCount = -1; l.StartT = -1f; }
        }

        // ---------------------------------------------------------------- one-shots

        bool Play(SoundEvent s)
        {
            if (!Plugin.ReplaySounds.Value) return false;
            var clip = Clip(s.Clip);
            if (clip == null) return false;

            var anchor = ResolveAnchor(s.Obj, s.Path, s.Flags);
            AudioSource orig = null;
            if (anchor != null)
            {
                var all = anchor.GetComponents<AudioSource>();
                orig = s.SourceIndex < all.Length ? all[s.SourceIndex] : all.FirstOrDefault();
            }

            AudioSource target;
            if (orig != null && orig.isActiveAndEnabled)
            {
                target = orig;
                if (s.Obj < 0) _usedSources.Add(orig);
            }
            else
            {
                target = Take();
                if (orig != null) CopySettings(orig, target);
                else
                {
                    target.outputAudioMixerGroup = null;
                    target.spatialBlend = s.SpatialBlend;
                    target.rolloffMode = (AudioRolloffMode)s.Rolloff == AudioRolloffMode.Custom ? AudioRolloffMode.Logarithmic : (AudioRolloffMode)s.Rolloff;
                    target.minDistance = s.MinDistance;
                    target.maxDistance = s.MaxDistance;
                    target.dopplerLevel = 0f;
                }
                var pos = anchor != null ? anchor.position : s.Pos;
                if (target.spatialBlend < 0.01f && _listener != null) pos = _listener.position;
                target.transform.position = pos;
            }
            target.pitch = s.Pitch;
            target.volume = s.SourceVolume;
            AudioCapture.OwnPlay = true;
            try { target.PlayOneShot(clip, s.VolumeScale); }
            finally { AudioCapture.OwnPlay = false; }
            if (LogEach)
                Plugin.Log.LogDebug($"[sound] t={s.T:0.00} clip={s.Clip} via={(target == orig ? "source" : "standin")} anchor={(s.Obj >= 0 ? _data.Objects[s.Obj].Name + "/" + s.Path : s.Path)} 2d={s.SpatialBlend < 0.01f} rpc={s.Rpc?.Method ?? "-"}");
            return true;
        }

        Transform ResolveAnchor(int obj, string path, byte flags)
        {
            if (obj >= 0)
            {
                var p = _puppets != null && obj < _puppets.Length ? _puppets[obj] : null;
                if (p?.Go == null) return null;
                return string.IsNullOrEmpty(path) ? p.Go.transform : p.Go.transform.Find(path);
            }
            if ((flags & SoundFlags.Ddol) != 0)
            {
                var sm = SoundManager.Instance;
                return sm != null ? FindScenePath(sm.gameObject.scene, path) : null;
            }
            if ((flags & SoundFlags.ScenePath) != 0) return FindScenePath(SceneManager.GetActiveScene(), path);
            return null;
        }

        internal static Transform FindScenePath(Scene scene, string path)
        {
            if (string.IsNullOrEmpty(path) || !scene.IsValid()) return null;
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != rootName) continue;
                var t = slash < 0 ? root.transform : root.transform.Find(path.Substring(slash + 1));
                if (t != null) return t;
            }
            return null;
        }

        AudioSource Take()
        {
            if (_pool.Count < 32)
            {
                var go = new GameObject("Streplay Sound");
                go.transform.SetParent(_root.transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                _pool.Add(src);
                return src;
            }
            _poolNext = (_poolNext + 1) % _pool.Count;
            var s = _pool[_poolNext];
            s.Stop();
            return s;
        }

        static void CopySettings(AudioSource from, AudioSource to)
        {
            to.outputAudioMixerGroup = from.outputAudioMixerGroup;
            to.spatialBlend = from.spatialBlend;
            to.rolloffMode = from.rolloffMode;
            to.minDistance = from.minDistance;
            to.maxDistance = from.maxDistance;
            to.dopplerLevel = from.dopplerLevel;
            to.spread = from.spread;
            to.priority = from.priority;
            to.reverbZoneMix = from.reverbZoneMix;
            to.bypassEffects = from.bypassEffects;
            to.bypassListenerEffects = from.bypassListenerEffects;
            to.bypassReverbZones = from.bypassReverbZones;
            if (from.rolloffMode == AudioRolloffMode.Custom)
                to.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
        }

        AudioClip Clip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_clips.TryGetValue(name, out var c) && c != null) return c;
            if (_missing.Contains(name)) return null;
            foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (clip != null && !_clips.ContainsKey(clip.name)) _clips[clip.name] = clip;
            if (_clips.TryGetValue(name, out c) && c != null) return c;
            _missing.Add(name);
            Plugin.Log.LogWarning($"Replay sound clip '{name}' isn't loaded; it will be silent");
            return null;
        }

        // ---------------------------------------------------------------- looping sources

        // Drive tracked sources (slide, fall wind, NetworkAudioSource...) to their recorded state.
        public void ApplySources(int frame, float t)
        {
            if (_data == null || _data.AudioSources.Count == 0 || !Plugin.ReplaySounds.Value) return;
            foreach (var info in _data.AudioSources.Values)
            {
                if (!_loops.TryGetValue(info.Id, out var ls)) _loops[info.Id] = ls = new LoopState();
                if (!ls.Resolved)
                {
                    var anchor = ResolveAnchor(info.Obj, info.Path, 0);
                    if (anchor != null)
                    {
                        var all = anchor.GetComponents<AudioSource>();
                        ls.Src = info.SourceIndex < all.Length ? all[info.SourceIndex] : all.FirstOrDefault();
                    }
                    ls.Resolved = true;
                }
                var src = ls.Src;
                if (src == null) continue;

                int k = StateAt(info, frame);
                bool playing = k >= 0 && info.States[k].Playing && src.gameObject.activeInHierarchy;
                if (!playing)
                {
                    if (ls.PlayCount >= 0 && src.isPlaying) src.Stop();
                    ls.PlayCount = -1;
                    continue;
                }
                var st = info.States[k];
                var clip = Clip(st.Clip);
                if (clip == null) continue;
                if (!src.enabled) src.enabled = true;
                src.loop = st.Loop;
                src.volume = st.Volume;
                src.pitch = st.Pitch;
                if (ls.PlayCount == st.PlayCount && ls.StartT == st.StartT && src.clip == clip) continue;
                float off = (t - st.StartT) * Mathf.Abs(st.Pitch);
                if (st.Loop && clip.length > 0f) off %= clip.length;
                else if (off >= clip.length - 0.02f) { ls.PlayCount = st.PlayCount; ls.StartT = st.StartT; continue; }
                src.clip = clip;
                AudioCapture.OwnPlay = true;
                try
                {
                    src.Play();
                    src.time = Mathf.Max(0f, off);
                }
                finally { AudioCapture.OwnPlay = false; }
                ls.PlayCount = st.PlayCount;
                ls.StartT = st.StartT;
                LoopsStarted++;
                if (LogEach)
                    Plugin.Log.LogDebug($"[sound] t={t:0.00} loop={st.Clip} at {off:0.00}s vol={st.Volume:0.00} anchor={_data.Objects[info.Obj].Name}/{info.Path}");
            }
        }

        static int StateAt(AudioSrcInfo info, int frame)
        {
            var fr = info.Frames;
            int lo = 0, hi = fr.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (fr[mid] <= frame) { best = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return best;
        }
    }
}
