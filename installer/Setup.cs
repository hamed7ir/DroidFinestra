// =============================================================================
//  DroidFinestra installer (Setup.cs)
//
//  Finestra's AnyCPU/MSIL installer (installer\anycpu\Setup.cs), adapted. MSIL so it RUNS ON WINDOWS RT 8.1
//  (ARM32) as well as x86/x64 — an Inno Setup loader is native x86 and cannot start on RT.
//
//  Kept from Finestra: per-user %LocalAppData%\Programs\<App> (no admin), IShellLink shortcuts, HKCU Uninstall
//  entry, uninstall.exe self-copy, self-deleting uninstall, user data (Documents\DroidFinestra) never touched.
//
//  Changed:
//   * .NET Framework 4.0 — and only Client-Profile assemblies — so it starts on RT 8.1's in-box 4.5.1 and on a
//     bare 4.0 Client machine, where it can still say what is missing. 4.0 has no ZipArchive and no async, so the
//     payload is EMBEDDED in this exe as gzip resources ("payload/<file>.gz") listed with SHA-256 in payload.list,
//     and work runs on a plain thread. One file to ship; nothing to keep together.
//   * A REQUIREMENTS CHECK, on demand (the "Check requirements" button, or --check): can this Windows/CPU run each
//     binary in the payload; is .NET 4 Full installed (the app needs it); and does every DLL the binaries import
//     that the package does not carry — the Universal C Runtime, Visual C++ runtimes, Windows components — exist
//     on this machine. Missing items are named with where to get them. It NEVER blocks or gates installing
//     (Hamed's rule): Install always installs.
//   * An adb server left running from the install folder locks adb.exe, so it is stopped ("adb kill-server")
//     before an upgrade or uninstall; a running DroidFinestra or scrcpy from that folder must be closed first.
//
//  Modes:
//    (no args)              install wizard, with a "Check requirements" button
//    --check [file]         requirements report only, to file (or a dialog); installs nothing.
//                           exit 0 all present, 3 something missing, 2 the package's programs cannot run here
//    --silent [dir] [--no-desktop]   install without UI (no requirements check). exit 0 ok, 4 files in use, 1 error
//    --uninstall <dir>      remove the install dir, shortcuts and uninstall entry (user data kept)
//    (run as uninstall.exe) confirm, then uninstall its own folder
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DroidFinestraSetup
{
    internal static class Program
    {
        internal const string AppName = "DroidFinestra";
        internal const string InstallFolderName = "DroidFinestra";
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DroidFinestra";
        internal const string ShortcutDesc = "DroidFinestra - use your Android phone from Windows";
        internal const string Publisher = "Hamed Ghorbani";
        internal const string ExeName = "DroidFinestra.exe";
        internal const string DataFolderNote = @"Documents\DroidFinestra";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string a0 = args.Length >= 1 ? args[0] : "";
                if (Eq(a0, "--uninstall"))
                {
                    string dir = args.Length >= 2 ? args[1] : Path.GetDirectoryName(Application.ExecutablePath);
                    string busy = InUse.Release(dir);
                    if (busy != null) return 4;
                    return Uninstaller.Run(dir) ? 0 : 1;
                }
                if (Eq(a0, "--check"))
                {
                    var rep = Requirements.Check(null);
                    string text = Requirements.Format(rep);
                    if (args.Length >= 2) File.WriteAllText(args[1], text, Encoding.UTF8);
                    else MessageBox.Show(text, AppName + " " + Info.AppVersion + " (" + Info.Flavor + ") - requirements");
                    return Requirements.ExitCode(rep);
                }
                if (Eq(a0, "--silent"))
                {
                    string dir = DefaultDir();
                    bool desktop = true;
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (Eq(args[i], "--no-desktop")) desktop = false;
                        else dir = args[i];
                    }
                    if (InUse.Release(dir) != null) return 4;
                    Installer.Install(dir, desktop, null);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Double-clicking the dropped uninstall.exe (no args) -> confirm + uninstall its own folder.
                if (Eq(Path.GetFileNameWithoutExtension(Application.ExecutablePath), "uninstall"))
                {
                    string dir = Path.GetDirectoryName(Application.ExecutablePath);
                    if (MessageBox.Show("Remove " + AppName + " from this computer?\n\n(Your device profiles and settings in " + DataFolderNote + " are kept.)",
                        "Uninstall " + AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                    string busy;
                    while ((busy = InUse.Release(dir)) != null)
                        if (MessageBox.Show(busy + "\n\nClose it, then press Retry.", "Uninstall " + AppName,
                            MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return 4;
                    Uninstaller.Run(dir);
                    return 0;
                }

                Application.Run(new InstallForm());
                return 0;
            }
            catch (Exception ex)
            {
                try { MessageBox.Show(ex.ToString(), AppName + " Setup - error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                return 1;
            }
        }

        internal static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        internal static string DefaultDir()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(p, "Programs"), InstallFolderName);
        }
    }

    // ── payload: embedded gzip resources + payload.list (sha256 <TAB> size <TAB> relative name) ─────────────
    internal sealed class PayloadFile
    {
        public string Name, Sha256;
        public long Size;
    }

    internal static class Payload
    {
        internal static List<PayloadFile> List()
        {
            var list = new List<PayloadFile>();
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.list"))
            {
                if (s == null) throw new InvalidDataException("This Setup has no payload.list - it was not built by scripts\\build.ps1.");
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var f = line.Split('\t');
                        if (f.Length != 3) continue;
                        list.Add(new PayloadFile { Sha256 = f[0], Size = long.Parse(f[1]), Name = f[2] });
                    }
                }
            }
            return list;
        }

        internal static Stream Open(PayloadFile f)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/" + f.Name.Replace('\\', '/') + ".gz");
            if (s == null) throw new InvalidDataException("Payload resource missing: " + f.Name);
            return new GZipStream(s, CompressionMode.Decompress);
        }

        internal static byte[] ReadAll(PayloadFile f)
        {
            using (var s = Open(f))
            using (var m = new MemoryStream())
            {
                Copy(s, m);
                return m.ToArray();
            }
        }

        internal static void Copy(Stream from, Stream to)
        {
            var buf = new byte[81920];
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n);
        }

        internal static string Hex(byte[] h)
        {
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // ── requirements ─────────────────────────────────────────────────────────────────────────────────────────
    internal enum Level { Ok, Warn, Missing, Blocked }

    internal sealed class Req
    {
        public Level Level;
        public string Text, Fix, Url;
    }

    internal static class Requirements
    {
        internal const ushort X86 = 0x14c, X64 = 0x8664, ArmNt = 0x1c4, Arm = 0x1c0, Arm64 = 0xaa64;

        [StructLayout(LayoutKind.Sequential)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
            public byte wProductType, wReserved;
        }
        [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OSVERSIONINFOEX v);

        /// <summary>Real OS version (Environment.OSVersion stops at 6.2 for an unmanifested app on 8.1+).</summary>
        internal static Version OsVersion()
        {
            try
            {
                var v = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX)) };
                if (RtlGetVersion(ref v) == 0) return new Version(v.dwMajorVersion, v.dwMinorVersion, v.dwBuildNumber);
            }
            catch { }
            return Environment.OSVersion.Version;
        }

        /// <summary>"AMD64" | "X86" | "ARM" | "ARM64" — the OS, not this process.</summary>
        internal static string OsArch()
        {
            return (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
                    ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
        }

        internal static string MachineName(ushort m)
        {
            switch (m)
            {
                case X86: return "x86";
                case X64: return "x64";
                case ArmNt: case Arm: return "ARM32";
                case Arm64: return "ARM64";
                default: return "machine 0x" + m.ToString("X");
            }
        }

        private static string OsArchName(string a)
        {
            switch (a) { case "AMD64": return "x64"; case "X86": return "x86"; case "ARM": return "ARM32"; case "ARM64": return "ARM64"; default: return a; }
        }

        /// <summary>Null when this Windows can run programs of machine type <paramref name="m"/>, else why not.</summary>
        internal static string CannotRun(ushort m, string os, Version ver)
        {
            switch (m)
            {
                case X86: return os == "ARM" ? "ARM32 Windows cannot run x86 programs" : null;
                case X64:
                    if (os == "AMD64") return null;
                    if (os == "ARM64") return ver.Build >= 22000 ? null : "Windows 10 on ARM64 cannot run x64 programs (Windows 11 can)";
                    return OsArchName(os) + " Windows cannot run x64 programs";
                case ArmNt: case Arm:
                    if (os == "ARM") return null;
                    if (os == "ARM64") return ver.Build >= 26100 ? "Windows 11 24H2 and later no longer run 32-bit ARM programs" : null;
                    return OsArchName(os) + " Windows cannot run ARM32 programs";
                case Arm64: return os == "ARM64" ? null : OsArchName(os) + " Windows cannot run ARM64 programs";
                default: return "unknown machine type 0x" + m.ToString("X");
            }
        }

        /// <summary>The folder a program of machine type <paramref name="m"/> loads system DLLs from on this Windows.</summary>
        internal static string SystemDirFor(ushort m, string os)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // a 32-bit Setup on 64-bit Windows sees SysWOW64 as System32; Sysnative is the real one
            string native = Path.Combine(win, Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32");
            switch (m)
            {
                case X86: return os == "X86" ? native : Path.Combine(win, "SysWOW64");
                case ArmNt: case Arm: return os == "ARM" ? native : Path.Combine(win, "SysArm32");
                default: return native;   // x64 on x64 or ARM64 (ARM64X System32), ARM64 on ARM64
            }
        }

        /// <summary>What a DLL name is: part of this package, the Universal CRT, a Visual C++ runtime, an API-set
        /// contract (resolved by Windows itself), or an ordinary Windows DLL.</summary>
        internal static string Kind(string dll)
        {
            string d = dll.ToLowerInvariant();
            if (d.StartsWith("api-ms-win-crt-") || d == "ucrtbase.dll") return "ucrt";
            if (d.StartsWith("vcruntime") || d.StartsWith("msvcp1") || d.StartsWith("msvcr1") || d.StartsWith("concrt1")
                || d.StartsWith("vcomp1") || d.StartsWith("vccorlib")) return "vc";
            if (d.StartsWith("api-ms-") || d.StartsWith("ext-ms-")) return "apiset";
            return "system";
        }

        /// <summary>Machine type + imported DLL names of a PE image; false if it is not one.</summary>
        internal static bool ParsePe(byte[] b, out ushort machine, out List<string> imports)
        {
            machine = 0; imports = new List<string>();
            try
            {
                if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return false;
                int pe = BitConverter.ToInt32(b, 0x3C);
                if (pe < 0 || pe + 24 > b.Length || b[pe] != 'P' || b[pe + 1] != 'E' || b[pe + 2] != 0 || b[pe + 3] != 0) return false;
                machine = BitConverter.ToUInt16(b, pe + 4);
                int nsec = BitConverter.ToUInt16(b, pe + 6);
                int optSize = BitConverter.ToUInt16(b, pe + 20);
                int opt = pe + 24;
                bool plus = BitConverter.ToUInt16(b, opt) == 0x20b;
                int numDirs = BitConverter.ToInt32(b, opt + (plus ? 108 : 92));
                if (numDirs < 2) return true;
                uint impRva = BitConverter.ToUInt32(b, opt + (plus ? 112 : 96) + 8);
                if (impRva == 0) return true;
                int secs = opt + optSize;
                Func<uint, int> off = rva =>
                {
                    for (int i = 0; i < nsec; i++)
                    {
                        int s = secs + 40 * i;
                        uint vs = BitConverter.ToUInt32(b, s + 8), va = BitConverter.ToUInt32(b, s + 12);
                        uint raw = BitConverter.ToUInt32(b, s + 16), ptr = BitConverter.ToUInt32(b, s + 20);
                        uint size = Math.Max(vs, raw);
                        if (rva >= va && rva < va + size) return (int)(rva - va + ptr);
                    }
                    return -1;
                };
                for (int d = off(impRva); d >= 0 && d + 20 <= b.Length && imports.Count < 1024; d += 20)
                {
                    uint nameRva = BitConverter.ToUInt32(b, d + 12), first = BitConverter.ToUInt32(b, d + 16);
                    if (nameRva == 0 && first == 0) break;
                    int n = off(nameRva);
                    if (n < 0) break;
                    int e = n;
                    while (e < b.Length && b[e] != 0) e++;
                    imports.Add(Encoding.ASCII.GetString(b, n, e - n));
                }
                return true;
            }
            catch { return true; }   // a truncated table: keep what was read
        }

        private static string RedistUrl(ushort m)
        {
            switch (m)
            {
                case X86: return "https://aka.ms/vs/17/release/vc_redist.x86.exe";
                case X64: return "https://aka.ms/vs/17/release/vc_redist.x64.exe";
                case Arm64: return "https://aka.ms/vs/17/release/vc_redist.arm64.exe";
                default: return null;   // Microsoft publishes no current redistributable for ARM32
            }
        }

        /// <summary>Checks this machine against the payload. <paramref name="exists"/> answers "is this file there"
        /// (null = the real file system) — it lets the check be tested against a machine that lacks a runtime.</summary>
        internal static List<Req> Check(Func<string, bool> exists)
        {
            if (exists == null) exists = File.Exists;
            var reqs = new List<Req>();
            string os = OsArch();
            Version ver = OsVersion();
            reqs.Add(new Req { Level = Level.Ok, Text = "Windows " + ver + " on " + OsArchName(os) + "; this package: " + Info.Flavor });

            // .NET Framework 4 Full — the app targets it (this Setup itself needs only the 4.0 Client Profile)
            int release = 0; bool full = false;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k != null)
                    {
                        object inst = k.GetValue("Install"), rel = k.GetValue("Release");
                        full = inst is int && (int)inst == 1;
                        if (rel is int) release = (int)rel;
                    }
                }
            }
            catch { }
            if (full) reqs.Add(new Req { Level = Level.Ok, Text = ".NET Framework " + NetName(release) + " (full)" });
            else reqs.Add(new Req
            {
                Level = Level.Missing,
                Text = ".NET Framework 4 (full) is not installed - " + Program.AppName + " will not start",
                Fix = "Install .NET Framework 4.8 (or any 4.x full)",
                Url = "https://dotnet.microsoft.com/download/dotnet-framework"
            });

            // every binary in the payload: can it run here, and is everything it imports available?
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = Payload.List();
            foreach (var f in files) names.Add(Path.GetFileName(f.Name));
            var blocked = new List<string>();
            var needs = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // "kind|machine|dll" -> users
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f.Name).ToLowerInvariant();
                if (ext != ".exe" && ext != ".dll") continue;
                ushort m; List<string> imps;
                if (!ParsePe(Payload.ReadAll(f), out m, out imps)) continue;
                bool managed = imps.Count == 1 && Program.Eq(imps[0], "mscoree.dll");
                if (managed) continue;   // MSIL (AnyCPU) — runs wherever .NET does
                string why = CannotRun(m, os, ver);
                if (why != null) { blocked.Add(Path.GetFileName(f.Name) + " (" + MachineName(m) + "): " + why); continue; }
                foreach (var dll in imps)
                {
                    if (names.Contains(dll)) continue;
                    string kind = Kind(dll);
                    if (kind == "apiset") continue;
                    string key = kind + "|" + m + "|" + (kind == "ucrt" ? "ucrtbase.dll" : dll.ToLowerInvariant());
                    List<string> users;
                    if (!needs.TryGetValue(key, out users)) needs[key] = users = new List<string>();
                    string user = Path.GetFileName(f.Name);
                    if (!users.Contains(user)) users.Add(user);
                }
            }

            if (blocked.Count > 0)
                reqs.Add(new Req
                {
                    Level = Level.Blocked,
                    Text = "This package's programs cannot run on this PC:\n    " + string.Join("\n    ", blocked.ToArray()),
                    Fix = Info.Flavor == "arm32" ? "Use the x64 package on a 64-bit PC." : "Use the arm32 package on Windows RT / ARM32 Windows."
                });

            int sysChecked = 0, sysMissing = 0;
            foreach (var kv in needs)
            {
                var p = kv.Key.Split('|');
                string kind = p[0]; ushort m = ushort.Parse(p[1]); string dll = p[2];
                string dir = SystemDirFor(m, os);
                bool ok = exists(Path.Combine(dir, dll));
                if (kind == "system") { sysChecked++; if (!ok) sysMissing++; }
                string by = " - needed by " + string.Join(", ", kv.Value.ToArray());
                if (kind == "ucrt")
                    reqs.Add(ok
                        ? new Req { Level = Level.Ok, Text = "Universal C Runtime (" + MachineName(m) + ")" + by }
                        : new Req
                        {
                            Level = Level.Missing,
                            Text = "Universal C Runtime (" + MachineName(m) + ") is missing" + by,
                            Fix = "Install the Microsoft Visual C++ 2015-2022 Redistributable (" + MachineName(m) + "), which adds it on Windows 7/8.1, or Windows Update KB2999226",
                            Url = RedistUrl(m)
                        });
                else if (kind == "vc")
                    reqs.Add(ok
                        ? new Req { Level = Level.Ok, Text = "Visual C++ runtime " + dll + " (" + MachineName(m) + ")" + by }
                        : new Req
                        {
                            Level = Level.Missing,
                            Text = "Visual C++ runtime " + dll + " (" + MachineName(m) + ") is missing" + by,
                            Fix = "Install the Microsoft Visual C++ 2015-2022 Redistributable (" + MachineName(m) + ")",
                            Url = RedistUrl(m)
                        });
                else if (!ok)   // an ordinary Windows DLL: only worth a line when it is absent
                    reqs.Add(new Req
                    {
                        Level = Level.Missing,
                        Text = "Windows component " + dll + " (" + MachineName(m) + ") is missing" + by,
                        Fix = "This Windows edition lacks it; the program that needs it will not start"
                    });
            }
            if (sysChecked > 0 && sysMissing == 0)
                reqs.Add(new Req { Level = Level.Ok, Text = "Windows components the programs import: all " + sysChecked + " present" });
            return reqs;
        }

        internal static string NetName(int release)
        {
            if (release >= 533320) return "4.8.1";
            if (release >= 528040) return "4.8";
            if (release >= 461808) return "4.7.2";
            if (release >= 461308) return "4.7.1";
            if (release >= 460798) return "4.7";
            if (release >= 394802) return "4.6.2";
            if (release >= 394254) return "4.6.1";
            if (release >= 393295) return "4.6";
            if (release >= 379893) return "4.5.2";
            if (release >= 378675) return "4.5.1";
            if (release >= 378389) return "4.5";
            return "4.0";
        }

        internal static int ExitCode(List<Req> reqs)
        {
            int code = 0;
            foreach (var r in reqs)
            {
                if (r.Level == Level.Blocked) return 2;
                if (r.Level == Level.Missing || r.Level == Level.Warn) code = 3;
            }
            return code;
        }

        internal static string Tag(Level l)
        {
            switch (l) { case Level.Ok: return "[ OK ]"; case Level.Warn: return "[WARN]"; case Level.Missing: return "[MISSING]"; default: return "[CANNOT RUN]"; }
        }

        internal static string Format(List<Req> reqs)
        {
            var sb = new StringBuilder();
            foreach (var r in reqs)
            {
                sb.Append(Tag(r.Level)).Append(' ').Append(r.Text.Replace("\n", "\r\n")).Append("\r\n");
                if (r.Fix != null) sb.Append("      -> ").Append(r.Fix).Append("\r\n");
                if (r.Url != null) sb.Append("      ").Append(r.Url).Append("\r\n");
            }
            return sb.ToString();
        }
    }

    // ── files in use: an adb server, DroidFinestra or scrcpy running from the install folder ─────────────────
    internal static class InUse
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static string ImagePath(Process p)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) return null;
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        private static List<string> RunningFrom(string dir, params string[] names)
        {
            var hits = new List<string>();
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\') + "\\"; } catch { return hits; }
            foreach (var n in names)
                foreach (var p in Process.GetProcessesByName(n))
                {
                    string img = ImagePath(p);
                    if (img != null && img.StartsWith(full, StringComparison.OrdinalIgnoreCase)) hits.Add(n + ".exe");
                    p.Dispose();
                }
            return hits;
        }

        /// <summary>Stops an adb server running from <paramref name="dir"/>; null when nothing from it is running, else
        /// a sentence naming what still is (DroidFinestra and scrcpy are the user's to close).</summary>
        internal static string Release(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            string adb = Path.Combine(dir, "adb.exe");
            if (File.Exists(adb) && RunningFrom(dir, "adb").Count > 0)
            {
                try
                {
                    var psi = new ProcessStartInfo(adb, "kill-server") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
                    using (var p = Process.Start(psi)) p.WaitForExit(15000);
                }
                catch { }
                for (int i = 0; i < 20 && RunningFrom(dir, "adb").Count > 0; i++) Thread.Sleep(250);
            }
            var still = RunningFrom(dir, "DroidFinestra", "scrcpy", "adb");
            if (still.Count == 0) return null;
            return string.Join(", ", still.ToArray()) + " is running from " + dir + ".";
        }
    }

    internal static class Installer
    {
        internal static void Install(string targetDir, bool desktopShortcut, Action<string> progress)
        {
            Report(progress, "Preparing...");
            var files = Payload.List();

            // Binaries only: user data lives in Documents\DroidFinestra, never in the install dir, so wiping the
            // target dir is safe (and it is how a re-install upgrades in place).
            if (Directory.Exists(targetDir)) { Report(progress, "Removing previous version..."); TryDeleteDir(targetDir); }
            Directory.CreateDirectory(targetDir);

            using (var sha = SHA256.Create())
            {
                int i = 0;
                foreach (var f in files)
                {
                    Report(progress, "Extracting " + (++i) + " of " + files.Count + ": " + f.Name);
                    string dest = Path.Combine(targetDir, f.Name);
                    string d = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    using (var src = Payload.Open(f))
                    using (var os = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var cs = new CryptoStream(os, sha, CryptoStreamMode.Write))
                    {
                        Payload.Copy(src, cs);
                        cs.FlushFinalBlock();
                        string got = Payload.Hex(sha.Hash);
                        if (!string.Equals(got, f.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(f.Name + " did not extract intact (SHA-256 " + got + ", expected " + f.Sha256 + ").");
                    }
                    sha.Initialize();
                }
            }

            string exe = Path.Combine(targetDir, Program.ExeName);
            string uninst = Path.Combine(targetDir, "uninstall.exe");

            Report(progress, "Creating shortcuts...");
            File.Copy(Application.ExecutablePath, uninst, true);   // uninstall.exe = this Setup (uninstall mode only)
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            Shortcut.Create(Path.Combine(programs, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            if (desktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Shortcut.Create(Path.Combine(desktop, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            }

            Report(progress, "Registering...");
            RegisterUninstall(targetDir, exe, uninst);
            Report(progress, "Done.");
        }

        private static void RegisterUninstall(string dir, string exe, string uninst)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", Program.AppName);
                    k.SetValue("DisplayVersion", Info.AppVersion);
                    k.SetValue("Publisher", Program.Publisher);
                    k.SetValue("Comments", Info.Flavor + " package");
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall \"" + dir + "\"");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try { k.SetValue("EstimatedSize", (int)DirSizeKB(dir), RegistryValueKind.DWord); } catch { }
                }
            }
            catch { /* uninstall entry is best-effort */ }
        }

        private static long DirSizeKB(string dir)
        {
            long total = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
            return total / 1024;
        }

        internal static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
        private static void Report(Action<string> p, string s) { if (p != null) p(s); }
    }

    internal static class Uninstaller
    {
        internal static bool Run(string dir)
        {
            // OWNERSHIP: the shortcut names and the uninstall key are fixed, so another install of this app (a copy in
            // another folder, or the one a later install moved them to) may own them. Remove only what points INTO
            // the folder being removed — Finestra's OwnedByThisInstall rule for its Run value, applied to all three.
            // Removing them blind once deleted a real install's shortcuts and uninstall entry during a test.
            RemoveShortcutIfOurs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.AppName + ".lnk"), dir);
            RemoveShortcutIfOurs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk"), dir);
            RemoveUninstallKeyIfOurs(dir);
            SelfDeleteDir(dir);   // remove the install dir last (uninstall.exe runs from inside it)
            return true;
        }

        // POLICY: PRESERVE the user's data — Documents\DroidFinestra (profiles, settings, launch logs) is never touched.

        /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or inside it. Unreadable or empty
        /// counts as NOT ours: leaving a stale entry is recoverable, deleting another install's is not.</summary>
        internal static bool Inside(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            try
            {
                string d = Path.GetFullPath(dir).TrimEnd('\\');
                string p = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
                return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void RemoveShortcutIfOurs(string lnk, string dir)
        {
            try { if (File.Exists(lnk) && Inside(Shortcut.TargetOf(lnk), dir)) File.Delete(lnk); } catch { }
        }

        private static void RemoveUninstallKeyIfOurs(string dir)
        {
            try
            {
                string loc;
                using (var k = Registry.CurrentUser.OpenSubKey(Program.UninstallKey))
                    loc = k != null ? k.GetValue("InstallLocation") as string : null;
                if (Inside(loc, dir) && Inside(dir, loc)) Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey);
            }
            catch { }
        }

        private static void SelfDeleteDir(string dir)
        {
            try
            {
                // uninstall.exe lives inside `dir` and is running -> it can't delete itself. A detached cmd waits ~2s
                // for this process to exit, then removes the whole dir (incl. uninstall.exe).
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }
    }

    /// <summary>Creates a .lnk via the shell's IShellLink COM object (works on RT desktop). Finestra's, unchanged.</summary>
    internal static class Shortcut
    {
        internal static void Create(string lnkPath, string target, string args, string workDir, string iconPath, string desc)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                link.SetPath(target);
                if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
                if (!string.IsNullOrEmpty(workDir)) link.SetWorkingDirectory(workDir);
                if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);
                if (!string.IsNullOrEmpty(desc)) link.SetDescription(desc);
                ((IPersistFile)link).Save(lnkPath, true);
            }
            catch { /* shortcut is best-effort */ }
        }

        /// <summary>The target path a .lnk points at, or null when it cannot be read.</summary>
        internal static string TargetOf(string lnkPath)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }

    /// <summary>Finestra's install wizard, plus a "Check requirements" button. The check only informs: Install is
    /// always enabled and never runs it.</summary>
    internal sealed class InstallForm : Form
    {
        private readonly TextBox _path, _reqs;
        private readonly CheckBox _desktop;
        private readonly Button _install, _cancel, _browse, _check;
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private readonly FlowLayoutPanel _links;
        private static readonly Color Accent = Color.FromArgb(16, 137, 62);   // DroidFinestra green

        public InstallForm()
        {
            Text = Program.AppName + " " + Info.AppVersion + " (" + Info.Flavor + ") Setup";
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.icon.ico")) if (s != null) Icon = new Icon(s); } catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 470);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Accent };
            var title = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 14f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0), Text = "Install " + Program.AppName + " " + Info.AppVersion + " (" + Info.Flavor + ")" };
            header.Controls.Add(title);

            var lbl = new Label { Left = 18, Top = 80, Width = 524, Height = 18, Text = "Install location (per-user, no admin needed):" };
            _path = new TextBox { Left = 18, Top = 100, Width = 430, Text = Program.DefaultDir() };
            _browse = new Button { Left = 458, Top = 98, Width = 84, Height = 26, Text = "Browse..." };
            _browse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { try { d.SelectedPath = _path.Text; } catch { } if (d.ShowDialog(this) == DialogResult.OK) _path.Text = Path.Combine(d.SelectedPath, Program.InstallFolderName); } };
            _desktop = new CheckBox { Left = 18, Top = 132, Width = 300, Text = "Create a desktop shortcut", Checked = true };

            var rl = new Label { Left = 18, Top = 164, Width = 524, Height = 18, Text = "Requirements (optional check - installing does not depend on it):" };
            _reqs = new TextBox { Left = 18, Top = 184, Width = 524, Height = 150, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 8.5f),
                                  Text = "Press \"Check requirements\" to see whether this PC has what " + Program.AppName + " needs:\r\n.NET Framework 4, and the runtimes its programs use." };
            _links = new FlowLayoutPanel { Left = 18, Top = 338, Width = 524, Height = 44, FlowDirection = FlowDirection.TopDown, WrapContents = false };

            _status = new Label { Left = 18, Top = 386, Width = 524, Height = 18, ForeColor = Color.Gray, Text = "" };
            _bar = new ProgressBar { Left = 18, Top = 406, Width = 524, Height = 14, Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30 };
            _check = new Button { Left = 18, Top = 430, Width = 150, Height = 28, Text = "Check requirements" };
            _install = new Button { Left = 380, Top = 430, Width = 84, Height = 28, Text = "Install" };
            _cancel = new Button { Left = 468, Top = 430, Width = 74, Height = 28, Text = "Cancel" };
            _check.Click += (s, e) => RunCheck();
            _install.Click += OnInstall;
            _cancel.Click += (s, e) => Close();

            Controls.Add(header);
            Controls.Add(lbl); Controls.Add(_path); Controls.Add(_browse); Controls.Add(_desktop);
            Controls.Add(rl); Controls.Add(_reqs); Controls.Add(_links);
            Controls.Add(_status); Controls.Add(_bar); Controls.Add(_check); Controls.Add(_install); Controls.Add(_cancel);
            AcceptButton = _install; CancelButton = _cancel;
        }

        private void RunCheck()
        {
            _status.ForeColor = Color.Gray;
            _status.Text = "Checking requirements...";
            _check.Enabled = false;
            _bar.Visible = true;
            var t = new Thread(() =>
            {
                List<Req> rep = null; Exception err = null;
                try { rep = Requirements.Check(null); } catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _bar.Visible = false;
                        _check.Enabled = true;
                        if (err != null) { _reqs.Text = "The check failed: " + err.Message; _status.Text = ""; return; }
                        ShowReport(rep);
                    }));
                }
                catch { }
            }) { IsBackground = true };
            t.Start();
        }

        private void ShowReport(List<Req> rep)
        {
            _reqs.Text = Requirements.Format(rep);
            _links.Controls.Clear();
            foreach (var r in rep)
            {
                if (r.Url == null) continue;
                var url = r.Url;
                var ll = new LinkLabel { AutoSize = true, Text = "Get: " + url, Margin = new Padding(0, 0, 0, 2) };
                ll.LinkClicked += (s, e) => { try { Process.Start(url); } catch { } };
                _links.Controls.Add(ll);
            }
            int code = Requirements.ExitCode(rep);
            _status.ForeColor = code == 0 ? Color.Gray : Color.DarkOrange;
            _status.Text = code == 0 ? "Everything needed is present."
                         : code == 2 ? "This package's programs cannot run on this PC - see above. Installing is still allowed."
                         : "Something is missing - see above. Installing is still allowed.";
        }

        private void OnInstall(object sender, EventArgs e)
        {
            string dir = (_path.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) { MessageBox.Show(this, "Choose an install location."); return; }
            string busy;
            while ((busy = InUse.Release(dir)) != null)
                if (MessageBox.Show(this, busy + "\n\nClose it, then press Retry.", "Setup", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return;

            bool desktop = _desktop.Checked;
            SetBusy(true);
            var t = new Thread(() =>
            {
                Exception err = null;
                try { Installer.Install(dir, desktop, msg => { try { BeginInvoke((Action)(() => _status.Text = msg)); } catch { } }); }
                catch (Exception ex) { err = ex; }
                try { BeginInvoke((Action)(() => Finished(dir, err))); } catch { }
            }) { IsBackground = true };
            t.Start();
        }

        private void Finished(string dir, Exception err)
        {
            _bar.Visible = false;
            if (err != null)
            {
                MessageBox.Show(this, "Install failed:\n\n" + err.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
                return;
            }
            if (MessageBox.Show(this, Program.AppName + " was installed.\n\nLaunch it now?", "Setup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                try { Process.Start(Path.Combine(dir, Program.ExeName)); } catch { }
            Close();
        }

        private void SetBusy(bool busy)
        {
            _install.Enabled = _cancel.Enabled = _browse.Enabled = _path.Enabled = _desktop.Enabled = _check.Enabled = !busy;
            _bar.Visible = busy;
        }
    }
}
