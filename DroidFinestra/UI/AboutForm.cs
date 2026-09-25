using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using DroidFinestra.Core;
using DroidFinestra.Helpers;
using DroidFinestra.UI.Controls;

namespace DroidFinestra.UI
{
    /// <summary>
    /// The themed About box — Finestra's AboutForm: app identity + version, featured cards for what it is built on,
    /// source / licence links, open-source credits, privacy and footer, as cards on the <see cref="ThemedDialog"/>
    /// chrome. One change: Finestra places text at fixed pixel heights, which clip once fonts grow above 96 DPI; here
    /// every block is MEASURED at its width and the layout flows from those heights, so no text is ever cut off.
    /// </summary>
    public sealed class AboutForm : ThemedDialog
    {
        private const string RepoUrl = "https://github.com/hamed7ir/DroidFinestra";
        private const string LicenseUrl = "https://github.com/hamed7ir/DroidFinestra/blob/main/LICENSE";
        private const string ScrcpyUrl = "https://github.com/Genymobile/scrcpy";
        private const string ScrcpyRtUrl = "https://github.com/hamed7ir/scrcpy-rt";
        private const string FinestraUrl = "https://github.com/hamed7ir/Finestra";

        private readonly Color _bg, _card, _border, _title, _sub, _accent;
        private readonly float _s;   // DPI scale: layout constants are 96-DPI pixels

        public AboutForm() : base("About " + AppIdentity.Name, Px(470), DialogHeight())
        {
            _s = DpiScale();
            bool dark = ThemeHelper.IsDark;
            _accent = ThemeHelper.GetWindowsAccentColor();
            // the body colour ThemedDialog paints — Finestra used a slightly different one, so every label showed as a box
            _bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            _card = dark ? Color.FromArgb(50, 50, 55) : Color.White;
            _border = dark ? Color.FromArgb(62, 62, 68) : Color.FromArgb(226, 226, 230);
            _title = dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38);
            _sub = dark ? Color.FromArgb(158, 158, 165) : Color.FromArgb(110, 110, 116);
            Color featBg = Blend(_accent, _card, 0.16f);
            Color featBrd = Blend(_accent, _card, 0.55f);

            var host = Body.Host;
            host.BackColor = _bg;

            string ver;   // 3-part display version, read from the assembly at runtime — no hardcoded string to rot
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                ver = v != null ? v.Major + "." + v.Minor + "." + v.Build : "1.0.0";
            }
            catch { ver = "1.0.0"; }

            int M = S(22), CW = S(424);   // left margin, content width
            int y = S(22);

            // ── Header: icon + name + description + version (centered) ──
            int icon = S(96);
            var pic = new PictureBox
            {
                Size = new Size(icon, icon), Location = new Point(M + (CW - icon) / 2, y),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent
            };
            try { var app = ThemedChrome.AppIcon; if (app != null) pic.Image = new Icon(app, 256, 256).ToBitmap(); } catch { }
            host.Controls.Add(pic);
            y += icon + S(10);

            y += Flow(host, AppIdentity.Name, M, y, CW, _title, FontHelper.Ui(20f, FontStyle.Bold), _bg, ContentAlignment.MiddleCenter);
            y += Flow(host, "Use your Android phone from Windows", M, y, CW, _accent, FontHelper.Ui(10.5f, FontStyle.Bold), _bg, ContentAlignment.MiddleCenter);
            y += Flow(host, "Version " + ver, M, y, CW, _sub, FontHelper.Ui(9.5f), _bg, ContentAlignment.MiddleCenter) + S(2);
            y += Flow(host, "A GUI for scrcpy — device profiles, Wi-Fi pairing and one-click mirroring, on Windows RT 8.1 (ARM32) and Windows 10/11.",
                      M, y, CW, _sub, FontHelper.Ui(9f), _bg, ContentAlignment.TopCenter) + S(12);

            // ── FEATURED — what it is built on (accent-tinted, identical card geometry) ──
            y += Feature(host, M, y, CW, featBg, featBrd, "Mirroring — scrcpy by Genymobile",
                "scrcpy 4.1 does the mirroring and the control; DroidFinestra saves your settings and launches it. Apache-2.0.",
                "→  github.com/Genymobile/scrcpy", ScrcpyUrl) + S(14);
            y += Feature(host, M, y, CW, featBg, featBrd, "ARM32 — scrcpy-rt, adb-rt, ffmpeg-rt",
                "On Windows RT the package carries scrcpy, adb and FFmpeg built for 32-bit ARM Windows. The x64 package carries the official scrcpy release with Google's adb.",
                "→  github.com/hamed7ir/scrcpy-rt", ScrcpyRtUrl) + S(14);
            y += Feature(host, M, y, CW, featBg, featBrd, "Built from Finestra",
                "The window, profile store, settings and theming come from Finestra, the remote connection manager for ARM32 Windows.",
                "→  github.com/hamed7ir/Finestra", FinestraUrl) + S(14);

            // ── Source + licence + where scrcpy is ──
            int row = S(46);
            var meta = Card(M, y, CW, row * 3, _card, _border);
            host.Controls.Add(meta);
            MetaRow(meta, 0, "Source code", "github.com/hamed7ir/DroidFinestra", RepoUrl, CW, row);
            meta.Controls.Add(new Panel { Location = new Point(S(14), row), Size = new Size(CW - S(28), 1), BackColor = _border });
            MetaRow(meta, row, "License", "GPL-3.0-only", LicenseUrl, CW, row);
            meta.Controls.Add(new Panel { Location = new Point(S(14), row * 2), Size = new Size(CW - S(28), 1), BackColor = _border });
            MetaRow(meta, row * 2, "scrcpy folder", ScrcpyTools.Folder, null, CW, row, openFolder: true);
            y += row * 3 + S(14);

            // ── Open-source credits ──
            string[][] credits =
            {
                new[] { "scrcpy 4.1 (Genymobile)", "Apache-2.0" },
                new[] { "adb — Android platform-tools", "Apache-2.0" },
                new[] { "FFmpeg", "LGPL-2.1+" },
                new[] { "SDL 3", "zlib" },
                new[] { "libusb", "LGPL-2.1+" },
                new[] { "Newtonsoft.Json 13.0.4", "MIT" },
                new[] { "Roboto font 3.009", "SIL OFL 1.1" },
            };
            using (var rf = FontHelper.Ui(9f))
            {
                int rh = Math.Max(S(22), rf.Height + S(6));
                int head = S(38);
                int ch = head + credits.Length * rh + S(10);
                var cr = Card(M, y, CW, ch, _card, _border);
                host.Controls.Add(cr);
                cr.Controls.Add(Text2("OPEN-SOURCE CREDITS", S(16), S(12), CW - S(32), _accent, FontHelper.Ui(8.25f, FontStyle.Bold), S(20), _card, ContentAlignment.MiddleLeft));
                int ry = head;
                foreach (var c in credits)
                {
                    cr.Controls.Add(Text2(c[0], S(16), ry, CW - S(170), _title, FontHelper.Ui(9f), rh, _card, ContentAlignment.MiddleLeft));
                    cr.Controls.Add(Text2(c[1], CW - S(152), ry, S(136), _sub, FontHelper.Ui(8.5f), rh, _card, ContentAlignment.MiddleRight));
                    ry += rh;
                }
                y += ch + S(14);
            }

            // ── Privacy + footer ──
            y += Flow(host, "Privacy: collects no data and makes no network calls; adb talks only to your phone, over USB or your own network.",
                      M, y, CW, _sub, FontHelper.Ui(8.5f), _bg, ContentAlignment.TopCenter) + S(4);
            y += Flow(host, "Full license texts: THIRD-PARTY-NOTICES.txt", M, y, CW, _sub, FontHelper.Ui(8.25f), _bg, ContentAlignment.MiddleCenter);
            y += Flow(host, "© 2026 Hamed Ghorbani · Licensed under GPL-3.0-only", M, y, CW, _sub, FontHelper.Ui(8.25f), _bg, ContentAlignment.MiddleCenter) + S(4);

            host.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(4, S(10)), BackColor = _bg });   // extend the scroll extent
            host.Height = y + S(10);

            AddFooterButton("Close", RoundedButtonKind.Primary, DialogResult.OK);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }

        // Absolute-positioned card layout → skip ThemedDialog's full-width row resize; just refresh the scrollbar.
        protected override void OnShown(EventArgs e) { try { Body.RelayoutContent(); } catch { } }

        // ── DPI: 96-DPI layout constants scaled to the screen; the dialog never taller than the work area ──

        private static float DpiScale()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); }
            catch { return 1f; }
        }

        private static int Px(int v) => (int)Math.Round(v * DpiScale());
        private int S(int v) => (int)Math.Round(v * _s);

        private static int DialogHeight()
        {
            int want = Px(640);
            try { want = Math.Min(want, (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.9)); } catch { }
            return want;
        }

        // ── building blocks (Finestra's Card / Text2 / Link / MetaRow, plus a measured Flow) ──

        /// <summary>Adds a label sized to its wrapped text at width <paramref name="w"/>; returns the height used.</summary>
        private int Flow(Control parent, string text, int x, int y, int w, Color color, Font font, Color parentBg, ContentAlignment align)
        {
            int h = Measure(text, font, w) + S(4);
            parent.Controls.Add(Text2(text, x, y, w, color, font, h, parentBg, align));
            return h;
        }

        /// <summary>One line: the path as-is if it fits, else "C:\…\" plus as many trailing folders as fit.</summary>
        private static string CompactPath(string path, Font font, int w)
        {
            Func<string, bool> fits = t => TextRenderer.MeasureText(t, font, new Size(int.MaxValue, 0), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width <= w;
            if (string.IsNullOrEmpty(path) || fits(path)) return path;
            string root = System.IO.Path.GetPathRoot(path) ?? "";
            var parts = path.Substring(root.Length).Split('\\');
            for (int keep = parts.Length - 1; keep >= 1; keep--)
            {
                string t = root + "…\\" + string.Join("\\", parts, parts.Length - keep, keep);
                if (fits(t)) return t;
            }
            return "…\\" + parts[parts.Length - 1];
        }

        private static int Measure(string text, Font font, int w)
        {
            return TextRenderer.MeasureText(text, font, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        }

        /// <summary>A featured card: accent title, wrapped body, link — its height follows the text. Returns the card height.</summary>
        private int Feature(Control host, int x, int y, int w, Color fill, Color brd, string title, string body, string linkText, string url)
        {
            int pad = S(16), inner = w - 2 * pad;
            var tf = FontHelper.Ui(12f, FontStyle.Bold);
            var bf = FontHelper.Ui(9f);
            var lf = FontHelper.Ui(9.5f, FontStyle.Bold);
            int th = Measure(title, tf, inner) + S(4);
            int bh = Measure(body, bf, inner) + S(4);
            int lh = Measure(linkText, lf, inner) + S(6);
            int h = S(14) + th + S(6) + bh + S(8) + lh + S(12);

            var card = Card(x, y, w, h, fill, brd);
            host.Controls.Add(card);
            int cy = S(14);
            card.Controls.Add(Text2(title, pad, cy, inner, _accent, tf, th, fill, ContentAlignment.MiddleLeft)); cy += th + S(6);
            card.Controls.Add(Text2(body, pad, cy, inner, _title, bf, bh, fill, ContentAlignment.TopLeft)); cy += bh + S(8);
            var link = Link(linkText, pad, cy, inner, url, lf, fill);
            link.Height = lh;
            card.Controls.Add(link);
            return h;
        }

        private void MetaRow(Panel card, int cy, string left, string linkText, string url, int cw, int rowH, bool openFolder = false)
        {
            card.Controls.Add(Text2(left, S(16), cy, S(130), _title, FontHelper.Ui(9.5f), rowH, _card, ContentAlignment.MiddleLeft));
            int lw = cw - S(166);
            var font = openFolder ? FontHelper.Ui(9f) : FontHelper.Ui(9.5f, FontStyle.Bold);
            var link = Link(openFolder ? CompactPath(linkText, font, lw) : linkText, S(150), cy, lw, url, font, _card);
            link.Height = rowH; link.TextAlign = ContentAlignment.MiddleRight;
            if (openFolder)
            {
                link.Click += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", CommandLine.Quote(ScrcpyTools.Folder)); } catch { } };
                var tip = new ToolTip(); tip.SetToolTip(link, linkText);   // the full path on hover
            }
            card.Controls.Add(link);
        }

        private static Label Text2(string text, int x, int y, int w, Color color, Font font, int h, Color parentBg, ContentAlignment align)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, h), UseMnemonic = false,
                ForeColor = color, BackColor = parentBg, Font = font, TextAlign = align
            };
        }

        private Label Link(string text, int x, int y, int w, string url, Font font, Color parentBg)
        {
            var l = new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, S(22)), UseMnemonic = false,
                ForeColor = _accent, BackColor = parentBg, Font = font, Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleLeft
            };
            if (url != null) l.Click += (s, e) => OpenUrl(url);
            return l;
        }

        private Panel Card(int x, int y, int w, int h, Color fill, Color brd)
        {
            var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = fill };
            int r = S(12);
            using (var path = DrawHelper.RoundedRect(new Rectangle(0, 0, w, h), r))
                p.Region = new Region(path);
            p.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(brd))
                using (var pa = DrawHelper.RoundedRect(new Rectangle(0, 0, w - 1, h - 1), r))
                    g.DrawPath(pen, pa);
            };
            return p;
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { System.Diagnostics.Process.Start(url); } catch { /* RT-safe: no default browser / blocked → ignore */ }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }
    }
}
