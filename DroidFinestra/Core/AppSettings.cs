using System;
using System.IO;
using Newtonsoft.Json;

namespace DroidFinestra.Core
{
    /// <summary>
    /// App-level settings (NOT per-device options) — theme mode, DPI awareness, where scrcpy lives.
    /// Finestra's AppSettings, same store: settings.json beside profiles.json, loaded FILE-ONLY before any
    /// window or Screen metric is touched (Program.Main reads DpiUnaware/ThemeMode here), saved atomically.
    /// </summary>
    public sealed class AppSettings
    {
        public string ThemeMode { get; set; } = "System";      // System | Light | Dark
        public bool   DpiUnaware { get; set; } = false;         // opt-in "proportional scaling" rescue
        /// <summary>"" = follow the Windows accent (live, Finestra's behaviour); "#RRGGBB" = a fixed accent.</summary>
        public string AccentColor { get; set; } = "";

        /// <summary>Folder holding scrcpy.exe, adb.exe, the FFmpeg DLLs and scrcpy-server. Empty = the folder
        /// this app runs from (the release layout ships them side by side).</summary>
        public string ScrcpyFolder { get; set; } = "";

        /// <summary>ADB_LIBUSB for adb and scrcpy's adb: Auto | On | Off. adb-rt (ARM32) needs ADB_LIBUSB=1 for
        /// USB; Auto sets it only when adb.exe is an ARM32 binary, so an x64 platform-tools adb is left alone.</summary>
        public string AdbLibusb { get; set; } = "Auto";

        public string CloseAction { get; set; } = "Ask";        // Ask | MinimizeToTray | Exit — what the ✕ does (resettable in Settings)

        /// <summary>Show the full command line and wait for "Launch" (true), or launch straight away.</summary>
        public bool ConfirmBeforeLaunch { get; set; } = true;

        /// <summary>Write the app's own diagnostic log? OFF by default (Finestra's rule). scrcpy's output is
        /// always written to its per-launch log regardless — that one is the product, not a diagnostic.</summary>
        public bool   EnableLogging { get; set; } = false;
        public bool   KeyboardAutoResize { get; set; } = true;  // give the RT touch keyboard room; off = escape hatch
        public bool   KeyboardIgnoreHardwareCheck { get; set; } = false;   // Finestra diagnostic override (DEBUG UI only)

        private static AppSettings _instance;
        public static AppSettings Instance { get { return _instance ?? (_instance = Load()); } }

        private static AppSettings Load()
        {
            try
            {
                var path = StoragePaths.SettingsFile;
                if (File.Exists(path))
                {
                    var s = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                    // Json.NET overwrites a field initializer with an explicit null (Finestra's B18) — re-establish.
                    if (s.ScrcpyFolder == null) s.ScrcpyFolder = "";
                    if (s.AdbLibusb == null) s.AdbLibusb = "Auto";
                    if (s.ThemeMode == null) s.ThemeMode = "System";
                    if (s.AccentColor == null) s.AccentColor = "";
                    if (s.CloseAction == null) s.CloseAction = "Ask";
                    return s;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SETTINGS] load failed: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            // atomic temp-then-replace, so a crash or power-loss mid-write can't corrupt settings.json
            try
            {
                string path = StoragePaths.SettingsFile;
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SETTINGS] save failed: " + ex.Message); }
        }
    }
}
