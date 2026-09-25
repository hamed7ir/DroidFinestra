using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI.Controls;

namespace DroidFinestra.UI
{
    /// <summary>
    /// §5 — one window per launch. Before launching it shows the full command line (read-only, selectable,
    /// copyable). After: scrcpy's own output live, a note on the known icon messages, and on exit the exit code
    /// plus — on failure — the last lines that matter. scrcpy's errors are shown as scrcpy wrote them.
    /// scrcpy's video window is its own top-level window; this one only launches and watches it.
    /// </summary>
    public sealed class SessionForm : ThemedDialog
    {
        private readonly DeviceProfile _profile;
        private ScrcpySession _session;
        private Action _exitHandler;

        private readonly Panel _pane;
        private readonly Label _cmdLabel, _envLabel, _status, _logLabel;
        private readonly TextBox _cmd, _log;
        private readonly RoundedButton _launch, _copy, _openLog;
        private readonly Timer _flush;
        private readonly List<string> _pending = new List<string>();
        private bool _statusIsError;

        public string ProfileId => _profile.Id;
        public bool IsRunning => _session != null && _session.IsRunning;

        public SessionForm(DeviceProfile profile) : base(profile.DisplayName + " — scrcpy", 760, 540)
        {
            _profile = profile.Copy();
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = true;

            // The session UI is not a stack of setting rows: hide the scrolling Body and put a docked pane in its place.
            Body.Visible = false;
            _pane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 6) };

            _cmdLabel = MakeLabel("Command line", 9.5f, false);
            _cmd = new TextBox { Dock = DockStyle.Top, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Mono(9.5f) };
            _cmd.Height = _cmd.Font.Height * 4 + 8;   // four lines at any DPI: a full path plus the arguments
            _envLabel = MakeLabel("", 9f, false);
            _status = MakeLabel("Ready — press Launch.", 10.5f, true);
            _logLabel = MakeLabel("scrcpy output", 9.5f, false);
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Mono(9f) };

            // Dock order: Fill first, then the Top strips bottom-up (the last added docks highest).
            _pane.Controls.Add(_log);
            _pane.Controls.Add(_logLabel);
            _pane.Controls.Add(_status);
            _pane.Controls.Add(_envLabel);
            _pane.Controls.Add(_cmd);
            _pane.Controls.Add(_cmdLabel);
            Body.Parent.Controls.Add(_pane);
            _pane.BringToFront();   // docked after the footer, so the footer keeps its band

            _launch = AddFooterButton("Launch", RoundedButtonKind.Primary, DialogResult.None);
            _launch.Click += (s, e) => { if (IsRunning) _session.Stop(); else Launch(); };
            var close = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.None);
            close.Click += (s, e) => Close();
            _openLog = AddFooterButton("Open log", RoundedButtonKind.Neutral, DialogResult.None);
            _openLog.Enabled = false;
            _openLog.Click += (s, e) => { if (_session?.LogPath != null) ScrcpyTools.OpenInNotepad(_session.LogPath); };
            _copy = AddFooterButton("Copy command", RoundedButtonKind.Neutral, DialogResult.None);
            _copy.Width = 160;
            _copy.Click += (s, e) => CopyCommand();

            _flush = new Timer { Interval = 150 };
            _flush.Tick += (s, e) => FlushLines();
            _flush.Start();

            PrepareSession();
            ApplySessionTheme();
        }

        private static Label MakeLabel(string text, float size, bool bold)
        {
            var f = FontHelper.Ui(size, bold ? FontStyle.Bold : FontStyle.Regular);
            return new Label { Dock = DockStyle.Top, AutoSize = false, Height = f.Height + 8, Text = text, TextAlign = ContentAlignment.MiddleLeft,
                               Font = f, AutoEllipsis = true };
        }

        /// <summary>A fresh session for the next launch: the command line is computed now and shown before anything runs.</summary>
        private void PrepareSession()
        {
            if (_session != null) { _session.Line -= OnLine; _session.Exited -= _exitHandler; }   // a relaunch supersedes it
            var s = new ScrcpySession(_profile);
            s.Line += OnLine;
            _exitHandler = () => OnExited(s);   // bound to THIS session: a late exit of an older one is ignored
            s.Exited += _exitHandler;
            _session = s;
            _cmd.Text = _session.CommandLineText;
            _envLabel.Text = "Environment:  " + _session.EnvText;
            string problem = ScrcpyTools.Problem();
            if (problem != null) SetStatus(problem, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!AppSettings.Instance.ConfirmBeforeLaunch) Launch();
        }

        public void Launch()
        {
            if (IsRunning) return;
            if (_session.HasRun) PrepareSession();   // relaunch: new log file, same profile
            string error;
            _log.Clear();
            lock (_pending) _pending.Clear();
            if (!_session.Start(out error))
            {
                SetStatus(error, true);
                return;
            }
            SetStatus("Running. Close the scrcpy window, or press Stop.", false);
            _launch.Text = "Stop";
            _launch.Kind = RoundedButtonKind.Danger;
            _openLog.Enabled = _session.LogPath != null;
        }

        private void OnLine(string text, LineKind kind)
        {
            lock (_pending) _pending.Add(kind == LineKind.Note ? "» " + text : text);
        }

        private void FlushLines()
        {
            string[] batch;
            lock (_pending)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }
            var sb = new StringBuilder();
            foreach (var l in batch) sb.Append(l).Append("\r\n");
            // Keep the view bounded on RT: scrcpy --verbosity=verbose logs every touch.
            if (_log.TextLength > 400000) { _log.Text = _log.Text.Substring(_log.TextLength - 200000); }
            _log.AppendText(sb.ToString());
        }

        private void OnExited(ScrcpySession s)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed || s != _session) return;   // a relaunch already replaced this session
                    FlushLines();
                    _launch.Text = "Launch";
                    _launch.Kind = RoundedButtonKind.Primary;
                    if (s.ExitCode == 0 || s.StopRequested)
                    {
                        SetStatus("scrcpy exited with code " + s.ExitCode + (s.StopRequested ? " (stopped)" : "") + ".", false);
                        return;
                    }
                    SetStatus("scrcpy exited with code " + s.ExitCode + " — its last lines are below.", true);
                    var sb = new StringBuilder();
                    sb.Append("\r\n──── scrcpy exited with code ").Append(s.ExitCode).Append(" · last lines ────\r\n");
                    foreach (var l in s.FailureTail(12)) sb.Append(l).Append("\r\n");
                    _log.AppendText(sb.ToString());
                }));
            }
            catch { }
        }

        private void SetStatus(string text, bool error)
        {
            _status.Text = text;
            _statusIsError = error;
            ApplySessionTheme();
        }

        private void CopyCommand()
        {
            try { Clipboard.SetText(_cmd.Text); _copy.Text = "Copied"; }
            catch { _copy.Text = "Copy failed"; }
            var t = new Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!_copy.IsDisposed) _copy.Text = "Copy command"; };
            t.Start();
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            ApplySessionTheme();
        }

        private void ApplySessionTheme()
        {
            if (_pane == null) return;
            bool dark = ThemeHelper.IsDark;
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            Color fg = dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
            Color sub = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);
            Color box = dark ? Color.FromArgb(44, 44, 50) : Color.White;
            _pane.BackColor = bg;
            foreach (var l in new[] { _cmdLabel, _envLabel, _logLabel }) { l.BackColor = bg; l.ForeColor = sub; }
            _status.BackColor = bg;
            _status.ForeColor = _statusIsError ? Color.FromArgb(214, 72, 60) : ThemeHelper.GetWindowsAccentColor();
            foreach (var t in new[] { _cmd, _log }) { t.BackColor = box; t.ForeColor = fg; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (IsRunning && e.CloseReason == CloseReason.UserClosing)
            {
                if (!ConfirmDialog.Ask(this, "scrcpy is still running. Stop it and close this window?", _profile.DisplayName, "Stop and close", "Keep open"))
                { e.Cancel = true; return; }
                _session.Stop();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _flush.Stop();
            _flush.Dispose();
            if (_session != null) { _session.Line -= OnLine; _session.Exited -= _exitHandler; }
            base.OnFormClosed(e);
        }
    }
}
