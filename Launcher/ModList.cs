// ModList.cs — список модов карточками: переключатель, значок, название, описание, метки, статус.
// Ниже установленных — «Доступны для установки» (из ModManager\Library, «Загрузок», своих папок).
// При поиске совпадения подсвечиваются, а в длинном описании показывается фрагмент с совпадением.
//
// Быстродействие (сотни модов):
//  • виртуальная прокрутка — видимые карточки находятся арифметически, остальные не трогаются вовсе;
//  • каждая карточка кэшируется готовой картинкой и рисуется заново, только когда меняется то, что
//    на ней видно (подпись Sig: отметка, статус, метки, наведение, ширина…);
//  • наведение, выбор и переключение перерисовывают одну-две карточки, а не весь список;
//  • колесо мыши прокручивает плавно (~150 мс), значки модов грузятся в фоне (IconCache).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed class ModList : Canvas
{
    private readonly Model _m;
    public List<ModEntry> View { get; private set; } = new();
    public string? SelectedId { get; private set; }
    /// Текущий поисковый запрос (null — без подсветки).
    public SearchQuery? Query;
    public event Action? SelectionChanged;
    public event Action<ModEntry>? ToggleRequested;
    public event Action<ModEntry>? InstallRequested;
    public event Action<ModEntry, Point>? MenuRequested;
    public event Action<ModEntry>? DeleteRequested;
    public event Action<char>? TypeRequested;   // начали печатать в списке — буквы уходят в поиск
    public string EmptyTitle = "", EmptyText = "";
    public string EmptyAction = "";          // кнопка в пустом состоянии ("" — без кнопки)
    public event Action? EmptyActionClick;
    public bool EmptyHappy;
    public IconCache? Icons;
    private bool _drop;
    private int _scroll;
    private bool _dragThumb, _hoverBar;
    private int _dragOffset;
    private int _groupAt = -1;               // индекс первого доступного (не установленного) мода в View
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    // плавная прокрутка колесом
    private readonly System.Windows.Forms.Timer _wheel = new() { Interval = 10 };
    private int _animFrom, _animTo;
    private long _animStart;
    private const int WheelMs = 150;

    // кэш готовых карточек: подпись → картинка
    public static bool CacheEnabled = true;
    private sealed class Cached { public Bitmap Bmp = null!; public long Used; }
    private readonly Dictionary<string, Cached> _cache = new();
    private readonly Dictionary<string, string> _shown = new(StringComparer.OrdinalIgnoreCase); // Id → подпись нарисованной карточки
    private long _clock;
    private int _cacheW = -1;
    private Rectangle _clip;

    // замеры (--bench)
    public int Paints, CardsDrawn, CacheHits, CacheMisses;
    public long PaintedPixels;
    public double PaintMs, MaxPaintMs;
    public bool RecordFrames { get; set; }
    public readonly List<double> FrameMs = new();
    public int CacheCount => _cache.Count;
    public void ResetStats() { Paints = CardsDrawn = CacheHits = CacheMisses = 0; PaintedPixels = 0; PaintMs = MaxPaintMs = 0; FrameMs.Clear(); }

    public ModList(Model m)
    {
        _m = m;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        _wheel.Tick += (_, _) => WheelStep();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _wheel.Dispose(); ClearCache(); }
        base.Dispose(disposing);
    }

    public bool DropHint { get => _drop; set { if (_drop != value) { _drop = value; Invalidate(); } } }

    private int CardH => Px(82);
    private int Gap => Px(8);
    private int Pad => Px(3);
    private int BarW => Px(10);
    private int HeaderH => Px(34);
    private int Pitch => CardH + Gap;
    private int ShadowMargin => Px(10);            // тень выбранной карточки выходит за её края
    /// Верх карточки i в координатах содержимого (без прокрутки).
    private int CardTop(int i) => Pad + i * Pitch + (_groupAt >= 0 && i >= _groupAt ? HeaderH : 0);
    private int ContentH => View.Count == 0 ? 0 : Pad * 2 + View.Count * Pitch - Gap + (_groupAt >= 0 ? HeaderH : 0);
    private int MaxScroll => Math.Max(0, ContentH - Height);
    private bool HasBar => ContentH > Height;
    private int CardW => Width - Pad * 2 - (HasBar ? BarW + Px(2) : 0);

    private Rectangle CardRect(int i) => new(Pad, CardTop(i) - _scroll, CardW, CardH);

    /// Карточки, задевающие полосу [top, bottom) содержимого: O(видимых), без обхода всего списка.
    private (int First, int Last) Range(int top, int bottom)
    {
        int n = View.Count;
        if (n == 0) return (0, -1);
        int i = Math.Clamp((top - Pad) / Pitch, 0, n - 1);
        while (i > 0 && CardTop(i - 1) + CardH > top) i--;
        while (i < n - 1 && CardTop(i) + CardH <= top) i++;
        if (CardTop(i) >= bottom) return (i, i - 1);
        int j = i;
        while (j < n - 1 && CardTop(j + 1) < bottom) j++;
        return (i, j);
    }

    private (int First, int Last) VisibleRange() => Range(_scroll - ShadowMargin, _scroll + Height + ShadowMargin);

    private Rectangle ThumbRect()
    {
        if (!HasBar) return Rectangle.Empty;
        int track = Height - Px(6);
        int th = Math.Max(Px(40), (int)((long)track * Height / Math.Max(1, ContentH)));
        int ty = Px(3) + (int)((long)(track - th) * _scroll / Math.Max(1, MaxScroll));
        return new Rectangle(Width - BarW + Px(3), ty, BarW - Px(5), th);
    }

    public ModEntry? Selected => _m.FindAny(SelectedId);

    private int IndexOf(string? id) => id != null && _index.TryGetValue(id, out var i) && i < View.Count ? i : -1;
    private ModEntry? ById(string id) { int i = IndexOf(id); return i >= 0 ? View[i] : null; }

    public void SetView(List<ModEntry> view)
    {
        int group = view.FindIndex(x => x.Available);
        bool same = view.Count == View.Count && group == _groupAt;
        for (int i = 0; same && i < view.Count; i++) same = ReferenceEquals(view[i], View[i]);
        View = view;
        _groupAt = group;
        _index.Clear();
        for (int i = 0; i < view.Count; i++) _index.TryAdd(view[i].Id, i);
        int before = _scroll;
        ClampScroll();
        // тот же список — перерисовать только изменившиеся карточки
        if (same && before == _scroll) RefreshCards();
        else Invalidate();
    }

    public void Select(string? id, bool ensureVisible = true)
    {
        if (string.Equals(SelectedId, id, StringComparison.OrdinalIgnoreCase)) { if (ensureVisible) EnsureVisible(); return; }
        string? old = SelectedId;
        SelectedId = id;
        if (ensureVisible) EnsureVisible();
        InvalidateCard(old);
        InvalidateCard(id);
        SelectionChanged?.Invoke();
    }

    public void ScrollToTop() { StopWheel(); if (_scroll != 0) { _scroll = 0; Invalidate(); } }

    public void EnsureVisible()
    {
        int i = IndexOf(SelectedId);
        if (i < 0) return;
        var r = CardRect(i);
        int before = _scroll;
        int top = r.Top - (i == _groupAt ? HeaderH : 0); // у первого доступного — вместе с заголовком группы
        if (top < 0) _scroll += top - Pad;
        else if (r.Bottom > Height) _scroll += r.Bottom - Height + Pad;
        ClampScroll();
        if (_scroll != before) { StopWheel(); Invalidate(); }
    }

    private void ClampScroll() => _scroll = Math.Clamp(_scroll, 0, MaxScroll);

    /// Перерисовать одну карточку (с полями под тень).
    public void InvalidateCard(string? id)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        var r = CardRect(i);
        r.Inflate(ShadowMargin, ShadowMargin);
        if (r.IntersectsWith(ClientRectangle)) Invalidate(r);
    }

    /// После изменения отметок, статусов или проверки совместимости: перерисовать только те видимые
    /// карточки, вид которых изменился. Возвращает их число.
    public int RefreshCards()
    {
        if (View.Count == 0 || Width <= 0 || Height <= 0) { Invalidate(); return -1; }
        int changed = 0, w = CardW;
        var (a, b) = VisibleRange();
        for (int i = a; i <= b; i++)
        {
            var c = Info(View[i], w);
            if (!_shown.TryGetValue(c.M.Id, out var sig) || sig != c.Sig) { InvalidateCard(c.M.Id); changed++; }
        }
        return changed;
    }

    /// Наведение/нажатие — перерисовать только затронутые карточки.
    protected override void InvalidateHot(string? a, string? b)
    {
        foreach (var k in new[] { a, b })
        {
            if (k == null) continue;
            string? id = KeyId(k);
            if (id != null) InvalidateCard(id); else Invalidate();
        }
    }

    /// Id мода из ключа зоны: card:ID, tgl:ID, inst:ID, st:ID, tag:ID:n.
    private static string? KeyId(string key)
    {
        int c = key.IndexOf(':');
        if (c <= 0) return null;
        switch (key.Substring(0, c))
        {
            case "card": case "tgl": case "inst": case "st": return key.Substring(c + 1);
            case "tag": int c2 = key.LastIndexOf(':'); return c2 > c ? key.Substring(c + 1, c2 - c - 1) : null;
            default: return null;
        }
    }

    private int _lastH = -1;
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // выбранная карточка была видна целиком — остаётся видна и после смены высоты (баннер, размер окна)
        bool keep = false;
        if (_lastH > 0 && SelectedId != null)
        {
            int i = IndexOf(SelectedId);
            if (i >= 0) { var r = CardRect(i); keep = r.Top >= 0 && r.Bottom <= _lastH; }
        }
        _lastH = Height;
        ClampScroll();
        if (_animTo > MaxScroll) _animTo = MaxScroll;
        if (keep) EnsureVisible();
        Invalidate();
    }

    private List<(int Start, int Length)>? Marks(string? s) => Query == null || string.IsNullOrEmpty(s) ? null : ModSearch.Highlights(s, Query);

    /// Если совпадение в тексте не влезает в ширину — фрагмент вокруг него: «…слово совпадение дальше».
    private (string Text, List<(int Start, int Length)>? Marks) Snippet(string text, Font f, int width)
    {
        var marks = Marks(text);
        if (marks == null || marks.Count == 0) return (text, marks);
        var (st, len) = marks[0];
        if (Theme.PrefixWidth(text, st + len, f) <= width - Px(16)) return (text, marks);
        int cut = Math.Max(0, st - 26);
        int sp = text.IndexOf(' ', cut);
        if (sp >= 0 && sp < st) cut = sp + 1;
        if (cut <= 0) return (text, marks);
        var shifted = marks.Where(x => x.Start >= cut).Select(x => (x.Start - cut + 1, x.Length)).ToList();
        return ("…" + text.Substring(cut), shifted);
    }

    // ───────── кэш карточек ─────────

    private int CacheMax => Math.Clamp((int)(12L * 1024 * 1024 / Math.Max(1L, (long)Math.Max(1, CardW) * CardH * 4)), 12, 64);

    private void ClearCache()
    {
        foreach (var c in _cache.Values) c.Bmp.Dispose();
        _cache.Clear();
        _shown.Clear();
    }

    private void TrimCache()
    {
        int max = CacheMax;
        if (_cache.Count <= max) return;
        foreach (var kv in _cache.OrderBy(kv => kv.Value.Used).Take(_cache.Count - max * 3 / 4).ToList())
        {
            kv.Value.Bmp.Dispose();
            _cache.Remove(kv.Key);
        }
    }

    // ───────── карточка: раскладка и подпись ─────────

    private sealed class TagPill
    {
        public string Text = "";
        public Color Bg, Fg;
        public string? Tip;
        public Rectangle R;
    }

    /// Всё, что видно на карточке (координаты — от левого верхнего угла карточки).
    private sealed class CardInfo
    {
        public ModEntry M = null!;
        public int W, H;
        public bool Sel, Avail, On, Hover, HTgl, HInst, DInst, Readable, ShowIcon, NoDesc;
        public Color Accent;
        public (string Text, Color Bg, Color Fg, string Tip) St;
        public Rectangle Tg, Ico, Pill;
        public Bitmap? Img;
        public long ImgSerial;
        public string Initials = "";
        public Color AvBg, AvFg;
        public string Title = "";
        public Rectangle TitleR;
        public List<(int Start, int Length)>? TitleMarks, VerMarks, DescMarks, AuthorMarks;
        public string? Ver;
        public Rectangle VerR;
        public string Desc = "";
        public Color DescColor;
        public Rectangle DescR;
        public string Author = "";
        public Rectangle AuthorR;
        public readonly List<TagPill> Tags = new();
        public string? More, MoreTip;
        public Rectangle MoreR;
        public string Sig = "";
    }

    private CardInfo Info(ModEntry m, int w)
    {
        string id = m.Id;
        var c = new CardInfo { M = m, W = w, H = CardH };
        string? hid = HoverKey == null ? null : KeyId(HoverKey);
        c.Sel = string.Equals(id, SelectedId, StringComparison.OrdinalIgnoreCase);
        c.Avail = m.Available;
        c.HTgl = IsHover("tgl:" + id);
        c.HInst = IsHover("inst:" + id);
        c.DInst = IsDown("inst:" + id);
        c.Hover = hid != null && hid.Equals(id, StringComparison.OrdinalIgnoreCase);
        c.On = _m.On(id);
        c.Readable = m.Readable;
        c.Accent = _m.Accent(m);
        c.St = _m.Status(m);
        int h = c.H;
        c.Tg = new Rectangle(Px(22), (h - Px(24)) / 2, Px(44), Px(24));
        c.ShowIcon = w >= Px(440);
        int tx;
        if (c.ShowIcon)
        {
            int isz = Px(40);
            c.Ico = new Rectangle(c.Tg.Right + Px(14), (h - isz) / 2, isz, isz);
            tx = c.Ico.Right + Px(12);
            if (Icons != null) c.Img = Icons.Get(m, isz, out _, out c.ImgSerial);
            if (c.Img == null) { c.Initials = IconCache.Initials(m.DisplayName); (c.AvBg, c.AvFg) = IconCache.AvatarColors(id); }
        }
        else tx = c.Tg.Right + Px(16);

        // статус справа
        int pw = Theme.PillWidth(c.St.Text, Theme.SmallBold, Px(22), false);
        c.Pill = new Rectangle(w - Px(14) - pw, Px(12), pw, Px(22));

        // название + версия
        int titleRight = c.Pill.X - Px(10);
        c.Title = m.DisplayName;
        int tw = Math.Min(Theme.Measure(c.Title, Theme.Strong).Width, Math.Max(10, titleRight - tx));
        c.TitleR = new Rectangle(tx, Px(10), tw + 2, Px(24));
        c.TitleMarks = Marks(c.Title);
        int vx = tx + tw + Px(8);
        if (!string.IsNullOrEmpty(m.Version) && vx + Px(30) < titleRight)
        {
            c.Ver = "v" + m.Version;
            c.VerR = new Rectangle(vx, Px(11), titleRight - vx, Px(24));
            c.VerMarks = Marks(c.Ver);
        }

        // описание (при поиске — фрагмент с совпадением)
        int dw = w - Px(16) - tx;
        c.DescR = new Rectangle(tx, Px(34), dw, Px(20));
        c.NoDesc = string.IsNullOrWhiteSpace(m.Description);
        if (c.NoDesc) { c.Desc = m.Readable ? "Описания нет" : "Не удалось прочитать метаданные DLL"; c.DescColor = Theme.Hint; }
        else
        {
            var (desc, dm) = Snippet(m.Description.Replace("\r", " ").Replace("\n", " "), Theme.Body, dw);
            c.Desc = desc; c.DescMarks = dm; c.DescColor = Theme.NavySoft;
        }

        // строка меток: автор, затем метки (первыми — совместимость)
        int ty = Px(57), th = Px(18), x = tx, limit = w - Px(14);
        c.Author = string.IsNullOrWhiteSpace(m.Author) ? m.FileName : m.Author;
        int aw = Math.Min(Theme.Measure(c.Author, Theme.Small).Width, Px(170));
        c.AuthorR = new Rectangle(x, ty, aw + 1, th);
        c.AuthorMarks = Marks(c.Author);
        x += aw + Px(10);
        var tags = BuildTags(m, c.On);
        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            int tw2 = Theme.PillWidth(t.Text, Theme.SmallBold, th, false);
            if (x + tw2 > limit)
            {
                string more = "+" + (tags.Count - i);
                int mw = Theme.PillWidth(more, Theme.SmallBold, th, false);
                if (x + mw <= limit + Px(4))
                {
                    c.More = more;
                    c.MoreR = new Rectangle(x, ty, mw, th);
                    c.MoreTip = string.Join("\n", tags.Skip(i).Select(z => "• " + (z.Tip ?? z.Text).Split('\n')[0]));
                }
                break;
            }
            t.R = new Rectangle(x, ty, tw2, th);
            c.Tags.Add(t);
            x += tw2 + Px(6);
        }
        c.Sig = Signature(c);
        return c;
    }

    private static void Marks(StringBuilder sb, List<(int Start, int Length)>? marks)
    {
        if (marks == null) return;
        foreach (var (s, l) in marks) sb.Append('[').Append(s).Append(',').Append(l).Append(']');
    }

    private string Signature(CardInfo c)
    {
        var sb = new StringBuilder(320);
        sb.Append(c.M.Id).Append('|').Append(c.W).Append('x').Append(c.H).Append('@').Append(DeviceDpi).Append('|').Append(BackColor.ToArgb()).Append('|')
          .Append(c.Sel ? 'S' : '-').Append(c.Hover ? 'H' : '-').Append(c.HTgl ? 'T' : '-').Append(c.HInst ? 'I' : '-').Append(c.DInst ? 'D' : '-')
          .Append(c.Avail ? 'A' : '-').Append(c.On ? '1' : '0').Append(c.Readable ? 'R' : '-').Append(c.ShowIcon ? 'i' : '-').Append('|')
          .Append(c.Accent.ToArgb()).Append('|').Append(c.St.Text).Append(c.St.Bg.ToArgb()).Append(c.St.Fg.ToArgb()).Append('|');
        if (c.Img != null) sb.Append("img").Append(c.ImgSerial); else sb.Append(c.Initials).Append(c.AvBg.ToArgb());
        sb.Append('|').Append(c.Title).Append(c.TitleR.Width); Marks(sb, c.TitleMarks);
        sb.Append('|').Append(c.Ver); Marks(sb, c.VerMarks);
        sb.Append('|').Append(c.Desc).Append(c.DescColor.ToArgb()); Marks(sb, c.DescMarks);
        sb.Append('|').Append(c.Author).Append(c.AuthorR.Width); Marks(sb, c.AuthorMarks);
        foreach (var t in c.Tags) sb.Append('|').Append(t.Text).Append(t.Bg.ToArgb()).Append(t.Fg.ToArgb());
        sb.Append('|').Append(c.More);
        return sb.ToString();
    }

    private static string WrapTip(string s, int width = 90)
    {
        var sb = new StringBuilder();
        foreach (var para in s.Split('\n'))
        {
            int col = 0;
            foreach (var word in para.Split(' '))
            {
                if (col > 0 && col + word.Length + 1 > width) { sb.Append('\n'); col = 0; }
                else if (col > 0) { sb.Append(' '); col++; }
                sb.Append(word); col += word.Length;
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string CompatTip(CompatIssue i)
        => WrapTip(i.Title + "\n" + i.Text + (i.Fix.Length > 0 ? "\nЧто сделать: " + i.Fix : ""));

    private List<TagPill> BuildTags(ModEntry m, bool on)
    {
        var tags = new List<TagPill>();
        void Add(string text, Color bg, Color fg, string? tip = null) => tags.Add(new TagPill { Text = text, Bg = bg, Fg = fg, Tip = tip });
        if (m.Available)
        {
            Add("в «" + m.SourceLabel + "»", Theme.BlueTint, Theme.BlueText);
            foreach (var d in m.DependsOn)
            {
                var di = _m.Find(d);
                var da = di ?? _m.FindAny(d);
                if (di != null) Add("нужен " + di.DisplayName, Theme.Beige, Theme.NavySoft);
                else if (da != null) Add("нужен " + da.DisplayName + " (доступен)", Theme.BlueTint, Theme.BlueText);
                else if (_m.IsKnownLib(d)) Add(d, Theme.Beige, Theme.NavySoft, "Библиотека " + d + " уже есть: " + _m.Env!.WhereOf(d));
                else Add($"нет «{d}»", Theme.CoralTint, Theme.CoralText);
            }
            return tags;
        }
        // совместимость — первыми: из-за этого мод может не загрузиться
        foreach (var i in _m.CompatBadges(m))
        {
            bool err = i.Level == CompatLevel.Error;
            Add(i.Badge, err ? Theme.CoralTint : Theme.OrangeTint, err ? Theme.CoralText : Theme.OrangeText, CompatTip(i));
        }
        var u = _m.UpdateFor(m);
        if (u != null)
        {
            if (u.Kind == "outdated") Add("устарел?", Theme.OrangeTint, Theme.OrangeText, WrapTip("По журналу игры: " + u.Reason));
            else Add("обновление v" + u.Version, Theme.Blue, Color.White, WrapTip("Новая версия v" + u.Version + ": " + u.Where));
        }
        foreach (var (text, tone) in _m.BaseProblems(m))
            Add(text, tone == Tone.Error ? Theme.CoralTint : Theme.OrangeTint, tone == Tone.Error ? Theme.CoralText : Theme.OrangeText);
        if (m.Kind == "Plugin") Add("плагин", Theme.BlueTint, Theme.BlueText);
        foreach (var d in m.DependsOn)
        {
            var dm = _m.Find(d);
            if (dm != null && (_m.On(dm.Id) || !on)) Add("нужен " + dm.DisplayName, Theme.Beige, Theme.NavySoft);
        }
        var ls = _m.LogFor(m);
        if (ls != null && ls.Errors == 0 && ls.Warnings > 0)
            Add(ls.Warnings + " " + Theme.Plural(ls.Warnings, "предупреждение", "предупреждения", "предупреждений"), Theme.OrangeTint, Theme.OrangeText,
                ls.LastWarning.Length > 0 ? WrapTip(ls.LastWarning.Length > 400 ? ls.LastWarning.Substring(0, 400) + "…" : ls.LastWarning) : null);
        return tags;
    }

    // ───────── отрисовка ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        _clip = e.ClipRectangle;
        long t0 = Stopwatch.GetTimestamp();
        base.OnPaint(e);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Paints++;
        PaintMs += ms;
        if (ms > MaxPaintMs) MaxPaintMs = ms;
        PaintedPixels += (long)_clip.Width * _clip.Height;
        if (RecordFrames) FrameMs.Add(ms);
    }

    protected override void Draw(Graphics g)
    {
        if (View.Count == 0) { DrawEmpty(g); if (_drop) DrawDrop(g); return; }
        int w = CardW;
        if (w != _cacheW) { ClearCache(); _cacheW = w; }
        var clip = Rectangle.Inflate(_clip, ShadowMargin, ShadowMargin);
        if (_groupAt >= 0)
        {
            var hr = new Rectangle(Pad, CardTop(_groupAt) - HeaderH - _scroll, w, HeaderH);
            if (hr.IntersectsWith(_clip)) DrawGroupHeader(g, hr);
        }
        var (a, b) = VisibleRange();
        for (int i = a; i <= b; i++)
        {
            var r = CardRect(i);
            var c = Info(View[i], w);
            AddCardHits(c, r);                       // зоны — у всех видимых, даже если рисуется одна карточка
            if (r.IntersectsWith(clip)) PaintCard(g, c, r);
        }
        if (HasBar)
        {
            var tr = ThumbRect();
            Theme.FillRound(g, tr, tr.Width / 2f, _dragThumb ? Theme.NavySoft : _hoverBar ? Theme.LineDark : Theme.Line);
        }
        if (_drop) DrawDrop(g);
    }

    private void PaintCard(Graphics g, CardInfo c, Rectangle r)
    {
        CardsDrawn++;
        _shown[c.M.Id] = c.Sig;
        if (!CacheEnabled) { DrawCard(g, c, r.X, r.Y); return; }
        // тень выходит за края картинки — её рисуем вживую, а в углах картинки она уже есть
        if (c.Sel || c.Hover) Theme.Shadow(g, r, Px(14), Px(7), c.Sel ? 20 : 12);
        if (!_cache.TryGetValue(c.Sig, out var e))
        {
            CacheMisses++;
            e = new Cached { Bmp = RenderCard(c), Used = ++_clock };
            _cache[c.Sig] = e;
            TrimCache();
        }
        else CacheHits++;
        e.Used = ++_clock;
        var im = g.InterpolationMode; var pm = g.PixelOffsetMode; var cm = g.CompositingMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(e.Bmp, new Rectangle(r.X, r.Y, c.W, c.H), 0, 0, c.W, c.H, GraphicsUnit.Pixel);
        g.InterpolationMode = im; g.PixelOffsetMode = pm; g.CompositingMode = cm;
    }

    // ── картинка карточки для кэша рисуется в DIB-секции через HDC — как буфер WinForms на экране:
    // иначе GDI-текст (TextRenderer, ClearType) на Bitmap из GDI+ сглаживается по чёрному и получает тёмную кайму.
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void CopyMemory(IntPtr dst, IntPtr src, UIntPtr count);

    private Bitmap RenderCard(CardInfo c)
    {
        int w = Math.Max(1, c.W), h = Math.Max(1, c.H);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), dib = IntPtr.Zero, old = IntPtr.Zero, bits = IntPtr.Zero;
        try
        {
            if (dc != IntPtr.Zero) dib = CreateDIBSection(dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {   // GDI не дала буфер — рисуем как есть (текст может быть чуть темнее)
                using var bg = Graphics.FromImage(bmp);
                bg.SmoothingMode = SmoothingMode.AntiAlias; bg.PixelOffsetMode = PixelOffsetMode.Half;
                bg.Clear(BackColor); DrawCard(bg, c, 0, 0);
                return bmp;
            }
            old = SelectObject(dc, dib);
            using (var g = Graphics.FromHdc(dc))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(BackColor);
                DrawCard(g, c, 0, 0);
            }
            GdiFlush();
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                if (d.Stride == w * 4) CopyMemory(d.Scan0, bits, (UIntPtr)(uint)(w * 4 * h));
                else for (int y = 0; y < h; y++) CopyMemory(d.Scan0 + y * d.Stride, bits + y * w * 4, (UIntPtr)(uint)(w * 4));
            }
            finally { bmp.UnlockBits(d); }
            return bmp;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(dc, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (dc != IntPtr.Zero) DeleteDC(dc);
        }
    }

    /// Нарисовать карточку с левым верхним углом в (ox, oy). TextRenderer не учитывает Transform,
    /// поэтому сдвиг — явный (в кэш карточка рисуется в (0, 0)).
    private void DrawCard(Graphics g, CardInfo c, int ox, int oy)
    {
        var r = new Rectangle(ox, oy, c.W, c.H);
        Rectangle O(Rectangle q) => new(q.X + ox, q.Y + oy, q.Width, q.Height);
        bool sel = c.Sel, avail = c.Avail, hover = c.Hover;
        float rad = Px(14);
        if (sel || hover) Theme.Shadow(g, r, rad, Px(7), sel ? 20 : 12);
        Theme.FillRound(g, r, rad, sel ? Theme.Selected : avail && !hover ? Theme.Mix(Theme.Paper, Theme.Cream, 0.45f) : Theme.Paper);
        if (avail && !sel)
        {   // ещё не установлен — пунктирная рамка
            using var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), rad);
            using var pen = new Pen(hover ? Theme.Blue : Theme.Alpha(Theme.Blue, 120), Math.Max(1f, S)) { DashPattern = new[] { 4f, 3f } };
            g.DrawPath(pen, path);
        }
        else Theme.StrokeRound(g, r, rad, sel ? Theme.Teal : hover ? Theme.LineDark : Theme.Line, sel ? Math.Max(1.5f, 1.6f * S) : Math.Max(1f, S));

        // полоска состояния
        Theme.FillRound(g, new RectangleF(r.X + Px(7), r.Y + Px(18), Px(4), r.Height - Px(36)), Px(2), c.Accent);

        // переключатель (или «установить» у доступного)
        var tg = O(c.Tg);
        if (avail)
        {
            Theme.FillRound(g, tg, tg.Height / 2f, c.DInst ? Theme.BlueText : c.HInst ? Theme.Blue : Theme.BlueTint);
            Theme.Glyph(g, Theme.GDownload, tg, c.HInst || c.DInst ? Color.White : Theme.BlueText, Theme.IconsSmall);
        }
        else Theme.Toggle(g, tg, c.On, c.HTgl, !c.Readable);

        // значок мода (или аватар с буквами, пока значка нет)
        if (c.ShowIcon)
        {
            var ir = O(c.Ico);
            if (c.Img != null) g.DrawImage(c.Img, ir, 0, 0, c.Img.Width, c.Img.Height, GraphicsUnit.Pixel);
            else
            {
                Theme.FillRound(g, ir, ir.Width * 0.25f, avail ? Theme.Mix(c.AvBg, Theme.Paper, 0.35f) : c.AvBg);
                Theme.Text(g, c.Initials, Theme.Strong, ir, avail ? Theme.Mix(c.AvFg, Theme.Paper, 0.2f) : c.AvFg, TextFormatFlags.HorizontalCenter);
            }
        }

        // статус справа
        Theme.Pill(g, r.Right - Px(14), r.Y + Px(12), Px(22), c.St.Text, Theme.SmallBold, c.St.Bg, c.St.Fg, null, null, true);

        // название + версия
        Theme.TextMarked(g, c.Title, Theme.Strong, O(c.TitleR), Theme.Navy, c.TitleMarks);
        if (c.Ver != null) Theme.TextMarked(g, c.Ver, Theme.Small, O(c.VerR), Theme.Hint, c.VerMarks);

        // описание
        if (c.NoDesc) Theme.Text(g, c.Desc, Theme.Body, O(c.DescR), c.DescColor);
        else Theme.TextMarked(g, c.Desc, Theme.Body, O(c.DescR), c.DescColor, c.DescMarks);

        // автор и метки
        Theme.TextMarked(g, c.Author, Theme.Small, O(c.AuthorR), Theme.Hint, c.AuthorMarks);
        foreach (var t in c.Tags) Theme.Pill(g, t.R.X + ox, t.R.Y + oy, t.R.Height, t.Text, Theme.SmallBold, t.Bg, t.Fg);
        if (c.More != null) Theme.Pill(g, c.MoreR.X + ox, c.MoreR.Y + oy, c.MoreR.Height, c.More, Theme.SmallBold, Theme.Beige, Theme.NavySoft);
    }

    private void AddCardHits(CardInfo c, Rectangle r)
    {
        string id = c.M.Id;
        Rectangle O(Rectangle q) => new(q.X + r.X, q.Y + r.Y, q.Width, q.Height);
        void Menu(Point pt) { Select(id); var m = ById(id); if (m != null) MenuRequested?.Invoke(m, pt); }
        AddHit(r, "card:" + id, () => Select(id), null, Menu, false);
        for (int i = 0; i < c.Tags.Count; i++)
            if (c.Tags[i].Tip != null) AddHit(O(c.Tags[i].R), "tag:" + id + ":" + i, () => Select(id), c.Tags[i].Tip, Menu, false);
        if (c.More != null) AddHit(O(c.MoreR), "tag:" + id + ":more", () => Select(id), c.MoreTip, Menu, false);
        var tg = Rectangle.Inflate(O(c.Tg), Px(8), Px(10));
        if (c.Avail)
            AddHit(tg, "inst:" + id, () => { Select(id); var m = ById(id); if (m != null) InstallRequested?.Invoke(m); }, "Установить в Mods (Enter)", Menu);
        else
            AddHit(tg, "tgl:" + id, () => { Select(id); var m = ById(id); if (m != null) ToggleRequested?.Invoke(m); }, c.On ? "Выключить (Пробел)" : "Включить (Пробел)", Menu);
        AddHit(O(c.Pill), "st:" + id, () => Select(id), c.St.Tip, Menu, false);
    }

    private void DrawGroupHeader(Graphics g, Rectangle r)
    {
        if (r.Bottom < 0 || r.Top > Height) return;
        int n = View.Count - _groupAt;
        string title = "ДОСТУПНЫ ДЛЯ УСТАНОВКИ · " + n;
        int x = r.X + Px(6), y = r.Y + (_groupAt == 0 ? Px(2) : Px(8));
        var ts = Theme.Measure(title, Theme.Section);
        Theme.Glyph(g, Theme.GDownload, new Rectangle(x, y, Px(16), Px(18)), Theme.BlueText, Theme.IconsSmall);
        Theme.Text(g, title, Theme.Section, new Rectangle(x + Px(20), y, ts.Width + 2, Px(18)), Theme.BlueText);
        string hint = "не установлены — Enter или кнопка ↓ ставит в Mods";
        int hw = Theme.Measure(hint, Theme.Small).Width;
        int lineFrom = x + Px(20) + ts.Width + Px(10), lineTo = r.Right - Px(6);
        if (lineTo - lineFrom > hw + Px(40))
        {
            Theme.Text(g, hint, Theme.Small, new Rectangle(lineTo - hw, y, hw + 1, Px(18)), Theme.Hint);
            lineTo -= hw + Px(10);
        }
        using var p = new Pen(Theme.Alpha(Theme.Blue, 70), Math.Max(1f, S));
        if (lineTo > lineFrom) g.DrawLine(p, lineFrom, y + Px(9), lineTo, y + Px(9));
    }

    private void DrawEmpty(Graphics g)
    {
        var r = new Rectangle(Pad, Pad, Width - Pad * 2, Height - Pad * 2);
        Theme.FillRound(g, r, Px(16), Theme.Paper);
        Theme.StrokeRound(g, r, Px(16), Theme.Line, Math.Max(1f, S));
        int sz = Px(64);
        int cy = r.Y + Math.Max(Px(20), r.Height / 2 - Px(84));
        Theme.Slime(g, new RectangleF(r.X + (r.Width - sz) / 2f, cy, sz, sz * 0.86f), EmptyHappy ? Theme.Teal : Theme.Hex(0xB9C2D0), EmptyHappy);
        Theme.Text(g, EmptyTitle, Theme.Strong, new Rectangle(r.X + Px(20), cy + sz, r.Width - Px(40), Px(28)), Theme.Navy, TextFormatFlags.HorizontalCenter);
        int tw = r.Width - Px(80);
        int th = Math.Min(Theme.WrapHeight(EmptyText, Theme.Body, tw), Px(80));
        Theme.Wrap(g, EmptyText, Theme.Body, new Rectangle(r.X + Px(40), cy + sz + Px(30), tw, th + Px(2)), Theme.Hint, true);
        if (EmptyAction.Length > 0)
        {
            int bh = Px(34), bw = Theme.Measure(EmptyAction, Theme.ButtonFont).Width + Px(36);
            var br = new Rectangle(r.X + (r.Width - bw) / 2, cy + sz + Px(30) + th + Px(16), bw, bh);
            bool h = IsHover("empty:act"), dn = IsDown("empty:act");
            Theme.FillRound(g, br, bh / 2f, dn ? Theme.TealDark : h ? Theme.Teal : Theme.TealTint);
            Theme.Text(g, EmptyAction, Theme.ButtonFont, br, h || dn ? Color.White : Theme.TealText, TextFormatFlags.HorizontalCenter);
            AddHit(br, "empty:act", () => EmptyActionClick?.Invoke());
        }
    }

    private void DrawDrop(Graphics g)
    {
        using (var b = new SolidBrush(Color.FromArgb(232, Theme.Cream))) g.FillRectangle(b, ClientRectangle);
        var r = new RectangleF(Px(6), Px(6), Width - Px(12), Height - Px(12));
        using (var p = Theme.RoundRect(r, Px(18)))
        using (var pen = new Pen(Theme.Teal, Math.Max(2f, 2f * S)) { DashStyle = DashStyle.Dash })
        {
            using var fill = new SolidBrush(Theme.Alpha(Theme.TealTint, 200));
            g.FillPath(fill, p);
            g.DrawPath(pen, p);
        }
        int cy = (int)(r.Y + r.Height / 2) - Px(46);
        Theme.Glyph(g, Theme.GDownload, new Rectangle(0, cy, Width, Px(36)), Theme.TealDark, Theme.IconsBig);
        Theme.Text(g, "Отпустите, чтобы установить", Theme.H2, new Rectangle(0, cy + Px(40), Width, Px(30)), Theme.Navy, TextFormatFlags.HorizontalCenter);
        Theme.Text(g, ".dll или .zip: мод → Mods, плагин → Plugins, библиотека → UserLibs", Theme.Body, new Rectangle(Px(20), cy + Px(72), Width - Px(40), Px(22)), Theme.NavySoft, TextFormatFlags.HorizontalCenter);
    }

    // ───────── мышь, колесо, полоса прокрутки ─────────

    private bool OnBar(Point p) => HasBar && p.X >= Width - BarW - Px(2);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Focused) Focus();
        if (e.Button == MouseButtons.Left && OnBar(e.Location))
        {
            StopWheel();
            var tr = ThumbRect();
            if (tr.Contains(e.Location)) { _dragThumb = true; _dragOffset = e.Y - tr.Y; Capture = true; }
            else { _scroll += (e.Y < tr.Y ? -1 : 1) * Math.Max(Px(40), Height * 9 / 10); ClampScroll(); }
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
            int ny = e.Y - _dragOffset - Px(3);
            int before = _scroll;
            _scroll = track <= 0 ? 0 : (int)((long)ny * MaxScroll / track);
            ClampScroll();
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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-e.Delta * Px(66) / 120);
    }

    /// Прокрутить на dy пикселей: плавно (~150 мс, ease-out), в снимке окна — сразу.
    public void ScrollBy(int dy, bool smooth = true)
    {
        int target = Math.Clamp((_wheel.Enabled ? _animTo : _scroll) + dy, 0, MaxScroll);
        if (!smooth || !Anim.Enabled || !IsHandleCreated)
        {
            StopWheel();
            if (target != _scroll) { _scroll = target; Invalidate(); Update(); UpdateHover(PointToClient(MousePosition)); }
            return;
        }
        if (target == _scroll && !_wheel.Enabled) return;
        _animFrom = _scroll; _animTo = target; _animStart = Stopwatch.GetTimestamp();
        if (!_wheel.Enabled) { _wheel.Start(); WheelStep(); }
    }

    public bool Scrolling => _wheel.Enabled;

    private void StopWheel() { _wheel.Stop(); _animTo = _scroll; }

    private void WheelStep()
    {
        double t = Math.Clamp((Stopwatch.GetTimestamp() - _animStart) * 1000.0 / Stopwatch.Frequency / WheelMs, 0, 1);
        double k = 1 - Math.Pow(1 - t, 3);
        int pos = (int)Math.Round(_animFrom + (_animTo - _animFrom) * k);
        if (t >= 1) { pos = _animTo; _wheel.Stop(); }
        pos = Math.Clamp(pos, 0, MaxScroll);
        if (pos != _scroll)
        {
            _scroll = pos;
            Invalidate();
            Update();
            var p = PointToClient(MousePosition);
            if (ClientRectangle.Contains(p)) UpdateHover(p);
        }
    }

    // ───────── клавиатура ─────────

    protected override bool IsInputKey(Keys k)
    {
        var key = k & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Space or Keys.Enter || base.IsInputKey(k);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (char.IsLetterOrDigit(e.KeyChar)) { TypeRequested?.Invoke(e.KeyChar); e.Handled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (View.Count == 0 || e.Control || e.Alt) return;
        int i = IndexOf(SelectedId);
        int per = Math.Max(1, Height / Pitch);
        int ni = e.KeyCode switch
        {
            Keys.Up => Math.Max(0, i - 1),
            Keys.Down => Math.Min(View.Count - 1, i + 1),
            Keys.Home => 0,
            Keys.End => View.Count - 1,
            Keys.PageUp => Math.Max(0, i - per),
            Keys.PageDown => Math.Min(View.Count - 1, Math.Max(0, i) + per),
            _ => -1,
        };
        if (ni >= 0) { Select(View[ni].Id); e.Handled = true; return; }
        var m = Selected;
        if (m == null) return;
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            if (m.Available) InstallRequested?.Invoke(m); else ToggleRequested?.Invoke(m);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete) { if (!m.Available) DeleteRequested?.Invoke(m); e.Handled = true; }
        else if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
        {
            int idx = IndexOf(m.Id);
            var r = idx >= 0 ? CardRect(idx) : new Rectangle(0, 0, Width, Px(40));
            MenuRequested?.Invoke(m, new Point(r.X + Px(80), Math.Clamp(r.Bottom - Px(12), 0, Height)));
            e.Handled = true;
        }
    }
}
