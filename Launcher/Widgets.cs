// Widgets.cs — самодельные элементы интерфейса: кнопки-капсулы, сегменты, поле поиска,
// полоса профилей, строка статуса, меню и диалоги в одном стиле.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

/// Двойная буферизация + масштаб под DPI.
internal class Surface : Control
{
    public Surface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Cream;
        ForeColor = Theme.Navy;
        Font = Theme.Body;
    }

    public float S => DeviceDpi / 96f;
    public int Px(float v) => (int)Math.Round(v * S);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        PaintBack(g, e.ClipRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw(g);
    }

    /// Фон под рисунком (только в перерисовываемой части). Прямоугольник по целым пикселям
    /// без сглаживания даёт те же пиксели, что и со сглаживанием, но заливается заметно быстрее.
    protected virtual void PaintBack(Graphics g, Rectangle clip)
    {
        var r = Rectangle.Intersect(clip, ClientRectangle);
        if (r.Width <= 0 || r.Height <= 0) return;
        using var b = new SolidBrush(BackColor);
        g.FillRectangle(b, r);
    }

    protected virtual void Draw(Graphics g) { }
}

/// Холст с «горячими зонами»: всё рисуется вручную, клики/наведение — по прямоугольникам.
internal class Canvas : Surface
{
    protected sealed class Hit
    {
        public Rectangle R;
        public string Key = "";
        public Action? Click;
        public Action<Point>? Right;
        public string? Tip;
        public bool Hand = true;
    }

    protected readonly List<Hit> Hits = new();
    protected string? HoverKey, PressKey;
    private static ToolTip? _tip;
    private string? _tipText;

    protected static ToolTip Tips => _tip ??= new ToolTip { InitialDelay = 400, ReshowDelay = 150, AutoPopDelay = 12000, ShowAlways = true };

    protected override void OnPaint(PaintEventArgs e)
    {
        Hits.Clear();
        base.OnPaint(e);
    }

    protected Hit AddHit(Rectangle r, string key, Action? click, string? tip = null, Action<Point>? right = null, bool hand = true)
    {
        var h = new Hit { R = r, Key = key, Click = click, Tip = tip, Right = right, Hand = hand };
        Hits.Add(h);
        return h;
    }

    protected bool IsHover(string key) => HoverKey == key;
    protected bool IsDown(string key) => PressKey == key && HoverKey == key;

    protected Hit? HitAt(Point p)
    {
        for (int i = Hits.Count - 1; i >= 0; i--) if (Hits[i].R.Contains(p)) return Hits[i];
        return null;
    }

    /// Перерисовать то, что зависит от наведения/нажатия на зоны a и b. По умолчанию — весь холст;
    /// список модов перерисовывает только две затронутые карточки.
    protected virtual void InvalidateHot(string? a, string? b) => Invalidate();

    protected void UpdateHover(Point p)
    {
        var h = HitAt(p);
        string? k = h?.Key;
        if (k != HoverKey) { string? old = HoverKey; HoverKey = k; InvalidateHot(old, k); }
        Cursor = h != null && h.Hand ? Cursors.Hand : Cursors.Default;
        string? tip = h?.Tip;
        if (tip != _tipText) { _tipText = tip; Tips.SetToolTip(this, tip ?? ""); }
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); UpdateHover(e.Location); }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (HoverKey != null || PressKey != null) { string? oh = HoverKey, op = PressKey; HoverKey = null; PressKey = null; InvalidateHot(oh, op); }
        Cursor = Cursors.Default;
        if (_tipText != null) { _tipText = null; Tips.SetToolTip(this, ""); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { string? old = PressKey; PressKey = HitAt(e.Location)?.Key; InvalidateHot(old, PressKey); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var h = HitAt(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            string? pk = PressKey;
            PressKey = null;
            InvalidateHot(pk, HoverKey);
            if (h != null && pk != null && h.Key == pk) h.Click?.Invoke();
        }
        else if (e.Button == MouseButtons.Right && h?.Right != null) h.Right(e.Location);
    }
}

internal enum Look { Primary, Secondary, Ghost, Danger, DangerFill, Quiet, Outline }

/// Кнопка-капсула.
internal sealed class PillButton : Surface
{
    private bool _hover, _down;
    public Look Look;
    public string Glyph;
    public bool IconOnly;
    public VIcon Icon;                 // векторный значок вместо глифа (Outline-кнопки «Компактного» стиля)
    public string Keycap = "";         // клавиша справа рамкой («Ctrl+K»), только у Outline
    public bool ShowKeycap = true;
    /// Заливка Outline-кнопки в покое (null — прозрачная): белая кнопка Fluent и «Слаймового» стилей.
    /// У «Слаймового» такая кнопка ещё и с мягкой тенью, как главная.
    public Color? Fill;
    public string? Tip { get => _tip; set { if (_tip == value) return; _tip = value; Tips.SetToolTip(this, value ?? ""); } }
    private string? _tip;
    private static ToolTip? _tips;
    private static ToolTip Tips => _tips ??= new ToolTip { InitialDelay = 400, ShowAlways = true };

    public PillButton(string text, Look look = Look.Secondary, string glyph = "")
    {
        Text = text; Look = look; Glyph = glyph;
        Cursor = Cursors.Hand;
        Font = Theme.ButtonFont;
        Height = Px(36);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); Cursor = Enabled ? Cursors.Hand : Cursors.Default; }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

    public int PreferredWidth()
    {
        int h = Height;
        if (IconOnly || string.IsNullOrEmpty(Text)) return h;
        if (Look == Look.Outline)
        {
            int ow = Px(12) + (Icon != VIcon.None || Glyph.Length > 0 ? Px(13) + Px(10) : 0) + Theme.Measure(Text, Font).Width;
            return ow + (Keycap.Length > 0 && ShowKeycap ? Px(7) + Theme.KeycapW(Keycap, S) + Px(5) : Px(12));
        }
        int w = Theme.Measure(Text, Font).Width + (int)(h * 0.9f);
        if (!string.IsNullOrEmpty(Glyph)) w += Px(22);
        return Math.Max(w, Px(44));
    }

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(0, 0, Width, Height);
        Color bg, fg, border = Color.Empty;
        switch (Look)
        {
            case Look.Primary:
                bg = _down ? Theme.Mix(Theme.TealDark, Theme.Navy, 0.12f) : _hover ? Theme.TealDark : Theme.Teal; fg = Color.White; break;
            case Look.Danger:
                bg = _hover ? Theme.CoralTint : Color.Transparent; fg = Theme.CoralText; border = _hover ? Theme.Coral : Theme.Line; break;
            case Look.DangerFill:
                bg = _down ? Theme.Mix(Theme.Coral, Theme.Navy, 0.15f) : _hover ? Theme.Mix(Theme.Coral, Theme.Navy, 0.08f) : Theme.Coral; fg = Color.White; break;
            case Look.Ghost:
                bg = _down ? Theme.BeigeDark : _hover ? Theme.Beige : Color.Transparent; fg = Theme.NavySoft; break;
            case Look.Quiet:
                bg = _down ? Theme.BeigeDark : _hover ? Theme.Beige : Theme.Paper; fg = Theme.Navy; border = Theme.Line; break;
            case Look.Outline when Fill is { } fill:
                bg = _down ? Theme.Mix(fill, Theme.BeigeDark, 0.8f) : _hover ? Theme.Mix(fill, Theme.Beige, 0.75f) : fill;
                fg = Theme.Navy; border = Theme.Mix(Theme.Line, Theme.LineDark, _hover ? 0.9f : 0.5f); break;
            case Look.Outline:
                bg = _down ? Theme.BeigeDark : _hover ? Theme.Beige : Color.Transparent; fg = _hover ? Theme.Navy : Theme.NavySoft; border = Theme.LineDark; break;
            default:
                bg = _down ? Theme.BeigeDark : _hover ? Theme.Mix(Theme.Beige, Theme.BeigeDark, 0.5f) : Theme.Beige; fg = Theme.Navy; break;
        }
        if (!Enabled)
        {
            bg = Look == Look.Primary ? Theme.Mix(Theme.Teal, Theme.Cream, 0.55f) : bg.A == 0 ? bg : Theme.Mix(bg, Theme.Cream, 0.5f);
            fg = Look == Look.Primary ? Color.FromArgb(235, 255, 255, 255) : Theme.Hint;
        }
        bool shadow = (Look == Look.Primary || Look == Look.Outline && Fill.HasValue) && Theme.Spec.Shadows;
        if (shadow && Enabled && !_down)
            Theme.Shadow(g, new RectangleF(r.X + Px(3), r.Y + Px(3), r.Width - Px(6), r.Height - Px(5)), Theme.Radius(r.Height - Px(5), S), Px(6), Look == Look.Primary ? 30 : 16);
        var body = shadow ? new RectangleF(r.X + Px(1), r.Y + Px(1), r.Width - Px(2), r.Height - Px(3)) : r;
        float rad = Theme.Radius(body.Height, S);
        Theme.FillRound(g, body, rad, bg);
        if (border != Color.Empty) Theme.StrokeRound(g, body, rad, border, Math.Max(1f, S));
        var cr = Rectangle.Round(body);
        int isz = Px(13);
        if (IconOnly || string.IsNullOrEmpty(Text))
        {
            if (Icon != VIcon.None) Theme.Icon(g, Icon, new RectangleF(cr.X + (cr.Width - isz) / 2f, cr.Y + (cr.Height - isz) / 2f, isz, isz), fg);
            else Theme.Glyph(g, Glyph, cr, fg, Theme.Icons);
            return;
        }
        if (Look == Look.Outline)
        {   // значок, подпись и клавиша — слева направо, как у кнопки «Команды» в редакторах кода
            int ox = cr.X + Px(12);
            if (Icon != VIcon.None) { Theme.Icon(g, Icon, new RectangleF(ox, cr.Y + (cr.Height - isz) / 2f, isz, isz), fg); ox += isz + Px(10); }
            else if (Glyph.Length > 0) { Theme.Glyph(g, Glyph, new Rectangle(ox - Px(2), cr.Y, isz + Px(4), cr.Height), fg, Theme.IconsSmall); ox += isz + Px(10); }
            int tw = Theme.Measure(Text, Font).Width;
            Theme.Text(g, Text, Font, new Rectangle(ox, cr.Y, Math.Min(tw + 2, cr.Right - ox), cr.Height), fg);
            if (Keycap.Length > 0 && ShowKeycap)
            {
                int kw = Theme.KeycapW(Keycap, S), kh = Px(16);
                Theme.Keycap(g, new Rectangle(ox + tw + Px(7), cr.Y + (cr.Height - kh) / 2, kw, kh), Keycap, Theme.LineDark, Theme.Hint, S);
            }
            return;
        }
        var ts = Theme.Measure(Text, Font);
        int gw = string.IsNullOrEmpty(Glyph) ? 0 : Px(22);
        int x = cr.X + (cr.Width - ts.Width - gw) / 2;
        if (gw > 0) Theme.Glyph(g, Glyph, new Rectangle(x, cr.Y, Px(16), cr.Height), fg, Theme.Icons);
        Theme.Text(g, Text, Font, new Rectangle(x + gw, cr.Y, cr.Right - x - gw, cr.Height), fg);
    }
}

// ───────── плавные переходы ─────────

/// Значение, которое плавно (ease-out) едет к цели. В режиме снимка (--render) сразу берёт конечное.
internal sealed class Tween
{
    private float _from, _to;
    private long _start;
    private int _ms;

    public Tween(float v) { _from = _to = v; }

    public float Target => _to;
    private float Progress => _ms <= 0 ? 1f : Math.Clamp((Environment.TickCount64 - _start) / (float)_ms, 0f, 1f);
    public bool Running => Anim.Enabled && Progress < 1f;

    public float Value
    {
        get
        {
            if (!Anim.Enabled) return _to;
            float t = 1f - Progress;
            return _from + (_to - _from) * (1f - t * t * t);
        }
    }

    public void To(float target, int ms, params Control[] owners)
    {
        if (Math.Abs(target - _to) < 0.001f) return;
        _from = Value; _to = target; _start = Environment.TickCount64; _ms = ms;
        foreach (var c in owners) Anim.Watch(c, ms);
    }

    public void Snap(float v) { _from = _to = v; _ms = 0; }
}

/// Один таймер на все анимации: пока что-то едет, перерисовывает свои элементы (~60 кадров/с).
internal static class Anim
{
    public static bool Enabled = true;
    private static readonly Dictionary<Control, long> Until = new();
    private static System.Windows.Forms.Timer? _timer;

    public static void Watch(Control? c, int ms)
    {
        if (c == null || c.IsDisposed) return;
        c.Invalidate();
        if (!Enabled) return;
        long end = Environment.TickCount64 + ms + 30;
        Until[c] = Until.TryGetValue(c, out var u) ? Math.Max(u, end) : end;
        if (_timer == null) { _timer = new System.Windows.Forms.Timer { Interval = 15 }; _timer.Tick += (_, _) => Tick(); }
        if (!_timer.Enabled) _timer.Start();
    }

    private static void Tick()
    {
        long now = Environment.TickCount64;
        foreach (var kv in Until.ToList())
        {
            if (!kv.Key.IsDisposed) kv.Key.Invalidate();
            if (kv.Key.IsDisposed || now > kv.Value) Until.Remove(kv.Key);
        }
        if (Until.Count == 0) _timer?.Stop();
    }
}

/// Переключатель-сегменты (быстрые фильтры, «Steam / Напрямую»): выделение плавно переезжает, у счётчиков — свой тон.
internal sealed class Segmented : Canvas
{
    public sealed class Item
    {
        public string Text;
        public string? Badge;
        public string? Tip;
        public Tone Tone;   // Warn — счётчик красный, Info — синий (если не 0)
        public VIcon Icon;  // значок перед подписью (только у вкладок «Компактного» стиля)

        public Item(string text, string? badge = null, string? tip = null, Tone tone = Tone.Neutral)
        { Text = text; Badge = badge; Tip = tip; Tone = tone; }
    }

    public readonly List<Item> Items = new();
    private int _selected;
    private bool _slide;
    private readonly Tween _x = new(-1f), _w = new(0f);
    public int Selected { get => _selected; set { if (_selected != value) { _selected = value; _slide = true; Invalidate(); } } }
    public event Action<int>? Changed;
    public Color? TrackColor { get; set; }   // null — Theme.Beige активного стиля
    /// Вкладки «Компактного» стиля: без дорожки, выбранная — в рамке; значки и счётчики — цветом тона.
    /// Selected = −1 — ни одна не выбрана (открыт раздел боковой панели).
    public bool Tabs { get => _tabs; set { if (_tabs == value) return; _tabs = value; Invalidate(); } }
    private bool _tabs;
    /// Вкладки Fluent — как SelectorBar Windows 11: без дорожки, у выбранной полужирная подпись и короткая акцентная
    /// черта снизу (плавно переезжает к выбранной), счётчики — в капсулах. Selected = −1 — ни одна не выбрана.
    public bool Selector { get => _selector; set { if (_selector == value) return; _selector = value; _x.Snap(-1f); Invalidate(); } }
    private bool _selector;

    public Segmented() { Height = Px(34); }

    private int TabGap => _selector ? Px(2) : Px(6);
    private int SelBadgeW(string b) => Math.Max(Px(18), Theme.Measure(b, Theme.SmallBold).Width + Px(10));

    private int SegWidth(int i)
    {
        var it = Items[i];
        if (_tabs)
            return Px(10) + (it.Icon != VIcon.None ? Px(11) + Px(7) : 0) + Theme.Measure(it.Text, Theme.ButtonFont).Width
                   + (string.IsNullOrEmpty(it.Badge) ? 0 : Px(6) + Theme.Measure(it.Badge, Theme.Small).Width) + Px(10);
        if (_selector)
            return Px(12) + Theme.Measure(it.Text, Theme.ButtonFont).Width + (string.IsNullOrEmpty(it.Badge) ? 0 : Px(7) + SelBadgeW(it.Badge)) + Px(12);
        int w = Theme.Measure(it.Text, Theme.ButtonFont).Width + Px(26);
        if (!string.IsNullOrEmpty(it.Badge)) w += Theme.Measure(it.Badge, Theme.SmallBold).Width + Px(16);
        return w;
    }

    public int PreferredWidth()
    {
        if (_tabs || _selector) { int t = 0; for (int i = 0; i < Items.Count; i++) t += SegWidth(i) + (i > 0 ? TabGap : 0); return t; }
        int w = Px(6); for (int i = 0; i < Items.Count; i++) w += SegWidth(i); return w;
    }

    private void DrawTabs(Graphics g)
    {
        int h = Height, ph = Math.Min(h, Px(26)), py = (h - ph) / 2, x = 0;
        float rad = ph / 2f;   // выбранная вкладка — капсула, как в макете
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            int w = SegWidth(i);
            var sr = new Rectangle(x, py, w, ph);
            bool sel = i == _selected;
            string key = "seg" + i;
            if (sel)
            {
                Theme.FillRound(g, sr, rad, Theme.Beige);
                Theme.StrokeRound(g, sr, rad, Theme.Mix(Theme.Line, Theme.LineDark, 0.6f), Math.Max(1f, S));
            }
            else if (IsHover(key)) Theme.FillRound(g, sr, rad, Theme.Alpha(Theme.Beige, 170));
            bool lit = it.Badge != "0" && it.Tone is Tone.Warn or Tone.Error or Tone.Info;
            Color tone = it.Tone == Tone.Info ? Theme.BlueText : it.Tone == Tone.Error ? Theme.CoralText : Theme.OrangeText;
            Color fg = lit ? tone : sel || IsHover(key) ? Theme.Navy : Theme.NavySoft;
            int cx = sr.X + Px(10);
            if (it.Icon != VIcon.None)
            {
                int isz = Px(11);
                Theme.Icon(g, it.Icon, new RectangleF(cx, sr.Y + (ph - isz) / 2f, isz, isz), lit ? tone : Theme.Hint);
                cx += isz + Px(7);
            }
            var font = sel ? Theme.ButtonFont : Theme.Body;
            int tw = Theme.Measure(it.Text, font).Width;
            Theme.Text(g, it.Text, font, new Rectangle(cx, sr.Y, tw + 2, ph), fg);
            cx += tw + Px(6);
            if (!string.IsNullOrEmpty(it.Badge))
                Theme.Text(g, it.Badge, Theme.Small, new Rectangle(cx, sr.Y, Math.Max(0, sr.Right - cx), ph), lit ? tone : Theme.Hint);
            int idx = i;
            AddHit(sr, key, () => { if (_selected != idx) { _selected = idx; Invalidate(); Changed?.Invoke(idx); } }, it.Tip);
            x += w + TabGap;
        }
    }

    private void DrawSelector(Graphics g)
    {
        int h = Height, lineY = h - Px(4), x = 0, selX = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            int w = SegWidth(i);
            var sr = new Rectangle(x, 0, w, h);
            bool sel = i == _selected;
            string key = "seg" + i;
            bool hov = IsHover(key);
            if (hov && !sel) Theme.FillRound(g, new Rectangle(sr.X, Px(3), sr.Width, lineY - Px(4)), Px(4), Theme.Alpha(Theme.Navy, 10));
            int cx = sr.X + Px(12), tw = Theme.Measure(it.Text, Theme.ButtonFont).Width;
            Theme.Text(g, it.Text, sel ? Theme.ButtonFont : Theme.Body, new Rectangle(cx, 0, tw + 2, lineY), sel || hov ? Theme.Navy : Theme.NavySoft);
            cx += tw + Px(7);
            if (!string.IsNullOrEmpty(it.Badge))
            {
                var br = new Rectangle(cx, (lineY - Px(17)) / 2 + Px(1), SelBadgeW(it.Badge), Px(17));
                bool lit = it.Badge != "0" && it.Tone is Tone.Warn or Tone.Error or Tone.Info;
                var (bg, fg) = BadgeColors(it, sel);
                if (!lit && !sel) bg = Theme.Mix(Theme.Beige, Theme.BeigeDark, 0.5f);
                Theme.FillRound(g, br, br.Height / 2f, bg);
                Theme.Text(g, it.Badge, Theme.SmallBold, br, fg, TextFormatFlags.HorizontalCenter);
            }
            if (sel) selX = sr.X + sr.Width / 2;
            int idx = i;
            AddHit(sr, key, () => { if (_selected != idx) { _selected = idx; _slide = true; Invalidate(); Changed?.Invoke(idx); } }, it.Tip);
            x += w + TabGap;
        }
        // черта под выбранной вкладкой
        if (selX < 0) { _x.Snap(-1f); _slide = false; return; }
        if (_x.Target < 0 || !_slide && Math.Abs(_x.Target - selX) > 0.5f) _x.Snap(selX);
        else if (_slide) _x.To(selX, 210, this);
        _slide = false;
        Theme.FillRound(g, new RectangleF(_x.Value - Px(9), lineY, Px(18), Px(3)), Px(1.5f), Theme.Teal);
    }

    private (Color Bg, Color Fg) BadgeColors(Item it, bool sel)
    {
        bool lit = it.Badge != "0" && it.Tone is Tone.Warn or Tone.Error or Tone.Info;
        if (!lit) return (sel ? Theme.TealTint : Theme.Alpha(Theme.Paper, 160), sel ? Theme.TealText : Theme.NavySoft);
        bool warn = it.Tone != Tone.Info;
        if (sel) return (warn ? Theme.Coral : Theme.Blue, Color.White);
        return (warn ? Theme.CoralTint : Theme.BlueTint, warn ? Theme.CoralText : Theme.BlueText);
    }

    protected override void Draw(Graphics g)
    {
        if (_tabs) { DrawTabs(g); return; }
        if (_selector) { DrawSelector(g); return; }
        var r = new RectangleF(0, 0, Width, Height);
        float tr = Theme.Radius(Height, S), ir = Math.Max(Px(2), tr - Px(3));
        Theme.FillRound(g, r, tr, TrackColor ?? Theme.Beige);
        if (Items.Count == 0) return;
        _selected = Math.Clamp(_selected, -1, Items.Count - 1);

        // выделение: белая «таблетка», которая плавно едет к выбранному сегменту (−1 — ни один не выбран)
        if (_selected < 0) { _x.Snap(-1f); _slide = false; }
        else
        {
            int sx = Px(3);
            for (int i = 0; i < _selected; i++) sx += SegWidth(i);
            int swid = SegWidth(_selected);
            if (_x.Target < 0 || !_slide && (Math.Abs(_x.Target - sx) > 0.5f || Math.Abs(_w.Target - swid) > 0.5f)) { _x.Snap(sx); _w.Snap(swid); }
            else if (_slide) { _x.To(sx, 210, this); _w.To(swid, 210, this); }
            _slide = false;
            var pill = new RectangleF(_x.Value, Px(3), _w.Value, Height - Px(6));
            if (!Theme.Dark) Theme.Shadow(g, pill, ir, Px(3), 14);
            Theme.FillRound(g, pill, ir, Theme.Dark ? Theme.BeigeDark : Theme.Paper);
        }

        int x = Px(3);
        for (int i = 0; i < Items.Count; i++)
        {
            int w = SegWidth(i);
            var sr = new Rectangle(x, Px(3), w, Height - Px(6));
            bool sel = i == _selected;
            string key = "seg" + i;
            if (!sel && IsHover(key)) Theme.FillRound(g, sr, ir, Theme.Alpha(Theme.Dark ? Theme.BeigeDark : Theme.Paper, 110));
            var it = Items[i];
            var ts = Theme.Measure(it.Text, Theme.ButtonFont);
            int bw = string.IsNullOrEmpty(it.Badge) ? 0 : Theme.Measure(it.Badge, Theme.SmallBold).Width + Px(12);
            int tx = sr.X + (sr.Width - ts.Width - (bw > 0 ? bw + Px(4) : 0)) / 2;
            Theme.Text(g, it.Text, Theme.ButtonFont, new Rectangle(tx, sr.Y, ts.Width + 2, sr.Height), sel ? Theme.Navy : Theme.NavySoft);
            if (bw > 0)
            {
                var br = new Rectangle(tx + ts.Width + Px(5), sr.Y + (sr.Height - Px(18)) / 2, bw, Px(18));
                var (bg, fg) = BadgeColors(it, sel);
                Theme.FillRound(g, br, br.Height / 2f, bg);
                Theme.Text(g, it.Badge, Theme.SmallBold, br, fg, TextFormatFlags.HorizontalCenter);
            }
            int idx = i;
            AddHit(sr, key, () => { if (_selected != idx) { _selected = idx; _slide = true; Invalidate(); Changed?.Invoke(idx); } }, it.Tip);
            x += w;
        }
    }
}

/// TextBox со своей подсказкой: она плавно появляется и отъезжает от курсора при фокусе,
/// а ещё попадает в снимки окна (системный PlaceholderText в DrawToBitmap не рисуется).
internal sealed class HintBox : TextBox
{
    private const int WM_PAINT = 0x000F, WM_PRINTCLIENT = 0x0318;
    public string Hint = "";
    public string HintShort = "";            // если полная подсказка не влезает в узкое поле
    public readonly Tween Alpha = new(1f);   // непрозрачность подсказки 0..1
    public readonly Tween Shift = new(0f);   // сдвиг вправо, px
    public Color? HintColor { get; set; }    // null — Theme.Hint активного стиля

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if ((m.Msg == WM_PAINT || m.Msg == WM_PRINTCLIENT) && TextLength == 0 && Hint.Length > 0 && IsHandleCreated)
        {
            float a = Alpha.Value;
            if (a < 0.03f) return;
            try
            {
                using var g = m.Msg == WM_PRINTCLIENT ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle);
                int dx = (int)Math.Round(Shift.Value);
                var r = new Rectangle(1 + dx, 1, Math.Max(0, ClientSize.Width - 1 - dx), ClientSize.Height);
                // выбор по ширине без сдвига — чтобы текст не менялся посреди анимации
                string hint = HintShort.Length > 0 && TextRenderer.MeasureText(g, Hint, Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width > ClientSize.Width - 1 ? HintShort : Hint;
                TextRenderer.DrawText(g, hint, Font, r, Theme.Mix(BackColor, HintColor ?? Theme.Hint, a),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Top | TextFormatFlags.Left);
            }
            catch { }
        }
    }
}

/// Поле ввода-капсула. В режиме поиска (есть глиф): лупа, плавная подсказка, счётчик «3 из 12»,
/// крестик очистки (Esc), подсказка горячей клавиши, Ctrl+Backspace — стереть слово.
internal sealed class TextField : Surface
{
    public readonly HintBox Box = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Paper, ForeColor = Theme.Navy, Font = Theme.Body };
    public string Glyph = "";
    /// «Ctrl+F»: видна справа, пока поле пустое и не в фокусе. Смена (другой стиль окна) — поле ввода перекладывается под новую ширину.
    public string Shortcut { get => _shortcut; set { value ??= ""; if (_shortcut == value) return; _shortcut = value; PerformLayout(); Invalidate(); } }
    private string _shortcut = "";
    public bool Round = true;
    /// «Компактный» стиль: лупа линией, клавиша-подсказка рамкой, поле поуже.
    public bool Flat { get => _flat; set { if (_flat == value) return; _flat = value; PerformLayout(); Invalidate(); } }
    private bool _flat;
    /// Fluent: как поле ввода Windows 11 — снизу тёмная черта, в фокусе она становится акцентной и толще (без свечения).
    public bool Underline { get => _underline; set { if (_underline == value) return; _underline = value; Invalidate(); } }
    private bool _underline;
    private Color? _fill;
    /// Заливка поля (null — Theme.Paper активного стиля); вместе с ней перекрашивается и само поле ввода.
    public void SetFill(Color? c) { _fill = c; Box.BackColor = c ?? Theme.Paper; Invalidate(); Box.Invalidate(); }
    private Color FillColor => _fill ?? Theme.Paper;
    public event Action? Changed;
    public event Action? Submitted;       // Enter (только если кто-то подписан)
    private string? _count;
    private bool _countBad, _focus, _hoverClear, _downClear;
    private readonly Tween _focusT = new(0f), _clearT = new(0f);
    private static ToolTip? _tips;
    private static ToolTip Tips => _tips ??= new ToolTip { InitialDelay = 400, ReshowDelay = 150, AutoPopDelay = 8000, ShowAlways = true };

    public TextField(string placeholder, string glyph = "")
    {
        Glyph = glyph;
        Box.Hint = placeholder ?? "";
        Controls.Add(Box);
        Height = Px(36);
        Cursor = Cursors.IBeam;
        Box.TextChanged += (_, _) => OnBoxText();
        Box.GotFocus += (_, _) => SetFocus(true);
        Box.LostFocus += (_, _) => SetFocus(false);
        Box.KeyDown += OnBoxKey;
    }

    public string Placeholder { get => Box.Hint; set { Box.Hint = value ?? ""; Box.Invalidate(); } }
    public string PlaceholderShort { get => Box.HintShort; set { Box.HintShort = value ?? ""; Box.Invalidate(); } }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }

    public bool IsFocused => _focus;
    private bool SearchMode => !string.IsNullOrEmpty(Glyph);
    private bool Clearable => SearchMode && Box.TextLength > 0;
    private Rectangle ClearRect => new(Width - Height + Px(5), Px(5), Height - Px(10), Height - Px(10));
    private int CountW => _count == null ? 0 : Theme.Measure(_count, Theme.SmallBold).Width + Px(16);
    private Rectangle CountRect => new(ClearRect.X - Px(4) - CountW, (Height - Px(20)) / 2, CountW, Px(20));
    private int KeyW => Shortcut.Length == 0 ? 0 : _flat ? Theme.KeycapW(Shortcut, S) : Theme.Measure(Shortcut, Theme.SmallBold).Width + Px(14);

    /// Счётчик результатов справа («3 из 12»); null — скрыть. bad — ничего не нашлось.
    public void SetCount(string? text, bool bad)
    {
        if (text == _count && bad == _countBad) return;
        bool relayout = (text?.Length ?? -1) != (_count?.Length ?? -1);
        _count = text; _countBad = bad;
        if (relayout) PerformLayout();
        Invalidate();
    }

    private float HintRest => _focus ? 0.72f : 1f;   // в фокусе подсказка чуть бледнее
    private float HintShift => _focus ? Px(5) : 0f;   // и отходит от мигающего курсора

    private void SetFocus(bool f)
    {
        _focus = f;
        _focusT.To(f ? 1f : 0f, 170, this);
        if (Box.TextLength == 0) { Box.Alpha.To(HintRest, 170, Box); Box.Shift.To(HintShift, 200, Box); }
        Invalidate();
    }

    private void OnBoxText()
    {
        bool has = Box.TextLength > 0;
        _clearT.To(has ? 1f : 0f, has ? 150 : 90, this);
        if (!has)
        {   // текст стёрт — подсказка проявляется и подъезжает справа
            Box.Alpha.Snap(0f); Box.Alpha.To(HintRest, 230, Box);
            Box.Shift.Snap(Px(16)); Box.Shift.To(HintShift, 280, Box);
        }
        PerformLayout();
        Invalidate();
        Changed?.Invoke();
    }

    private void OnBoxKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && Submitted != null) { Submitted(); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Back && e.Control)
        {   // Ctrl+Backspace — стереть слово целиком (обычный TextBox вставил бы «□»)
            if (Box.SelectionLength > 0) Box.SelectedText = "";
            else
            {
                int pos = Box.SelectionStart, i = pos;
                string t = Box.Text;
                while (i > 0 && t[i - 1] == ' ') i--;
                while (i > 0 && t[i - 1] != ' ') i--;
                if (i < pos) { Box.Text = t.Remove(i, pos - i); Box.SelectionStart = i; }
            }
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int left = SearchMode ? Px(_flat ? 32 : 36) : Px(14);
        int right = !SearchMode ? Px(14) : Clearable ? Width - (_count != null ? CountRect.X : ClearRect.X) + Px(6) : Px(_flat ? 12 : 14) + KeyW;
        Box.SetBounds(left, (Height - Box.PreferredHeight) / 2 + 1, Math.Max(10, Width - left - right), Box.PreferredHeight);
    }

    protected override void Draw(Graphics g)
    {
        var r = new RectangleF(0, 0, Width, Height);
        float rad = Round ? Theme.Radius(Height, S) : Math.Min(Px(10), Theme.Radius(Height, S));
        float f = _focusT.Value;
        Color fill = FillColor, line = _flat ? Theme.Mix(Theme.Line, Theme.LineDark, 0.45f) : Theme.Line;
        Theme.FillRound(g, r, rad, fill);
        if (_underline)
        {
            Theme.StrokeRound(g, r, rad, line, Math.Max(1f, S));
            using var path = Theme.RoundRect(r, rad);
            var st = g.Save();
            g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Intersect);
            float lh = Math.Max(1f, S) * (1f + f);
            using (var b = new SolidBrush(Theme.Mix(Theme.Mix(line, Theme.NavySoft, 0.6f), Theme.Teal, f))) g.FillRectangle(b, 0, Height - lh, Width, lh);
            g.Restore(st);
        }
        else
        {
            if (f > 0.01f) // мягкое свечение внутри рамки, пока поле в фокусе
                Theme.StrokeRound(g, RectangleF.Inflate(r, -Px(2), -Px(2)), Math.Max(1f, rad - Px(2)), Theme.Alpha(Theme.Teal, (int)(44 * f)), Math.Max(2f, 3f * S));
            Theme.StrokeRound(g, r, rad, Theme.Mix(line, Theme.Teal, f), Math.Max(1f, S) * (1f + 0.55f * f));
        }
        if (!SearchMode) return;
        if (_flat) Theme.Icon(g, VIcon.Search, new RectangleF(Px(11), (Height - Px(13)) / 2f, Px(13), Px(13)), Theme.Mix(Theme.Hint, Theme.TealText, f));
        else Theme.Glyph(g, Glyph, new Rectangle(Px(12), 0, Px(18), Height), Theme.Mix(Theme.Hint, Theme.TealText, f));

        float c = _clearT.Value;
        if (c > 0.02f)
        {
            var cr = ClearRect;
            float sc = 0.55f + 0.45f * c;
            var rr = new RectangleF(cr.X + cr.Width * (1 - sc) / 2f, cr.Y + cr.Height * (1 - sc) / 2f, cr.Width * sc, cr.Height * sc);
            Color bg = _downClear ? Theme.BeigeDark : _hoverClear ? Theme.Beige : Theme.Mix(fill, Theme.Beige, 0.6f);
            Theme.FillRound(g, rr, rr.Height / 2f, Theme.Mix(fill, bg, c));
            Theme.Glyph(g, Theme.GCancel, Rectangle.Round(rr), Theme.Mix(fill, _hoverClear ? Theme.Navy : Theme.NavySoft, c), Theme.IconsSmall);
        }
        if (_count != null && Clearable)
        {
            var pr = CountRect;
            Theme.FillRound(g, pr, pr.Height / 2f, _countBad ? Theme.CoralTint : Theme.TealTint);
            Theme.Text(g, _count, Theme.SmallBold, pr, _countBad ? Theme.CoralText : Theme.TealText, TextFormatFlags.HorizontalCenter);
        }
        if (!Clearable && KeyW > 0)
        {
            float a = 1f - f;
            if (a > 0.02f && _flat)
            {
                var kr = new Rectangle(Width - Px(9) - KeyW, (Height - Px(18)) / 2, KeyW, Px(18));
                Theme.Keycap(g, kr, Shortcut, Theme.Mix(fill, Theme.LineDark, a), Theme.Mix(fill, Theme.Hint, a), S);
            }
            else if (a > 0.02f)
            {
                var kr = new Rectangle(Width - Px(12) - KeyW, (Height - Px(20)) / 2, KeyW, Px(20));
                Theme.FillRound(g, kr, Px(6), Theme.Mix(fill, Theme.Cream, a));
                Theme.StrokeRound(g, kr, Px(6), Theme.Mix(fill, Theme.Line, a), Math.Max(1f, S));
                Theme.Text(g, Shortcut, Theme.SmallBold, kr, Theme.Mix(fill, Theme.Hint, a), TextFormatFlags.HorizontalCenter);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = Clearable && ClearRect.Contains(e.Location);
        if (h != _hoverClear)
        {
            _hoverClear = h;
            Cursor = h ? Cursors.Hand : Cursors.IBeam;
            Tips.SetToolTip(this, h ? "Очистить поиск (Esc)" : "");
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverClear || _downClear) { _hoverClear = _downClear = false; Tips.SetToolTip(this, ""); Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Clearable && ClearRect.Contains(e.Location)) { _downClear = true; Invalidate(); return; }
        Box.Focus();
        if (e.X >= Box.Right) Box.SelectionStart = Box.TextLength;
        else if (e.X <= Box.Left) Box.SelectionStart = 0;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_downClear) return;
        _downClear = false;
        if (ClearRect.Contains(e.Location)) Box.Text = "";
        Box.Focus();
        Invalidate();
    }
}

/// Полоса профилей (наборов модов) — капсулы с переносом строк.
internal sealed class ChipBar : Canvas
{
    public List<Profile> Profiles = new();
    public string? Active;
    public event Action<Profile>? Picked;
    public event Action<Profile, Point>? MenuRequested;
    public event Action? AddRequested;

    private int ChipH => Px(30);
    private int Gap => Px(8);
    private const string AddText = "Сохранить набор";

    private int ChipW(Profile p) => Theme.PillWidth(p.Name, Theme.ButtonFont, ChipH, p.Name == Active);

    private List<(Rectangle r, Profile? p)> LayoutChips(int width)
    {
        var res = new List<(Rectangle, Profile?)>();
        int labelW = Theme.Measure("НАБОРЫ", Theme.Section).Width + Px(14);
        int x = labelW, y = 0;
        void Place(int w, Profile? p)
        {
            if (x + w > width && x > labelW) { x = labelW; y += ChipH + Gap; }
            res.Add((new Rectangle(x, y, w, ChipH), p));
            x += w + Gap;
        }
        foreach (var p in Profiles) Place(ChipW(p), p);
        Place(Theme.PillWidth(AddText, Theme.ButtonFont, ChipH, true), null);
        return res;
    }

    public int MeasureHeight(int width)
    {
        var l = LayoutChips(width);
        return l.Count == 0 ? ChipH : l.Max(c => c.r.Bottom) + Px(2);
    }

    protected override void Draw(Graphics g)
    {
        Theme.Text(g, "НАБОРЫ", Theme.Section, new Rectangle(0, 0, Px(80), ChipH), Theme.Hint);
        foreach (var (r, p) in LayoutChips(Width))
        {
            if (p == null)
            {
                bool h = IsHover("add");
                Theme.Pill(g, r.X, r.Y, r.Height, AddText, Theme.ButtonFont, h ? Theme.TealTint : Color.Transparent, Theme.TealText, Theme.GAdd, h ? Theme.Teal : Theme.Mix(Theme.Teal, Theme.Cream, 0.45f));
                AddHit(r, "add", () => AddRequested?.Invoke(), "Сохранить текущий выбор модов как новый набор");
                continue;
            }
            bool active = p.Name == Active, hover = IsHover("p:" + p.Name);
            if (active)
                Theme.Pill(g, r.X, r.Y, r.Height, p.Name, Theme.ButtonFont, hover ? Theme.TealDark : Theme.Teal, Color.White, Theme.GCheck);
            else
                Theme.Pill(g, r.X, r.Y, r.Height, p.Name, Theme.ButtonFont, hover ? Theme.Beige : Theme.Paper, Theme.Navy, null, hover ? Theme.LineDark : Theme.Line);
            var cap = p;
            string tip = p.All ? "Все моды из папки Mods" : p.Mods.Count == 0 ? "Без модов — чистая игра" : "Моды: " + string.Join(", ", p.Mods);
            AddHit(r, "p:" + p.Name, () => Picked?.Invoke(cap), tip + "\nПравая кнопка — переименовать, перезаписать, ярлык, удалить", pt => MenuRequested?.Invoke(cap, pt));
        }
    }
}

internal enum Tone { Neutral, Info, Ok, Warn, Error }

/// Холст, который рисует владелец (боковая панель и строка состояния «Компактного» стиля); зоны щелчков — Zone.
internal sealed class PaintBox : Canvas
{
    public Action<Graphics, PaintBox>? Painter;
    public int Repaints;   // для замеров
    protected override void Draw(Graphics g) { Repaints++; Painter?.Invoke(g, this); }
    public void Zone(Rectangle r, string key, Action? click, string? tip = null, Action<Point>? right = null, bool hand = true) => AddHit(r, key, click, tip, right, hand);
    public bool Hot(string key) => IsHover(key);
    public bool Pressed(string key) => IsDown(key);
}

/// «Играть» со стрелкой: основная часть запускает игру, стрелка — меню запуска (Steam или напрямую, применить без запуска…).
internal sealed class SplitPlay : Canvas
{
    public string Caption { get; private set; } = "Играть";
    public string Sub { get; private set; } = "Steam";
    public bool MainEnabled { get; private set; } = true;
    private string? _mainTip;
    public string? MenuTip;
    public event Action? Play;
    public event Action? MenuRequested;

    public SplitPlay() { Height = Px(32); Font = Theme.ButtonFont; }

    private int ArrowW => Px(28);

    public int PreferredWidth() => Px(32) + Theme.Measure(Caption, Theme.ButtonFont).Width
        + (Sub.Length > 0 ? Px(9) + Theme.Measure(Sub, Theme.Small).Width : 0) + Px(14) + ArrowW;

    public void Set(string caption, string sub, bool enabled, string? tip)
    {
        if (caption == Caption && sub == Sub && enabled == MainEnabled && tip == _mainTip) return;
        Caption = caption; Sub = sub; MainEnabled = enabled; _mainTip = tip;
        Invalidate();
    }

    protected override void Draw(Graphics g)
    {
        // у «Слаймового» стиля — мягкая тень, как у главной кнопки: корпус на пиксель уже и на 3 px ниже края
        bool shadow = Theme.Spec.Shadows;
        var r = shadow ? new RectangleF(Px(1), Px(1), Width - Px(2), Height - Px(3)) : new RectangleF(0, 0, Width, Height);
        var cr = Rectangle.Round(r);
        float rad = Theme.Radius(r.Height, S);
        int ax = cr.Right - ArrowW;
        Color body = MainEnabled ? Theme.Teal : Theme.Mix(Theme.Teal, BackColor, 0.55f);
        if (shadow && MainEnabled && !IsDown("main") && !IsDown("menu"))
            Theme.Shadow(g, new RectangleF(Px(3), Px(3), Width - Px(6), Height - Px(5)), Theme.Radius(Height - Px(5), S), Px(6), 30);
        Theme.FillRound(g, r, rad, body);
        void Part(int x0, int x1, Color c)
        {
            var st = g.Save();
            g.SetClip(new Rectangle(x0, 0, x1 - x0, Height), System.Drawing.Drawing2D.CombineMode.Intersect);
            Theme.FillRound(g, r, rad, c);
            g.Restore(st);
        }
        if (MainEnabled && (IsHover("main") || IsDown("main"))) Part(0, ax, IsDown("main") ? Theme.Mix(Theme.TealDark, Theme.OnAccent, 0.12f) : Theme.TealDark);
        if (IsHover("menu") || IsDown("menu")) Part(ax, Width, IsDown("menu") ? Theme.Mix(Theme.TealDark, Theme.OnAccent, 0.12f) : Theme.Mix(body, Theme.TealDark, 0.8f));
        Color fg = MainEnabled ? Theme.OnAccent : Theme.Mix(Theme.OnAccent, body, 0.3f);
        using (var lp = new Pen(Theme.Mix(Theme.Teal, Theme.OnAccent, 0.22f), Math.Max(1f, S))) g.DrawLine(lp, ax, cr.Y + Px(1), ax, cr.Bottom - Px(1));
        int isz = Px(11);
        Theme.Icon(g, VIcon.Play, new RectangleF(cr.X + Px(12), cr.Y + (cr.Height - isz) / 2f, isz, isz), fg, Math.Max(1f, S));
        int tx = cr.X + Px(32), tw = Theme.Measure(Caption, Theme.ButtonFont).Width;
        Theme.Text(g, Caption, Theme.ButtonFont, new Rectangle(tx, cr.Y, Math.Min(tw + 2, ax - tx), cr.Height), fg);
        if (Sub.Length > 0)
        {
            int sx = tx + tw + Px(9);
            if (sx < ax - Px(10)) Theme.Text(g, Sub, Theme.Small, new Rectangle(sx, cr.Y, ax - Px(6) - sx, cr.Height), Theme.Mix(fg, body, 0.32f));
        }
        int csz = Px(12);
        Theme.Icon(g, VIcon.ChevronDown, new RectangleF(ax + (ArrowW - csz) / 2f, cr.Y + (cr.Height - csz) / 2f, csz, csz), Theme.OnAccent, Math.Max(1.2f, 1.6f * S));
        AddHit(new Rectangle(0, 0, ax, Height), "main", MainEnabled ? () => Play?.Invoke() : null, _mainTip, null, MainEnabled);
        AddHit(new Rectangle(ax, 0, Width - ax, Height), "menu", () => MenuRequested?.Invoke(), MenuTip);
    }
}

/// Строка состояния / баннер: значок, текст, ссылки-действия справа.
internal sealed class StatusLine : Canvas
{
    public Tone Tone;
    public string Message = "";
    public string? Glyph;
    public bool Filled = true;
    public bool Square;     // «Компактный»: полоса во всю ширину без скруглений, снизу — линия
    public readonly List<(string Text, Action Act)> Actions = new();
    /// Текст, тон или кнопки изменились (строку состояния «Компактного» стиля рисует другой элемент).
    public event Action? Changed;

    public StatusLine() { Height = Px(40); }

    public int Repaints;   // сколько раз строка реально перерисовывалась (для замеров)

    public void Set(Tone tone, string msg, string? glyph = null, params (string, Action)[] actions)
    {
        msg ??= "";
        // тот же текст и те же кнопки — не перерисовывать (действия всё равно берутся свежие: см. AddHit ниже)
        bool same = Tone == tone && Message == msg && Glyph == glyph && Actions.Count == actions.Length
                    && Actions.Select(a => a.Text).SequenceEqual(actions.Select(a => a.Item1));
        Tone = tone; Message = msg; Glyph = glyph;
        Actions.Clear(); Actions.AddRange(actions);
        if (!same) { Repaints++; Invalidate(); Changed?.Invoke(); }
    }

    public static (Color bg, Color fg, Color icon) Colors(Tone t) => t switch
    {
        Tone.Info => (Theme.BlueTint, Theme.Navy, Theme.Blue),
        Tone.Ok => (Theme.TealTint, Theme.Navy, Theme.TealDark),
        Tone.Warn => (Theme.OrangeTint, Theme.Navy, Theme.Orange),
        Tone.Error => (Theme.CoralTint, Theme.Navy, Theme.Coral),
        _ => (Theme.Paper, Theme.NavySoft, Theme.Hint),
    };

    protected override void Draw(Graphics g)
    {
        var (bg, fg, ic) = Colors(Tone);
        var r = new RectangleF(0, 0, Width, Height);
        if (Filled && Square)
        {
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, Width, Height);
            using var lp = new Pen(Theme.Line, Math.Max(1f, S));
            g.DrawLine(lp, 0, Height - 1, Width, Height - 1);
        }
        else if (Filled)
        {
            float rad = Math.Min(Px(12), Px(Theme.Spec.PanelRadius));
            Theme.FillRound(g, r, rad, bg);
            if (Tone == Tone.Neutral) Theme.StrokeRound(g, r, rad, Theme.Line, Math.Max(1f, S));
        }
        int x = Px(14);
        string glyph = Glyph ?? Tone switch { Tone.Warn => Theme.GWarning, Tone.Error => Theme.GError, Tone.Ok => Theme.GCheck, Tone.Info => Theme.GInfo, _ => Theme.GInfo };
        Theme.Glyph(g, glyph, new Rectangle(x - Px(2), 0, Px(20), Height), ic);
        x += Px(26);
        int right = Width - Px(10);
        for (int i = Actions.Count - 1; i >= 0; i--)
        {
            var (text, act) = Actions[i];
            int w = Theme.Measure(text, Theme.ButtonFont).Width + Px(22);
            var ar = new Rectangle(right - w, (Height - Px(28)) / 2, w, Px(28));
            string key = "a" + i;
            bool h = IsHover(key);
            float arad = Theme.Radius(ar.Height, S);
            Theme.FillRound(g, ar, arad, h ? Theme.Paper : Theme.Alpha(Theme.Paper, Filled ? 150 : 0));
            Theme.StrokeRound(g, ar, arad, h ? ic : Theme.Alpha(ic, 110), Math.Max(1f, S));
            Theme.Text(g, text, Theme.ButtonFont, ar, Theme.Navy, TextFormatFlags.HorizontalCenter);
            int idx = i;
            AddHit(ar, key, () => { if (idx < Actions.Count) Actions[idx].Act(); });
            right = ar.X - Px(8);
        }
        var tr = new Rectangle(x, 0, Math.Max(10, right - x - Px(4)), Height);
        bool cut = Theme.Measure(Message, Theme.Body).Width > tr.Width;
        if (cut && Height >= Px(50))
            Theme.Wrap(g, Message, Theme.Body, new Rectangle(tr.X, Px(6), tr.Width, Height - Px(12)), fg);
        else
            Theme.Text(g, Message, Theme.Body, tr, fg);
        if (cut) AddHit(tr, "msg", null, Message, null, false);
    }
}

/// Меню в стиле лаунчера.
internal static class Menus
{
    private sealed class Colors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Beige;
        public override Color MenuItemBorder => Theme.Beige;
        public override Color MenuBorder => Theme.Line;
        public override Color ToolStripDropDownBackground => Theme.Paper;
        public override Color ImageMarginGradientBegin => Theme.Paper;
        public override Color ImageMarginGradientMiddle => Theme.Paper;
        public override Color ImageMarginGradientEnd => Theme.Paper;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Paper;
        public override Color MenuItemSelectedGradientBegin => Theme.Beige;
        public override Color MenuItemSelectedGradientEnd => Theme.Beige;
        public override Color MenuItemPressedGradientBegin => Theme.BeigeDark;
        public override Color MenuItemPressedGradientEnd => Theme.BeigeDark;
    }

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is ToolStripMenuItem mi && mi.Tag is Color c && mi.Enabled) e.TextColor = c;
            else e.TextColor = e.Item.Enabled ? Theme.Navy : Theme.Hint;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? Theme.Hint : Theme.NavySoft;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // своя галочка: системная рисуется чёрной и теряется на тёмном фоне
            var r = e.ImageRectangle;
            if (r.Width <= 0) return;
            Theme.FillRound(e.Graphics, new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), Math.Max(3f, r.Height / 4f), Theme.TealTint);
            Theme.Glyph(e.Graphics, Theme.GCheck, r, Theme.TealText, Theme.IconsSmall);
        }
    }

    /// Оформление меню и выпадающих панелей (палитра берётся при каждой отрисовке).
    public static ToolStripRenderer NewRenderer() => new Renderer();

    public static ContextMenuStrip Create(Control owner)
    {
        float s = owner.DeviceDpi / 96f;
        var m = new ContextMenuStrip
        {
            Renderer = new Renderer(),
            Font = Theme.Body,
            ShowImageMargin = true,
            ImageScalingSize = new Size((int)(18 * s), (int)(18 * s)),
            Padding = new Padding(2, (int)(4 * s), 2, (int)(4 * s)),
        };
        return m;
    }

    public static ToolStripMenuItem Add(ContextMenuStrip m, string text, string glyph, Action act, bool enabled = true, Color? color = null, string? keys = null)
    {
        float s = m.ImageScalingSize.Width / 18f;
        var it = new ToolStripMenuItem(text)
        {
            Image = GlyphBitmap(glyph, color ?? Theme.NavySoft, m.ImageScalingSize.Width),
            Enabled = enabled,
            ShortcutKeyDisplayString = keys,
            Padding = new Padding(0, (int)(3 * s), 0, (int)(3 * s)),
            Tag = color,
        };
        it.Click += (_, _) => act();
        m.Items.Add(it);
        return it;
    }

    public static void Separator(ContextMenuStrip m) => m.Items.Add(new ToolStripSeparator());

    private static readonly Dictionary<string, Bitmap> Cache = new();

    public static Bitmap GlyphBitmap(string glyph, Color c, int size)
    {
        string key = glyph + "|" + c.ToArgb() + "|" + size;
        if (Cache.TryGetValue(key, out var bmp)) return bmp;
        bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var f = new Font(Theme.HasIcons ? "Segoe MDL2 Assets" : "Segoe UI", size * 0.62f, GraphicsUnit.Pixel);
            using var b = new SolidBrush(c);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, f, b, new RectangleF(0, 0, size, size + 1), sf);
        }
        Cache[key] = bmp;
        return bmp;
    }
}

/// Диалоги в стиле лаунчера (вместо серых MessageBox).
internal static class Dialogs
{
    private sealed class DialogForm : Form
    {
        public int Result = -1;
        public DialogForm() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            float s = DeviceDpi / 96f;
            using var b = new SolidBrush(Theme.Paper);
            g.FillRectangle(b, 0, ClientSize.Height - (int)(64 * s), ClientSize.Width, (int)(64 * s));
            using var p = new Pen(Theme.Line);
            g.DrawLine(p, 0, ClientSize.Height - (int)(64 * s), ClientSize.Width, ClientSize.Height - (int)(64 * s));
        }
    }

    private sealed class Badge : Surface
    {
        public Tone Tone;
        protected override void Draw(Graphics g)
        {
            var (bg, _, ic) = StatusLine.Colors(Tone);
            if (Tone == Tone.Neutral) { bg = Theme.TealTint; ic = Theme.TealDark; }
            Theme.FillRound(g, new RectangleF(0, 0, Width, Height), Width / 2f, bg);
            string gl = Tone switch { Tone.Warn => Theme.GWarning, Tone.Error => Theme.GError, Tone.Ok => Theme.GCheck, Tone.Info => Theme.GInfo, _ => Theme.GInfo };
            Theme.Glyph(g, gl, new Rectangle(0, 0, Width, Height), ic, Theme.IconsBig);
        }
    }

    public static int Ask(IWin32Window? owner, string title, string text, string[] buttons, int primary = 0, Tone tone = Tone.Info, int cancel = -1, Look primaryLook = Look.Primary)
    {
        using var f = new DialogForm
        {
            Text = "SR2 Mod Manager",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = owner == null,
            BackColor = Theme.Cream, Font = Theme.Body, KeyPreview = true,
        };
        float s = f.DeviceDpi / 96f;
        int P(float v) => (int)Math.Round(v * s);
        int width = P(500), textX = P(84), textW = width - textX - P(28);
        int titleH = Theme.WrapHeight(title, Theme.H2, textW);
        int bodyH = Math.Min(P(380), Theme.WrapHeight(text, Theme.Body, textW));
        int contentH = Math.Max(P(52), titleH + P(8) + bodyH);
        f.ClientSize = new Size(width, P(26) + contentH + P(24) + P(64));
        var badge = new Badge { Tone = tone, Bounds = new Rectangle(P(24), P(26), P(44), P(44)) };
        var lt = new Label { Text = title, Font = Theme.H2, ForeColor = Theme.Navy, AutoSize = false, Bounds = new Rectangle(textX, P(24), textW, titleH + P(2)), BackColor = Color.Transparent, UseMnemonic = false };
        var lb = new Label { Text = text, Font = Theme.Body, ForeColor = Theme.NavySoft, AutoSize = false, Bounds = new Rectangle(textX, P(24) + titleH + P(8), textW, bodyH + P(4)), BackColor = Color.Transparent, UseMnemonic = false };
        f.Controls.AddRange(new Control[] { badge, lt, lb });
        int right = width - P(20), by = f.ClientSize.Height - P(64) + P(14);
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            var look = i == primary ? primaryLook : Look.Secondary;
            var btn = new PillButton(buttons[i], look) { Height = P(36) };
            btn.Width = btn.PreferredWidth();
            btn.Location = new Point(right - btn.Width, by);
            btn.BackColor = Theme.Paper;
            int idx = i;
            btn.Click += (_, _) => { f.Result = idx; f.Close(); };
            f.Controls.Add(btn);
            right -= btn.Width + P(10);
        }
        f.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { f.Result = primary; f.Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { f.Result = cancel; f.Close(); e.Handled = true; }
        };
        if (owner != null) f.ShowDialog(owner); else f.ShowDialog();
        return f.Result < 0 ? cancel : f.Result;
    }

    public static void Info(IWin32Window? owner, string title, string text, Tone tone = Tone.Info)
        => Ask(owner, title, text, new[] { "Понятно" }, 0, tone, 0);

    public static string? Prompt(IWin32Window? owner, string title, string label, string value, string ok = "Сохранить")
    {
        using var f = new DialogForm
        {
            Text = "SR2 Mod Manager",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = Theme.Cream, Font = Theme.Body, KeyPreview = true,
        };
        float s = f.DeviceDpi / 96f;
        int P(float v) => (int)Math.Round(v * s);
        int width = P(440);
        f.ClientSize = new Size(width, P(24) + P(30) + P(22) + P(40) + P(26) + P(64));
        var lt = new Label { Text = title, Font = Theme.H2, ForeColor = Theme.Navy, AutoSize = false, Bounds = new Rectangle(P(24), P(22), width - P(48), P(30)), BackColor = Color.Transparent, UseMnemonic = false };
        var lb = new Label { Text = label, Font = Theme.Body, ForeColor = Theme.NavySoft, AutoSize = false, Bounds = new Rectangle(P(24), P(54), width - P(48), P(22)), BackColor = Color.Transparent, UseMnemonic = false };
        var tf = new TextField("") { Bounds = new Rectangle(P(24), P(80), width - P(48), P(38)), Round = false };
        tf.Text = value;
        f.Controls.AddRange(new Control[] { lt, lb, tf });
        string? result = null;
        int by = f.ClientSize.Height - P(64) + P(14);
        var okb = new PillButton(ok, Look.Primary) { Height = P(36), BackColor = Theme.Paper };
        okb.Width = okb.PreferredWidth();
        okb.Location = new Point(width - P(20) - okb.Width, by);
        var cb = new PillButton("Отмена", Look.Secondary) { Height = P(36), BackColor = Theme.Paper };
        cb.Width = cb.PreferredWidth();
        cb.Location = new Point(okb.Left - P(10) - cb.Width, by);
        void Ok() { if (!string.IsNullOrWhiteSpace(tf.Text)) { result = tf.Text.Trim(); f.Close(); } }
        okb.Click += (_, _) => Ok();
        cb.Click += (_, _) => f.Close();
        f.Controls.Add(okb); f.Controls.Add(cb);
        f.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Ok(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { f.Close(); e.Handled = true; }
        };
        f.Shown += (_, _) => { tf.Box.Focus(); tf.Box.SelectAll(); };
        f.ShowDialog(owner);
        return result;
    }
}


