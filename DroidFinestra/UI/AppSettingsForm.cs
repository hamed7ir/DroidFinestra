using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI.Controls;

namespace DroidFinestra.UI
{
    /// <summary>
    /// App-level settings (NOT per-device) — Finestra's AppSettingsForm shape: a path row + Browse + a read-only
    /// "currently resolves to" line, then app behaviour. Theme and accent live in the hamburger menu, as in
    /// Finestra. The DPI toggle takes effect on next launch (awareness is declared once at startup).
    /// </summary>
    public sealed class AppSettingsForm : ThemedDialog
    {
        private static readonly string[] CloseActionOptions = { "Ask each time", "Minimize to tray", "Exit" };   // CloseAction order
        private static readonly string[] LibusbOptions = { "Auto (on for an ARM32 adb)", "On", "Off" };

        private readonly TextRow _folder;
        private readonly NoteRow _resolves, _resolvesPath;
        private readonly ChoiceRow _libusb, _closeAction;
        private readonly ToggleRow _confirm, _dpi, _logging, _kbResize;
#if DEBUG
        private readonly ToggleRow _kbIgnoreHw;
#endif

        public AppSettingsForm() : base("Settings", 540, 600)
        {
            var s = AppSettings.Instance;

            _folder = new TextRow("scrcpy folder (blank = this app's folder)", s.ScrcpyFolder ?? "");
            _folder.Changed += UpdateResolves;
            var browse = new RoundedButton { Text = "Browse…", Kind = RoundedButtonKind.Neutral, Height = 40, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            browse.Click += (a, b) =>
            {
                using (var fbd = new FolderBrowserDialog { Description = "The folder with scrcpy.exe, adb.exe, the FFmpeg DLLs and scrcpy-server", ShowNewFolderButton = false })
                {
                    try { string cur = (_folder.Value ?? "").Trim(); if (cur.Length > 0 && Directory.Exists(cur)) fbd.SelectedPath = cur; } catch { }
                    if (fbd.ShowDialog(this) == DialogResult.OK) _folder.Value = fbd.SelectedPath;
                }
            };
            _resolvesPath = new NoteRow("") { PathLine = true };
            _resolves = new NoteRow("");

            int li = string.Equals(s.AdbLibusb, "On", StringComparison.OrdinalIgnoreCase) ? 1
                   : string.Equals(s.AdbLibusb, "Off", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            _libusb = new ChoiceRow("ADB_LIBUSB=1", LibusbOptions, li) { ValueWidth = 220 };
            var libusbNote = new NoteRow("adb-rt needs ADB_LIBUSB=1 to see a phone over USB on Windows RT.");

            _confirm = new ToggleRow("Show the command line before launching", s.ConfirmBeforeLaunch);

            _closeAction = new ChoiceRow("When I close the window", CloseActionOptions, (int)CloseActions.Parse(s.CloseAction)) { ValueWidth = 200 };
            _logging = new ToggleRow("Write a diagnostic log (off by default)", s.EnableLogging);
            var loggingNote = new NoteRow("scrcpy's own output is always kept, one log per launch, in the data folder's logs.");
            _kbResize = new ToggleRow("Make room for the touch keyboard (RT)", s.KeyboardAutoResize);
#if DEBUG
            _kbIgnoreHw = new ToggleRow("  Ignore hardware-keyboard check (diagnostic)", s.KeyboardIgnoreHardwareCheck);
#endif
            var openFolder = new RoundedButton { Text = "Open data folder", Kind = RoundedButtonKind.Neutral, Height = 40, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            openFolder.Click += (a, b) => { try { Process.Start("explorer.exe", CommandLine.Quote(StoragePaths.AppDataDir)); } catch { } };

            _dpi = new ToggleRow("Proportional scaling — DPI-unaware (restart required)", s.DpiUnaware);

            var rows = new List<Control>
            {
                new SectionHeader("scrcpy"), _folder, browse, _resolvesPath, _resolves, _libusb, libusbNote, _confirm,
                new SectionHeader("App behaviour"), _closeAction, _logging, loggingNote, _kbResize,
            };
#if DEBUG
            rows.Add(_kbIgnoreHw);
#endif
            rows.Add(openFolder);
            rows.AddRange(new Control[] { new SectionHeader("Display"), _dpi });
            PopulateBody(rows.ToArray());
            UpdateResolves();

            var save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);
            save.Click += (a, b) => OnSave();
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }

        /// <summary>What the typed folder would give: found or not, and each binary's architecture vs this PC's.</summary>
        private void UpdateResolves()
        {
            string f = (_folder.Value ?? "").Trim();
            if (f.Length == 0) f = ScrcpyTools.AppDir;
            string sc = Path.Combine(f, "scrcpy.exe"), adb = Path.Combine(f, "adb.exe");
            _resolvesPath.Text2 = "Uses " + f;
            string text = "scrcpy.exe: " + (File.Exists(sc) ? ScrcpyTools.MachineName(ScrcpyTools.PeMachine(sc)) : "not found")
                + "   ·   adb.exe: " + (File.Exists(adb) ? ScrcpyTools.MachineName(ScrcpyTools.PeMachine(adb)) : "not found")
                + "   ·   this PC: " + ScrcpyTools.DetectArch();
            _resolves.Text2 = text;
        }

        private void OnSave()
        {
            string folder = (_folder.Value ?? "").Trim();
            if (folder.Length > 0 && !Directory.Exists(folder))
            {
                ConfirmDialog.Info(this, "That folder does not exist.", "Settings");
                return;
            }
            var s = AppSettings.Instance;
            s.ScrcpyFolder = folder;
            s.AdbLibusb = _libusb.SelectedIndex == 1 ? "On" : _libusb.SelectedIndex == 2 ? "Off" : "Auto";
            s.ConfirmBeforeLaunch = _confirm.On;
            s.CloseAction = ((CloseAction)_closeAction.SelectedIndex).ToString();
            s.EnableLogging = _logging.On;
            s.KeyboardAutoResize = _kbResize.On;
#if DEBUG
            s.KeyboardIgnoreHardwareCheck = _kbIgnoreHw.On;
#endif
            s.DpiUnaware = _dpi.On;
            s.Save();
            FileLog.Refresh();               // toggled on → the log opens now; off → it is closed and no file grows
            KeyboardInset.RefreshRunning();  // toggled off → the poller stops now
            DialogResult = DialogResult.OK;
        }
    }
}
