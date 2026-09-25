using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using DroidFinestra.Core;
using DroidFinestra.Helpers;

namespace DroidFinestra.UI.Controls
{
    /// <summary>
    /// Per-transport device glyph tiles — USB (phone + cable), Wireless (phone + signal arcs) — so a row's
    /// transport reads at a glance. Finestra's TypeGlyph with the RDP/SSH/FTP shapes swapped for these two. HAND-DRAWN GDI+ vector paths on a rounded gradient tile: no icon
    /// font (Segoe MDL2 is Win10+ — empty boxes on RT 8.1), no image assets, no new dependency.
    ///
    /// Tints are ACCENT-DERIVED, not hardcoded: the Windows accent is converted to HSL and each kind gets a
    /// BOUNDED hue offset (RDP 0° / SSH +26° / FTP −26°) with saturation+lightness normalized into a narrow band,
    /// so the three tiles read as a family of the current accent and the knocked-out near-white glyph stays
    /// legible on every one. (On a near-gray accent the offsets collapse to grays — the glyph shape remains the
    /// primary cue, by design.)
    ///
    /// Rendered ONCE per (kind, size, accent, theme) into a cached Bitmap — never rebuilt per paint (RT / Tegra 3
    /// scrolls through these). The cache invalidates on <see cref="ThemeHelper.ThemeChanged"/> (fires for both
    /// mode and accent changes). Returned bitmaps are CACHE-OWNED: callers draw them, never dispose them.
    /// </summary>
    public static class TypeGlyph
    {
        private static readonly Dictionary<Tuple<int, int, int, bool>, Bitmap> _cache
            = new Dictionary<Tuple<int, int, int, bool>, Bitmap>();
        private static readonly object _lock = new object();

        static TypeGlyph()
        {
            ThemeHelper.ThemeChanged += InvalidateCache;   // accent OR dark/light flip → re-derive tints
        }

        private static void InvalidateCache()
        {
            lock (_lock)
            {
                foreach (var b in _cache.Values) { try { b.Dispose(); } catch { } }
                _cache.Clear();
            }
        }

        /// <summary>Map a profile's transport to its glyph kind.</summary>
        public static SessionKind KindOf(Transport t) => t == Transport.Wireless ? SessionKind.Wireless : SessionKind.Usb;

        /// <summary>The tile for the CURRENT theme/accent. Cache-owned bitmap — do not dispose.</summary>
        public static Bitmap Get(SessionKind kind, int px)
            => Get(kind, px, ThemeHelper.GetWindowsAccentColor(), ThemeHelper.IsDark);

        /// <summary>Explicit accent/theme overload (also used by the visual-gate harness). Cache-owned.</summary>
        public static Bitmap Get(SessionKind kind, int px, Color accent, bool dark)
        {
            if (px < 10) px = 10;
            var key = Tuple.Create((int)kind, px, accent.ToArgb(), dark);
            lock (_lock)
            {
                Bitmap hit;
                if (_cache.TryGetValue(key, out hit)) return hit;
                var bmp = Render(kind, px, accent, dark);
                _cache[key] = bmp;
                return bmp;
            }
        }

        // ── tint derivation ──────────────────────────────────────────────────────────────────────────────

        private static Color TileTop(SessionKind kind, Color accent, bool dark)
        {
            float h, s, l;
            ToHsl(accent, out h, out s, out l);
            // bounded hue offset per kind — a family of the accent, not three unrelated colors
            float dh = kind == SessionKind.Wireless ? 26f : 0f;
            h = (h + dh + 360f) % 360f;
            // normalize S/L into a narrow band so all three carry equal weight and near-white stays legible
            s = Clamp(s, 0.42f, 0.70f);
            l = Clamp(l, dark ? 0.40f : 0.44f, dark ? 0.50f : 0.54f);
            return FromHsl(h, s, l);
        }

        private static Color Darken(Color c, float dl)
        {
            float h, s, l;
            ToHsl(c, out h, out s, out l);
            return FromHsl(h, s, Math.Max(0f, l - dl));
        }

        // ── rendering ────────────────────────────────────────────────────────────────────────────────────

        private static Bitmap Render(SessionKind kind, int px, Color accent, bool dark)
        {
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, px - 1, px - 1);
                int radius = Math.Max(2, (int)Math.Round(px * 0.22));   // the app icon's corner language
                Color top = TileTop(kind, accent, dark);
                Color bottom = Darken(top, 0.13f);

                using (var path = DrawHelper.RoundedRect(rect, radius))
                {
                    using (var lg = new LinearGradientBrush(new Rectangle(0, 0, px, px), top, bottom, LinearGradientMode.Vertical))
                        g.FillPath(lg, path);
                    // subtle lighter inner stroke along the top edge (the icon's highlight)
                    var clip = g.Clip;
                    g.SetClip(new Rectangle(0, 0, px, Math.Max(2, px / 3)));
                    using (var hp = new Pen(Color.FromArgb(55, 255, 255, 255), 1f))
                    using (var inner = DrawHelper.RoundedRect(new Rectangle(1, 1, px - 3, px - 3), Math.Max(1, radius - 1)))
                        g.DrawPath(hp, inner);
                    g.Clip = clip;
                }

                DrawKindGlyph(g, kind, px);
            }
            return bmp;
        }

        /// <summary>The knocked-out near-white glyph, stroke width scaled to the tile.</summary>
        private static void DrawKindGlyph(Graphics g, SessionKind kind, int px)
        {
            float w = Math.Max(1.5f, px * 0.085f);
            var ink = Color.FromArgb(242, 246, 248, 252);
            float cx = px / 2f, cy = px / 2f, u = px * 0.30f;   // u = half-extent of the glyph box
            using (var p = new Pen(ink, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                // phone: a tall rounded body with a small home dot
                float bw = u * 1.05f, bh = u * 1.9f;
                float bx = kind == SessionKind.Wireless ? cx - u * 0.95f : cx - bw / 2f;
                var body = new RectangleF(bx, cy - bh / 2f, bw, bh);
                using (var bp = DrawHelper.RoundedRect(Rectangle.Round(body), Math.Max(1, (int)(px * 0.07))))
                    g.DrawPath(p, bp);
                g.DrawLine(p, body.Left + bw * 0.38f, body.Bottom - bh * 0.14f, body.Right - bw * 0.38f, body.Bottom - bh * 0.14f);
                if (kind == SessionKind.Wireless)
                {
                    // two signal arcs to the right of the phone
                    float ox = body.Right + u * 0.1f, oy = cy;
                    for (int i = 1; i <= 2; i++)
                    {
                        float r = u * 0.42f * i;
                        g.DrawArc(p, ox - r, oy - r, r * 2f, r * 2f, -45f, 90f);
                    }
                }
                else
                {
                    // cable stub under the phone
                    g.DrawLine(p, cx, body.Bottom + w * 0.6f, cx, cy + u * 1.12f);
                }
            }
        }

                // ── HSL helpers ──────────────────────────────────────────────────────────────────────────────────

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        private static void ToHsl(Color c, out float h, out float s, out float l)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2f;
            if (Math.Abs(max - min) < 0.0001f) { h = 0f; s = 0f; return; }
            float d = max - min;
            s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6f : 0f)) * 60f;
            else if (max == g) h = ((b - r) / d + 2f) * 60f;
            else h = ((r - g) / d + 4f) * 60f;
        }

        private static Color FromHsl(float h, float s, float l)
        {
            float c = (1f - Math.Abs(2f * l - 1f)) * s;
            float hp = h / 60f;
            float x = c * (1f - Math.Abs(hp % 2f - 1f));
            float r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            float m = l - c / 2f;
            return Color.FromArgb(255,
                (int)Math.Round(Clamp(r + m, 0f, 1f) * 255f),
                (int)Math.Round(Clamp(g + m, 0f, 1f) * 255f),
                (int)Math.Round(Clamp(b + m, 0f, 1f) * 255f));
        }
    }

    public enum SessionKind { Usb, Wireless }
}
