using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Streplay
{
    // Every round's metadata in the replay folder, kept between loads: a file is only read again when it changed, so
    // listing a folder of thousands of rounds takes one directory scan after the first time. Safe to call from any
    // thread (the browser loads in the background, the folder cleanup on the thread pool); one load runs at a time.
    internal static class ReplayIndex
    {
        sealed class Entry
        {
            public DateTime Stamp;
            public ReplayMeta Meta;
        }

        internal sealed class Snapshot
        {
            public List<ReplayMeta> List = new List<ReplayMeta>();   // newest first
            public long Bytes;                                       // every .strp in the folder
            public double SecondsHz;                                 // finished rounds' length x their sample rate
            public long SecondsHzBytes;                              // the bytes those rounds take
        }

        static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly object Lock = new object();
        static string _dir;

        public static Snapshot Load(string dir)
        {
            lock (Lock)
            {
                if (!string.Equals(dir, _dir, StringComparison.OrdinalIgnoreCase)) { Cache.Clear(); _dir = dir; }
                var snap = new Snapshot();
                var di = new DirectoryInfo(dir);
                if (!di.Exists) return snap;

                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in di.GetFiles("*.strp"))
                {
                    sizes[f.Name] = f.Length;
                    snap.Bytes += f.Length;
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in di.GetFiles("*.strp.json"))
                {
                    seen.Add(f.FullName);
                    if (!Cache.TryGetValue(f.FullName, out var e) || e.Stamp != f.LastWriteTimeUtc)
                    {
                        ReplayMeta m = null;
                        try { m = ReplayMeta.Load(f.FullName); }
                        catch (Exception ex) { Plugin.Log.LogWarning($"Skipping unreadable replay metadata {f.Name}: {ex.Message}"); }
                        Cache[f.FullName] = e = new Entry { Stamp = f.LastWriteTimeUtc, Meta = m };
                    }
                    var meta = e.Meta;
                    if (meta == null || !sizes.TryGetValue(meta.ReplayFile ?? "", out long len)) continue;
                    snap.List.Add(meta);
                    if (meta.Finished && meta.Duration > 5f)
                    {
                        snap.SecondsHz += meta.Duration * (meta.SampleRate > 0f ? meta.SampleRate : 60f);
                        snap.SecondsHzBytes += len;
                    }
                }
                foreach (var gone in Cache.Keys.Where(k => !seen.Contains(k)).ToList()) Cache.Remove(gone);
                snap.List.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
                return snap;
            }
        }
    }
}
