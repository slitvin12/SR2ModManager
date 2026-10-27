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

    /// Миниатюра окна в палитре стиля: шапка, панель поиска, список (таблица / карточки / капсулы), нижняя панель, подвал.
    private void DrawPreview(Graphics g, DesignSpec spec, Rectangle pr)
    {
        var p = spec.Palette;
        float u = pr.Width / 184f;
        RectangleF R(float x, float y, float w, float h) => new(pr.X + x * u, pr.Y + y * u, w * u, h * u);
        void Fill(RectangleF r, float rad, Color c) => Theme.FillRound(g, r, rad * u, c);
        void Stroke(RectangleF r, float rad, Color c) => Theme.StrokeRound(g, r, rad * u, c, Math.Max(1f, S * 0.8f));
        float ctrl = spec.CtrlRadius < 0 ? 4f : Math.Min(4f, spec.CtrlRadius * 0.5f);

        using var frame = Theme.RoundRect(pr, Px(7));
        var st = g.Save();
        g.SetClip(frame, CombineMode.Intersect);
        using (var b = new SolidBrush(p.Cream)) g.FillRectangle(b, pr);
        float hh = spec.HeaderH * 0.16f;   // шапка: 10–15 px миниатюры

        // шапка
        float sd = spec.Kind == DesignKind.Compact ? 7f : spec.Kind == DesignKind.Fluent ? 8f : 9.5f;
        Fill(R(6, (hh - sd) / 2f + 0.5f, sd, sd * 0.88f), sd / 2.2f, p.Pink);
        Fill(R(9 + sd, hh / 2f - 2.4f, spec.Kind == DesignKind.Slime ? 42 : 36, 4.2f), 2f, Theme.Alpha(p.Navy, 220));
        if (spec.Kind == DesignKind.Slime) Fill(R(9 + sd, hh / 2f + 3f, 22, 1.4f), 0.7f, p.Teal);
        var chip = R(148, hh / 2f - 3.5f, 30, 7);
        Fill(chip, 3.5f, p.Paper);
        Stroke(chip, 3.5f, p.Line);
        Fill(R(151.5f, hh / 2f - 1f, 2, 2), 1, p.Teal);

        // поиск, фильтры, «Дизайн»
        float ty = hh + 3;
        var search = R(6, ty, 54, 8);
        Fill(search, ctrl, p.Paper);
        Stroke(search, ctrl, p.Line);
        Fill(R(64, ty, 20, 8), ctrl, p.Teal);
        Fill(R(87, ty, 20, 8), ctrl, p.Beige);
        Fill(R(110, ty, 20, 8), ctrl, p.Beige);
        Fill(R(160, ty, 18, 8), ctrl, p.Beige);

        // список
        float ly = ty + 12, lh = 102 - 26 - ly;
        switch (spec.Mode)
        {
            case ListMode.Table:
            {
                var t = R(6, ly, 172, lh);
                Fill(t, 3, p.Paper);
                Fill(R(6, ly, 172, 6), 0, p.HeaderBg);
                float rh = (lh - 6) / 6f;
                for (int i = 0; i < 6; i++)
                {
                    float y = ly + 6 + i * rh;
                    bool sel = i == 1;
                    if (sel) Fill(R(6, y, 172, rh), 0, p.Selected);
                    else if (i % 2 == 1) Fill(R(6, y, 172, rh), 0, p.RowAlt);
                    if (sel) Fill(R(6, y, 1.2f, rh), 0, p.Teal);
                    float cy = y + rh / 2f;
                    Fill(R(9.5f, cy - 1.8f, 3.6f, 3.6f), 0.8f, i == 3 ? p.ToggleOff : p.Teal);
                    Fill(R(17, cy - 1.2f, 30 + (i * 7) % 14, 2.4f), 1.2f, Theme.Alpha(p.Navy, 200));
                    Fill(R(68, cy - 1f, 12, 2), 1, p.Hint);
                    Fill(R(90, cy - 1f, 18, 2), 1, p.Hint);
                    Theme.Dot(g, pr.X + 121 * u, pr.Y + cy * u, 1.3f * u, i == 4 ? p.Orange : p.Teal);
                    Fill(R(124, cy - 1f, 16, 2), 1, i == 4 ? p.OrangeText : p.TealText);
                    Fill(R(166, cy - 1f, 9, 2), 1, p.Hint);
                }
                Stroke(t, 3, p.Line);
                break;
            }
            case ListMode.Cards:
            {
                float ch = (lh - 4) / 3f;
                for (int i = 0; i < 3; i++)
                {
                    var c = R(6, ly + i * (ch + 2), 172, ch);
                    float y = ly + i * (ch + 2);
                    Fill(c, 2.5f, i == 0 ? p.Selected : p.Paper);
                    Stroke(c, 2.5f, i == 0 ? p.Teal : p.Line);
                    Fill(R(9, y + ch / 2f - 4f, 8, 8), 2, i == 1 ? p.BlueTint : p.PurpleTint);
                    Fill(R(21, y + ch / 2f - 3.4f, 40, 2.6f), 1.3f, Theme.Alpha(p.Navy, 210));
                    Fill(R(21, y + ch / 2f + 1.2f, 74 - i * 9, 1.9f), 1, p.Hint);
                    Fill(R(128, y + ch / 2f - 2.6f, 22, 5.2f), 2.6f, i == 2 ? p.OrangeTint : p.TealTint);
                    Fill(R(156, y + ch / 2f - 2.6f, 11, 5.2f), 2.6f, i == 2 ? p.ToggleOff : p.Teal);
                }
                break;
            }
            default:
            {
                float ch = (lh - 6) / 3f;
                for (int i = 0; i < 3; i++)
                {
                    float y = ly + i * (ch + 3);
                    var c = R(6, y, 172, ch);
                    if (spec.Shadows) Fill(R(7, y + 1.2f, 170, ch), ch / 2f, Theme.Alpha(Theme.Ink, 22));
                    Fill(c, ch / 2f, i == 0 ? p.Selected : p.Paper);
                    if (i == 0) Stroke(c, ch / 2f, p.Teal);
                    Theme.Dot(g, pr.X + (6 + ch / 2f) * u, pr.Y + (y + ch / 2f) * u, (ch / 2f - 2.2f) * u, i == 1 ? p.Blue : p.Pink);
                    Fill(R(8 + ch + 3, y + ch / 2f - 3f, 42, 2.8f), 1.4f, Theme.Alpha(p.Navy, 210));
                    Fill(R(8 + ch + 3, y + ch / 2f + 1.4f, 64 - i * 8, 1.9f), 1, p.Hint);
                    Fill(R(152, y + ch / 2f - 3f, 18, 6f), 3f, i == 2 ? p.ToggleOff : p.Teal);
                }
                break;
            }
        }

        // нижняя панель с вкладками
        float dy = 102 - 23;
        Fill(R(84, dy - 1.9f, 16, 0.9f), 0.45f, p.LineDark);
        var dock = R(6, dy, 172, 21);
        float drad = Math.Max(1.5f, spec.PanelRadius * 0.3f);
        Fill(dock, drad, p.Paper);
        using (var dp = Theme.RoundRect(dock, drad * u))
        {
            var s2 = g.Save();
            g.SetClip(dp, CombineMode.Intersect);
            Fill(R(6, dy, 172, 6.5f), 0, p.HeaderBg);
            g.Restore(s2);
        }
        for (int i = 0; i < 4; i++)
        {
            float tx = 10 + i * 21;
            bool act = i == 3;
            if (act && spec.CtrlRadius < 0) Fill(R(tx - 2, dy + 1, 20, 4.6f), 2.3f, p.TealTint);
            Fill(R(tx, dy + 2.6f, 15, 1.5f), 0.75f, act ? (spec.CtrlRadius < 0 ? p.TealText : p.Navy) : p.NavySoft);
            if (act && spec.CtrlRadius >= 0) Fill(R(tx, dy + 5.4f, 15, 1.1f), 0.5f, p.Teal);
        }
        Fill(R(18, dy + 4.2f, 5, 2.6f), 1.3f, p.Coral);
        Theme.Dot(g, pr.X + 12 * u, pr.Y + (dy + 10.5f) * u, 1.2f * u, p.Coral);
        Fill(R(16, dy + 9.6f, 64, 1.8f), 0.9f, p.NavySoft);
        Theme.Dot(g, pr.X + 12 * u, pr.Y + (dy + 15f) * u, 1.2f * u, p.Orange);
        Fill(R(16, dy + 14.1f, 48, 1.8f), 0.9f, p.Hint);
        Stroke(dock, drad, p.Line);

        // подвал
        using (var b = new SolidBrush(p.Paper)) g.FillRectangle(b, R(0, 104, 184, 14));
        using (var lp = new Pen(p.Line, Math.Max(1f, S * 0.8f))) g.DrawLine(lp, pr.X, pr.Y + 104 * u, pr.Right, pr.Y + 104 * u);
        Fill(R(6, 107.5f, 30, 6), ctrl, p.Beige);
        Fill(R(124, 107.5f, 18, 6), ctrl, p.Beige);
        Fill(R(146, 106.5f, 32, 8), spec.CtrlRadius < 0 ? 4 : ctrl, p.Teal);
        g.Restore(st);
        Theme.StrokeRound(g, pr, Px(7), Theme.Alpha(p.LineDark, 200), Math.Max(1f, S));
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
