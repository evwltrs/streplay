using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public string SessionId = "";
        public int RoundIndex;
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

        [JsonIgnore] public string MetaPath = "";
        [JsonIgnore] public string ReplayPath => Path.Combine(Path.GetDirectoryName(MetaPath) ?? "", ReplayFile);

        public sealed class MetaPlayer
        {
            public string Name = "";
            public int PlayerId;
            public ulong SteamId;
            public bool IsLocal;
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
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                string tmp = MetaPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllText(tmp, json);
                try
                {
                    if (!File.Exists(MetaPath)) File.Move(tmp, MetaPath);
                    else
                    {
                        try { File.Replace(tmp, MetaPath, null); }
                        catch (PlatformNotSupportedException) { File.Delete(MetaPath); File.Move(tmp, MetaPath); }
                    }
                }
                catch
                {
                    try { File.Delete(tmp); } catch { /* best effort */ }
                    throw;
                }
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

        public static List<ReplayMeta> LoadAll(string dir)
        {
            var list = new List<ReplayMeta>();
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.strp.json"))
            {
                try
                {
                    var m = Load(f);
                    if (m != null && File.Exists(m.ReplayPath)) list.Add(m);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Skipping unreadable replay metadata {Path.GetFileName(f)}: {e.Message}");
                }
            }
            return list.OrderByDescending(m => m.StartUtc).ToList();
        }
    }
}
