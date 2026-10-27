// Theme.cs — палитра, шрифты и примитивы рисования лаунчера. Цвета берутся из активного стиля (Designs.cs):
// «Компактный» (тёмная таблица), «Fluent» (светлые карточки) или «Слаймовый» (цвета игры). Смена стиля (Use)
// перекрашивает уже созданные элементы (Retint) — перезапуск не нужен.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal static class Theme
{
    // ───── активный стиль и палитра ─────
    public static Palette P { get; private set; } = Designs.Compact.Palette;
    public static DesignKind Design { get; private set; } = DesignKind.Compact;
    public static DesignSpec Spec => Designs.Get(Design);
    /// Растёт при каждой смене палитры: входит в подписи кэшированных картинок (карточки, значки-аватары).
    public static int Generation { get; private set; }
    public static event Action? Changed;

    /// Включить стиль. false — он уже активен.
    public static bool Use(DesignKind d)
    {
        var spec = Designs.Get(d);
        if (d == Design && ReferenceEquals(P, spec.Palette)) return false;
        Design = d;
        P = spec.Palette;
        Generation++;
        Changed?.Invoke();
        return true;
    }

    public static Color Cream => P.Cream;
    public static Color Paper => P.Paper;
    public static Color Beige => P.Beige;
    public static Color BeigeDark => P.BeigeDark;
    public static Color Line => P.Line;
    public static Color LineDark => P.LineDark;
    public static Color Navy => P.Navy;
    public static Color NavySoft => P.NavySoft;
    public static Color Hint => P.Hint;
    public static Color Teal => P.Teal;
    public static Color TealDark => P.TealDark;
    public static Color Coral => P.Coral;
    public static Color Orange => P.Orange;
    public static Color Pink => P.Pink;
    public static Color Blue => P.Blue;
    public static Color TealTint => P.TealTint;
    public static Color CoralTint => P.CoralTint;
    public static Color OrangeTint => P.OrangeTint;
    public static Color BlueTint => P.BlueTint;
    public static Color PinkTint => P.PinkTint;
    public static Color Selected => P.Selected;
    public static Color TealText => P.TealText;
    public static Color CoralText => P.CoralText;
    public static Color OrangeText => P.OrangeText;
    public static Color BlueText => P.BlueText;
    public static Color PurpleTint => P.PurpleTint;
    public static Color PurpleText => P.PurpleText;
    public static Color RowAlt => P.RowAlt;
    public static Color RowHover => P.RowHover;
    public static Color HeaderBg => P.HeaderBg;
    public static bool Dark => P.Dark;
    /// Тёмно-синий «чернильный» цвет слайма и теней — не зависит от палитры (в тёмной теме Navy — светлый текст).
    public static readonly Color Ink = Hex(0x1F2A44);

    /// Радиус скругления элемента высотой h (кнопки, поле поиска, сегменты): капсула или скругление стиля.
    public static float Radius(float h, float scale)
    {
        int r = Spec.CtrlRadius;
        return r < 0 ? h / 2f : Math.Min(h / 2f, r * scale);
    }

    /// Порядок ролей для Retint: цвета, которые элементы получают при создании (BackColor, ForeColor).
    private static readonly Func<Palette, Color>[] Roles =
    {
        p => p.Cream, p => p.Paper, p => p.Navy, p => p.NavySoft, p => p.Hint, p => p.Beige, p => p.BeigeDark, p => p.Line, p => p.Selected, p => p.HeaderBg,
    };

    /// После смены палитры: BackColor/ForeColor элементов, совпадающие с ролью старой палитры, получают цвет той же роли
    /// новой. Прозрачные и свои (не из палитры) цвета остаются как есть.
    public static void Retint(Control root, Palette from, Palette to)
    {
        if (ReferenceEquals(from, to)) return;
        Color? Map(Color c)
        {
            if (c.A == 0) return null;
            int v = c.ToArgb();
            foreach (var role in Roles) if (role(from).ToArgb() == v) return role(to);
            return null;
        }
        void Walk(Control c)
        {
            if (Map(c.BackColor) is Color b && b != c.BackColor) c.BackColor = b;
            if (Map(c.ForeColor) is Color f && f != c.ForeColor) c.ForeColor = f;
            foreach (Control k in c.Controls) Walk(k);
        }
        Walk(root);
    }

    public static Color Hex(int rgb, int a = 255) => Color.FromArgb(a, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
    public static Color Alpha(Color c, int a) => Color.FromArgb(Math.Clamp(a, 0, 255), c.R, c.G, c.B);

    // ───── шрифты (в пунктах — сами масштабируются под DPI) ─────
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font SmallBold = new("Segoe UI Semibold", 8.5f);
    public static readonly Font Strong = new("Segoe UI Semibold", 10.5f);
    public static readonly Font ButtonFont = new("Segoe UI Semibold", 9.75f);
    public static readonly Font H2 = new("Segoe UI Semibold", 13.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 17f);
    public static readonly Font Section = new("Segoe UI Semibold", 8f);
    public static readonly Font Mono = MakeMono();
    public static readonly Font Icons = new("Segoe MDL2 Assets", 10f);
    public static readonly Font IconsSmall = new("Segoe MDL2 Assets", 8f);
    public static readonly Font IconsBig = new("Segoe MDL2 Assets", 15f);
    public static readonly bool HasIcons = Icons.Name == "Segoe MDL2 Assets";

    private static Font MakeMono()
    {
        var f = new Font("Consolas", 9f);
        if (f.Name == "Consolas") return f;
        f.Dispose();
        return new Font(FontFamily.GenericMonospace, 9f);
    }

    // ───── глифы Segoe MDL2 Assets (с запасным символом, если шрифта нет) ─────
    public static string G(string mdl2, string fallback) => HasIcons ? mdl2 : fallback;
    public static string GSearch => G("\uE721", "⌕");
    public static string GPlay => G("\uE768", "▶");
    public static string GRefresh => G("\uE72C", "↻");
    public static string GFolder => G("\uE8B7", "▣");
    public static string GMore => G("\uE712", "⋯");
    public static string GAdd => G("\uE710", "+");
    public static string GDelete => G("\uE74D", "✕");
    public static string GSave => G("\uE74E", "✓");
    public static string GSettings => G("\uE713", "⚙");
    public static string GWarning => G("\uE7BA", "⚠");
    public static string GInfo => G("\uE946", "i");
    public static string GCheck => G("\uE73E", "✓");
    public static string GCancel => G("\uE711", "✕");
    public static string GDownload => G("\uE896", "↓");
    public static string GHistory => G("\uE81C", "↺");
    public static string GRename => G("\uE8AC", "✎");
    public static string GCopy => G("\uE8C8", "⧉");
    public static string GPin => G("\uE718", "⌖");
    public static string GError => G("\uE783", "!");
    public static string GDoc => G("\uE8A5", "≡");
    public static string GUndo => G("\uE7A7", "↶");
    public static string GClock => G("\uE823", "◷");
    public static string GPlug => G("\uE7FC", "◆");
    public static string GSync => G("\uE895", "↻");
    public static string GOpen => G("\uE8A7", "↗");
    public static string GPalette => G("\uE790", "◐");
    public static string GChevronUp => G("\uE70E", "▴");
    public static string GChevronDown => G("\uE70D", "▾");
    public static string GSortUp => G("\uE70E", "▲");
    public static string GSortDown => G("\uE70D", "▼");
    public static string GBug => G("\uEBE8", "!");
    public static string GList => G("\uEA37", "≡");

    // ───── текст ─────
    public const TextFormatFlags Line1 = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
    public const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    public static void Text(Graphics g, string? s, Font f, Rectangle r, Color c, TextFormatFlags extra = 0)
    {
        if (string.IsNullOrEmpty(s) || r.Width <= 0 || r.Height <= 0) return;
        TextRenderer.DrawText(g, s, f, r, c, Line1 | extra);
    }

    /// Подсветка найденного в поиске (мягкий жёлтый маркер).
    public static Color Mark => P.Mark;

    /// Ширина начала строки длиной len (с хвостовыми пробелами — MeasureText их сам не считает).
    public static int PrefixWidth(string s, int len, Font f)
    {
        if (string.IsNullOrEmpty(s) || len <= 0) return 0;
        string p = s.Substring(0, Math.Min(len, s.Length));
        return Measure(p + "|", f).Width - Measure("|", f).Width;
    }

    /// Однострочный текст с подсветкой совпадений поиска. То, что ушло под многоточие, не подсвечивается.
    public static void TextMarked(Graphics g, string? s, Font f, Rectangle r, Color c, List<(int Start, int Length)>? marks, TextFormatFlags extra = 0)
    {
        if (string.IsNullOrEmpty(s) || r.Width <= 0 || r.Height <= 0) return;
        if (marks != null && marks.Count > 0)
        {
            int full = Measure(s, f).Width;
            int limit = full <= r.Width ? r.Width : r.Width - Measure("…", f).Width - 1;
            int lh = LineHeight(f);
            float y = r.Y + (r.Height - lh) / 2f;
            foreach (var (st, len) in marks)
            {
                if (st < 0 || st >= s.Length || len <= 0) continue;
                int x0 = PrefixWidth(s, st, f), x1 = Math.Min(PrefixWidth(s, Math.Min(s.Length, st + len), f), limit);
                if (x0 >= limit || x1 <= x0) continue;
                FillRound(g, new RectangleF(r.X + x0 - 1.5f, y, x1 - x0 + 3f, lh), Math.Max(3f, lh * 0.22f), Mark);
            }
        }
        Text(g, s, f, r, c, extra);
    }

    // Замеры текста кэшируются: карточки, капсулы и кнопки меряют одни и те же строки на каждой перерисовке.
    private static readonly Dictionary<(string, Font), Size> MeasureCache = new();
    public static int MeasureCalls, MeasureMisses;

    public static Size Measure(string? s, Font f)
    {
        if (string.IsNullOrEmpty(s)) return Size.Empty;
        MeasureCalls++;
        var key = (s, f);
        if (MeasureCache.TryGetValue(key, out var sz)) return sz;
        MeasureMisses++;
        if (MeasureCache.Count > 4000) MeasureCache.Clear();
        sz = TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        MeasureCache[key] = sz;
        return sz;
    }

    private static readonly Dictionary<(string, Font, int), List<string>> WrapCache = new();

    /// Перенос по словам; слово длиннее строки режется по символам. Своя раскладка вместо
    /// DrawText+WordBreak: тот иногда рвал кириллические слова посередине («отк|рытия»).
    public static List<string> WrapLines(string? s, Font f, int width)
    {
        if (string.IsNullOrEmpty(s) || width <= 0) return new List<string>();
        var key = (s, f, width);
        if (WrapCache.TryGetValue(key, out var hit)) return hit;
        if (WrapCache.Count > 400) WrapCache.Clear();
        var lines = new List<string>();
        foreach (var para in s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string cur = "";
            foreach (var word in para.Split(' '))
            {
                string w = word;
                string next = cur.Length == 0 ? w : cur + " " + w;
                if (Measure(next, f).Width <= width) { cur = next; continue; }
                if (cur.Length > 0) lines.Add(cur);
                while (w.Length > 1 && Measure(w, f).Width > width)
                {
                    int n = w.Length - 1;
                    while (n > 1 && Measure(w.Substring(0, n), f).Width > width) n--;
                    lines.Add(w.Substring(0, n));
                    w = w.Substring(n);
                }
                cur = w;
            }
            lines.Add(cur);
        }
        WrapCache[key] = lines;
        return lines;
    }

    public static int LineHeight(Font f) => Measure("Ag", f).Height;

    public static int WrapHeight(string? s, Font f, int width)
        => string.IsNullOrEmpty(s) || width <= 0 ? 0 : WrapLines(s, f, width).Count * LineHeight(f);

    /// Многострочный текст в прямоугольнике; если строк больше, чем влезает, последняя — с многоточием.
    public static void Wrap(Graphics g, string? s, Font f, Rectangle r, Color c, bool center = false)
    {
        if (string.IsNullOrEmpty(s) || r.Width <= 0 || r.Height <= 0) return;
        var lines = WrapLines(s, f, r.Width);
        int lh = Math.Max(1, LineHeight(f));
        int max = Math.Max(1, r.Height / lh);
        for (int i = 0; i < lines.Count && i < max; i++)
        {
            bool cut = i == max - 1 && lines.Count > max;
            string line = cut ? string.Join(" ", lines.Skip(i)) : lines[i];
            TextRenderer.DrawText(g, line, f, new Rectangle(r.X, r.Y + i * lh, r.Width, lh), c,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | (cut ? TextFormatFlags.EndEllipsis : 0) | (center ? TextFormatFlags.HorizontalCenter : 0));
        }
    }

    public static void Glyph(Graphics g, string glyph, Rectangle r, Color c, Font? f = null)
    {
        if (string.IsNullOrEmpty(glyph) || r.Width <= 0) return;
        TextRenderer.DrawText(g, glyph, f ?? Icons, r, c, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    // ───── фигуры ─────
    public static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
        if (d <= 1f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, RectangleF r, float rad, Color c)
    {
        if (r.Width <= 0 || r.Height <= 0 || c.A == 0) return;
        using var p = RoundRect(r, rad);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, RectangleF r, float rad, Color c, float w = 1f)
    {
        if (r.Width <= 0 || r.Height <= 0 || c.A == 0) return;
        float h = w / 2f;
        using var p = RoundRect(new RectangleF(r.X + h, r.Y + h, r.Width - w, r.Height - w), Math.Max(0f, rad - h));
        using var pen = new Pen(c, w);
        g.DrawPath(pen, p);
    }

    /// Мягкая тень под карточкой (несколько полупрозрачных слоёв).
    public static void Shadow(Graphics g, RectangleF r, float rad, float spread, int alpha = 18)
    {
        for (int i = 3; i >= 1; i--)
        {
            float k = spread * i / 3f;
            FillRound(g, new RectangleF(r.X - k * 0.5f, r.Y + k * 0.35f, r.Width + k, r.Height + k * 0.6f), rad + k,
                P.Dark ? Color.FromArgb(Math.Min(255, alpha * 3 / i), 0, 0, 0) : Color.FromArgb(alpha / i, 31, 42, 68));
        }
    }

    public static void Toggle(Graphics g, Rectangle r, bool on, bool hover, bool dim = false)
    {
        Color track = on ? (hover ? TealDark : Teal) : (hover ? LineDark : P.ToggleOff);
        if (dim) track = Mix(track, Cream, 0.55f);
        FillRound(g, r, r.Height / 2f, track);
        float inset = Math.Max(2f, r.Height * 0.13f), d = r.Height - 2f * inset;
        var kr = new RectangleF(on ? r.Right - inset - d : r.X + inset, r.Y + inset, d, d);
        using (var sb = new SolidBrush(Color.FromArgb(dim ? 18 : 40, 31, 42, 68))) g.FillEllipse(sb, kr.X, kr.Y + 1f, kr.Width, kr.Height);
        using (var wb = new SolidBrush(Color.White)) g.FillEllipse(wb, kr);
        if (on && !dim && HasIcons)
        {
            var cr = Rectangle.Round(kr);
            Glyph(g, "\uE73E", cr, TealDark, IconsSmall);
        }
    }

    public static int PillWidth(string text, Font f, int h, bool glyph)
        => Measure(text, f).Width + h + (glyph ? (int)(h * 0.72f) : 0);

    /// Капсула с текстом (и глифом слева). Возвращает её прямоугольник.
    public static Rectangle Pill(Graphics g, int x, int y, int h, string text, Font f, Color bg, Color fg, string? glyph = null, Color? border = null, bool fromRight = false)
    {
        int w = PillWidth(text, f, h, !string.IsNullOrEmpty(glyph));
        var r = new Rectangle(fromRight ? x - w : x, y, w, h);
        FillRound(g, r, h / 2f, bg);
        if (border.HasValue) StrokeRound(g, r, h / 2f, border.Value, 1f);
        int tx = r.X + h / 2;
        if (!string.IsNullOrEmpty(glyph))
        {
            int gw = (int)(h * 0.72f);
            Glyph(g, glyph, new Rectangle(tx - 2, r.Y, gw, h), fg, h >= 26 ? Icons : IconsSmall);
            tx += gw;
        }
        Text(g, text, f, new Rectangle(tx, r.Y, r.Right - tx - h / 2 + 2, h), fg);
        return r;
    }

    public static void Dot(Graphics g, float cx, float cy, float rad, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillEllipse(b, cx - rad, cy - rad, rad * 2f, rad * 2f);
    }

    /// Фирменный розовый слайм (рисуется векторно, без ресурсов).
    public static void Slime(Graphics g, RectangleF r, Color body, bool happy = true)
    {
        var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        // тень
        using (var sb = new SolidBrush(Color.FromArgb(28, 31, 42, 68))) g.FillEllipse(sb, x + w * 0.1f, y + h * 0.86f, w * 0.8f, h * 0.14f);
        using var path = new GraphicsPath();
        float rb = h * 0.2f, top = y + h * 0.04f, bottom = y + h * 0.92f;
        path.AddArc(x, top, w, h * 1.12f, 180, 180);
        path.AddLine(x + w, top + h * 0.56f, x + w, bottom - rb);
        path.AddArc(x + w - 2 * rb, bottom - 2 * rb, 2 * rb, 2 * rb, 0, 90);
        path.AddLine(x + w - rb, bottom, x + rb, bottom);
        path.AddArc(x, bottom - 2 * rb, 2 * rb, 2 * rb, 90, 90);
        path.CloseFigure();
        using (var lg = new LinearGradientBrush(new RectangleF(x, top, w, bottom - top + 1), Mix(body, Color.White, 0.35f), body, LinearGradientMode.Vertical))
            g.FillPath(lg, path);
        using (var pen = new Pen(Mix(body, Ink, 0.18f), Math.Max(1f, w / 40f))) g.DrawPath(pen, path);
        // блик
        using (var hb = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
        {
            g.FillEllipse(hb, x + w * 0.16f, top + h * 0.12f, w * 0.22f, h * 0.14f);
            g.FillEllipse(hb, x + w * 0.42f, top + h * 0.1f, w * 0.07f, h * 0.06f);
        }
        // глаза
        float ey = y + h * 0.42f, ew = w * 0.13f, eh = h * 0.2f;
        using (var eb = new SolidBrush(Ink))
        {
            g.FillEllipse(eb, x + w * 0.29f, ey, ew, eh);
            g.FillEllipse(eb, x + w * 0.58f, ey, ew, eh);
        }
        using (var gb = new SolidBrush(Color.White))
        {
            g.FillEllipse(gb, x + w * 0.31f, ey + eh * 0.12f, ew * 0.45f, eh * 0.32f);
            g.FillEllipse(gb, x + w * 0.60f, ey + eh * 0.12f, ew * 0.45f, eh * 0.32f);
        }
        // щёчки и улыбка
        using (var cb = new SolidBrush(Color.FromArgb(90, 255, 120, 150)))
        {
            g.FillEllipse(cb, x + w * 0.14f, y + h * 0.6f, w * 0.14f, h * 0.08f);
            g.FillEllipse(cb, x + w * 0.72f, y + h * 0.6f, w * 0.14f, h * 0.08f);
        }
        using (var mp = new Pen(Ink, Math.Max(1.2f, w / 26f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (happy) g.DrawArc(mp, x + w * 0.42f, y + h * 0.58f, w * 0.16f, h * 0.1f, 10, 160);
            else g.DrawLine(mp, x + w * 0.44f, y + h * 0.66f, x + w * 0.56f, y + h * 0.66f);
        }
        g.SmoothingMode = old;
    }

    public static void HGradient(Graphics g, RectangleF r, Color a, Color b)
    {
        if (r.Width < 1 || r.Height < 0.5f) return;
        using var lg = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), a, b, LinearGradientMode.Horizontal);
        using var p = RoundRect(r, r.Height / 2f);
        g.FillPath(lg, p);
    }

    // ───── мелочи ─────
    public static string Plural(int n, string one, string few, string many)
    {
        int a = Math.Abs(n) % 100, b = a % 10;
        if (a is >= 11 and <= 14) return many;
        return b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }

    public static string Count(int n, string one, string few, string many) => n + " " + Plural(n, one, few, many);

    public static string FileSize(long bytes)
        => bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.0") + " МБ" : Math.Max(1, (bytes + 512) / 1024) + " КБ";

    public static string Ago(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "только что";
        if (t.TotalHours < 1) return Count((int)t.TotalMinutes, "минуту", "минуты", "минут");
        int h = (int)t.TotalHours, m = t.Minutes;
        return h + " ч" + (m > 0 ? " " + m + " мин" : "");
    }
}


