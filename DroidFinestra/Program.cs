using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI;

namespace DroidFinestra
{
    internal static class Program
    {
        /// <summary>Physical system DPI captured at startup (96 = 100%). Read by the settings "DPI rescue"
        /// affordance. Stays 96 until the [DPI] probe runs (and reads 96 when already unaware).</summary>
        public static int SystemDpi { get; private set; } = 96;

        // PROCESS_SYSTEM_DPI_AWARE = 1 — deliberately system-aware (Finestra's model; RT 8.1 has no per-monitor story).
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);
        [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr hProcess, out int value);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]  private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private const int LOGPIXELSX = 88;

        [STAThread]
        static void Main()
        {
            // FIN-SINGLETON — the gate runs FIRST. A second launch activates the running app and exits 0
            // before ANY init (no forms, no JobGuard, no paths, no log). A hung/dead owner falls through
            // to a normal start (2s signal timeout) so a click never does nothing. A portable copy in a
            // different folder is a different gate (path-scoped) and runs as its own instance, by design.
            if (!SingleInstance.TryBecomeOwner()) return;

            // Surface otherwise-silent crashes (record + show). ThreadException is single-slot; UnhandledException multicasts.
            Application.ThreadException += (s, e) =>
            {
                try { Trace.WriteLine("[CRASH] UI: " + e.Exception); } catch { }
                MessageBox.Show(e.Exception.ToString(), AppIdentity.Name + " — unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Trace.WriteLine("[CRASH] fatal: " + (e.ExceptionObject as Exception)); } catch { }
                MessageBox.Show((e.ExceptionObject as Exception)?.ToString() ?? "Unknown error",
                    AppIdentity.Name + " — fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            // File logging first, so the earliest [PATHS]/[DPI]/[ACCENT] traces are captured on RT (no DebugView there).
            FileLog.Init();

            // Kill-on-close job created at startup; every scrcpy child is assigned to it at launch, so no orphaned
            // scrcpy survives the app (normal exit, crash OR taskkill).
            JobGuard.Init();

            // DPI: declared at runtime (no manifest). Settings read is FILE-ONLY (AppSettings/StoragePaths touch files,
            // never Screen/UI) — INVARIANT: nothing before this call may create UI or read Screen metrics.
            var settings = AppSettings.Instance;
            try { SetProcessDpiAwareness(settings.DpiUnaware ? 0 : 1); } catch { /* shcore unavailable */ }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Apply the persisted theme mode (non-notifying) before any form reads the theme.
            ThemeMode mode;
            if (!Enum.TryParse(settings.ThemeMode, true, out mode)) mode = ThemeMode.System;
            ThemeHelper.InitMode(mode);
            ThemeHelper.InitAccent(ThemeHelper.ParseAccent(settings.AccentColor));   // "" = follow the Windows accent

            // [DPI] truth line (permanent): effective awareness + system DPI — a manifest/compat-flag/library call can
            // silently override the declaration above, and that class of regression must be diagnosable from the log alone.
            try
            {
                int aw;
                if (GetProcessDpiAwareness(IntPtr.Zero, out aw) == 0)
                {
                    int dpi = 96;
                    IntPtr dc = GetDC(IntPtr.Zero);
                    if (dc != IntPtr.Zero) { dpi = GetDeviceCaps(dc, LOGPIXELSX); ReleaseDC(IntPtr.Zero, dc); }
                    SystemDpi = dpi;
                    Debug.WriteLine("[DPI] awareness=" + (aw == 0 ? "unaware" : aw == 1 ? "system" : "per-monitor")
                        + " systemDpi=" + dpi);
                }
            }
            catch { /* shcore unavailable (RT 8.1 has it) */ }


            var mainForm = new MainForm();
            // The activation listener marshals via BeginInvoke, which needs a live handle — force creation now.
            if (!mainForm.IsHandleCreated) { var _ = mainForm.Handle; }
            SingleInstance.StartListener(mainForm);
            Application.Run(mainForm);
        }
    }
}
