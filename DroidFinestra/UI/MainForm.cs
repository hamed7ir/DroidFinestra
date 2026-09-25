using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI.Controls;

namespace DroidFinestra.UI
{
    /// <summary>
    /// The app shell — Finestra's MainForm with connections replaced by device profiles: a borderless window with
    /// an owner-painted accent title bar (hamburger + title + close), a hamburger flyout, the empty state / the
    /// searchable profile list, a tray icon and the Ask / Minimize-to-tray / Exit close behaviour.
    /// Accent + dark/light come from <see cref="ThemeHelper"/> — the Windows accent (OS-branched: DWM\AccentColor
    /// on 10/11, Explorer\Accent\AccentColor on 8.1/RT, live via WM_DWMCOLORIZATIONCOLORCHANGED) unless the user
    /// picked one under Accent. Launching opens a <see cref="SessionForm"/>; scrcpy runs as a child process.
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int BarH = 46;

        private Panel _header, _content;
        private TableLayoutPanel _outer, _stack;
        private GlyphButton _menuBtn, _closeBtn;
        private Label _emptyTitle, _emptyHint;
        private RoundedButton _newBtn, _newBtn2;
        private Panel _listHost, _topBar;
        private Label _listTitle;
        private TextBox _searchBox;
        private ThemedScrollPanel _listScroll;
        private ThemedContextMenuStrip _flyout;
        private ToolStripMenuItem _miSystem, _miLight, _miDark, _miAccentWindows, _miAccentCustom;
        private readonly List<ToolStripMenuItem> _accentItems = new List<ToolStripMenuItem>();

        private bool _drag;
        private Point _dragStart;

        private NotifyIcon _tray;
        private bool _forceExit;              // an explicit Exit (hamburger / tray / dialog) → bypass CloseAction

        /// <summary>Open launch windows by profile Id — Launch on a profile that already has one brings it forward.</summary>
        private readonly Dictionary<string, SessionForm> _sessions = new Dictionary<string, SessionForm>();

        /// <summary>A few fixed accents besides "follow Windows". Green first: DroidFinestra's own colour.</summary>
        private static readonly KeyValuePair<string, Color>[] AccentSwatches =
        {
            new KeyValuePair<string, Color>("Green",  Color.FromArgb(16, 137, 62)),
            new KeyValuePair<string, Color>("Blue",   Color.FromArgb(0, 120, 215)),
            new KeyValuePair<string, Color>("Teal",   Color.FromArgb(0, 128, 128)),
            new KeyValuePair<string, Color>("Purple", Color.FromArgb(116, 77, 169)),
            new KeyValuePair<string, Color>("Red",    Color.FromArgb(196, 43, 28)),
            new KeyValuePair<string, Color>("Orange", Color.FromArgb(202, 80, 16)),
        };

        public MainForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 580);
            MinimumSize = new Size(600, 420);
            AutoScaleMode = AutoScaleMode.Font;   // RT DPI model: system-aware font scaling (not per-monitor)
            Font = FontHelper.Ui(9.75f);
            Text = AppIdentity.Name;
            DoubleBuffered = true;
            ThemedChrome.SetAppIcon(this);        // app icon on taskbar / Alt-Tab

            BuildChrome();
            BuildContent();
            BuildFlyout();
            BuildTray();

            ApplyTheme();
            RefreshProfiles();
            ThemeHelper.ThemeChanged += ApplyTheme;
            ThemeHelper.StartListening();

            // FIN-KEYBOARD — bottom-inset the content while the touch keyboard covers us (the list just gets shorter).
            KeyboardInset.KeyboardRectChanged += OnKeyboardRect;
            KeyboardInset.Register();
            Disposed += (s, e) => { KeyboardInset.KeyboardRectChanged -= OnKeyboardRect; KeyboardInset.Unregister(); };
        }

        private void OnKeyboardRect(Rectangle kb)
        {
            try
            {
                if (IsDisposed) return;
                int inset = KeyboardInset.ComputeBottomInset(this, kb);
                if (Padding.Bottom == inset) return;
                SuspendLayout();
                Padding = new Padding(0, 0, 0, inset);
                ResumeLayout(true);
                FileLog.Line("[KBD] manager inset=" + inset);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            foreach (Control r in _listScroll.Host.Controls) r.Width = _listScroll.Host.ClientSize.Width;
            _listScroll.RelayoutContent();
        }

        // ── chrome ─────────────────────────────────────────────────────────────
        private void BuildChrome()
        {
            _header = new Panel { Dock = DockStyle.Top, Height = BarH };
            _header.Paint += HeaderPaint;
            _header.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && WindowState == FormWindowState.Normal) { _drag = true; _dragStart = e.Location; } };
            _header.MouseMove += (s, e) => { if (_drag) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y); };
            _header.MouseUp += (s, e) => _drag = false;
            _header.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };

            _menuBtn = new GlyphButton(GlyphButton.Glyph.Menu) { Dock = DockStyle.Left, Width = 52, TabStop = false };
            _menuBtn.Click += (s, e) => _flyout.Show(_menuBtn, new Point(0, _menuBtn.Height));

            _closeBtn = new GlyphButton(GlyphButton.Glyph.Close) { Dock = DockStyle.Right, Width = 52, TabStop = false };
            _closeBtn.Click += (s, e) => Close();

            _header.Controls.Add(_menuBtn);
            _header.Controls.Add(_closeBtn);

            _content = new Panel { Dock = DockStyle.Fill };
            Controls.Add(_content);            // Fill added first → docks around the Top header
            Controls.Add(_header);
        }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            int left = _menuBtn.Width + 6;
            int right = _closeBtn.Width + 6;
            var rect = new Rectangle(left, 0, Math.Max(1, _header.Width - left - right), BarH);
            using (var f = FontHelper.Ui(13f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, AppIdentity.Name, f, rect, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        // ── content ─────────────────────────────────────────────────────────────
        private void BuildContent()
        {
            _outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            _outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            _outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            _stack = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Anchor = AnchorStyles.None };
            _stack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _emptyTitle = new Label { AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 6), Text = "No device profiles yet", Font = FontHelper.Ui(15f, FontStyle.Bold) };
            _emptyHint = new Label { AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 18), Text = "Create one — the Surface RT presets are built in.", Font = FontHelper.Ui(10f) };
            _newBtn = new RoundedButton { Kind = RoundedButtonKind.Primary, Text = "New profile", Anchor = AnchorStyles.None, Width = 200, Height = 42, Margin = new Padding(0), Font = FontHelper.Ui(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            _newBtn.Click += (s, e) => NewProfile();

            _stack.Controls.Add(_emptyTitle, 0, 0);
            _stack.Controls.Add(_emptyHint, 0, 1);
            _stack.Controls.Add(_newBtn, 0, 2);
            _outer.Controls.Add(_stack, 0, 1);

            _listHost = new Panel { Dock = DockStyle.Fill, Visible = false };
            _topBar = new Panel { Dock = DockStyle.Top, Height = 56 };
            _listTitle = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "Device profiles", Font = FontHelper.Ui(13.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0) };
            _newBtn2 = new RoundedButton { Kind = RoundedButtonKind.Primary, Text = "New", Width = 96, Height = 36, Dock = DockStyle.Right, Margin = new Padding(0), Font = FontHelper.Ui(10f, FontStyle.Bold) };
            _newBtn2.Click += (s, e) => NewProfile();
            var newWrap = new Panel { Dock = DockStyle.Right, Width = 116, Padding = new Padding(8, 10, 16, 10) };
            newWrap.Controls.Add(_newBtn2);
            _searchBox = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Font = FontHelper.Ui(9.75f) };
            _searchBox.TextChanged += (s, e) => RefreshProfiles();
            var searchWrap = new Panel { Dock = DockStyle.Right, Width = 200, Padding = new Padding(4, 14, 4, 14) };
            searchWrap.Controls.Add(_searchBox);
            _topBar.Controls.Add(_listTitle);
            _topBar.Controls.Add(searchWrap);
            _topBar.Controls.Add(newWrap);
            _listScroll = new ThemedScrollPanel { Dock = DockStyle.Fill };
            _listHost.Controls.Add(_listScroll);
            _listHost.Controls.Add(_topBar);

            _content.Controls.Add(_outer);
            _content.Controls.Add(_listHost);
        }

        // ── profile list ────────────────────────────────────────────────────────
        private void RefreshProfiles()
        {
            var items = DeviceStore.Instance.Items;
            bool any = items.Count > 0;
            _outer.Visible = !any;
            _listHost.Visible = any;
            if (!any) return;

            // a filter matching nothing shows an empty LIST, not the first-run empty state
            string q = (_searchBox?.Text ?? "").Trim();
            var shown = q.Length == 0 ? items :
                items.Where(p => (p.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                              || (p.Address ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                              || (p.Serial ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var old = new List<Control>();
            foreach (Control c in _listScroll.Host.Controls) old.Add(c);
            _listScroll.Host.Controls.Clear();
            foreach (var c in old) c.Dispose();

            int y = 8;
            int w = Math.Max(10, _listScroll.Host.ClientSize.Width);
            foreach (var p in shown)
            {
                var row = new DeviceRow(p)
                {
                    Location = new Point(0, y),
                    Width = w,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                row.LaunchClicked += Launch;
                row.EditClicked += EditProfile;
                row.DeleteClicked += DeleteProfile;
                row.DuplicateClicked += DuplicateProfile;
                row.CopyCommandClicked += CopyCommand;
                _listScroll.Host.Controls.Add(row);
                y += row.Height + 8;
            }
            _listScroll.Host.Height = y + 8;
            _listScroll.RelayoutContent();
        }

        private void NewProfile()
        {
            using (var f = new DeviceEditorForm(null))
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                {
                    DeviceStore.Instance.AddOrUpdate(f.Result);
                    RefreshProfiles();
                }
        }

        private void EditProfile(DeviceProfile p)
        {
            using (var f = new DeviceEditorForm(p))
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                {
                    DeviceStore.Instance.AddOrUpdate(f.Result);
                    RefreshProfiles();
                }
        }

        private void DeleteProfile(DeviceProfile p)
        {
            if (ConfirmDialog.Ask(this, "Delete \"" + p.DisplayName + "\"?"))
            {
                DeviceStore.Instance.Remove(p.Id);
                RefreshProfiles();
            }
        }

        private void DuplicateProfile(DeviceProfile p)
        {
            var dup = p.Clone();
            dup.Name = p.DisplayName + " (copy)";
            DeviceStore.Instance.AddOrUpdate(dup);
            RefreshProfiles();
        }

        private void CopyCommand(DeviceProfile p)
        {
            var errs = p.Validate();
            if (errs.Count > 0) { ConfirmDialog.Info(this, string.Join("\n", errs.ToArray()), "Check the profile"); return; }
            try { Clipboard.SetText(CommandLine.Quote(ScrcpyTools.ScrcpyExe) + " " + p.BuildArguments()); }
            catch (Exception ex) { ConfirmDialog.Info(this, "Could not copy: " + ex.Message); }
        }

        /// <summary>Opens (or brings forward) the launch window for a profile. The profile as saved is what runs.</summary>
        private void Launch(DeviceProfile p)
        {
            SessionForm f;
            if (_sessions.TryGetValue(p.Id, out f) && !f.IsDisposed)
            {
                if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                f.Activate();
                if (!f.IsRunning && !AppSettings.Instance.ConfirmBeforeLaunch) f.Launch();
                return;
            }
            f = new SessionForm(p);
            _sessions[p.Id] = f;
            var created = f;
            f.FormClosed += (s, e) => { SessionForm cur; if (_sessions.TryGetValue(p.Id, out cur) && cur == created) _sessions.Remove(p.Id); };
            f.Show();
        }

        // ── hamburger flyout ────────────────────────────────────────────────────
        private void BuildFlyout()
        {
            _flyout = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.75f) };

            var miNew = new ToolStripMenuItem("New profile…");
            miNew.Click += (s, e) => NewProfile();
            var miDevices = new ToolStripMenuItem("Devices…");
            miDevices.Click += (s, e) => { using (var d = new DevicesDialog(pickMode: false)) d.ShowDialog(this); };
            var miPair = new ToolStripMenuItem("Pair a phone over Wi-Fi…");
            miPair.Click += (s, e) => PairPhone();
            var miSettings = new ToolStripMenuItem("Settings…");
            miSettings.Click += (s, e) => OpenSettings();

            var miTheme = new ToolStripMenuItem("Theme");
            _miSystem = new ToolStripMenuItem("System") { Tag = ThemeMode.System };
            _miLight = new ToolStripMenuItem("Light") { Tag = ThemeMode.Light };
            _miDark = new ToolStripMenuItem("Dark") { Tag = ThemeMode.Dark };
            EventHandler pick = (s, e) => SetTheme((ThemeMode)((ToolStripMenuItem)s).Tag);
            _miSystem.Click += pick; _miLight.Click += pick; _miDark.Click += pick;
            miTheme.DropDownItems.AddRange(new ToolStripItem[] { _miSystem, _miLight, _miDark });
            miTheme.DropDownOpening += (s, e) => SyncThemeChecks();

            var miAccent = new ToolStripMenuItem("Accent");
            _miAccentWindows = new ToolStripMenuItem("Windows accent");
            _miAccentWindows.Click += (s, e) => SetAccent(null);
            miAccent.DropDownItems.Add(_miAccentWindows);
            miAccent.DropDownItems.Add(new ToolStripSeparator());
            foreach (var sw in AccentSwatches)
            {
                var c = sw.Value;
                var it = new ToolStripMenuItem(sw.Key) { Tag = c, Image = Swatch(c) };
                it.Click += (s, e) => SetAccent(c);
                _accentItems.Add(it);
                miAccent.DropDownItems.Add(it);
            }
            _miAccentCustom = new ToolStripMenuItem("Custom…");
            _miAccentCustom.Click += (s, e) => PickCustomAccent();
            miAccent.DropDownItems.Add(new ToolStripSeparator());
            miAccent.DropDownItems.Add(_miAccentCustom);
            miAccent.DropDownOpening += (s, e) => SyncAccentChecks();

            var miAbout = new ToolStripMenuItem("About " + AppIdentity.Name);
            miAbout.Click += (s, e) => ShowAbout();
            var miExit = new ToolStripMenuItem("Exit");
            miExit.Click += (s, e) => RequestExit();

            _flyout.Items.AddRange(new ToolStripItem[]
            {
                miNew, miDevices, miPair, miSettings, new ToolStripSeparator(),
                miTheme, miAccent, new ToolStripSeparator(),
                miAbout, miExit
            });
        }

        private static Bitmap Swatch(Color c)
        {
            var b = new Bitmap(14, 14);
            using (var g = Graphics.FromImage(b))
            using (var br = new SolidBrush(c)) { g.Clear(Color.Transparent); g.FillRectangle(br, 1, 1, 12, 12); }
            return b;
        }

        private void PairPhone()
        {
            using (var d = new PairDialog())
                if (d.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(d.ConnectAddress))
                    ConfirmDialog.Info(this, "Connected to " + d.ConnectAddress + ".\n\nPut this address in a Wireless profile, or pair from the profile's editor so it is stored there.", "Paired");
        }

        private void SyncThemeChecks()
        {
            var m = ThemeHelper.Mode;
            if (_miSystem != null) _miSystem.Checked = m == ThemeMode.System;
            if (_miLight != null) _miLight.Checked = m == ThemeMode.Light;
            if (_miDark != null) _miDark.Checked = m == ThemeMode.Dark;
        }

        private void SyncAccentChecks()
        {
            var cur = ThemeHelper.AccentOverride;
            _miAccentWindows.Checked = !cur.HasValue;
            bool matched = false;
            foreach (var it in _accentItems)
            {
                bool on = cur.HasValue && ((Color)it.Tag).ToArgb() == cur.Value.ToArgb();
                it.Checked = on; matched |= on;
            }
            _miAccentCustom.Checked = cur.HasValue && !matched;
        }

        private void SetTheme(ThemeMode mode)
        {
            ThemeHelper.SetMode(mode);   // raises ThemeChanged → live recolor
            try { var s = AppSettings.Instance; s.ThemeMode = mode.ToString(); s.Save(); } catch { }
            SyncThemeChecks();
        }

        /// <summary>null = follow the Windows accent again.</summary>
        private void SetAccent(Color? c)
        {
            ThemeHelper.SetAccent(c);    // raises ThemeChanged → every themed control recolors live
            try { var s = AppSettings.Instance; s.AccentColor = ThemeHelper.FormatAccent(c); s.Save(); } catch { }
        }

        private void PickCustomAccent()
        {
            using (var cd = new ColorDialog { FullOpen = true, AnyColor = true, Color = ThemeHelper.GetWindowsAccentColor() })
                if (cd.ShowDialog(this) == DialogResult.OK)
                    SetAccent(Color.FromArgb(cd.Color.R, cd.Color.G, cd.Color.B));
        }

        // ── theming ─────────────────────────────────────────────────────────────
        private void ApplyTheme()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)ApplyTheme); } catch { } return; }

            bool dark = ThemeHelper.IsDark;
            Color accent = ThemeHelper.GetWindowsAccentColor();
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            Color fg = dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(28, 28, 32);
            Color hint = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(110, 110, 118);

            BackColor = bg;
            _header.BackColor = accent;
            _menuBtn.Accent = accent; _menuBtn.BackColor = accent;
            _closeBtn.Accent = accent; _closeBtn.BackColor = accent;
            _content.BackColor = bg;
            _outer.BackColor = bg;
            _stack.BackColor = bg;
            _emptyTitle.ForeColor = fg;
            _emptyHint.ForeColor = hint;

            _listHost.BackColor = bg;
            _topBar.BackColor = bg;
            _listTitle.ForeColor = fg;
            if (_searchBox != null) { _searchBox.BackColor = dark ? Color.FromArgb(44, 44, 50) : Color.White; _searchBox.ForeColor = fg; }

            _header.Invalidate(true);
            _menuBtn.Invalidate(); _closeBtn.Invalidate();
            _content.Invalidate(true);
        }

        private void OpenSettings()
        {
            using (var f = new AppSettingsForm())
                if (f.ShowDialog(this) == DialogResult.OK) RefreshProfiles();
        }

        private void ShowAbout()
        {
            using (var f = new AboutForm()) f.ShowDialog(this);
        }

        private void ToggleMaximize()
            => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

        // ── system tray ─────────────────────────────────────────────────────────
        private void BuildTray()
        {
            _tray = new NotifyIcon { Icon = ThemedChrome.AppIcon, Text = AppIdentity.Name, Visible = true };
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.75f) };
            var miOpen = new ToolStripMenuItem("Open " + AppIdentity.Name);
            miOpen.Click += (s, e) => RestoreFromTray();
            var miExit = new ToolStripMenuItem("Exit");
            miExit.Click += (s, e) => RequestExit();
            menu.Items.AddRange(new ToolStripItem[] { miOpen, new ToolStripSeparator(), miExit });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            FileLog.Line("[TRAY] hidden to tray (scrcpy running=" + ScrcpySession.Live.Length + ")");
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void RequestExit()
        {
            _forceExit = true;
            Close();
        }

        /// <summary>FIN-SINGLETON — a second launch signaled us: surface the manager (shown if hidden to the tray,
        /// un-minimized to its previous state, foreground).</summary>
        public void ActivateFromSecondInstance()
        {
            try
            {
                if (!Visible) { ShowInTaskbar = true; Show(); }
                if (WindowState == FormWindowState.Minimized) ShowWindowNative(Handle, SW_RESTORE);
                Activate();
                BringToFront();
                SetForegroundWindow(Handle);
                FileLog.Line("[SINGLETON] manager activated by a second launch");
            }
            catch { }
        }

        // ── close behaviour: Ask / MinimizeToTray / Exit, with a running-scrcpy guard ──
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing) { StopAll(); base.OnFormClosing(e); return; }

            if (!_forceExit)
            {
                var action = CloseActions.Parse(AppSettings.Instance.CloseAction);
                if (action == CloseAction.Ask)
                {
                    using (var dlg = new CloseChoiceDialog())
                    {
                        var r = dlg.ShowDialog(this);
                        if (r != DialogResult.OK) { e.Cancel = true; return; }
                        if (dlg.Remember)
                        {
                            AppSettings.Instance.CloseAction = dlg.Choice.ToString();
                            AppSettings.Instance.Save();
                        }
                        if (dlg.Choice == CloseAction.MinimizeToTray) { e.Cancel = true; HideToTray(); return; }
                    }
                }
                else if (action == CloseAction.MinimizeToTray) { e.Cancel = true; HideToTray(); return; }
            }

            // EXIT PATH — ending live mirroring silently is worse than one prompt.
            int n = ScrcpySession.Live.Length;
            if (n > 0 && !ConfirmDialog.Ask(this, "End " + n + " running scrcpy session" + (n == 1 ? "" : "s") + " and exit?"))
            { e.Cancel = true; _forceExit = false; return; }
            StopAll();
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            FileLog.Line("[EXIT] manager closing, scrcpy sessions=" + n);
            base.OnFormClosing(e);
        }

        /// <summary>Asks every running scrcpy to close its window; the kill-on-close job is the backstop.</summary>
        private static void StopAll()
        {
            foreach (var s in ScrcpySession.Live) s.Stop();
            var until = DateTime.UtcNow.AddSeconds(4);
            while (ScrcpySession.Live.Length > 0 && DateTime.UtcNow < until)
            {
                Application.DoEvents();   // keep painting while scrcpy closes its windows
                System.Threading.Thread.Sleep(50);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _tray?.Dispose(); } catch { }
            ThemeHelper.ThemeChanged -= ApplyTheme;
            ThemeHelper.StopListening();
            base.OnFormClosed(e);
        }

        // ── native: live accent signal + borderless-maximize clamp ──────────────
        protected override void WndProc(ref Message m)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
            switch (m.Msg)
            {
                case WM_DWMCOLORIZATIONCOLORCHANGED:
                    ThemeHelper.NotifyAccentChanged();   // reliable accent signal on 8.1 AND 10/11
                    base.WndProc(ref m);
                    return;
                case WM_GETMINMAXINFO:
                    ConstrainMaximize(m.LParam);          // borderless maximize must not cover the taskbar
                    base.WndProc(ref m);
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }

        private void ConstrainMaximize(IntPtr lParam)
        {
            try
            {
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(mon, ref mi))
                    {
                        RECT work = mi.rcWork, full = mi.rcMonitor;
                        mmi.ptMaxPosition.X = work.Left - full.Left;
                        mmi.ptMaxPosition.Y = work.Top - full.Top;
                        mmi.ptMaxSize.X = work.Right - work.Left;
                        mmi.ptMaxSize.Y = work.Bottom - work.Top;
                        mmi.ptMinTrackSize.X = MinimumSize.Width;
                        mmi.ptMinTrackSize.Y = MinimumSize.Height;
                        Marshal.StructureToPtr(mmi, lParam, true);
                    }
                }
            }
            catch { }
        }

        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int SW_RESTORE = 9;
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    }
}
