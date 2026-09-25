using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DroidFinestra.Helpers;

namespace DroidFinestra.UI.Controls
{
    /// <summary>Shared base for the themed setting rows: colors from <see cref="ThemeHelper"/>, live re-theme,
    /// a common owner-painted background. All rows are custom-painted (no OS theming) so they look identical on
    /// RT 8.1 and Windows 10/11.</summary>
    public abstract class SettingRow : Control
    {
        protected string LabelText;

        protected SettingRow(string label)
        {
            LabelText = label ?? "";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 46;
            ThemeHelper.ThemeChanged += OnTc;
        }

        private void OnTc()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { OnThemeApplied(); Invalidate(); })); } catch { }
        }

        protected virtual void OnThemeApplied() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnTc;
            base.Dispose(disposing);
        }

        protected bool Dark => ThemeHelper.IsDark;
        protected Color Accent => ThemeHelper.GetWindowsAccentColor();
        protected Color Bg => Dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
        protected Color Fg => Dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
        protected Color Sub => Dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);

        protected void PaintLeftLabel(Graphics g, int rightReserved)
        {
            var r = new Rectangle(6, 0, Math.Max(1, Width - rightReserved - 10), Height);
            using (var f = FontHelper.Ui(10.5f))
                TextRenderer.DrawText(g, LabelText, f, r, Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A section title with an underline — groups related rows.</summary>
    public sealed class SectionHeader : SettingRow
    {
        public SectionHeader(string label) : base(label) { Height = 40; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg);
            var r = new Rectangle(6, 0, Width - 12, Height);
            using (var f = FontHelper.Ui(11f, FontStyle.Bold))
                TextRenderer.DrawText(g, LabelText, f, r, Accent,
                    TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
            using (var p = new Pen(Dark ? Color.FromArgb(58, 58, 64) : Color.FromArgb(220, 220, 226)))
                g.DrawLine(p, 6, Height - 4, Width - 8, Height - 4);
        }
    }

    /// <summary>Label + iOS-style on/off switch (accent when on). Maps to a boolean flag.</summary>
    public sealed class ToggleRow : SettingRow
    {
        private bool _on;
        public event Action Changed;
        public bool On { get => _on; set { if (_on != value) { _on = value; Invalidate(); Changed?.Invoke(); } } }

        public ToggleRow(string label, bool on) : base(label) { _on = on; Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            PaintLeftLabel(g, 62);
            int pw = 46, ph = 24, px = Width - pw - 10, py = (Height - ph) / 2;
            var pill = new Rectangle(px, py, pw, ph);
            using (var b = new SolidBrush(_on ? Accent : (Dark ? Color.FromArgb(74, 74, 80) : Color.FromArgb(200, 200, 206))))
            using (var p = DrawHelper.RoundedRect(pill, ph / 2)) g.FillPath(b, p);
            int kd = ph - 6, kx = _on ? px + pw - kd - 3 : px + 3;
            using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, new Rectangle(kx, py + 3, kd, kd));
        }

        protected override void OnMouseClick(MouseEventArgs e) { On = !On; base.OnMouseClick(e); }
    }

    /// <summary>Label + a value that opens a themed dropdown menu of choices. Maps to an enum/value option.</summary>
    public sealed class ChoiceRow : SettingRow
    {
        private readonly string[] _opts;
        private int _idx;
        public event Action Changed;

        public int SelectedIndex
        {
            get => _idx;
            set { if (value >= 0 && value < _opts.Length && _idx != value) { _idx = value; Invalidate(); Changed?.Invoke(); } }
        }
        public string SelectedText => (_idx >= 0 && _idx < _opts.Length) ? _opts[_idx] : "";

        /// <summary>Width of the right-hand value area. Widen it for options whose text carries a tradeoff, so the
        /// collapsed row shows the choice in full instead of ellipsizing it.</summary>
        public int ValueWidth { get; set; } = 168;

        public ChoiceRow(string label, string[] options, int index) : base(label)
        {
            _opts = options ?? new string[0];
            _idx = Math.Max(0, Math.Min(index, _opts.Length - 1));
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            PaintLeftLabel(g, ValueWidth + 32);
            var vr = new Rectangle(Width - ValueWidth - 28, 0, ValueWidth, Height);
            using (var f = FontHelper.Ui(10.5f))
                TextRenderer.DrawText(g, SelectedText, f, vr, Accent,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            int cx = Width - 16, cy = Height / 2;
            using (var p = new Pen(Sub, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            { g.DrawLine(p, cx - 5, cy - 2, cx, cy + 3); g.DrawLine(p, cx + 5, cy - 2, cx, cy + 3); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.75f) };
            for (int i = 0; i < _opts.Length; i++)
            {
                int ii = i;
                var it = new ToolStripMenuItem(_opts[i]) { Checked = (i == _idx) };
                it.Click += (s, ev) => SelectedIndex = ii;
                menu.Items.Add(it);
            }
            menu.Show(this, new Point(Width - ValueWidth - 28, Height - 6));
            base.OnMouseClick(e);
        }
    }

    /// <summary>Stacked label + themed single-line text box (flat, 1px accentable border). Optional numeric-only
    /// or password masking. Maps to a string/number flag value.</summary>
    public sealed class TextRow : SettingRow
    {
        private readonly Panel _wrap;
        private readonly TextBox _box;
        public event Action Changed;

        public string Value { get => _box.Text; set => _box.Text = value ?? ""; }

        private readonly int _labelH;

        public TextRow(string label, string value, bool numeric = false, bool password = false) : base(label)
        {
            _box = new TextBox { BorderStyle = BorderStyle.None, Font = FontHelper.Ui(11f) };
            // DroidFinestra: Finestra's fixed 62 px fits 96 DPI only; at 144 DPI the box covered the label.
            using (var lf = FontHelper.Ui(9.5f)) _labelH = lf.Height + 6;
            Height = 4 + _labelH + (_box.PreferredHeight + 12) + 6;
            if (password) _box.UseSystemPasswordChar = true;
            _box.TextChanged += (s, e) => Changed?.Invoke();
            if (numeric) _box.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            _wrap = new Panel { Padding = new Padding(8, 6, 8, 6) };
            _wrap.Controls.Add(_box);
            _box.Dock = DockStyle.Fill;
            Controls.Add(_wrap);
            _box.Text = value ?? "";
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); StyleBox(); }
        protected override void OnThemeApplied() { StyleBox(); }

        private void StyleBox()
        {
            _wrap.BackColor = Dark ? Color.FromArgb(72, 72, 78) : Color.FromArgb(204, 204, 210);   // 1px border ring
            _box.BackColor = Dark ? Color.FromArgb(44, 44, 50) : Color.White;
            _box.ForeColor = Fg;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_wrap == null || _box == null) return;   // base ctor sets Height, firing layout before fields exist
            int boxH = _box.PreferredHeight + 12;
            _wrap.SetBounds(6, Height - boxH - 6, Math.Max(40, Width - 12), boxH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg);
            var r = new Rectangle(8, 4, Width - 16, _labelH);
            using (var f = FontHelper.Ui(9.5f))
                TextRenderer.DrawText(g, LabelText, f, r, Sub,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>DroidFinestra addition to Finestra's row family: a short explanatory note in the secondary colour.
    /// It wraps, and its height follows the wrapped text once its width is known — the owning ThemedDialog
    /// re-stacks the rows after widths are set (<see cref="ThemedDialog"/>.StretchAndRestack).</summary>
    public sealed class NoteRow : SettingRow
    {
        private Color? _color;
        private readonly int _minLines;

        public NoteRow(string text, int lines = 1) : base(text) { _minLines = Math.Max(1, lines); Height = NeededHeight(400); }

        public string Text2 { get => LabelText; set { LabelText = value ?? ""; Height = NeededHeight(Width); Invalidate(); } }

        /// <summary>One line, shortened in the middle (C:\...older) instead of wrapped — for paths.</summary>
        public bool PathLine { get; set; }

        /// <summary>Height for the text wrapped at <paramref name="width"/>, never less than the requested line count.</summary>
        public int NeededHeight(int width)
        {
            using (var f = FontHelper.Ui(9.5f))
            {
                if (PathLine) return 10 + f.Height;
                int lines = _minLines * f.Height;
                int w = Math.Max(40, width - 16);
                var sz = TextRenderer.MeasureText(LabelText.Length == 0 ? " " : LabelText, f, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return 10 + Math.Max(lines, sz.Height);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = NeededHeight(Width);
            if (Width > 40 && h != Height) Height = h;
        }

        /// <summary>Null = the theme's secondary color.</summary>
        public Color? TextColor { get => _color; set { _color = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg);
            var r = new Rectangle(8, 4, Math.Max(1, Width - 16), Height - 6);
            using (var f = FontHelper.Ui(9.5f))
                TextRenderer.DrawText(g, LabelText, f, r, _color ?? Sub, PathLine
                    ? TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine
                    : TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>DroidFinestra addition: a read-only, selectable multi-line box for a tool's own output (adb's
    /// messages are shown verbatim, never replaced by a generic error). Themed like <see cref="TextRow"/>.</summary>
    public sealed class OutputRow : SettingRow
    {
        private readonly Panel _wrap;
        private readonly TextBox _box;

        public string Value { get => _box.Text; set { _box.Text = (value ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); } }

        public OutputRow(string label, int height) : base(label)
        {
            Height = height;
            _box = new TextBox
            {
                BorderStyle = BorderStyle.None, Multiline = true, ReadOnly = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, Font = FontHelper.Ui(9.5f)
            };
            _wrap = new Panel { Padding = new Padding(8, 6, 8, 6) };
            _wrap.Controls.Add(_box);
            _box.Dock = DockStyle.Fill;
            Controls.Add(_wrap);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); StyleBox(); }
        protected override void OnThemeApplied() { StyleBox(); }

        private void StyleBox()
        {
            _wrap.BackColor = Dark ? Color.FromArgb(72, 72, 78) : Color.FromArgb(204, 204, 210);
            _box.BackColor = Dark ? Color.FromArgb(44, 44, 50) : Color.White;
            _box.ForeColor = Fg;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_wrap == null) return;
            bool label = LabelText.Length > 0;
            int top = label ? 26 : 4;
            _wrap.SetBounds(6, top, Math.Max(40, Width - 12), Math.Max(24, Height - top - 6));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg);
            if (LabelText.Length == 0) return;
            var r = new Rectangle(8, 4, Width - 16, 20);
            using (var f = FontHelper.Ui(9.5f))
                TextRenderer.DrawText(g, LabelText, f, r, Sub,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
