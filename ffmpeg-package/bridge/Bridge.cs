using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace StreplayFfmpeg
{
    public static class Bridge
    {
        // what STREPLAY checks before using this assembly: bumped when the API changes (a property, not a const, so
        // reading it really loads this assembly)
        public static int Version => 1;
    }

    // One ffmpeg run that reads raw frames on stdin. Its stderr is drained continuously (ffmpeg blocks once the
    // pipe is full) and the last lines are kept for error messages.
    public sealed class Encoder : IDisposable
    {
        readonly Process _proc;
        readonly Queue<string> _log = new Queue<string>();

        public Encoder(string exe, string args)
        {
            _proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                }
            };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) { _log.Enqueue(e.Data); while (_log.Count > 20) _log.Dequeue(); } };
            _proc.OutputDataReceived += (_, e) => { };
            _proc.Start();
            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();
            Input = _proc.StandardInput.BaseStream;
        }

        public Stream Input { get; }
        public bool HasExited => _proc.HasExited;
        public string LastLog { get { lock (_log) return string.Join(" | ", _log); } }

        // closes stdin and waits for ffmpeg to finish writing; returns its exit code
        public int Finish()
        {
            try { Input.Flush(); Input.Close(); } catch (IOException) { }
            _proc.WaitForExit();
            return _proc.ExitCode;
        }

        public void Dispose() => _proc.Dispose();
    }
}
