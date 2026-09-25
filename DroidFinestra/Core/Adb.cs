using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using DroidFinestra.Helpers;

namespace DroidFinestra.Core
{
    /// <summary>The outcome of one adb invocation: adb's own text (stdout and stderr, in arrival order) and how it ended.</summary>
    public sealed class ToolResult
    {
        public int ExitCode = -1;
        public string Output = "";
        public bool TimedOut;
        /// <summary>Set when the process could not be started at all.</summary>
        public string StartError;

        /// <summary>What to show a user: adb's own words, or why there are none.</summary>
        public string Describe()
        {
            if (StartError != null) return StartError;
            string o = (Output ?? "").Trim();
            if (TimedOut) return (o.Length > 0 ? o + Environment.NewLine : "") + "(adb did not finish in time and was stopped)";
            return o.Length > 0 ? o : "(adb printed nothing; exit code " + ExitCode + ")";
        }
    }

    /// <summary>One line of "adb devices -l".</summary>
    public sealed class AdbDevice
    {
        public string Serial = "", State = "", Model = "", Product = "", Device = "";

        /// <summary>scrcpy's rule (adb_device.c): a serial with ':' is a TCP/IP device.</summary>
        public bool IsWireless => Serial.IndexOf(':') >= 0;

        public string Label => (Model.Length > 0 ? Model.Replace('_', ' ') + "  ·  " : "") + Serial + (IsWireless ? " (Wi-Fi)" : " (USB)");
    }

    /// <summary>
    /// Runs adb.exe from the scrcpy folder with the same environment scrcpy gets (<see cref="ScrcpyTools.ApplyEnv"/>).
    /// Blocking — call from a worker thread, never the UI thread. stdout and stderr go through pipes (the path
    /// adb-rt's patch 0003 fixed); both are captured, so a failure shows adb's own message.
    /// </summary>
    public static class Adb
    {
        public static ToolResult Run(string args, int timeoutMs)
        {
            var r = new ToolResult();
            string adb = ScrcpyTools.AdbExe;
            if (!File.Exists(adb)) { r.StartError = "adb.exe was not found in " + ScrcpyTools.Folder + "."; return r; }

            var psi = new ProcessStartInfo(adb, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,   // closed at once: a prompt (e.g. "adb pair" without a code) reads EOF, never hangs
                WorkingDirectory = ScrcpyTools.Folder,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ScrcpyTools.ApplyEnv(psi);

            var sb = new StringBuilder();
            var gate = new object();
            using (var outEof = new ManualResetEvent(false))
            using (var errEof = new ManualResetEvent(false))
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data == null) Set(outEof); else lock (gate) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data == null) Set(errEof); else lock (gate) sb.AppendLine(e.Data); };
                try { p.Start(); }
                catch (Exception ex) { r.StartError = "Could not start adb.exe: " + ex.Message; return r; }
                try { p.StandardInput.Close(); } catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(2000); } catch { }
                }
                else
                {
                    try { r.ExitCode = p.ExitCode; } catch { }
                }
                // Bounded: the adb server this call may have started must never keep us waiting on a pipe.
                outEof.WaitOne(2000);
                errEof.WaitOne(2000);
                try { p.CancelOutputRead(); p.CancelErrorRead(); } catch { }
            }
            lock (gate) r.Output = sb.ToString();
            FileLog.Line("[ADB] adb " + Redact(args) + " -> exit=" + r.ExitCode + (r.TimedOut ? " TIMEOUT" : "")
                         + (r.StartError != null ? " start-error=" + r.StartError : "") + " | " + r.Output.Trim().Replace(Environment.NewLine, " / "));
            return r;
        }

        private static void Set(ManualResetEvent e) { try { e.Set(); } catch (ObjectDisposedException) { } }

        /// <summary>The pairing code is a secret for its few seconds of life; keep it out of the app log.</summary>
        private static string Redact(string args)
            => args != null && args.StartsWith("pair ", StringComparison.Ordinal) ? "pair <address> ******" : args;

        // ── the commands the GUI uses ──

        /// <summary>A cold adb server takes seconds to start on a Surface RT, so the first call gets a long timeout.</summary>
        public static ToolResult Devices() => Run("devices -l", 30000);

        public static ToolResult RestartServer()
        {
            var k = Run("kill-server", 15000);
            var s = Run("start-server", 30000);
            return new ToolResult
            {
                ExitCode = s.ExitCode,
                TimedOut = k.TimedOut || s.TimedOut,
                StartError = k.StartError ?? s.StartError,
                Output = "> adb kill-server" + Environment.NewLine + k.Output + "> adb start-server" + Environment.NewLine + s.Output
            };
        }

        public static ToolResult Pair(string address, string code) => Run("pair " + CommandLine.Quote(address) + " " + CommandLine.Quote(code), 30000);

        public static ToolResult Connect(string address) => Run("connect " + CommandLine.Quote(address), 30000);

        /// <summary>"adb pair" prints "Successfully paired to ..." on success.</summary>
        public static bool PairSucceeded(ToolResult r)
            => r.StartError == null && !r.TimedOut && (r.Output ?? "").IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>"adb connect" exits 0 even on failure; scrcpy checks the text the same way (adb.c).</summary>
        public static bool ConnectSucceeded(ToolResult r)
        {
            if (r.StartError != null || r.TimedOut) return false;
            string o = (r.Output ?? "").TrimStart();
            return o.StartsWith("connected", StringComparison.OrdinalIgnoreCase)
                || o.StartsWith("already connected", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> States = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "device", "offline", "unauthorized", "no", "bootloader", "recovery", "sideload", "rescue",
          "authorizing", "connecting", "host", "unknown", "detached" };

        /// <summary>Parses "adb devices -l". Lines that are not devices (daemon notices, headers, errors) are skipped.</summary>
        public static List<AdbDevice> ParseDevices(string output)
        {
            var list = new List<AdbDevice>();
            foreach (var raw in (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("*") || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2 || !States.Contains(tok[1])) continue;
                var d = new AdbDevice { Serial = tok[0], State = tok[1] };
                int i = 2;
                if (d.State == "no" && tok.Length > 2 && tok[2].StartsWith("permissions", StringComparison.OrdinalIgnoreCase))
                {
                    d.State = "no permissions";
                    while (i < tok.Length && tok[i].IndexOf(':') < 0) i++;
                }
                for (; i < tok.Length; i++)
                {
                    int c = tok[i].IndexOf(':');
                    if (c <= 0) continue;
                    string k = tok[i].Substring(0, c), v = tok[i].Substring(c + 1);
                    if (k == "model") d.Model = v;
                    else if (k == "product") d.Product = v;
                    else if (k == "device") d.Device = v;
                }
                list.Add(d);
            }
            return list;
        }

        /// <summary>The state adb reports for one serial, or null when it is not listed.</summary>
        public static string StateOf(string serial)
        {
            foreach (var d in ParseDevices(Devices().Output))
                if (string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase)) return d.State;
            return null;
        }
    }
}
