using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Streplay
{
    // Keeps the replay folder under General.MaxFolderSizeGB, and the drive it's on above Disk.Floor free: after a round
    // is saved (and while recording, when the drive runs low), the oldest finished rounds without any marker the player
    // placed themselves are deleted until both hold. Renders are never touched. Runs off the main thread (it only
    // does file IO), one pass at a time.
    internal static class Retention
    {
        static int _running;
        public static bool Running => _running == 1;
        // the last pass couldn't get the drive back above Disk.Floor: every round left has the player's markers
        public static volatile bool Stuck;

        public static void EnforceAsync(string justSavedMeta)
        {
            float limitGb = Plugin.MaxFolderSizeGB.Value;
            string dir = Plugin.ReplayFolder.Value;
            if (string.IsNullOrEmpty(dir)) return;
            long free = Disk.Free(dir);
            if (limitGb <= 0f && (free < 0 || free >= Disk.Floor)) return;
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            long limit = limitGb <= 0f ? long.MaxValue : (long)(limitGb * 1024d * 1024d * 1024d);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Enforce(dir, limit, justSavedMeta); }
                catch (Exception e) { Plugin.Log.LogWarning($"Replay folder cleanup failed: {e.Message}"); }
                finally { Interlocked.Exchange(ref _running, 0); }
            });
        }

        static long SizeOf(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0L; }
            catch { return 0L; }
        }

        static void Enforce(string dir, long limit, string justSavedMeta)
        {
            if (!Directory.Exists(dir)) return;
            long total = 0;
            foreach (var f in Directory.GetFiles(dir, "*.strp")) total += SizeOf(f);
            long free = Disk.Free(dir);
            long needFree = free >= 0 && free < Disk.Floor ? Disk.Floor - free : 0L;
            if (total <= limit && needFree <= 0) { Stuck = false; return; }

            var candidates = ReplayIndex.Load(dir).List
                .Where(m => m.Finished
                            && !string.Equals(m.MetaPath, justSavedMeta, StringComparison.OrdinalIgnoreCase)
                            && !m.Markers.Any(k => !k.Auto))
                .OrderBy(m => m.StartUtc)
                .ToList();

            int deleted = 0;
            long freed = 0;
            foreach (var m in candidates)
            {
                if (total <= limit && freed >= needFree) break;
                long size = SizeOf(m.ReplayPath);
                try
                {
                    File.Delete(m.ReplayPath);
                    if (File.Exists(m.MetaPath)) File.Delete(m.MetaPath);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Could not delete old replay {Path.GetFileName(m.ReplayPath)}: {e.Message}");
                    continue;
                }
                total -= size;
                freed += size;
                deleted++;
            }
            Stuck = needFree > 0 && freed < needFree;
            if (deleted > 0)
                Plugin.Log.LogInfo((needFree > 0 ? $"Drive low on space ({Disk.Gb(free)} free)" : $"Replay folder over {limit / (1024d * 1024 * 1024):0.#} GB") +
                                   $": deleted the {deleted} oldest unmarked rounds ({freed / (1024d * 1024):0} MB)");
            if (Stuck)
                Plugin.Log.LogWarning($"Only {Disk.Gb(free)} free on {Disk.Drive(dir)} and every remaining round has your markers: recording pauses until there's {Disk.Gb(Disk.Floor)} free");
            else if (total > limit)
                Plugin.Log.LogWarning($"Replay folder is {total / (1024d * 1024 * 1024):0.0} GB, over the {limit / (1024d * 1024 * 1024):0.#} GB limit, " +
                                      "but every remaining round has your markers, so nothing was deleted");
        }
    }
}
