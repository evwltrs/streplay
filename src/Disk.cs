using System.IO;

namespace Streplay
{
    // Free space on the replay folder's drive. Streplay never takes that drive below Floor (old unmarked rounds go
    // early, and recording pauses if none can), and warns below Low. Unknown (network paths): -1, and no checks.
    internal static class Disk
    {
        public const long Floor = 2L << 30;
        public const long Low = 5L << 30;
        internal static long TestFree = -1; // test hook: pretend this much is free

        public static long Free(string dir)
        {
            if (TestFree >= 0) return TestFree;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dir));
                return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1; }
        }

        public static string Drive(string dir)
        {
            try { return Path.GetPathRoot(Path.GetFullPath(dir)).TrimEnd('\\'); }
            catch { return "the drive"; }
        }

        public static string Gb(long bytes) =>
            (bytes / (double)(1L << 30)).ToString(bytes < 10L << 30 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
    }
}
