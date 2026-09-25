using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using DroidFinestra.Helpers;

namespace DroidFinestra.Core
{
    /// <summary>
    /// Where scrcpy.exe and adb.exe are, whether this PC can run them, and the environment both need.
    /// The PE-machine guard and the env-var arch detection are Finestra's (RdpLauncher.PeMachine / DetectArch):
    /// a wrong-architecture bundle is reported by name instead of failing as a bare "cannot start".
    /// </summary>
    public static class ScrcpyTools
    {
        public const ushort MachineX86 = 0x14c, MachineX64 = 0x8664, MachineArm = 0x1c0, MachineArmNt = 0x1c4, MachineArm64 = 0xaa64;

        /// <summary>The folder this app runs from.</summary>
        public static string AppDir
        {
            get
            {
                try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ""; }
                catch { return ""; }
            }
        }

        /// <summary>The configured scrcpy folder, or the app's own folder when none is set.</summary>
        public static string Folder
        {
            get
            {
                string f = (AppSettings.Instance.ScrcpyFolder ?? "").Trim();
                return f.Length > 0 ? f : AppDir;
            }
        }

        public static string ScrcpyExe => Path.Combine(Folder, "scrcpy.exe");
        public static string AdbExe => Path.Combine(Folder, "adb.exe");

        /// <summary>The OS architecture: "x64" | "x86" | "arm64" | "arm" | "unknown". Env-var based (no .NET 4.7.1
        /// RuntimeInformation), so it works on Windows 8.1 / RT.</summary>
        public static string DetectArch()
        {
            string e = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
                        ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
            switch (e)
            {
                case "AMD64": return "x64";
                case "X86": return "x86";
                case "ARM64": return "arm64";
                case "ARM": return "arm";
                default: return "unknown";
            }
        }

        /// <summary>Reads the PE machine type (COFF header) of a binary; 0 on any failure.</summary>
        public static ushort PeMachine(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int peOff = br.ReadInt32();
                    fs.Seek(peOff + 4, SeekOrigin.Begin);
                    return br.ReadUInt16();
                }
            }
            catch { return 0; }
        }

        public static string MachineName(ushort m)
        {
            switch (m)
            {
                case MachineX86: return "x86";
                case MachineX64: return "x64";
                case MachineArm: case MachineArmNt: return "ARM32";
                case MachineArm64: return "ARM64";
                case 0: return "unreadable";
                default: return "machine 0x" + m.ToString("X");
            }
        }

        private static bool CanRun(ushort m, string arch)
        {
            if (m == 0) return true;   // unreadable: let Process.Start report it
            switch (arch)
            {
                case "x64": return m == MachineX64 || m == MachineX86;
                case "x86": return m == MachineX86;
                case "arm": return m == MachineArmNt || m == MachineArm;
                case "arm64": return true;   // Windows 11 ARM64 runs ARM32, ARM64, x86 and x64
                default: return true;
            }
        }

        /// <summary>Null when scrcpy can be launched from <see cref="Folder"/>; otherwise one sentence saying why not.</summary>
        public static string Problem()
        {
            string folder = Folder;
            if (!File.Exists(ScrcpyExe))
                return "scrcpy.exe was not found in " + folder + ". Put the scrcpy-rt files beside this app, or set the folder in Settings.";
            if (!File.Exists(AdbExe))
                return "adb.exe was not found in " + folder + ".";
            string arch = DetectArch();
            ushort m = PeMachine(ScrcpyExe);
            if (!CanRun(m, arch))
                return "scrcpy.exe in " + folder + " is an " + MachineName(m) + " build; this PC is " + arch + ".";
            ushort am = PeMachine(AdbExe);
            if (!CanRun(am, arch))
                return "adb.exe in " + folder + " is an " + MachineName(am) + " build; this PC is " + arch + ".";
            return null;
        }

        /// <summary>Whether ADB_LIBUSB=1 is set for adb. adb-rt needs it for USB; Auto = only for an ARM32 adb.exe.</summary>
        public static bool LibusbOn
        {
            get
            {
                string mode = AppSettings.Instance.AdbLibusb ?? "Auto";
                if (string.Equals(mode, "On", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return false;
                ushort m = PeMachine(AdbExe);
                return m == MachineArmNt || m == MachineArm;
            }
        }

        /// <summary>ADB = our adb.exe (so scrcpy uses the same adb as this app), plus ADB_LIBUSB per the setting.</summary>
        public static void ApplyEnv(ProcessStartInfo psi)
        {
            if (File.Exists(AdbExe)) psi.EnvironmentVariables["ADB"] = AdbExe;
            bool on = LibusbOn;
            if (on) psi.EnvironmentVariables["ADB_LIBUSB"] = "1";
            else if (string.Equals(AppSettings.Instance.AdbLibusb, "Off", StringComparison.OrdinalIgnoreCase))
                psi.EnvironmentVariables.Remove("ADB_LIBUSB");
        }

        /// <summary>The environment this app adds, for display beside the command line.</summary>
        public static string EnvSummary()
        {
            string s = "ADB=" + AdbExe;
            if (LibusbOn) s += "   ADB_LIBUSB=1";
            return s;
        }

        /// <summary>Opens a text file in Notepad (present on RT 8.1); falls back to the shell association.</summary>
        public static void OpenInNotepad(string path)
        {
            try { Process.Start("notepad.exe", CommandLine.Quote(path)); }
            catch
            {
                try { Process.Start(path); } catch (Exception ex) { FileLog.Line("[OPEN] " + path + " failed: " + ex.Message); }
            }
        }
    }
}
