using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DroidFinestra.Helpers;

namespace DroidFinestra.Core
{
    public enum LineKind { Output, Note, Info }

    /// <summary>
    /// One scrcpy.exe run: a child process with arguments (no embedding, no SetParent — BATCH-GUI-1 §1/§7.1),
    /// stdout+stderr captured to a per-launch log file and raised line by line, the exit code kept.
    /// The process is put in Finestra's kill-on-close job (<see cref="JobGuard"/>), so no scrcpy outlives the app.
    /// Events fire on worker threads; UI subscribers marshal.
    /// </summary>
    public sealed class ScrcpySession
    {
        private static readonly List<ScrcpySession> _live = new List<ScrcpySession>();
        private static readonly object _liveLock = new object();

        /// <summary>Sessions whose scrcpy is still running.</summary>
        public static ScrcpySession[] Live { get { lock (_liveLock) return _live.ToArray(); } }

        public DeviceProfile Profile { get; }
        public string ExePath { get; }
        public string Arguments { get; }
        public string EnvText { get; }
        public string LogPath { get; private set; }
        public bool IsRunning { get; private set; }
        public bool HasRun { get; private set; }
        public int ExitCode { get; private set; }
        public bool StopRequested { get; private set; }

        /// <summary>The full command line exactly as launched — what the user copies.</summary>
        public string CommandLineText => CommandLine.Quote(ExePath) + " " + Arguments;

        public event Action<string, LineKind> Line;
        public event Action Exited;

        private Process _proc;
        private StreamWriter _log;
        private readonly object _logLock = new object();
        private readonly ManualResetEvent _eof = new ManualResetEvent(false);
        private int _eofCount;
        private bool _iconNoted;
        private readonly List<string> _tail = new List<string>();   // recent lines that are not known noise

        public ScrcpySession(DeviceProfile profile)
        {
            Profile = profile.Copy();
            ExePath = ScrcpyTools.ScrcpyExe;
            Arguments = Profile.BuildArguments();
            EnvText = ScrcpyTools.EnvSummary();
        }

        public bool Start(out string error)
        {
            error = ScrcpyTools.Problem();
            if (error != null) return false;
            var errs = Profile.Validate();
            if (errs.Count > 0) { error = string.Join(Environment.NewLine, errs.ToArray()); return false; }

            OpenLog();
            var psi = new ProcessStartInfo(ExePath, Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // no console window; scrcpy's own SDL window still appears
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ScrcpyTools.Folder,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ScrcpyTools.ApplyEnv(psi);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => OnData(e.Data);
            p.ErrorDataReceived += (s, e) => OnData(e.Data);
            p.Exited += (s, e) => ThreadPool.QueueUserWorkItem(_ => OnExited());
            try { p.Start(); }
            catch (Exception ex)
            {
                error = "Could not start scrcpy.exe: " + ex.Message;
                WriteLog("# " + error);
                CloseLog();
                return false;
            }
            _proc = p;
            JobGuard.Assign(p);
            IsRunning = true; HasRun = true;
            lock (_liveLock) _live.Add(this);
            WriteLog("# started pid " + SafePid());
            FileLog.Line("[LAUNCH] pid=" + SafePid() + " " + CommandLineText);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return true;
        }

        /// <summary>Asks scrcpy to close its window (a clean exit, as if the user closed it); kills it if it has
        /// not gone within 3 s. Non-blocking.</summary>
        public void Stop()
        {
            var p = _proc;
            if (p == null || !IsRunning) return;
            try { if (p.HasExited) return; } catch { }   // exited on its own: keep its real exit code and last lines
            StopRequested = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool asked = false;
                    try { p.Refresh(); asked = p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow(); } catch { }
                    if (!asked || !p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
                }
                catch { }
            });
        }

        /// <summary>The last lines worth reading after a failure — known noise (the icon messages) left out.</summary>
        public string[] FailureTail(int n)
        {
            lock (_tail) return _tail.Skip(Math.Max(0, _tail.Count - n)).ToArray();
        }

        // ── output ──

        private void OnData(string data)
        {
            if (data == null)
            {
                if (Interlocked.Increment(ref _eofCount) >= 2) { try { _eof.Set(); } catch { } }
                return;
            }
            WriteLog(data);
            Raise(data, LineKind.Output);

            bool noise = IsIconNoise(data);
            if (noise && !_iconNoted)
            {
                _iconNoted = true;
                Note("Expected, not an error: this FFmpeg build has no PNG decoder, so scrcpy cannot load its window icon. Mirroring is unaffected.");
            }
            if (!noise && !data.StartsWith("VERBOSE:", StringComparison.Ordinal))
                lock (_tail) { _tail.Add(data); if (_tail.Count > 60) _tail.RemoveAt(0); }

            string hint = HintFor(data);
            if (hint != null) Note(hint);
        }

        private void Note(string text)
        {
            WriteLog("[" + AppIdentity.Name + "] " + text);
            Raise(text, LineKind.Note);
        }

        private void Raise(string text, LineKind kind)
        {
            try { Line?.Invoke(text, kind); } catch { }
        }

        /// <summary>The icon-loading chain seen at every start on RT (scrcpy's icon.c + FFmpeg's probe warning).
        /// BATCH-GUI-1 names "Could not open image codec"; the Surface's own log shows the neighbouring messages.</summary>
        private static readonly string[] IconNoise =
        {
            "Could not open image codec",
            "Could not find best image stream",
            "Could not load icon",
            "Could not find codec parameters for stream 0 (Video: png",
            "Consider increasing the value for the 'analyzeduration'"
        };

        public static bool IsIconNoise(string line)
        {
            foreach (var k in IconNoise) if (line.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private string HintFor(string line)
        {
            bool wifi = Profile.Transport == Transport.Wireless;
            if (line.IndexOf("(state=offline)", StringComparison.Ordinal) >= 0 && wifi)
                return "The phone is 'offline' at that address. If that was the pairing port, use the port shown under Wireless debugging's IP address & port instead.";
            if (line.IndexOf("Device is unauthorized", StringComparison.Ordinal) >= 0)
                return "Unlock the phone and allow USB debugging on the prompt it shows, then launch again.";
            if (line.IndexOf("Could not connect to ", StringComparison.Ordinal) >= 0 && wifi)
                return "adb could not reach the phone. Check it is on the same network with Wireless debugging on; the port changes each time Wireless debugging is turned on.";
            if (line.IndexOf("Could not find any ADB device", StringComparison.Ordinal) >= 0)
                return wifi ? null : "adb sees no USB device. Check the cable and that USB debugging is on; Devices… lists what adb sees.";
            if (line.StartsWith("ERROR: Multiple (", StringComparison.Ordinal))
                return "More than one device matches. Edit the profile and pick one with Pick device….";
            return null;
        }

        // ── exit ──

        private void OnExited()
        {
            _eof.WaitOne(3000);   // let the last lines arrive (bounded — a stray inherited handle must not hang us)
            int code = -1;
            try { code = _proc.ExitCode; } catch { }
            ExitCode = code;
            IsRunning = false;
            lock (_liveLock) _live.Remove(this);
            WriteLog("# exited with code " + code + (StopRequested ? " (stopped from " + AppIdentity.Name + ")" : ""));
            CloseLog();
            FileLog.Line("[LAUNCH] pid exited code=" + code);
            try { Exited?.Invoke(); } catch { }
        }

        private string SafePid() { try { return _proc.Id.ToString(); } catch { return "?"; } }

        // ── the per-launch log file ──

        private void OpenLog()
        {
            try
            {
                string dir = StoragePaths.SessionLogDir;
                Directory.CreateDirectory(dir);
                PruneLogs(dir, 30);
                string name = SafeName(Profile.DisplayName) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
                LogPath = Path.Combine(dir, name);
                // UTF-8 WITH BOM: Notepad on 8.1 needs it to show non-ASCII device names correctly.
                _log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) { AutoFlush = true };
                WriteLog("# " + AppIdentity.Name + " — " + Profile.DisplayName + " — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                WriteLog("# command: " + CommandLineText);
                WriteLog("# env:     " + EnvText);
            }
            catch (Exception ex) { FileLog.Line("[LAUNCH] log open failed: " + ex.Message); _log = null; }
        }

        private void WriteLog(string s)
        {
            lock (_logLock) { try { _log?.WriteLine(s); } catch { } }
        }

        private void CloseLog()
        {
            lock (_logLock) { try { _log?.Dispose(); } catch { } _log = null; }
        }

        private static string SafeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c);
            string r = sb.ToString().Trim('_');
            if (r.Length > 40) r = r.Substring(0, 40);
            return r.Length == 0 ? "scrcpy" : r;
        }

        private static void PruneLogs(string dir, int keep)
        {
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep).ToList();
                foreach (var f in files) try { f.Delete(); } catch { }
            }
            catch { }
        }
    }
}
