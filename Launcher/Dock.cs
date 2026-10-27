// Dock.cs — нижняя панель окна (как в редакторах кода): вкладки «Проблемы», «Хуки Harmony», «Логи» и «Детали».
//  • DockPanel — полоса вкладок со счётчиками, кнопками текущей вкладки и «свернуть»; страницы — обычные элементы,
//    видна только активная;
//  • DockGrip — ручка между списком модов и панелью: потянуть — высота, двойной щелчок — свернуть/развернуть;
//  • RowsView — строки с виртуальной прокруткой (рисуются только видимые) для «Проблем», «Хуков» и «Логов».
// Цвета берутся из Theme при каждой отрисовке — смена дизайна перекрашивает панель сразу.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed class DockPanel : Canvas
{
    public sealed class Tab
    {
        public DockTab Id;
        public string Text = "", Short = "", Glyph = "", Tip = "";
        public VIcon Icon;             // векторный значок вкладки «Компактного» стиля
        public string? Badge;          // счётчик рядом с названием (null — нет)
        public Tone Tone;              // цвет счётчика: Error — красный, Warn — оранжевый, Info — синий
        public Control? Page;
    }

    public sealed class Tool
    {
        public string Key = "", Text = "", Glyph = "", Tip = "";
        public bool IsToggle, On;
        public Action? Act;
    }

    public readonly List<Tab> Tabs = new();
    public readonly List<Tool> Tools = new();   // кнопки справа в полосе — свои у каждой вкладки
    public string Note = "", NoteTip = "";      // пояснение справа (время журнала, что проверено)
    public string NoteShort = "";               // то же короче — если полное не помещается
    public DockTab Active { get; private set; } = DockTab.Details;
    public bool Collapsed { get; private set; }
    public event Action<DockTab>? Picked;
    public event Action? CollapseToggled;

    public DockPanel() { BackColor = Theme.Cream; }

    private static bool Flat => Theme.Spec.Flat;

    public int BarH => Flat ? Px(34) : Px(36);

    /// Отступ страницы от края: прямоугольная страница не должна закрывать скруглённые нижние углы
    /// (у «Компактного» углов нет — страница во всю ширину).
    private int Inset => Flat ? 0 : Math.Max(Px(1), (int)Math.Ceiling(Px(Theme.Spec.PanelRadius) * 0.3f));

    public Rectangle PageRect => new(Inset, BarH, Math.Max(0, Width - 2 * Inset), Math.Max(0, Height - BarH - Inset));

    public Tab Add(DockTab id, string text, string shortText, string glyph, Control page, string tip, VIcon icon = VIcon.None)
    {
        var t = new Tab { Id = id, Text = text, Short = shortText, Glyph = glyph, Page = page, Tip = tip, Icon = icon };
        Tabs.Add(t);
        page.Visible = false;
        Controls.Add(page);
        Relayout();
        return t;
    }

    public Tab? TabOf(DockTab id)
    {
        foreach (var t in Tabs) if (t.Id == id) return t;
        return null;
    }

    public void SetActive(DockTab id)
    {
        if (Active == id) return;
        Active = id;
        Relayout();
        Invalidate();
    }

    public void SetCollapsed(bool collapsed)
    {
        if (Collapsed == collapsed) return;
        Collapsed = collapsed;
        Relayout();
        Invalidate();
    }

    /// Разложить страницы: видна только активная, у свёрнутой панели — ни одной.
    public void Relayout()
    {
        var pr = PageRect;
        foreach (var t in Tabs)
        {
            if (t.Page == null) continue;
            bool show = !Collapsed && t.Id == Active && pr.Width > 0 && pr.Height > Px(8);
            if (show && t.Page.Bounds != pr) t.Page.Bounds = pr;
            if (t.Page.Visible != show) t.Page.Visible = show;
        }
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Relayout(); }

    public static (Color Bg, Color Fg) BadgeColors(Tone t) => t switch
    {
        Tone.Error => (Theme.Coral, Color.White),
        Tone.Warn => (Theme.OrangeTint, Theme.OrangeText),
        Tone.Info => (Theme.BlueTint, Theme.BlueText),
        Tone.Ok => (Theme.TealTint, Theme.TealText),
        _ => (Theme.Beige, Theme.NavySoft),
    };

    /// Заливка под наведённой кнопкой полосы: чуть темнее (в тёмной теме — светлее) фона полосы.
    private static Color HoverBg => Theme.Mix(Theme.HeaderBg, Theme.Navy, 0.08f);

    private int BadgeW(string s) => Math.Max(Px(18), Theme.Measure(s, Theme.Section).Width + Px(10));

    /// Счётчик вкладки «Компактного» стиля: маленький, тонированный (ошибки — красным, предупреждения — жёлтым).
    private static (Color Bg, Color Fg) FlatBadgeColors(Tone t) => t switch
    {
        Tone.Error => (Theme.CoralTint, Theme.CoralText),
        Tone.Warn => (Theme.OrangeTint, Theme.OrangeText),
        Tone.Info => (Theme.BlueTint, Theme.BlueText),
        Tone.Ok => (Theme.TealTint, Theme.TealText),
        _ => (Theme.LineDark, Theme.NavySoft),
    };

    private int FlatBadgeW(string s) => Math.Max(Px(18), Theme.Measure(s, Theme.Section).Width + Px(8));

    private void DrawBadge(Graphics g, int x, int barH, string s, Tone tone)
    {
        var (bg, fg) = BadgeColors(tone);
        var r = new Rectangle(x, (barH - Px(18)) / 2, BadgeW(s), Px(18));
        Theme.FillRound(g, r, r.Height / 2f, bg);
        Theme.Text(g, s, Theme.Section, r, fg, TextFormatFlags.HorizontalCenter);
    }

    /// Квадратик-галочка у переключателей («Только ошибки»).
    private void DrawCheck(Graphics g, Rectangle r, bool on, bool hover)
    {
        float rad = Math.Max(2f, Px(3));
        if (on)
        {
            Theme.FillRound(g, r, rad, hover ? Theme.TealDark : Theme.Teal);
            Theme.Glyph(g, Theme.GCheck, r, Color.White, Theme.IconsSmall);
        }
        else Theme.StrokeRound(g, r, rad, hover ? Theme.NavySoft : Theme.LineDark, Math.Max(1f, S * 1.2f));
    }

    protected override void Draw(Graphics g)
    {
        float rad = Px(Theme.Spec.PanelRadius);
        bool flat = Flat;
        int h = Collapsed ? Height : BarH;
        if (flat)
        {   // «Компактный»: полоса во всю ширину, снизу — линия; страница закрывает остальное
            int one = Math.Max(1, Px(1));
            using (var b = new SolidBrush(Theme.HeaderBg)) g.FillRectangle(b, 0, 0, Width, Height);
            if (!Collapsed) using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, 0, h - one, Width, one);
            h -= Collapsed ? 0 : one;
        }
        else
        {
            var panel = new RectangleF(0, 0, Width, Height);
            using (var path = Theme.RoundRect(panel, rad))
            {
                using (var b = new SolidBrush(Theme.Paper)) g.FillPath(b, path);
                var st = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                using (var b = new SolidBrush(Theme.HeaderBg)) g.FillRectangle(b, 0, 0, Width, h);
                if (!Collapsed) using (var p = new Pen(Theme.Line, Math.Max(1f, S))) g.DrawLine(p, 0, h - 1, Width, h - 1);
                g.Restore(st);
            }
            Theme.StrokeRound(g, panel, rad, Theme.Line, Math.Max(1f, S));
        }

        // справа: «свернуть/развернуть»
        var cr = new Rectangle(Width - Px(8) - Px(30), (h - Px(26)) / 2, Px(30), Px(26));
        bool chv = IsHover("collapse");
        if (chv) Theme.FillRound(g, cr, Theme.Radius(cr.Height, S), HoverBg);
        Theme.Glyph(g, Collapsed ? Theme.GChevronUp : Theme.GChevronDown, cr, chv ? Theme.Navy : Theme.NavySoft, Theme.IconsSmall);
        AddHit(cr, "collapse", () => CollapseToggled?.Invoke(), Collapsed ? "Развернуть панель (Ctrl+J)" : "Свернуть панель (Ctrl+J)");
        int right = cr.X - Px(6), left = flat ? Px(8) : Px(6);

        // что влезает: вкладки с полными названиями → короткими → без значков; кнопки — с подписями → только значки
        var font = Theme.ButtonFont;
        bool shortT = false, glyphs = true, toolIcons = false;
        int TabW(Tab t)
        {
            if (flat)
            {
                int fw = Px(13) * 2 + Theme.Measure(shortT ? t.Short : t.Text, font).Width;
                if (glyphs && (t.Icon != VIcon.None || t.Glyph.Length > 0)) fw += Px(12) + Px(9);
                if (!string.IsNullOrEmpty(t.Badge)) fw += Px(7) + FlatBadgeW(t.Badge!);
                return fw + Px(4);
            }
            int w = Px(12) * 2 + Theme.Measure(shortT ? t.Short : t.Text, font).Width;
            if (glyphs && t.Glyph.Length > 0) w += Px(22);
            if (!string.IsNullOrEmpty(t.Badge)) w += Px(7) + BadgeW(t.Badge!);
            return w;
        }
        int ToolW(Tool t) => toolIcons ? Px(30) : Px(8) + Px(20) + Theme.Measure(t.Text, Theme.Small).Width + Px(10);
        int Need() => Tabs.Sum(TabW) + Tools.Sum(t => ToolW(t) + Px(4));
        int avail = right - left;
        if (Need() > avail) toolIcons = true;
        if (Need() > avail) shortT = true;
        if (Need() > avail) glyphs = false;

        // кнопки вкладки — справа налево
        int tx = right;
        for (int i = Tools.Count - 1; i >= 0; i--)
        {
            var t = Tools[i];
            int w = ToolW(t);
            var r = new Rectangle(tx - w, (h - Px(26)) / 2, w, Px(26));
            if (r.X < left) break;
            string key = "tool:" + t.Key;
            bool hv = IsHover(key), dn = IsDown(key);
            float rr = Theme.Radius(r.Height, S);
            if (dn) Theme.FillRound(g, r, rr, Theme.Mix(HoverBg, Theme.Navy, 0.06f));
            else if (hv) Theme.FillRound(g, r, rr, HoverBg);
            Color fg = t.IsToggle && t.On ? Theme.Navy : hv ? Theme.Navy : Theme.NavySoft;
            if (toolIcons)
            {
                if (t.IsToggle && t.On) Theme.FillRound(g, r, rr, Theme.TealTint);
                Theme.Glyph(g, t.Glyph, r, t.IsToggle && t.On ? Theme.TealText : fg, Theme.IconsSmall);
            }
            else
            {
                int gx = r.X + Px(8);
                if (t.IsToggle) DrawCheck(g, new Rectangle(gx + Px(1), r.Y + (r.Height - Px(14)) / 2, Px(14), Px(14)), t.On, hv);
                else Theme.Glyph(g, t.Glyph, new Rectangle(gx, r.Y, Px(16), r.Height), fg, Theme.IconsSmall);
                Theme.Text(g, t.Text, Theme.Small, new Rectangle(gx + Px(20), r.Y, r.Right - gx - Px(24), r.Height), fg);
            }
            var tool = t;
            string tip = toolIcons ? t.Text + (t.Tip.Length > 0 ? ": " + t.Tip : "") : t.Tip;
            AddHit(r, key, () => tool.Act?.Invoke(), tip.Length > 0 ? tip : null);
            tx = r.X - Px(4);
        }

        // вкладки — слева направо
        bool capsule = Theme.Spec.CtrlRadius < 0;
        int x = left;
        foreach (var t in Tabs)
        {
            int w = TabW(t);
            if (x + w > tx && x > left) break;
            if (flat) { DrawFlatTab(g, t, new Rectangle(x, 0, w - Px(4), h), shortT, glyphs); x += w; continue; }
            var r = new Rectangle(x, 0, w, h);
            string key = "tab:" + (int)t.Id;
            bool act = t.Id == Active && !Collapsed, hv = IsHover(key);
            var pill = new Rectangle(r.X + Px(2), r.Y + Px(5), r.Width - Px(4), r.Height - Px(10));
            if (act && capsule) Theme.FillRound(g, pill, pill.Height / 2f, Theme.TealTint);
            else if (hv) Theme.FillRound(g, pill, Theme.Radius(pill.Height, S), HoverBg);
            Color fg = act ? (capsule ? Theme.TealText : Theme.Navy) : hv ? Theme.Navy : Theme.NavySoft;
            Color gc = act ? (capsule ? Theme.TealText : Theme.Teal) : Theme.Hint;
            int cx = r.X + Px(12);
            if (glyphs && t.Glyph.Length > 0)
            {
                Theme.Glyph(g, t.Glyph, new Rectangle(cx - Px(1), r.Y, Px(18), h), gc, Theme.IconsSmall);
                cx += Px(22);
            }
            string s = shortT ? t.Short : t.Text;
            int sw = Theme.Measure(s, font).Width;
            Theme.Text(g, s, font, new Rectangle(cx, r.Y, sw + Px(2), h), fg);
            cx += sw;
            if (!string.IsNullOrEmpty(t.Badge)) DrawBadge(g, cx + Px(7), h, t.Badge!, t.Tone);
            if (act && !capsule) Theme.FillRound(g, new RectangleF(r.X + Px(10), h - Px(3), r.Width - Px(20), Px(3)), Px(1.5f), Theme.Teal);
            var tab = t;
            AddHit(r, key, () => Picked?.Invoke(tab.Id), t.Tip.Length > 0 ? t.Tip : null);
            x += w;
        }

        // пояснение — между вкладками и кнопками, если есть место
        if (Note.Length > 0)
        {
            var nr = new Rectangle(x + Px(14), 0, tx - Px(10) - (x + Px(14)), h);
            if (nr.Width >= Px(90))
            {
                // полное → короткое; если есть короткое, но и оно не влезает — не показываем вовсе (обрезанное хуже пустого)
                string note = Note;
                if (NoteShort.Length > 0 && Theme.Measure(note, Theme.Small).Width > nr.Width)
                    note = Theme.Measure(NoteShort, Theme.Small).Width <= nr.Width ? NoteShort : "";
                if (note.Length > 0)
                {
                    Theme.Text(g, note, Theme.Small, nr, Theme.Hint, TextFormatFlags.Right);
                    bool cut = note != Note || Theme.Measure(note, Theme.Small).Width > nr.Width;
                    if (cut || NoteTip.Length > 0) AddHit(nr, "note", null, NoteTip.Length > 0 ? NoteTip : Note, null, false);
                }
            }
        }
    }

    /// Вкладка «Компактного» стиля: значок, подпись, счётчик; активная — светлее и подчёркнута бирюзой.
    private void DrawFlatTab(Graphics g, Tab t, Rectangle r, bool shortT, bool glyphs)
    {
        string key = "tab:" + (int)t.Id;
        bool act = t.Id == Active && !Collapsed, hv = IsHover(key);
        Color fg = act || hv ? Theme.Navy : Theme.NavySoft;
        int x = r.X + Px(13);
        if (glyphs && t.Icon != VIcon.None)
        {
            int isz = Px(12);
            Theme.Icon(g, t.Icon, new RectangleF(x, r.Y + (r.Height - isz) / 2f, isz, isz), fg);
            x += isz + Px(9);
        }
        else if (glyphs && t.Glyph.Length > 0)
        {
            Theme.Glyph(g, t.Glyph, new Rectangle(x - Px(2), r.Y, Px(16), r.Height), fg, Theme.IconsSmall);
            x += Px(12) + Px(9);
        }
        string s = shortT ? t.Short : t.Text;
        int sw = Theme.Measure(s, Theme.ButtonFont).Width;
        Theme.Text(g, s, act ? Theme.ButtonFont : Theme.Body, new Rectangle(x, r.Y, sw + Px(2), r.Height), fg);
        x += sw;
        if (!string.IsNullOrEmpty(t.Badge))
        {
            var (bg, bf) = FlatBadgeColors(t.Tone);
            var br = new Rectangle(x + Px(7), r.Y + (r.Height - Px(15)) / 2, FlatBadgeW(t.Badge!), Px(15));
            Theme.FillRound(g, br, Px(7), bg);
            Theme.Text(g, t.Badge, Theme.Section, br, bf, TextFormatFlags.HorizontalCenter);
        }
        if (act) using (var b = new SolidBrush(Theme.Teal)) g.FillRectangle(b, r.X, r.Bottom - Px(2), r.Width, Px(2));
        var tab = t;
        AddHit(r, key, () => Picked?.Invoke(tab.Id), t.Tip.Length > 0 ? t.Tip : null);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // двойной щелчок по пустому месту полосы — свернуть или развернуть
        if (e.Button == MouseButtons.Left && e.Y < BarH && HitAt(e.Location) == null) CollapseToggled?.Invoke();
    }
}

/// Ручка между списком модов и нижней панелью.
internal sealed class DockGrip : Surface
{
    private bool _hover, _drag;
    private int _startY;
    public event Action? DragStarted;
    public event Action<int>? Dragged;     // смещение указателя от начала, px (вниз — больше)
    public event Action? DragEnded;
    public event Action? Toggle;           // двойной щелчок

    public DockGrip()
    {
        Cursor = Cursors.SizeNS;
        Height = Px(10);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _drag = true;
        _startY = Control.MousePosition.Y;   // в экранных координатах: сама ручка во время перетаскивания сдвигается
        Capture = true;
        DragStarted?.Invoke();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) Dragged?.Invoke(Control.MousePosition.Y - _startY);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        End();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) End();
    }

    private void End()
    {
        if (!_drag) return;
        _drag = false;
        Capture = false;
        DragEnded?.Invoke();
        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); Toggle?.Invoke(); }

    protected override void Draw(Graphics g)
    {
        if (Theme.Spec.Flat)
        {   // «Компактный»: тонкая линия у нижнего края; при наведении и перетаскивании — бирюзовая
            int lh = _drag || _hover ? Math.Max(2, Px(2)) : Math.Max(1, Px(1));
            using var lb = new SolidBrush(_drag ? Theme.Teal : _hover ? Theme.Mix(Theme.Line, Theme.Teal, 0.55f) : Theme.Line);
            g.FillRectangle(lb, 0, Height - lh, Width, lh);
            return;
        }
        int w = Px(44), h = Math.Max(2, Px(4));
        var r = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
        Theme.FillRound(g, r, h / 2f, _drag ? Theme.Teal : _hover ? Theme.LineDark : Theme.Line);
    }
}

/// Список строк для вкладок нижней панели: виртуальная прокрутка, выбор стрелками, Ctrl+C — копировать строку.
internal sealed class RowsView : Canvas
{
    public sealed class Row
    {
        public string Key = "";
        public bool Header;            // заголовок раздела: Title — прописными, Right — счётчик
        public string Glyph = "";
        public Tone Tone;              // цвет значка
        public string Lead = "";       // узкий столбец слева (время в журнале)
        public string Title = "";
        public Tone TitleTone;         // Neutral — обычный цвет текста
        public string Text = "";
        public Tone TextTone;
        public bool Dim;               // второстепенная строка (продолжение сообщения, стек вызовов)
        public string Badge = "";
        public Tone BadgeTone;
        public string Right = "";
        public bool Mark;              // относится к выбранному моду — полоска слева
        public string? Tip;
        public string? ModId;
        public object? Tag;
        public int Indent;
        public string? Copy;           // текст для Ctrl+C (по умолчанию — заголовок и текст)
        public VIcon Icon;             // векторный значок вместо глифа (высокие строки «Компактного» стиля)
        public bool Rich;              // в тексте разметка: **жирный**, `код`
        public List<(string Text, Action Act)>? Actions;   // кнопки справа (высокие строки)
    }

    private List<Row> _rows = new();
    private int _scroll, _sel = -1, _dragOffset;
    private bool _dragThumb, _hoverBar;

    public IReadOnlyList<Row> Rows => _rows;
    public bool Mono;                  // моноширинный шрифт (журнал)
    public bool StickToEnd;            // держать прокрутку у конца, если она уже там (журнал)
    public int LeadW;                  // ширина столбца Lead, px при 96 dpi (0 — нет столбца)
    public int TitleW;                 // ширина столбца заголовка, px при 96 dpi (0 — по тексту)
    /// Высокие строки (46 px) — вкладки «Проблемы» и «Изменения»: значок, название, текст с разметкой и кнопки справа.
    /// В «Компактном» — во всю ширину с линиями между строками, в остальных стилях — скруглённые плашки.
    public bool Tall { get => _tall; set { if (_tall == value) return; _tall = value; ClampScroll(); Invalidate(); } }
    private bool _tall;
    // Заглушка пустого списка. Смена текста сразу перерисовывает панель: фоновая проверка совместимости
    // часто заканчивается без новых строк, и без перерисовки на вкладке оставалось «Идёт проверка…».
    public string EmptyTitle { get => _emptyTitle; set => SetEmpty(ref _emptyTitle, value ?? ""); }
    public string EmptyText { get => _emptyText; set => SetEmpty(ref _emptyText, value ?? ""); }
    public bool EmptyHappy { get => _emptyHappy; set { if (_emptyHappy == value) return; _emptyHappy = value; if (_rows.Count == 0) Invalidate(); } }
    private string _emptyTitle = "", _emptyText = "";
    private bool _emptyHappy;
    public event Action<Row>? RowClicked;
    public event Action<Row>? RowActivated;   // двойной щелчок или Enter
    public Func<Row, string?>? ModName;       // подпись пункта меню «Перейти к …»

    public RowsView()
    {
        BackColor = Theme.Paper;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public Row? SelectedRow => _sel >= 0 && _sel < _rows.Count ? _rows[_sel] : null;
    public int Count => _rows.Count;

    private Font RowFont => Mono ? Theme.Mono : Theme.Body;
    public int RowH => Mono ? Math.Max(Px(20), Theme.LineHeight(Theme.Mono) + Px(6)) : _tall ? Px(46) : Px(28);
    private int Pad => _tall && Theme.Spec.Flat ? 0 : Px(4);
    private int BarW => Px(10);
    private int ContentH => _rows.Count * RowH + Pad * 2;
    private int MaxScroll => Math.Max(0, ContentH - Height);
    private bool HasBar => ContentH > Height && Height > 0;
    private bool AtEnd => _scroll >= MaxScroll - Px(2);
    /// Журнал: прокрутка «приклеена» к концу, пока её не увели вверх (флаг, а не геометрия: у ещё не размещённой
    /// панели высота 0 и «конец» по размерам не определить).
    private bool _stuck = true;

    /// После прокрутки пользователем: у конца — снова следить за новыми строками.
    private void UserScrolled() { ClampScroll(); if (StickToEnd) _stuck = AtEnd; }

    private Rectangle RowRect(int i) => new(0, Pad + i * RowH - _scroll, Width - (HasBar ? BarW : 0), RowH);

    /// Новые строки. Выбранная строка (по Key) остаётся выбранной; у журнала прокрутка остаётся у конца.
    public void SetRows(List<Row> rows, bool resetScroll = false)
    {
        string? key = SelectedRow?.Key;
        _rows = rows;
        _sel = key == null ? -1 : rows.FindIndex(r => !r.Header && r.Key == key);
        if (resetScroll) { _scroll = 0; _stuck = true; }
        if (StickToEnd && _stuck) _scroll = MaxScroll;
        ClampScroll();
        Invalidate();
    }

    public void ScrollToEnd() { _scroll = MaxScroll; _stuck = true; Invalidate(); }

    private void ClampScroll() => _scroll = Math.Clamp(_scroll, 0, MaxScroll);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (StickToEnd && _stuck) _scroll = MaxScroll;
        ClampScroll();
    }

    private void EnsureVisible(int i)
    {
        if (i < 0 || i >= _rows.Count) return;
        int top = Pad + i * RowH, bottom = top + RowH;
        if (top - Pad < _scroll) _scroll = top - Pad;
        else if (bottom + Pad > _scroll + Height) _scroll = bottom + Pad - Height;
        UserScrolled();
    }

    private void SelectIndex(int i, bool click)
    {
        if (i < 0 || i >= _rows.Count || _rows[i].Header) return;
        if (_sel != i) { _sel = i; EnsureVisible(i); Invalidate(); }
        if (click) RowClicked?.Invoke(_rows[i]);
    }

    private static Color Fg(Tone t) => t switch
    {
        Tone.Info => Theme.BlueText,
        Tone.Ok => Theme.TealText,
        Tone.Warn => Theme.OrangeText,
        Tone.Error => Theme.CoralText,
        _ => Theme.Navy,
    };

    private static Color Icon(Tone t) => t switch
    {
        Tone.Info => Theme.Blue,
        Tone.Ok => Theme.Teal,
        Tone.Warn => Theme.Orange,
        Tone.Error => Theme.Coral,
        _ => Theme.Hint,
    };

    protected override void InvalidateHot(string? a, string? b) { InvalidateKey(a); InvalidateKey(b); }

    private void InvalidateKey(string? key)
    {
        if (key == null) return;
        if (RowIndex(key) is int i) Invalidate(RowRect(i));
        else Invalidate();
    }

    /// Номер строки из ключа зоны: «row:5» — строка, «row:5:a:1» — её кнопка.
    private static int? RowIndex(string key)
    {
        if (!key.StartsWith("row:", StringComparison.Ordinal)) return null;
        var rest = key.AsSpan(4);
        int c = rest.IndexOf(':');
        return int.TryParse(c >= 0 ? rest.Slice(0, c) : rest, out int i) ? i : null;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (_sel >= 0) Invalidate(RowRect(_sel)); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (_sel >= 0) Invalidate(RowRect(_sel)); }

    protected override void Draw(Graphics g)
    {
        if (_rows.Count == 0) { DrawEmpty(g); return; }
        ClampScroll();
        int rh = RowH;
        int first = Math.Max(0, (_scroll - Pad) / rh), last = Math.Min(_rows.Count - 1, (_scroll - Pad + Height) / rh);
        var font = RowFont;
        int leadW = LeadW > 0 ? Px(LeadW) : 0, titleW = TitleW > 0 ? Px(TitleW) : 0;
        for (int i = first; i <= last; i++) DrawRow(g, _rows[i], i, RowRect(i), font, leadW, titleW);
        if (HasBar)
        {
            var tr = ThumbRect();
            Theme.FillRound(g, tr, tr.Width / 2f, _dragThumb ? Theme.NavySoft : _hoverBar ? Theme.LineDark : Theme.Line);
        }
    }

    private void DrawRow(Graphics g, Row row, int i, Rectangle r, Font font, int leadW, int titleW)
    {
        string key = "row:" + i;
        if (row.Header)
        {
            int hx = r.X + Px(12) + row.Indent * Px(16);
            var ts = Theme.Measure(row.Title, Theme.Section);
            int ty = r.Y + Px(6);
            Theme.Text(g, row.Title, Theme.Section, new Rectangle(hx, ty, ts.Width + Px(2), r.Height - Px(6)), Theme.Hint);
            int lx = hx + ts.Width + Px(8);
            if (row.Right.Length > 0)
            {
                var rs = Theme.Measure(row.Right, Theme.Section);
                Theme.Text(g, row.Right, Theme.Section, new Rectangle(lx, ty, rs.Width + Px(2), r.Height - Px(6)), Theme.NavySoft);
                lx += rs.Width + Px(8);
            }
            using (var p = new Pen(Theme.Line, Math.Max(1f, S)))
                if (r.Right - Px(12) > lx + Px(10)) g.DrawLine(p, lx, ty + (r.Height - Px(6)) / 2, r.Right - Px(12), ty + (r.Height - Px(6)) / 2);
            if (row.Tip != null) AddHit(r, key, null, row.Tip, null, false);
            return;
        }

        if (_tall) { DrawTallRow(g, row, i, r, titleW); return; }
        bool sel = i == _sel, hov = IsHover(key);
        var bg = new Rectangle(r.X + Px(4), r.Y + Px(1), r.Width - Px(8), r.Height - Px(2));
        float rad = Theme.Radius(bg.Height, S);
        if (sel) Theme.FillRound(g, bg, rad, Focused ? Theme.Selected : Theme.Mix(Theme.Selected, Theme.Paper, 0.35f));
        else if (hov) Theme.FillRound(g, bg, rad, Theme.RowHover);
        if (sel && Focused) Theme.StrokeRound(g, bg, rad, Theme.Alpha(Theme.Teal, 120), Math.Max(1f, S));
        if (row.Mark) Theme.FillRound(g, new RectangleF(r.X + Px(5), r.Y + Px(5), Px(3), r.Height - Px(10)), Px(1.5f), Theme.Teal);

        int x = r.X + Px(14) + row.Indent * Px(18), right = r.Right - Px(12);
        if (leadW > 0)
        {
            if (row.Lead.Length > 0) Theme.Text(g, row.Lead, Mono ? Theme.Mono : Theme.Small, new Rectangle(x, r.Y, leadW, r.Height), Theme.Hint);
            x += leadW + Px(8);
        }
        if (row.Glyph.Length > 0) Theme.Glyph(g, row.Glyph, new Rectangle(x - Px(1), r.Y, Px(18), r.Height), Icon(row.Tone), Theme.IconsSmall);
        x += Px(22);

        // справа налево: подпись, затем бейдж. Подпись (пространство имён у хуков) уступает место названию и тексту:
        // полная — если всё помещается, иначе ужимается (но не меньше ~90 px) и не больше трети строки
        Font tf = Mono ? Theme.Mono : Theme.ButtonFont;
        if (row.Right.Length > 0)
        {
            int need = Theme.Measure(row.Right, Theme.Small).Width + Px(2);
            int body = (row.Title.Length > 0 ? Theme.Measure(row.Title, tf).Width + Px(12) : 0) + (row.Text.Length > 0 ? Theme.Measure(row.Text, font).Width : 0)
                     + (row.Badge.Length > 0 ? Theme.Measure(row.Badge, Theme.SmallBold).Width + Px(26) : 0);
            int free = right - x - body - Px(10);
            int rw = Math.Min(Math.Max(free, Math.Min(need, Px(90))), Math.Min(need, Math.Max(0, (right - x) / 3)));
            if (rw > 0)
            {
                // не помещается — хвост пространства имён: «…SlimeRancher.Player»
                string rt = row.Right;
                if (need > rw && rt.IndexOf('.') > 0)
                {
                    var parts = rt.Split('.');
                    for (int k = 1; k < parts.Length; k++)
                    {
                        string cand = "…" + string.Join(".", parts, k, parts.Length - k);
                        if (Theme.Measure(cand, Theme.Small).Width + Px(2) <= rw || k == parts.Length - 1) { rt = cand; break; }
                    }
                }
                Theme.Text(g, rt, Theme.Small, new Rectangle(right - rw, r.Y, rw, r.Height), Theme.Hint, TextFormatFlags.Right);
                right -= rw + Px(10);
            }
        }
        if (row.Badge.Length > 0)
        {
            var (bb, bf) = DockPanel.BadgeColors(row.BadgeTone);
            int bh = Px(20), bw = Theme.Measure(row.Badge, Theme.SmallBold).Width + Px(16);
            if (bw < (right - x) / 2)
            {
                var br = new Rectangle(right - bw, r.Y + (r.Height - bh) / 2, bw, bh);
                Theme.FillRound(g, br, Theme.Radius(bh, S), bb);
                Theme.Text(g, row.Badge, Theme.SmallBold, br, bf, TextFormatFlags.HorizontalCenter);
                right = br.X - Px(10);
            }
        }

        if (row.Title.Length > 0)
        {
            int tw = titleW > 0 ? titleW : Math.Min(Theme.Measure(row.Title, tf).Width + Px(2), Math.Max(Px(40), (right - x) * 45 / 100));
            tw = Math.Min(tw, Math.Max(0, right - x));
            Color tc = row.TitleTone == Tone.Neutral ? Theme.Navy : Fg(row.TitleTone);
            Theme.Text(g, row.Title, tf, new Rectangle(x, r.Y, tw, r.Height), row.Dim ? Theme.NavySoft : tc);
            x += tw + Px(10);
        }
        if (row.Text.Length > 0 && right > x)
        {
            Color c = row.Dim ? Theme.Hint : row.TextTone == Tone.Neutral ? (Mono ? Theme.Navy : Theme.NavySoft) : Fg(row.TextTone);
            Theme.Text(g, row.Text, font, new Rectangle(x, r.Y, right - x, r.Height), c);
        }
        int idx = i;
        AddHit(Rectangle.Intersect(r, ClientRectangle), key, () => SelectIndex(idx, true), row.Tip, pt => Menu(idx, pt), false);
    }

    /// Высокая строка: значок, название (колонка TitleW), текст с разметкой, кнопки справа; снизу — тонкая линия.
    private void DrawTallRow(Graphics g, Row row, int i, Rectangle r, int titleW)
    {
        string key = "row:" + i;
        bool sel = i == _sel, hov = HoverKey != null && (HoverKey == key || HoverKey.StartsWith(key + ":", StringComparison.Ordinal));
        bool flat = Theme.Spec.Flat, capsule = Theme.Spec.CtrlRadius < 0, fluent = Theme.Design == DesignKind.Fluent;
        int one = Math.Max(1, Px(1)), inset = flat ? 0 : Px(6);
        if (flat)
        {
            if (sel || hov) using (var b = new SolidBrush(sel ? Theme.TealTint : Theme.Mix(BackColor, Theme.Navy, 0.04f))) g.FillRectangle(b, r);
            using (var b = new SolidBrush(Theme.Mix(BackColor, Theme.Line, 0.47f))) g.FillRectangle(b, r.X, r.Bottom - one, r.Width, one);
            if (sel && Focused) using (var b = new SolidBrush(Theme.Teal)) g.FillRectangle(b, r.X, r.Y, Px(2), r.Height - one);
            else if (row.Mark) using (var b = new SolidBrush(Theme.Alpha(Theme.Teal, 150))) g.FillRectangle(b, r.X, r.Y, Px(2), r.Height - one);
        }
        else
        {   // плашка с отступами: у Fluent — скругление карточек и акцентная черта слева, у «Слаймового» — капсула
            var pr = new Rectangle(r.X + Px(4), r.Y + Px(2), r.Width - Px(8), r.Height - Px(4));
            float prad = capsule ? pr.Height / 2f : Px(Math.Max(4, Theme.Spec.CardRadius));
            if (sel || hov) Theme.FillRound(g, pr, prad, sel ? Theme.TealTint : Theme.RowHover);
            if (sel && Focused) Theme.StrokeRound(g, pr, prad, Theme.Alpha(Theme.Teal, 110), Math.Max(1f, S));
            if (row.Mark || sel && Focused)
                Theme.FillRound(g, new RectangleF(pr.X + (capsule ? Px(9) : Px(1)), pr.Y + (pr.Height - Px(16)) / 2f, Px(3), Px(16)), Px(1.5f),
                    sel && Focused ? Theme.Teal : Theme.Alpha(Theme.Teal, 150));
            one = 0;
        }
        var body = new Rectangle(r.X, r.Y, r.Width, r.Height - one);
        int isz = Px(15);
        var ir = new RectangleF(r.X + inset + Px(20) - isz / 2f, body.Y + (body.Height - isz) / 2f, isz, isz);
        if (row.Icon != VIcon.None) Theme.Icon(g, row.Icon, ir, Icon(row.Tone));
        else if (row.Glyph.Length > 0) Theme.Glyph(g, row.Glyph, Rectangle.Round(RectangleF.Inflate(ir, Px(3), Px(3))), Icon(row.Tone), Theme.IconsSmall);

        // кнопки — справа налево, пока остаётся место под название
        int tx = r.X + inset + Px(46), colW = titleW > 0 ? titleW : Px(190);
        int right = r.Right - Px(12) - inset;
        var acts = row.Actions;
        int shownFrom = acts?.Count ?? 0;
        var rects = new Rectangle[shownFrom];
        for (int j = shownFrom - 1; j >= 0; j--)
        {
            int aw = Theme.Measure(acts![j].Text, Theme.ButtonFont).Width + Px(24);
            var ar = new Rectangle(right - aw, body.Y + (body.Height - Px(26)) / 2, aw, Px(26));
            if (ar.X < tx + Math.Min(colW, Px(120))) break;
            rects[j] = ar;
            shownFrom = j;
            right = ar.X - Px(6);
        }
        int tw = Math.Max(0, Math.Min(colW, right - Px(8) - tx));
        Theme.Text(g, row.Title, Theme.ButtonFont, new Rectangle(tx, body.Y, tw, body.Height), row.TitleTone == Tone.Neutral ? Theme.Navy : Fg(row.TitleTone));
        int x = tx + colW + Px(10);
        if (row.Text.Length > 0 && right - Px(12) > x)
        {
            var trc = new Rectangle(x, body.Y, right - Px(12) - x, body.Height);
            Color c = row.TextTone == Tone.Neutral ? Theme.NavySoft : Fg(row.TextTone);
            if (row.Rich) Theme.Rich(g, row.Text, Theme.Body, trc, c, S, Theme.Navy);
            else Theme.Text(g, row.Text, Theme.Body, trc, c);
        }
        int idx = i;
        AddHit(Rectangle.Intersect(r, ClientRectangle), key, () => SelectIndex(idx, true), row.Tip, pt => Menu(idx, pt), false);
        for (int j = shownFrom; j < (acts?.Count ?? 0); j++)
        {
            var ar = rects[j];
            string ak = key + ":a:" + j;
            bool ah = IsHover(ak), ad = IsDown(ak);
            if (flat)
            {
                float rad = Math.Max(2f, Px(4));
                Theme.FillRound(g, ar, rad, ad ? Theme.BeigeDark : ah ? Theme.Mix(Theme.Beige, Theme.BeigeDark, 0.6f) : Theme.Beige);
                Theme.StrokeRound(g, ar, rad, ah ? Theme.Mix(Theme.LineDark, Theme.NavySoft, 0.35f) : Theme.LineDark, Math.Max(1f, S));
            }
            else if (fluent)
            {   // обычная кнопка Windows 11: белая, с тонкой рамкой
                float rad = Theme.Radius(ar.Height, S);
                Theme.FillRound(g, ar, rad, ad ? Theme.Beige : ah ? Theme.Mix(Theme.Paper, Theme.Beige, 0.6f) : Theme.Paper);
                Theme.StrokeRound(g, ar, rad, ah ? Theme.LineDark : Theme.Mix(Theme.Line, Theme.LineDark, 0.5f), Math.Max(1f, S));
            }
            else
            {   // «Слаймовый»: бежевая капсула без рамки
                Theme.FillRound(g, ar, ar.Height / 2f, ad ? Theme.BeigeDark : ah ? Theme.Mix(Theme.Beige, Theme.BeigeDark, 0.6f) : Theme.Beige);
            }
            Theme.Text(g, acts![j].Text, Theme.ButtonFont, ar, Theme.Navy, TextFormatFlags.HorizontalCenter);
            var act = acts[j].Act;
            AddHit(ar, ak, () => { SelectIndex(idx, false); act(); });
        }
    }

    private void SetEmpty(ref string field, string value)
    {
        if (field == value) return;
        field = value;
        if (_rows.Count == 0) Invalidate();
    }

    private void DrawEmpty(Graphics g)
    {
        if (EmptyTitle.Length == 0) return;
        int cy = Height / 2;
        bool room = Height >= Px(120);
        int ty = room ? cy - Px(4) : cy - Px(22);
        if (room)
        {
            int d = Px(36);
            var c = new Rectangle((Width - d) / 2, ty - d - Px(10), d, d);
            Theme.FillRound(g, c, d / 2f, EmptyHappy ? Theme.TealTint : Theme.Beige);
            Theme.Glyph(g, EmptyHappy ? Theme.GCheck : Theme.GInfo, c, EmptyHappy ? Theme.TealText : Theme.NavySoft);
        }
        Theme.Text(g, EmptyTitle, Theme.Strong, new Rectangle(Px(16), ty, Width - Px(32), Px(24)), Theme.Navy, TextFormatFlags.HorizontalCenter);
        if (EmptyText.Length > 0)
            Theme.Wrap(g, EmptyText, Theme.Body, new Rectangle(Math.Max(Px(16), (Width - Px(620)) / 2), ty + Px(26), Math.Min(Width - Px(32), Px(620)), Px(44)), Theme.NavySoft, true);
    }

    // ───────── прокрутка ─────────

    private Rectangle ThumbRect()
    {
        if (!HasBar) return Rectangle.Empty;
        int track = Height - Px(6);
        int th = Math.Min(track, Math.Max(Px(30), (int)((long)track * Height / Math.Max(1, ContentH))));
        int ty = Px(3) + (int)((long)(track - th) * _scroll / Math.Max(1, MaxScroll));
        return new Rectangle(Width - BarW + Px(3), ty, BarW - Px(5), th);
    }

    private bool OnBar(Point p) => HasBar && p.X >= Width - BarW - Px(2);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int before = _scroll;
        _scroll -= e.Delta * RowH * 3 / 120;
        UserScrolled();
        if (_scroll != before) { Invalidate(); Update(); UpdateHover(PointToClient(MousePosition)); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Focused) Focus();
        if (e.Button == MouseButtons.Left && OnBar(e.Location))
        {
            var tr = ThumbRect();
            if (tr.Contains(e.Location)) { _dragThumb = true; _dragOffset = e.Y - tr.Y; Capture = true; }
            else { _scroll += (e.Y < tr.Y ? -1 : 1) * Math.Max(RowH, Height * 9 / 10); UserScrolled(); }
            Invalidate();
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragThumb)
        {
            var tr = ThumbRect();
            int track = Height - Px(6) - tr.Height;
            int before = _scroll;
            _scroll = track <= 0 ? 0 : (int)((long)(e.Y - _dragOffset - Px(3)) * MaxScroll / track);
            UserScrolled();
            if (_scroll != before) Invalidate();
            return;
        }
        bool hb = OnBar(e.Location);
        if (hb != _hoverBar) { _hoverBar = hb; Invalidate(new Rectangle(Width - BarW - Px(2), 0, BarW + Px(2), Height)); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragThumb) { _dragThumb = false; Capture = false; Invalidate(); return; }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBar) { _hoverBar = false; Invalidate(new Rectangle(Width - BarW - Px(2), 0, BarW + Px(2), Height)); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || OnBar(e.Location)) return;
        var h = HitAt(e.Location);
        if (h != null && h.Key.StartsWith("row:", StringComparison.Ordinal) && h.Key.IndexOf(':', 4) < 0 && int.TryParse(h.Key.AsSpan(4), out int i) && i < _rows.Count && !_rows[i].Header)
            RowActivated?.Invoke(_rows[i]);
    }

    // ───────── клавиатура, копирование ─────────

    protected override bool IsInputKey(Keys k)
    {
        var key = k & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Enter || base.IsInputKey(k);
    }

    private int Step(int from, int dir)
    {
        int i = from;
        do i += dir; while (i >= 0 && i < _rows.Count && _rows[i].Header);
        return i >= 0 && i < _rows.Count ? i : from;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.C) { CopyText(e.Shift ? AllText() : RowText(SelectedRow)); e.Handled = true; return; }
        if (_rows.Count == 0 || e.Control || e.Alt) return;
        int per = Math.Max(1, Height / Math.Max(1, RowH) - 1);
        int cur = _sel;
        int ni = e.KeyCode switch
        {
            Keys.Up => cur < 0 ? Step(_rows.Count, -1) : Step(cur, -1),
            Keys.Down => Step(cur, 1),
            Keys.Home => Step(-1, 1),
            Keys.End => Step(_rows.Count, -1),
            Keys.PageUp => Step(Math.Max(0, cur - per) + 1, -1),
            Keys.PageDown => Step(Math.Min(_rows.Count - 1, Math.Max(0, cur) + per) - 1, 1),
            _ => -2,
        };
        if (ni >= 0 && ni < _rows.Count) { SelectIndex(ni, true); e.Handled = true; return; }
        if (e.KeyCode == Keys.Enter && SelectedRow is { } row) { RowActivated?.Invoke(row); e.Handled = true; }
        else if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
        {
            var r = _sel >= 0 ? RowRect(_sel) : new Rectangle(0, 0, Width, Px(30));
            Menu(_sel, new Point(r.X + Px(60), Math.Clamp(r.Bottom - Px(6), 0, Height)));
            e.Handled = true;
        }
    }

    private static string RowText(Row? r)
    {
        if (r == null) return "";
        if (r.Copy != null) return r.Copy;
        var parts = new[] { r.Lead, r.Title, r.Text, r.Badge.Length > 0 ? "(" + r.Badge + ")" : "", r.Right }.Where(s => s.Length > 0);
        return string.Join(" ", parts);
    }

    private string AllText()
    {
        var sb = new StringBuilder();
        foreach (var r in _rows)
        {
            if (r.Header) { if (sb.Length > 0) sb.AppendLine(); sb.AppendLine(r.Title + (r.Right.Length > 0 ? " " + r.Right : "")); }
            else sb.AppendLine(RowText(r));
        }
        return sb.ToString();
    }

    private static void CopyText(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        try { Clipboard.SetText(s); } catch { }
    }

    private ContextMenuStrip? _menu;

    private void Menu(int i, Point pt)
    {
        if (i >= 0 && i < _rows.Count && !_rows[i].Header) SelectIndex(i, false);
        var row = SelectedRow;
        _menu?.Dispose();
        var m = _menu = Menus.Create(this);
        Menus.Add(m, "Копировать строку", Theme.GCopy, () => CopyText(RowText(row)), row != null, null, "Ctrl+C");
        Menus.Add(m, "Копировать всё (" + Theme.Count(_rows.Count(r => !r.Header), "строка", "строки", "строк") + ")", Theme.GCopy, () => CopyText(AllText()), _rows.Count > 0, null, "Ctrl+Shift+C");
        string? name = row != null ? ModName?.Invoke(row) : null;
        if (row != null && name != null)
        {
            Menus.Separator(m);
            Menus.Add(m, "Подробности: " + name, Theme.GInfo, () => RowActivated?.Invoke(row), true, null, "Enter");
        }
        m.Show(this, pt);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu?.Dispose();
        base.Dispose(disposing);
    }
}
