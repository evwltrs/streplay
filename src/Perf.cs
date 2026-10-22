using System.Diagnostics;
using System.IO;

namespace Streplay
{
    // Debug builds only (calls to the [Conditional] methods vanish from Release builds): where the recorder's time and
    // raw bytes go, section by section, read by the autotest "perf" command.
    internal static class Perf
    {
        public static readonly string[] Names = { "objects", "players-meta", "states", "arms", "bones", "audio", "view", "hud", "flush" };
        public static readonly Stopwatch Sample = new Stopwatch(), Rpc = new Stopwatch(), Gui = new Stopwatch();
        public static readonly Stopwatch[] Sec = new Stopwatch[Names.Length];
        public static readonly long[] Bytes = new long[Names.Length], Alloc = new long[Names.Length];
        public static long GuiAlloc, OtherAlloc;
        public static int Samples, Rpcs;
        public static CountingStream Counter;
        static int _cur = -1;
        static long _mark, _mem;

        static Perf() { for (int i = 0; i < Sec.Length; i++) Sec[i] = new Stopwatch(); }

        public static void Reset()
        {
            Sample.Reset(); Rpc.Reset(); Gui.Reset();
            foreach (var s in Sec) s.Reset();
            for (int i = 0; i < Bytes.Length; i++) Bytes[i] = Alloc[i] = 0;
            GuiAlloc = OtherAlloc = 0;
            Samples = Rpcs = 0;
        }

        // ends the running section and starts section i (-1: none)
        [Conditional("STREPLAY_AUTOTEST")]
        public static void To(int i)
        {
            long pos = Counter != null ? Counter.Count : 0;
            long mem = System.GC.GetTotalMemory(false);
            if (_cur >= 0) { Sec[_cur].Stop(); Bytes[_cur] += pos - _mark; if (mem > _mem) Alloc[_cur] += mem - _mem; }
            _cur = i;
            _mark = pos;
            _mem = mem;
            if (i >= 0) Sec[i].Start();
        }

        [Conditional("STREPLAY_AUTOTEST")]
        public static void Wrap(ref Stream s) { s = Counter = new CountingStream(s); }
    }

    internal sealed class CountingStream : Stream
    {
        readonly Stream _s;
        public long Count;
        public CountingStream(Stream s) { _s = s; }
        public override void Write(byte[] buffer, int offset, int count) { Count += count; _s.Write(buffer, offset, count); }
        public override void WriteByte(byte value) { Count++; _s.WriteByte(value); }
        public override void Flush() => _s.Flush();
        protected override void Dispose(bool disposing) { if (disposing) _s.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new System.NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();
        public override void SetLength(long value) => throw new System.NotSupportedException();
    }
}
