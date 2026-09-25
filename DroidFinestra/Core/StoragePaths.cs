using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DroidFinestra.Core
{
    /// <summary>
    /// Resolves the app's persistent data directory. RT 8.1 INVARIANT: %APPDATA% is UNRELIABLE on
    /// jailbroken RT (confirmed in CS-Ray/TelegArm) — so we prefer <c>Documents\&lt;app&gt;\</c> with a REAL
    /// write-test, and only fall back to %APPDATA% then beside-exe if Documents is unwritable.
    /// File-only: never reads Screen/UI (safe to call before SetProcessDpiAwareness).
    ///
    /// Taken from Finestra unchanged except for the folder/file names (<see cref="AppIdentity"/>) and the
    /// removal of Finestra's one-time FinestraRDP migration, which has no counterpart here.
    /// </summary>
    public static class StoragePaths
    {
        private static string AppFolder => AppIdentity.DataFolder;

        private static string _dir;

        /// <summary>The resolved, write-tested data directory (cached). Logs which root won via [PATHS].</summary>
        public static string AppDataDir
        {
            get
            {
                if (_dir != null) return _dir;
                // PORTABLE wins outright: this copy's data lives with it.
                var portable = PortableDir();
                if (portable != null)
                {
                    _dir = portable;
                    System.Diagnostics.Debug.WriteLine("[PATHS] data dir = " + _dir + "  (PORTABLE - sentinel beside the exe)");
                    return _dir;
                }
                foreach (var root in CandidateRoots())
                {
                    try
                    {
                        var dir = Path.Combine(root, AppFolder);
                        Directory.CreateDirectory(dir);
                        var probe = Path.Combine(dir, ".writeprobe");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);                     // real write-test, not just existence
                        _dir = dir;
                        System.Diagnostics.Debug.WriteLine("[PATHS] data dir = " + dir);
                        return _dir;
                    }
                    catch { /* not writable — try the next candidate */ }
                }
                _dir = Path.Combine(Path.GetTempPath(), AppFolder);   // last resort (should never be needed)
                try { Directory.CreateDirectory(_dir); } catch { }
                System.Diagnostics.Debug.WriteLine("[PATHS] data dir (temp fallback) = " + _dir);
                return _dir;
            }
        }
        private static IEnumerable<string> CandidateRoots()
        {
            string docs = null, appdata = null, exe = null;
            try { docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { }
            if (!string.IsNullOrEmpty(docs)) yield return docs;                 // 1) Documents — RT-preferred
            try { appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch { }
            if (!string.IsNullOrEmpty(appdata)) yield return appdata;           // 2) %APPDATA% (unreliable on RT)
            try { exe = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            if (!string.IsNullOrEmpty(exe)) yield return exe;                   // 3) beside the exe
        }

        /// <summary>PORTABLE MODE. A file with this name beside the exe makes this copy keep ALL of its
        /// data with it — profiles, settings and the logs — instead of in
        /// the shared per-user data directory. It touches nothing in Documents.
        ///
        /// It applies to EVERY data file, not just the log. "Portable" means the folder is the whole of it.
        ///
        /// ⚠ CONSEQUENCE, and it is intended: a portable copy starts with ZERO profiles. It does not
        /// inherit an installed copy's profiles, and profiles made in it stay in it.
        ///
        /// Without the sentinel nothing changes. Any user can create one; it needs no content.</summary>
        public static string PortableSentinel => AppIdentity.PortableSentinel;

        /// <summary>The exe's own directory when this is a portable copy AND that directory is really
        /// writable, otherwise null. The write probe matters — a bundle opened read-only (a network
        /// share, a locked-down stick) must fall back to the shared dir rather than lose its data.</summary>
        private static string PortableDir()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(exeDir)) return null;
                if (!File.Exists(Path.Combine(exeDir, PortableSentinel))) return null;
                var probe = Path.Combine(exeDir, ".writeprobe");
                try { File.WriteAllText(probe, "ok"); File.Delete(probe); return exeDir; }
                catch { return null; }   // not writable — fall through to the normal candidates
            }
            catch { return null; }
        }

        public static string ProfilesFile { get { return Path.Combine(AppDataDir, "profiles.json"); } }
        public static string SettingsFile { get { return Path.Combine(AppDataDir, "settings.json"); } }
        public static string LogFile      { get { return Path.Combine(AppDataDir, AppIdentity.Id + ".log"); } }
        /// <summary>One scrcpy output log per launch (see <see cref="ScrcpySession"/>).</summary>
        public static string SessionLogDir { get { return Path.Combine(AppDataDir, "logs"); } }
    }
}
