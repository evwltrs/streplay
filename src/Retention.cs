using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Streplay
{
    // Keeps the replay folder under General.MaxFolderSizeGB: after a round is saved, the oldest finished rounds
    // without any marker the player placed themselves are deleted until the folder fits. Renders are never touched.
    // Runs off the main thread (it only does file IO), one pass at a time.
    internal static class Retention
    {
        static int _running;

        public static void EnforceAsync(string justSavedMeta)
        {
            float limitGb = Plugin.MaxFolderSizeGB.Value;
            string dir = Plugin.ReplayFolder.Value;
            if (limitGb <= 0f || string.IsNullOrEmpty(dir)) return;
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            long limit = (long)(limitGb * 1024d * 1024d * 1024d);
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
            if (total <= limit) return;

            var candidates = ReplayMeta.LoadAll(dir)
                .Where(m => m.Finished
                            && !string.Equals(m.MetaPath, justSavedMeta, StringComparison.OrdinalIgnoreCase)
                            && !m.Markers.Any(k => !k.Auto))
                .OrderBy(m => m.StartUtc)
                .ToList();

            int deleted = 0;
            long freed = 0;
            foreach (var m in candidates)
            {
                if (total <= limit) break;
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
            if (deleted > 0)
                Plugin.Log.LogInfo($"Replay folder over {limit / (1024d * 1024 * 1024):0.#} GB: deleted the {deleted} oldest unmarked rounds ({freed / (1024d * 1024):0} MB)");
            else if (total > limit)
                Plugin.Log.LogWarning($"Replay folder is {total / (1024d * 1024 * 1024):0.0} GB, over the {limit / (1024d * 1024 * 1024):0.#} GB limit, " +
                                      "but every remaining round has your markers, so nothing was deleted");
        }
    }
}
