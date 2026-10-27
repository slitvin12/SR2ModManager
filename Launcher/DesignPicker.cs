// DesignPicker.cs — выбор дизайна окна: три плитки с миниатюрами «Компактный», «Fluent» и «Слаймовый».
// Открывается кнопкой «Дизайн» на панели инструментов (или из меню «Ещё»). Миниатюра каждой плитки рисуется
// палитрой своего стиля, сама панель — палитрой текущего; после выбора она остаётся открытой и сразу
// перекрашивается, чтобы стили было легко сравнить. ← → — по плиткам, Enter/Пробел или 1–3 — выбрать, Esc — закрыть.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed class DesignPicker : Canvas
{
    private const int PadX = 16, TopH = 50, TileW = 200, TileH = 184, Gap = 12, DescH = 54;
    private int _focus;
    public event Action<DesignKind>? Picked;
    /// Для снимка окна (--render --picker): своя рамка и тень вместо рамки выпадающей панели.
    public bool Standalone;

    public DesignPicker()
    {
        BackColor = Theme.Paper;
        Size = new Size(Px(PadX * 2 + TileW * 3 + Gap * 2), Px(TopH + TileH + DescH));
        _focus = Index(Theme.Design);
        Theme.Changed += OnTheme;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnTheme;
        base.Dispose(disposing);
    }

    private void OnTheme()
    {
        BackColor = Theme.Paper;
        Invalidate();
        Parent?.Invalidate();   // рамка выпадающей панели — тоже цветом нового стиля
    }

    private static int Index(DesignKind k)
    {
        for (int i = 0; i < Designs.All.Count; i++) if (Designs.All[i].Kind == k) return i;
        return 0;
    }

    private Rectangle TileRect(int i) => new(Px(PadX) + i * Px(TileW + Gap), Px(TopH), Px(TileW), Px(TileH));

    private void Pick(int i)
    {
        if (i < 0 || i >= Designs.All.Count) return;
        _focus = i;
        Invalidate();
        Picked?.Invoke(Designs.All[i].Kind);
    }

    /// Клавиши из выпадающей панели. true — обработано.
    public bool HandleKey(Keys k)
    {
        switch (k & Keys.KeyCode)
        {
            case Keys.Left: _focus = (_focus + Designs.All.Count - 1) % Designs.All.Count; Invalidate(); return true;
            case Keys.Right: case Keys.Tab: _focus = (_focus + 1) % Designs.All.Count; Invalidate(); return true;
            case Keys.Enter: case Keys.Space: Pick(_focus); return true;
            case Keys.D1: case Keys.NumPad1: Pick(0); return true;
            case Keys.D2: case Keys.NumPad2: Pick(1); return true;
            case Keys.D3: case Keys.NumPad3: Pick(2); return true;
        }
        return false;
    }

    protected override void Draw(Graphics g)
    {
        if (Standalone)
        {
            Theme.FillRound(g, new RectangleF(0, 0, Width, Height), Px(10), Theme.Paper);
            Theme.StrokeRound(g, new RectangleF(0, 0, Width, Height), Px(10), Theme.LineDark, Math.Max(1f, S));
        }
        Theme.Text(g, "Дизайн окна", Theme.Strong, new Rectangle(Px(PadX), Px(12), Px(220), Px(24)), Theme.Navy);
        string hint = "← → и Enter · 1–3 · Ctrl+Shift+D — следующий";
        Theme.Text(g, hint, Theme.Small, new Rectangle(Width - Px(PadX) - Px(330), Px(14), Px(330), Px(20)), Theme.Hint, TextFormatFlags.Right);

        int hover = -1;
        for (int i = 0; i < Designs.All.Count; i++)
        {
            var spec = Designs.All[i];
            var r = TileRect(i);
            string key = "d:" + i;
            bool active = spec.Kind == Theme.Design, hv = IsHover(key), focus = i == _focus;
            if (hv) hover = i;
            float rad = Px(10);
            Theme.FillRound(g, r, rad, hv ? Theme.RowHover : Theme.Paper);
            if (active) Theme.StrokeRound(g, r, rad, Theme.Teal, Math.Max(2f, S * 2f));
            else Theme.StrokeRound(g, r, rad, hv || focus ? Theme.LineDark : Theme.Line, Math.Max(1f, S));
            if (focus && !active) Theme.StrokeRound(g, Rectangle.Inflate(r, Px(3), Px(3)), rad + Px(3), Theme.Alpha(Theme.Teal, 110), Math.Max(1f, S));

            var pr = new Rectangle(r.X + Px(8), r.Y + Px(8), r.Width - Px(16), Px(118));
            DrawPreview(g, spec, pr);

            int ny = pr.Bottom + Px(10);
            Theme.Text(g, spec.Name, Theme.Strong, new Rectangle(r.X + Px(12), ny, r.Width - Px(52), Px(22)), Theme.Navy);
            Theme.Text(g, spec.Tagline, Theme.Small, new Rectangle(r.X + Px(12), ny + Px(23), r.Width - Px(24), Px(18)), Theme.NavySoft);
            var num = new Rectangle(r.Right - Px(12) - Px(22), ny, Px(22), Px(22));
            if (active)
            {
                Theme.FillRound(g, num, num.Height / 2f, Theme.Teal);
                Theme.Glyph(g, Theme.GCheck, num, Color.White, Theme.IconsSmall);
            }
            else
            {
                Theme.FillRound(g, num, num.Height / 2f, Theme.Beige);
                Theme.Text(g, (i + 1).ToString(), Theme.SmallBold, num, Theme.NavySoft, TextFormatFlags.HorizontalCenter);
            }
            int idx = i;
            AddHit(r, key, () => Pick(idx), null);
        }

        var show = Designs.All[hover >= 0 ? hover : _focus];
        var dr = new Rectangle(Px(PadX), Px(TopH + TileH) + Px(10), Width - Px(PadX * 2), Px(DescH) - Px(18));
        string lead = (show.Kind == Theme.Design ? "Сейчас: " : "") + "«" + show.Name + "» — ";
        Theme.Wrap(g, lead + show.Description, Theme.Small, dr, Theme.NavySoft);
    }

    /// Миниатюра «Компактного» стиля: верхняя полоса с фильтром и «Играть», боковая панель с наборами и разделами,
    /// плотная таблица (две строки с конфликтом хуков), нижняя панель с высокими строками и кнопками, строка состояния.
    private void DrawFlatPreview(Graphics g, Palette p, Rectangle pr)
    {
        float u = pr.Width / 184f, H = pr.Height / u;
        RectangleF R(float x, float y, float w, float h) => new(pr.X + x * u, pr.Y + y * u, w * u, h * u);
        void Fill(RectangleF r, float rad, Color c) { if (rad <= 0) { using var b = new SolidBrush(c); g.FillRectangle(b, r); } else Theme.FillRound(g, r, rad * u, c); }
        void Stroke(RectangleF r, float rad, Color c) => Theme.StrokeRound(g, r, rad * u, c, Math.Max(1f, S * 0.8f));
        void HLine(float x, float y, float w, Color c) => Fill(R(x, y, w, Math.Max(0.13f, 1f / u)), 0, c);
        void Bar(float x, float y, float w, Color c, float h = 1.5f) => Fill(R(x, y - h / 2f, w, h), h / 2f, c);
        Color sep = Theme.Mix(p.Cream, p.Line, 0.43f);

        // верхняя полоса
        Fill(R(0, 0, 184, 6.4f), 0, p.HeaderBg);
        HLine(0, 6.4f, 184, p.Line);
        Fill(R(1.4f, 1.2f, 32, 4), 0.8f, p.Cream);
        Bar(4.5f, 3.2f, 16, p.Hint, 1.1f);
        Fill(R(35.5f, 1.5f, 8.5f, 3.4f), 1.7f, p.Beige);
        Bar(37, 3.2f, 5.5f, p.Navy, 1.1f);
        Bar(47, 3.2f, 5, p.NavySoft, 1.1f);
        Bar(55, 3.2f, 5, p.NavySoft, 1.1f);
        Bar(63, 3.2f, 9, p.Orange, 1.1f);
        Bar(75, 3.2f, 11, p.Blue, 1.1f);
        Stroke(R(139.5f, 1.2f, 20.5f, 4), 0.8f, p.LineDark);
        Bar(143, 3.2f, 11, p.NavySoft, 1.1f);
        Fill(R(161.5f, 1, 21, 4.4f), 0.8f, p.Teal);
        Bar(165, 3.2f, 10, p.OnAccent, 1.2f);

        // боковая панель: наборы, разделы, окружение
        float sideW = 27, barY = H - 3.4f;
        Fill(R(0, 6.4f + 0.2f, sideW, barY - 6.6f), 0, p.HeaderBg);
        Fill(R(sideW, 6.6f, Math.Max(0.13f, 1f / u), barY - 6.6f), 0, p.Line);
        Bar(2.2f, 9.6f, 7, p.Hint, 0.9f);
        var dots = new[] { p.Teal, p.Hint, p.Hint, p.Pink, p.Orange };
        for (int i = 0; i < 5; i++)
        {
            float y = 11.4f + i * 3.8f;
            if (i == 0) Fill(R(1.1f, y, 24.8f, 3.6f), 0.7f, p.TealTint);
            Theme.Dot(g, pr.X + 3.2f * u, pr.Y + (y + 1.8f) * u, 0.55f * u, dots[i]);
            Bar(5, y + 1.8f, i == 0 ? 9 : 10 + (i * 3) % 5, i == 0 ? p.Navy : p.NavySoft, 1.1f);
            if (i == 0) Fill(R(15, y + 1.05f, 5.5f, 1.5f), 0.75f, p.OrangeTint);
        }
        Bar(2.2f, 34.5f, 7, p.Hint, 0.9f);
        for (int i = 0; i < 5; i++)
        {
            float y = 37.6f + i * 3.8f;
            Fill(R(2, y + 1.1f, 1.5f, 1.5f), 0.3f, i == 0 ? p.Navy : p.NavySoft);
            Bar(5.2f, y + 1.8f, 11 + (i * 5) % 8, i == 0 ? p.Navy : p.NavySoft, 1.1f);
        }
        for (int i = 0; i < 3; i++) Bar(2.2f, barY - 9.2f + i * 2.4f, i == 0 ? 9 : 16 - i * 2, i == 0 ? p.NavySoft : p.Hint, 0.9f);

        // таблица
        float tx = sideW + 0.2f, tw = 184 - tx, ty = 6.6f;
        Fill(R(tx, ty, tw, 3.6f), 0, p.HeaderBg);
        HLine(tx, ty + 3.6f, tw, p.Line);
        foreach (var (x, w) in new[] { (5.5f, 4f), (44, 5), (54, 4), (77, 9), (101, 3), (109, 8), (174, 5) }) Bar(tx + x, ty + 1.8f, w, p.Hint, 0.9f);
        float dockY = H * 0.635f;
        var compat = new[] { p.Orange, p.Green, p.Green, p.Green, p.Orange, p.Orange, p.Coral, p.Green, p.Green, p.Green, p.Green, p.Green };
        var av = new[] { 0xFF9BB5, 0xE0A8F5, 0x7EE0C6, 0x9CC3FF, 0xFFD28A, 0xA6E08F, 0xB5C0D8, 0xFFB38A, 0xC7B6FF, 0x8AE3F0, 0xFFE38A, 0xB8E986 };
        for (int i = 0; ; i++)
        {
            float y = ty + 3.8f + i * 3.8f;
            if (y + 3.8f > dockY - 0.8f || i >= compat.Length) break;
            bool conflict = i is 4 or 5, on = i != 6 && i != 9;
            if (conflict) Fill(R(tx, y, tw, 3.7f), 0, p.OrangeTint);
            HLine(tx, y + 3.7f, tw, sep);
            float cy = y + 1.85f;
            if (on) Fill(R(tx + 1.1f, cy - 0.95f, 1.9f, 1.9f), 0.4f, p.Teal); else Stroke(R(tx + 1.1f, cy - 0.95f, 1.9f, 1.9f), 0.4f, p.Hint);
            Fill(R(tx + 5.5f, cy - 1.2f, 2.4f, 2.4f), 0.5f, Theme.Hex(av[i]));
            Bar(tx + 9.2f, cy, 13 + (i * 7) % 12, on ? p.Navy : p.NavySoft, 1.2f);
            Bar(tx + 45, cy, 4.5f, on ? p.NavySoft : p.Hint, 1f);
            Bar(tx + 55, cy, 10 + (i * 3) % 7, on ? p.NavySoft : p.Hint, 1f);
            Theme.Dot(g, pr.X + (tx + 78) * u, pr.Y + cy * u, 0.7f * u, compat[i]);
            Bar(tx + 80, cy, 10 + (i % 3) * 2, compat[i], 1f);
            Bar(tx + 101.5f, cy, 2, on ? p.NavySoft : p.Hint, 1f);
            Bar(tx + 110, cy, i is 3 or 8 ? 5 : 2, i is 3 or 8 ? p.Blue : p.Hint, 1f);
            Bar(tx + 173, cy, 6, on ? p.NavySoft : p.Hint, 1f);
        }

        // ручка и нижняя панель: вкладки с подчёркиванием, строки проблем с кнопками
        Fill(R(tx, dockY - 0.7f, tw, 0.7f), 0, p.Cream);
        Fill(R(tx, dockY, tw, barY - dockY), 0, p.HeaderBg);
        HLine(tx, dockY + 4.5f, tw, p.Line);
        for (int i = 0; i < 4; i++)
        {
            float x = tx + 2 + i * 15.5f;
            Bar(x + 2, dockY + 2.2f, 10, i == 0 ? p.Navy : p.NavySoft, 1.1f);
            if (i == 0) { Fill(R(x + 12.6f, dockY + 1.5f, 2.6f, 1.6f), 0.8f, p.OrangeTint); Fill(R(x, dockY + 4.1f, 15, 0.4f), 0, p.Teal); }
        }
        var tones = new[] { p.Coral, p.Orange, p.Orange, p.Orange };
        for (int i = 0; i < 4; i++)
        {
            float y = dockY + 4.6f + i * 6f;
            if (y + 6f > barY) break;
            if (i == 1) Fill(R(tx, y, tw, 5.9f), 0, p.TealTint);
            HLine(tx, y + 5.9f, tw, Theme.Mix(p.HeaderBg, p.Line, 0.47f));
            float cy = y + 2.95f;
            Theme.Dot(g, pr.X + (tx + 2.7f) * u, pr.Y + cy * u, 0.9f * u, tones[i]);
            Bar(tx + 6, cy, 16, p.Navy, 1.2f);
            Bar(tx + 32, cy, 46 - i * 6, p.NavySoft, 1.1f);
            float bx = 182.4f;
            for (int j = 0; j < (i < 2 ? 2 : 1); j++)
            {
                float bw = j == 0 && i < 2 ? 9 : 13;
                bx -= bw;
                var br = R(bx, cy - 1.7f, bw, 3.4f);
                Fill(br, 0.6f, p.Beige);
                Stroke(br, 0.6f, p.LineDark);
                bx -= 1;
            }
        }

        // строка состояния
        Fill(R(0, barY, 184, H - barY), 0, p.HeaderBg);
        HLine(0, barY, 184, p.Line);
        float sy = barY + (H - barY) / 2f;
        Theme.Dot(g, pr.X + 2 * u, pr.Y + sy * u, 0.6f * u, p.Hint);
        Bar(3.6f, sy, 12, p.Hint, 0.9f);
        Bar(18, sy, 13, p.NavySoft, 0.9f);
        Bar(34, sy, 18, p.Navy, 0.9f);
        Bar(140, sy, 42, p.Hint, 0.9f);
    }

    /// Миниатюра окна в палитре и пластике стиля.
    private void DrawPreview(Graphics g, DesignSpec spec, Rectangle pr)
    {
        var p = spec.Palette;
        using var frame = Theme.RoundRect(pr, Px(7));
        var st = g.Save();
        g.SetClip(frame, CombineMode.Intersect);
        using (var b = new SolidBrush(p.Cream)) g.FillRectangle(b, pr);
        switch (spec.Kind)
        {
            case DesignKind.Fluent: DrawFluentPreview(g, p, pr); break;
            case DesignKind.Slime: DrawSlimePreview(g, p, pr); break;
            default: DrawFlatPreview(g, p, pr); break;
        }
        g.Restore(st);
        Theme.StrokeRound(g, pr, Px(7), Theme.Alpha(p.LineDark, 200), Math.Max(1f, S));
    }

    /// Миниатюра Fluent: навигация слева (слайм, поиск, наборы с акцентной чертой, разделы, окружение), заголовок
    /// страницы с «Командами» и «Играть», вкладки с чертой снизу, белые карточки, нижняя панель, строка состояния.
    private void DrawFluentPreview(Graphics g, Palette p, Rectangle pr)
    {
        float u = pr.Width / 184f, H = pr.Height / u;
        RectangleF R(float x, float y, float w, float h) => new(pr.X + x * u, pr.Y + y * u, w * u, h * u);
        void Fill(RectangleF r, float rad, Color c) { if (rad <= 0) { using var b = new SolidBrush(c); g.FillRectangle(b, r); } else Theme.FillRound(g, r, rad * u, c); }
        void Stroke(RectangleF r, float rad, Color c) => Theme.StrokeRound(g, r, rad * u, c, Math.Max(1f, S * 0.8f));
        void Bar(float x, float y, float w, Color c, float h = 1.5f) => Fill(R(x, y - h / 2f, w, h), h / 2f, c);
        void Dot(float x, float y, float r, Color c) => Theme.Dot(g, pr.X + x * u, pr.Y + y * u, r * u, c);
        Color sel = Theme.Mix(p.Cream, p.Navy, 0.07f), soft = Theme.Mix(p.Line, p.LineDark, 0.5f);
        float barY = H - 4.4f;

        // навигация
        Theme.Slime(g, R(3.6f, 3.2f, 6.6f, 5.8f), p.Pink);
        Bar(12.5f, 5, 18, p.Navy, 1.4f);
        Bar(12.5f, 7.6f, 12, p.Hint, 0.9f);
        var search = R(3, 11, 39, 5.4f);
        Fill(search, 0.9f, p.Paper);
        Stroke(search, 0.9f, p.Line);
        Fill(R(3.5f, 15.9f, 38, 0.5f), 0, Theme.Mix(p.Line, p.NavySoft, 0.6f));
        Bar(6, 13.7f, 16, p.Hint, 0.9f);
        Bar(4.5f, 21.4f, 8, p.NavySoft, 1f);
        var dots = new[] { p.Teal, p.Hint, p.Hint, p.Pink, p.Orange };
        for (int i = 0; i < 5; i++)
        {
            float y = 23.4f + i * 4.6f;
            if (i == 0) { Fill(R(2, y, 41, 4.2f), 0.8f, sel); Fill(R(2, y + 1.1f, 0.8f, 2f), 0.4f, p.Teal); }
            Dot(6.2f, y + 2.1f, 0.75f, dots[i]);
            Bar(9.5f, y + 2.1f, i == 0 ? 13 : 11 + (i * 4) % 7, p.Navy, 1.2f);
            Bar(38.5f, y + 2.1f, 2, p.Hint, 1f);
        }
        Bar(4.5f, 48.6f, 8, p.NavySoft, 1f);
        for (int i = 0; i < 5; i++)
        {
            float y = 50.6f + i * 4.6f;
            Fill(R(4.6f, y + 1.2f, 1.8f, 1.8f), 0.4f, p.NavySoft);
            Bar(9.5f, y + 2.1f, 12 + (i * 5) % 9, p.Navy, 1.2f);
            if (i == 1) Fill(R(36.5f, y + 1.1f, 4.5f, 2f), 1f, p.Teal);
        }
        var env = R(3, barY - 15, 39, 12);
        Fill(env, 1.2f, p.Paper);
        Stroke(env, 1.2f, p.Line);
        Dot(6, barY - 12, 0.7f, p.Teal);
        Bar(8, barY - 12, 12, p.Navy, 1f);
        Bar(5.5f, barY - 8.6f, 26, p.NavySoft, 0.9f);
        Bar(5.5f, barY - 6.2f, 20, p.NavySoft, 0.9f);

        // заголовок страницы, «Команды» и «Играть», вкладки с чертой
        const float L = 50, Rr = 180;
        Bar(L, 6.2f, 17, p.Navy, 3.2f);
        Bar(L + 20, 6.8f, 18, p.Hint, 1f);
        var cmd = R(131, 3.4f, 23, 6);
        Fill(cmd, 0.9f, p.Paper);
        Stroke(cmd, 0.9f, soft);
        Bar(135, 6.4f, 13, p.Navy, 1.1f);
        Fill(R(156, 3.4f, 24, 6), 0.9f, p.Teal);
        Bar(160, 6.4f, 12, p.OnAccent, 1.2f);
        float tx = L;
        var tabs = new (float W, Color Badge)[]
        {
            (12, Theme.Mix(p.Beige, p.BeigeDark, 0.5f)), (9, Theme.Mix(p.Beige, p.BeigeDark, 0.5f)), (10, Theme.Mix(p.Beige, p.BeigeDark, 0.5f)),
            (17, Theme.Mix(p.CoralTint, p.Coral, 0.35f)), (19, Theme.Mix(p.BlueTint, p.Blue, 0.35f)),
        };
        for (int i = 0; i < tabs.Length; i++)
        {
            Bar(tx, 14.6f, tabs[i].W - 4.4f, i == 0 ? p.Navy : p.NavySoft, 1.2f);
            Fill(R(tx + tabs[i].W - 3.4f, 13.5f, 3.4f, 2.2f), 1.1f, i == 0 ? p.TealTint : tabs[i].Badge);
            if (i == 0) Fill(R(tx + tabs[i].W / 2f - 3, 17.7f, 6, 0.8f), 0.4f, p.Teal);
            tx += tabs[i].W + 4;
        }

        // карточки
        float dockY = H * 0.66f;
        var av = new[] { p.PurpleTint, p.BlueTint, p.PinkTint, p.TealTint, p.OrangeTint };
        for (int i = 0; ; i++)
        {
            float y = 21 + i * 8.6f;
            if (y + 7.6f > dockY - 2.5f) break;
            bool s1 = i == 1, on = i != 2;
            var c = R(L, y, Rr - L, 7.6f);
            Fill(c, 1f, s1 ? p.Selected : p.Paper);
            Stroke(c, 1f, s1 ? Theme.Mix(p.Teal, p.Paper, 0.3f) : p.Line);
            Fill(R(L + 3, y + 2.6f, 5.4f, 2.6f), 1.3f, on ? p.Teal : p.ToggleOff);
            Dot(on ? L + 7.1f : L + 4.3f, y + 3.9f, 0.9f, p.Paper);
            Fill(R(L + 11, y + 1.8f, 4.2f, 4.2f), 0.9f, av[i % av.Length]);
            Bar(L + 18, y + 2.7f, 18 + (i * 7) % 12, p.Navy, 1.4f);
            Bar(L + 18, y + 5.2f, 40 - (i * 6) % 16, p.Hint, 0.9f);
            Fill(R(Rr - 15, y + 2.6f, 12, 2.6f), 1.3f, i == 3 ? p.OrangeTint : on ? p.TealTint : p.Beige);
        }

        // нижняя панель: вкладки с чертой, строки с белыми кнопками
        var dock = R(L, dockY, Rr - L, barY - 2.5f - dockY);
        Fill(dock, 1.4f, p.Paper);
        using (var dp = Theme.RoundRect(dock, 1.4f * u))
        {
            var s2 = g.Save();
            g.SetClip(dp, CombineMode.Intersect);
            Fill(R(L, dockY, Rr - L, 5.4f), 0, p.HeaderBg);
            Fill(R(L, dockY + 5.4f, Rr - L, Math.Max(0.13f, 1f / u)), 0, p.Line);
            g.Restore(s2);
        }
        Stroke(dock, 1.4f, p.Line);
        for (int i = 0; i < 4; i++)
        {
            float x = L + 3 + i * 17;
            Bar(x, dockY + 2.7f, 11, i == 0 ? p.Navy : p.NavySoft, 1.1f);
            if (i == 0) { Fill(R(x + 12, dockY + 1.7f, 3, 2f), 1f, Theme.Mix(p.CoralTint, p.Coral, 0.35f)); Fill(R(x + 2.5f, dockY + 4.5f, 6, 0.8f), 0.4f, p.Teal); }
        }
        var tones = new[] { p.Coral, p.Orange, p.Orange };
        for (int i = 0; i < 3; i++)
        {
            float y = dockY + 7f + i * 6.4f;
            if (y + 5.6f > barY - 3f) break;
            if (i == 0) Fill(R(L + 1.2f, y, Rr - L - 2.4f, 5.6f), 1f, p.TealTint);
            float cy = y + 2.8f;
            Dot(L + 4.5f, cy, 0.9f, tones[i]);
            Bar(L + 8, cy, 16, p.Navy, 1.2f);
            Bar(L + 30, cy, 50 - i * 8, p.NavySoft, 1.1f);
            var br = R(Rr - 16, cy - 1.7f, 13, 3.4f);
            Fill(br, 0.7f, p.Paper);
            Stroke(br, 0.7f, soft);
        }

        // строка состояния
        Fill(R(0, barY, 184, Math.Max(0.13f, 1f / u)), 0, p.Line);
        float sy = barY + (H - barY) / 2f;
        Dot(3, sy, 0.6f, p.Hint);
        Bar(4.6f, sy, 12, p.Hint, 0.9f);
        Bar(19, sy, 13, p.NavySoft, 0.9f);
        Bar(140, sy, 42, p.Hint, 0.9f);
    }

    /// Миниатюра «Слаймового»: «парящая» белая боковая панель со слаймом, наборами-слаймиками и цветными кружками
    /// разделов; сверху — капсулы поиска, «Команд» и «Играть», под ними — вкладки; капсулы модов, нижняя панель,
    /// строка состояния из капсул.
    private void DrawSlimePreview(Graphics g, Palette p, Rectangle pr)
    {
        float u = pr.Width / 184f, H = pr.Height / u;
        RectangleF R(float x, float y, float w, float h) => new(pr.X + x * u, pr.Y + y * u, w * u, h * u);
        void Fill(RectangleF r, float rad, Color c) { if (rad <= 0) { using var b = new SolidBrush(c); g.FillRectangle(b, r); } else Theme.FillRound(g, r, rad * u, c); }
        void Stroke(RectangleF r, float rad, Color c) => Theme.StrokeRound(g, r, rad * u, c, Math.Max(1f, S * 0.8f));
        void Bar(float x, float y, float w, Color c, float h = 1.5f) => Fill(R(x, y - h / 2f, w, h), h / 2f, c);
        void Dot(float x, float y, float r, Color c) => Theme.Dot(g, pr.X + x * u, pr.Y + y * u, r * u, c);
        Color shadow = Theme.Alpha(Theme.Ink, 20), soft = Theme.Mix(p.Line, p.LineDark, 0.5f);
        float barY = H - 6.4f;

        // боковая панель
        Fill(R(3.2f, 3.8f, 42, barY - 3.8f), 3.6f, shadow);
        Fill(R(2.6f, 2.6f, 42, barY - 3.8f), 3.6f, p.Paper);
        Theme.Slime(g, R(5.2f, 5, 9.4f, 8.2f), p.Pink);
        Bar(17, 7.2f, 20, p.Navy, 1.6f);
        Bar(17, 10, 12, p.Hint, 0.8f);
        Theme.HGradient(g, R(17, 12, 14, 0.9f), p.Teal, p.Pink);
        Bar(6.4f, 18.4f, 7, p.Hint, 0.8f);
        var cols = new[] { p.Teal, p.LineDark, p.Pink, p.Orange, p.Blue };
        for (int i = 0; i < 5; i++)
        {
            float y = 20.4f + i * 4.8f;
            if (i == 0) Fill(R(4.4f, y, 38.4f, 4.2f), 2.1f, p.TealTint);
            Fill(R(6.4f, y + 0.9f, 3, 2.5f), 1.25f, cols[i]);
            Bar(11.4f, y + 2.1f, i == 0 ? 13 : 10 + (i * 4) % 7, i == 0 ? p.TealText : p.Navy, 1.2f);
            Fill(R(36.4f, y + 1.1f, 4.4f, 2f), 1f, i == 0 ? p.Paper : p.Beige);
        }
        Bar(6.4f, 46.2f, 7, p.Hint, 0.8f);
        var tints = new[] { (p.TealTint, p.TealText), (p.PinkTint, p.Pink), (p.OrangeTint, p.OrangeText), (p.BlueTint, p.BlueText), (p.PurpleTint, p.PurpleText) };
        for (int i = 0; i < 5; i++)
        {
            float y = 48.2f + i * 4.8f;
            Dot(7.9f, y + 2.1f, 1.8f, tints[i].Item1);
            Dot(7.9f, y + 2.1f, 0.6f, tints[i].Item2);
            Bar(11.4f, y + 2.1f, 12 + (i * 5) % 9, p.Navy, 1.2f);
            if (i == 1) Fill(R(36.4f, y + 1.1f, 4.4f, 2f), 1f, p.Pink);
        }
        Fill(R(4.6f, barY - 14.4f, 37.8f, 10.6f), 2.4f, p.Cream);
        Dot(7.4f, barY - 11.2f, 0.7f, p.Teal);
        Bar(9, barY - 11.2f, 12, p.Navy, 1f);
        Bar(7, barY - 8.2f, 24, p.NavySoft, 0.9f);
        Bar(7, barY - 5.8f, 18, p.NavySoft, 0.9f);

        // поиск, «Команды», «Играть»; вкладки
        const float L = 49, Rr = 181;
        var search = R(L, 3, 76, 6.4f);
        Fill(search, 3.2f, p.Paper);
        Stroke(search, 3.2f, p.Line);
        Bar(L + 4.4f, 6.2f, 24, p.Hint, 0.9f);
        var cmd = R(128, 3, 22, 6.4f);
        Fill(cmd, 3.2f, p.Paper);
        Stroke(cmd, 3.2f, soft);
        Bar(132, 6.2f, 13, p.Navy, 1.1f);
        Fill(R(153.6f, 4, 27.4f, 6.4f), 3.2f, Theme.Alpha(Theme.Ink, 26));
        Fill(R(153, 3, 28, 6.4f), 3.2f, p.Teal);
        Bar(157.5f, 6.2f, 14, p.OnAccent, 1.2f);
        Fill(R(L, 12.4f, 90, 5.8f), 2.9f, p.Beige);
        Fill(R(L + 0.7f, 13.1f, 16.4f, 4.4f), 2.2f, p.Paper);
        float tx = L + 3;
        var segs = new (float W, Color Badge)[] { (16.4f, p.TealTint), (14, p.Paper), (15, p.Paper), (20, p.CoralTint), (22, p.BlueTint) };
        for (int i = 0; i < segs.Length; i++)
        {
            Bar(tx, 15.3f, segs[i].W - 8.5f, i == 0 ? p.Navy : p.NavySoft, 1.1f);
            Fill(R(tx + segs[i].W - 7.6f, 14.3f, 3.6f, 2f), 1f, segs[i].Badge);
            tx += segs[i].W;
        }

        // капсулы модов
        float dockY = H * 0.64f;
        var av = new[] { p.Pink, p.Blue, p.Orange, p.Teal };
        for (int i = 0; ; i++)
        {
            float y = 21.6f + i * 11.2f;
            if (y + 9.6f > dockY - 2.5f) break;
            bool s1 = i == 0, on = i != 2;
            Fill(R(L + 0.6f, y + 1.2f, Rr - L - 1.2f, 9.6f), 4.8f, Theme.Alpha(Theme.Ink, s1 ? 26 : 14));
            var c = R(L, y, Rr - L, 9.6f);
            Fill(c, 4.8f, s1 ? p.Selected : p.Paper);
            if (s1) Stroke(c, 4.8f, p.Teal);
            Fill(R(L + 3.4f, y + 3.3f, 6.4f, 3f), 1.5f, on ? p.Teal : p.ToggleOff);
            Dot(L + 15, y + 4.8f, 2.7f, Theme.Mix(av[i % av.Length], p.Paper, 0.7f));
            Bar(L + 20, y + 3.4f, 20 + (i * 7) % 12, p.Navy, 1.4f);
            Bar(L + 20, y + 6.3f, 38 - (i * 6) % 14, p.Hint, 0.9f);
            Fill(R(Rr - 17, y + 3.4f, 13, 2.8f), 1.4f, on ? p.TealTint : p.Beige);
        }

        // нижняя панель: вкладки-капсулы, строки-плашки с кнопками-капсулами
        var dock = R(L, dockY, Rr - L, barY - 2 - dockY);
        Fill(dock, 3f, p.Paper);
        using (var dp = Theme.RoundRect(dock, 3f * u))
        {
            var s2 = g.Save();
            g.SetClip(dp, CombineMode.Intersect);
            Fill(R(L, dockY, Rr - L, 5.6f), 0, p.HeaderBg);
            g.Restore(s2);
        }
        Stroke(dock, 3f, p.Line);
        for (int i = 0; i < 4; i++)
        {
            float x = L + 3 + i * 17;
            if (i == 0) Fill(R(x - 1.4f, dockY + 1, 15.4f, 3.6f), 1.8f, p.TealTint);
            Bar(x + 1, dockY + 2.8f, 10, i == 0 ? p.TealText : p.NavySoft, 1.1f);
        }
        var tones = new[] { p.Coral, p.Orange, p.Orange };
        for (int i = 0; i < 3; i++)
        {
            float y = dockY + 7.2f + i * 6.2f;
            if (y + 5.4f > barY - 3.5f) break;
            if (i == 0) Fill(R(L + 1.4f, y, Rr - L - 2.8f, 5.4f), 2.7f, p.TealTint);
            float cy = y + 2.7f;
            Dot(L + 5.5f, cy, 1f, tones[i]);
            Bar(L + 9, cy, 16, p.Navy, 1.2f);
            Bar(L + 31, cy, 46 - i * 8, p.NavySoft, 1.1f);
            Fill(R(Rr - 17, cy - 1.7f, 13, 3.4f), 1.7f, p.Beige);
        }

        // строка состояния — капсулы
        float sy = barY + (H - barY) / 2f;
        void Chip(float x, float w, Color bg, Color? line)
        {
            var r = R(x, sy - 2f, w, 4f);
            Fill(r, 2f, bg);
            if (line is { } l) Stroke(r, 2f, l);
        }
        Chip(2.6f, 22, p.Paper, p.Line);
        Dot(5.2f, sy, 0.7f, p.Hint);
        Bar(7, sy, 15, p.NavySoft, 0.9f);
        Chip(27, 20, p.Paper, p.Line);
        Bar(29.6f, sy, 15, p.NavySoft, 0.9f);
        Chip(50, 27, p.OrangeTint, null);
        Dot(52.8f, sy, 0.7f, p.Orange);
        Bar(55, sy, 19, p.OrangeText, 0.9f);
        Bar(140, sy, 40, p.Hint, 0.9f);
    }

    // ───────── выпадающая панель ─────────

    private sealed class Drop : ToolStripDropDown
    {
        private readonly DesignPicker _p;

        public Drop(DesignPicker p)
        {
            _p = p;
            AutoSize = false;
            Padding = new Padding(1);
            DropShadowEnabled = true;
            Renderer = Menus.NewRenderer();
            var host = new ToolStripControlHost(p) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = p.Size };
            Items.Add(host);
            Size = new Size(p.Width + 2, p.Height + 2);
        }

        protected override bool ProcessDialogKey(Keys keyData) => _p.HandleKey(keyData) || base.ProcessDialogKey(keyData);

        protected override bool ProcessCmdKey(ref Message m, Keys keyData) => _p.HandleKey(keyData) || base.ProcessCmdKey(ref m, keyData);
    }

    private static Drop? _open;
    private static DateTime _closedAt;

    public static bool IsOpen => _open != null && !_open.IsDisposed && _open.Visible;

    /// Открыть под кнопкой anchor (правым краем к её правому краю). Повторный щелчок по кнопке закрывает.
    public static void Show(Control anchor, Action<DesignKind> pick)
    {
        if (IsOpen) { _open!.Close(); return; }
        // щелчок по кнопке сначала закрыл панель (щелчок мимо неё), а потом дошёл до кнопки — не открывать снова
        if ((DateTime.Now - _closedAt).TotalMilliseconds < 300) return;
        var p = new DesignPicker();
        p.Picked += k => pick(k);
        var d = new Drop(p);
        d.Closed += (_, _) =>
        {
            _closedAt = DateTime.Now;
            if (_open == d) _open = null;
            try { anchor.BeginInvoke(new Action(d.Dispose)); } catch { d.Dispose(); }
        };
        _open = d;
        d.Show(anchor, new Point(anchor.Width - d.Width, anchor.Height + (int)Math.Round(4 * anchor.DeviceDpi / 96f)));
    }

    public static void CloseOpen() { if (IsOpen) _open!.Close(); }
}
