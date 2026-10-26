using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace Streplay
{
    // Small JSON file written next to each .strp so the replay browser can list recordings
    // and their markers without decompressing every replay.
    internal sealed class ReplayMeta
    {
        public int Version = 1;
        public string ReplayFile = "";
        public string Scene = "";
        public DateTime StartUtc;
        public float Duration;
        public float SampleRate;        // snapshots per second it was recorded at, 0 = unknown (older files: 60)
        public string SessionId = "";
        public int RoundIndex;
        public int Part;                // 2+ when recording was switched off and on again during this take, 0/1 otherwise
        public int Take;                // take number within the map (1-based), 0 = unknown (older files)
        public string GameVersion = "";
        public bool Finished;
        public List<MetaPlayer> Players = new List<MetaPlayer>();
        public List<MetaMarker> Markers = new List<MetaMarker>();
        public int HighlightsVersion;   // 0 = not scanned yet (see Highlights.Version)
        public List<MetaHighlight> Highlights = new List<MetaHighlight>();
        // what the scan measured (see Highlights.FeaturesVersion): enough to score the highlights again without
        // reading the recording, so a scoring change re-ranks everything in a moment
        public int FeaturesVersion;
        public List<Highlights.KillRec> KillRecs;
        public List<float> TakeWins;
        public Dictionary<string, float> WeaponZoom;
        // played in a STRAFTH arena instead of the map ("STRAFTH"), null = the map. Such rounds stay out of
        // Highlights. ArenaCheck: Highlights.ArenaCheckVersion once known (recorded, or looked for in an older file).
        public string ModArena;
        public int ArenaCheck;

        [JsonIgnore] public string MetaPath = "";
        [JsonIgnore] public string ReplayPath => Path.Combine(Path.GetDirectoryName(MetaPath) ?? "", ReplayFile);

        public sealed class MetaPlayer
        {
            public string Name = "";
            public int PlayerId;
            public ulong SteamId;
            public bool IsLocal;
            public int? TeamId;         // the team the game outlined them with (1v1 / free-for-all: their own); null = unknown (older files)
        }

        public sealed class MetaMarker
        {
            public float T;
            public string Label = "";
            public bool Auto;
        }

        // a moment worth a clip: a kill or multi-kill (T0..T1 = first..last kill), or a flagged marker
        public sealed class MetaHighlight
        {
            public float T0, T1;
            public int Kills;
            public float Score;
            public List<float> KillT = new List<float>();  // every kill's time (a multi-kill has several)
            public List<string> Tags = new List<string>();
            public string Detail = "";
        }

        // One writer at a time (the highlight scan saves from a background thread), and the old file is only replaced
        // once the new one is fully written, so a crash or a locked file can't leave a replay without its .json.
        static readonly object SaveLock = new object();

        public void Save()
        {
            lock (SaveLock)
            {
                long seq = Interlocked.Increment(ref _seq);
                lock (QueueLock) Pending.Remove(MetaPath); // this copy is newer than anything still queued
                Write(MetaPath, JsonConvert.SerializeObject(this, Formatting.Indented), seq);
            }
        }

        // Saves made mid-round (a kill or death marker, a player joining) don't wait for the disk: the game thread only
        // turns the metadata into JSON, and a background thread writes it. On a hard drive that's also busy with the
        // recording, a write and its file swap can take a good part of a second, which showed as a hitch on every kill.
        public void SaveSoon()
        {
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            lock (QueueLock)
            {
                Pending[MetaPath] = (Interlocked.Increment(ref _seq), json);
                if (_writer == null)
                {
                    _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Streplay metadata writer" };
                    _writer.Start();
                }
            }
            Wake.Set();
        }

        // The recording is being deleted: nothing queued for it may write its .json back.
        public static void Discard(string metaPath)
        {
            lock (SaveLock)
            {
                lock (QueueLock) Pending.Remove(metaPath);
                Written[metaPath] = long.MaxValue;
            }
        }

        // Writes whatever is still queued, now (the game is closing).
        public static void FlushPending()
        {
            while (TakePending(out var path, out var item))
                try { lock (SaveLock) Write(path, item.json, item.seq); }
                catch (Exception e) { Plugin.Log.LogWarning($"Could not save {Path.GetFileName(path)}: {e.Message}"); }
        }

        static readonly object QueueLock = new object();
        static readonly Dictionary<string, (long seq, string json)> Pending = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, long> Written = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); // newest save on disk, under SaveLock
        static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        static Thread _writer;
        static long _seq;

        static bool TakePending(out string path, out (long seq, string json) item)
        {
            lock (QueueLock)
            {
                foreach (var kv in Pending)
                {
                    path = kv.Key;
                    item = kv.Value;
                    Pending.Remove(path);
                    return true;
                }
            }
            path = null;
            item = default;
            return false;
        }

        static void WriteLoop()
        {
            while (true)
            {
                Wake.WaitOne();
                FlushPending();
            }
        }

        // caller holds SaveLock
        static void Write(string path, string json, long seq)
        {
            if (Written.TryGetValue(path, out long newest) && newest > seq) return; // a newer copy is already there
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, json);
            try
            {
                if (!File.Exists(path)) File.Move(tmp, path);
                else
                {
                    try { File.Replace(tmp, path, null); }
                    catch (PlatformNotSupportedException) { File.Delete(path); File.Move(tmp, path); }
                }
                Written[path] = seq;
            }
            catch
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
        }

        // Read the file, change it and write it back as one step (no other save in between), so a background
        // update can't undo a marker the player just added.
        public static void UpdateFile(string metaPath, Action<ReplayMeta> change)
        {
            lock (SaveLock)
            {
                var m = Load(metaPath);
                if (m == null) return;
                change(m);
                m.Save();
            }
        }

        public static ReplayMeta Load(string metaPath)
        {
            var m = JsonConvert.DeserializeObject<ReplayMeta>(File.ReadAllText(metaPath));
            if (m == null) return null;
            m.MetaPath = metaPath;
            return m;
        }
    }
}
