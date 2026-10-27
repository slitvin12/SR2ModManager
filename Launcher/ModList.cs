// ModList.cs — список модов в одном из трёх видов (зависит от стиля, см. Designs.cs):
//  • таблица («Компактный»): строки ~30 px, закреплённая шапка колонок — щелчок по заголовку сортирует;
//  • карточки («Fluent»): ~58 px — название, версия, автор, описание и бейджи статуса справа;
//  • капсулы («Слаймовый»): ~76 px, скруглённые, в цветах игры, с метками и мягкой тенью.
// Ниже установленных — «Доступны для установки» (из ModManager\Library, «Загрузок», своих папок).
// При поиске совпадения подсвечиваются, а в длинном описании показывается фрагмент с совпадением.
//
// Быстродействие (сотни модов):
//  • виртуальная прокрутка — видимые строки находятся арифметически, остальные не трогаются вовсе;
//  • каждая строка/карточка кэшируется готовой картинкой и рисуется заново, только когда меняется то, что
//    на ней видно (подпись Sig: отметка, статус, метки, наведение, ширина, стиль…);
//  • наведение, выбор и переключение перерисовывают одну-две строки, а не весь список;
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
    /// Щелчок по заголовку колонки таблицы. Порядок решает окно (ModSort.Next) и присылает новый View.
    public event Action<ModColumn>? SortRequested;
    /// Двойной щелчок по моду — открыть его подробности (вкладка «Детали» нижней панели).
    public event Action<ModEntry>? OpenRequested;
    public string EmptyTitle = "", EmptyText = "";
    public string EmptyAction = "";          // кнопка в пустом состоянии ("" — без кнопки)
    public event Action? EmptyActionClick;
    public bool EmptyHappy;
    public VIcon EmptyIcon;                  // значок пустого состояния в «Компактном» (None — галочка или «i»)
    public IconCache? Icons;
    /// Активная сортировка — для стрелки в шапке таблицы.
    public ModColumn SortColumn { get; private set; }
    public bool SortDesc { get; private set; }
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
    private int _gen = -1;                   // Theme.Generation, под которую собран кэш
    private ListMode _mode;

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
        _mode = Mode;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _wheel.Dispose(); ClearCache(); }
        base.Dispose(disposing);
    }

    public bool DropHint { get => _drop; set { if (_drop != value) { _drop = value; Invalidate(); } } }

    /// Стрелка сортировки в шапке таблицы.
    public void SetSort(ModColumn column, bool desc)
    {
        if (column == SortColumn && desc == SortDesc) return;
        SortColumn = column; SortDesc = desc;
        if (TopH > 0) Invalidate(new Rectangle(0, 0, Width, TopH));
    }

    /// Сменился стиль: другой вид списка, размеры и цвета — сбросить кэш, выбранный мод оставить на виду.
    public void ApplyDesign()
    {
        StopWheel();
        ClearCache();
        _cacheW = -1;
        _colsW = -1;
        _gen = Theme.Generation;
        _mode = Mode;
        ClampScroll();
        EnsureVisible();
        Invalidate();
    }

    // ───────── геометрия ─────────

    private static ListMode Mode => Theme.Spec.Mode;
    private static bool IsTable => Mode == ListMode.Table;
    private static bool Flat => Theme.Spec.Flat;
    private int CardH => Px(Theme.Spec.RowH);
    private int Gap => Px(Theme.Spec.Gap);
    private int Pad => IsTable ? (Flat ? 0 : Px(1)) : Px(3);
    private int BarW => Px(10);
    /// Заголовок группы «Доступны для установки».
    private int HeaderH => Px(IsTable ? 28 : Mode == ListMode.Cards ? 30 : 34);
    /// Закреплённая шапка колонок таблицы (над прокручиваемыми строками).
    private int TopH => IsTable && View.Count > 0 ? Px(Flat ? 29 : 30) : 0;
    /// Высота прокручиваемой части.
    private int ViewH => Math.Max(1, Height - TopH);
    private int Pitch => CardH + Gap;
    private int ShadowMargin => Theme.Spec.Shadows ? Px(10) : Px(2);   // тень выбранной карточки выходит за её края
    /// Верх карточки i в координатах содержимого (без прокрутки).
    private int CardTop(int i) => Pad + i * Pitch + (_groupAt >= 0 && i >= _groupAt ? HeaderH : 0);
    private int ContentH => View.Count == 0 ? 0 : Pad * 2 + View.Count * Pitch - Gap + (_groupAt >= 0 ? HeaderH : 0);
    private int MaxScroll => Math.Max(0, ContentH - ViewH);
    private bool HasBar => ContentH > ViewH;
    private int CardW => Width - Pad * 2 - (HasBar ? BarW + Px(2) : 0);

    private Rectangle CardRect(int i) => new(Pad, TopH + CardTop(i) - _scroll, CardW, CardH);

    /// Карточки, задевающие полосу [top, bottom) содержимого: O(видимых), без обхода всего списка.
    private (int First, int Last) Range(int top, int bottom)
    {
        int n = View.Count;
        if (n == 0) return (0, -1);
        int i = Math.Clamp((top - Pad) / Math.Max(1, Pitch), 0, n - 1);
        while (i > 0 && CardTop(i - 1) + CardH > top) i--;
        while (i < n - 1 && CardTop(i) + CardH <= top) i++;
        if (CardTop(i) >= bottom) return (i, i - 1);
        int j = i;
        while (j < n - 1 && CardTop(j + 1) < bottom) j++;
        return (i, j);
    }

    private (int First, int Last) VisibleRange() => Range(_scroll - ShadowMargin, _scroll + ViewH + ShadowMargin);

    private Rectangle ThumbRect()
    {
        if (!HasBar) return Rectangle.Empty;
        int track = ViewH - Px(6);
        int th = Math.Min(track, Math.Max(Px(40), (int)((long)track * ViewH / Math.Max(1, ContentH))));
        int ty = TopH + Px(3) + (int)((long)(track - th) * _scroll / Math.Max(1, MaxScroll));
        return new Rectangle(Width - BarW + Px(3) - (IsTable ? Px(1) : 0), ty, BarW - Px(5), th);
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

    public void ScrollToTop() { StopWheel(); if (_scroll != 0) { _scroll = 0; InvalidateRows(); } }

    /// Прокрутка двигает только строки: закреплённая шапка таблицы остаётся как есть — её не перерисовываем.
    private void InvalidateRows()
    {
        if (TopH > 0) Invalidate(new Rectangle(0, TopH, Width, Math.Max(1, Height - TopH)));
        else Invalidate();
    }

    public void EnsureVisible()
    {
        int i = IndexOf(SelectedId);
        if (i < 0) return;
        var r = CardRect(i);
        int before = _scroll;
        int top = r.Top - (i == _groupAt ? HeaderH : 0); // у первого доступного — вместе с заголовком группы
        if (top < TopH) _scroll += top - TopH - Pad;
        else if (r.Bottom > Height) _scroll += r.Bottom - Height + Pad;
        ClampScroll();
        if (_scroll != before) { StopWheel(); InvalidateRows(); }
    }

    private void ClampScroll() => _scroll = Math.Clamp(_scroll, 0, MaxScroll);

    /// Перерисовать одну карточку (с полями под тень).
    public void InvalidateCard(string? id)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        var r = CardRect(i);
        r.Inflate(ShadowMargin, ShadowMargin);
        if (TopH > 0) r.Intersect(new Rectangle(0, TopH, Width, ViewH)); // строки под шапкой не видны — шапку не трогаем
        if (r.Width > 0 && r.Height > 0 && r.IntersectsWith(ClientRectangle)) Invalidate(r);
    }

    /// После изменения отметок, статусов или проверки совместимости: перерисовать только те видимые
    /// карточки, вид которых изменился. Возвращает их число.
    public int RefreshCards()
    {
        if (View.Count == 0 || Width <= 0 || Height <= 0) { Invalidate(); return -1; }
        if (_gen != Theme.Generation || _mode != Mode) { Invalidate(); return -1; }
        int changed = 0, w = CardW;
        var (a, b) = VisibleRange();
        for (int i = a; i <= b; i++)
        {
            var c = Info(View[i], w, i);
            if (!_shown.TryGetValue(c.M.Id, out var sig) || sig != c.Sig) { InvalidateCard(c.M.Id); changed++; }
        }
        return changed;
    }

    /// Наведение/нажатие — перерисовать только затронутые карточки (или шапку таблицы).
    protected override void InvalidateHot(string? a, string? b)
    {
        foreach (var k in new[] { a, b })
        {
            if (k == null) continue;
            if (k.StartsWith("col:", StringComparison.Ordinal)) { Invalidate(new Rectangle(0, 0, Width, Math.Max(1, TopH))); continue; }
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
        // выбранная карточка была видна целиком — остаётся видна и после смены высоты (баннер, размер окна, панель снизу)
        bool keep = false;
        if (_lastH > 0 && SelectedId != null)
        {
            int i = IndexOf(SelectedId);
            if (i >= 0) { var r = CardRect(i); keep = r.Top >= TopH && r.Bottom <= _lastH; }
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

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ");

    // ───────── кэш карточек ─────────

    /// Сколько картинок держать. Список теперь во всю ширину окна (детали — в нижней панели), картинки вдвое шире
    /// прежних — карточкам ~24 МБ (12–128 шт.), чтобы при прокрутке назад вмещалось столько же, сколько раньше;
    /// у таблицы строки низкие и их за тот же сдвиг проходит втрое больше — ей ~32 МБ (до 384 строк).
    private int CacheMax => IsTable
        ? Math.Clamp((int)(32L * 1024 * 1024 / Math.Max(1L, (long)Math.Max(1, CardW) * Math.Max(1, CardH) * 4)), 24, 384)
        : Math.Clamp((int)(24L * 1024 * 1024 / Math.Max(1L, (long)Math.Max(1, CardW) * Math.Max(1, CardH) * 4)), 12, 128);

    private void ClearCache()
    {
        foreach (var c in _cache.Values) c.Bmp.Dispose();
        _cache.Clear();
        _shown.Clear();
        _headBmp?.Dispose(); _headBmp = null; _headSig = "";
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

    // ───────── таблица: колонки ─────────

    private sealed class Column
    {
        public ModColumn Col;
        public string Title = "", Short = "", Tip = "";
        public int X, W;
        public bool Right;
    }

    // базовая ширина при 96 dpi (0 — «резиновая» колонка названия)
    private static readonly (ModColumn Col, string Title, string Short, int W, bool Right, string Tip)[] ColDefs =
    {
        (ModColumn.Check, "", "", 40, false, "Включён ли мод"),
        (ModColumn.Name, "НАЗВАНИЕ", "НАЗВАНИЕ", 0, false, "Название мода и описание"),
        (ModColumn.Version, "ВЕРСИЯ", "ВЕРСИЯ", 84, false, "Версия из метаданных DLL (MelonInfo)"),
        (ModColumn.Author, "АВТОР", "АВТОР", 130, false, "Автор мода"),
        (ModColumn.Compat, "СОВМЕСТИМОСТЬ", "СОВМЕСТ.", 158, false, "Совместимость с MelonLoader, игрой и другими модами; ошибки в журнале"),
        (ModColumn.Hooks, "ХУКИ HARMONY", "ХУКИ", 112, false, "Сколько методов игры патчит мод через Harmony и пересекается ли с другими модами"),
        (ModColumn.Update, "ОБНОВЛЕНИЕ", "ОБНОВЛ.", 112, false, "Новая версия в «Загрузках», Библиотеке или своих папках"),
        (ModColumn.Size, "РАЗМЕР", "РАЗМЕР", 72, true, "Размер файла DLL"),
    };
    // что прятать, когда окно узкое (по порядку)
    private static readonly ModColumn[] HideOrder = { ModColumn.Size, ModColumn.Author, ModColumn.Hooks, ModColumn.Version, ModColumn.Update };
    private const int NameMin = 210;

    private List<Column> _cols = new();
    private int _colsW = -1, _colsDpi = -1;
    private bool _colsFlat;

    /// Колонки для ширины строки w (x — от левого края строки).
    private List<Column> Columns(int w)
    {
        if (w == _colsW && DeviceDpi == _colsDpi && _colsFlat == Flat && _cols.Count > 0) return _cols;
        _colsFlat = Flat;
        if (_colsFlat) { _cols = FlatColumns(w); _colsW = w; _colsDpi = DeviceDpi; return _cols; }
        var set = ColDefs.ToList();
        int Fixed() => set.Where(d => d.W > 0).Sum(d => Px(d.W));
        foreach (var h in HideOrder)
        {
            if (Fixed() + Px(NameMin) <= w) break;
            set.RemoveAll(d => d.Col == h);
        }
        int nameW = Math.Max(Px(90), w - Fixed());
        var res = new List<Column>();
        int x = 0;
        foreach (var d in set)
        {
            int cw = d.W > 0 ? Px(d.W) : nameW;
            res.Add(new Column { Col = d.Col, Title = d.Title, Short = d.Short, Tip = d.Tip, X = x, W = cw, Right = d.Right });
            x += cw;
        }
        _cols = res; _colsW = w; _colsDpi = DeviceDpi;
        return res;
    }

    /// Видимые колонки таблицы (для подсказок и проверок).
    public IReadOnlyList<ModColumn> VisibleColumns => IsTable ? Columns(CardW).Select(c => c.Col).ToList() : Array.Empty<ModColumn>();

    // ───────── таблица «Компактного» стиля ─────────

    // ширина при 96 dpi: 0 — «резиновая» (обновление), -1 — название (четверть ширины, 210…330 px)
    private static readonly (ModColumn Col, string Title, int W, bool Right, string Tip)[] FlatColDefs =
    {
        (ModColumn.Check, "", 36, false, "Включён ли мод"),
        (ModColumn.Name, "Мод", -1, false, "Название мода"),
        (ModColumn.Version, "Версия", 78, false, "Версия из метаданных DLL (MelonInfo)"),
        (ModColumn.Author, "Автор", 170, false, "Автор мода"),
        (ModColumn.Compat, "Совместимость", 190, false, "Совместимость с MelonLoader, игрой и другими модами; ошибки и предупреждения в журнале"),
        (ModColumn.Hooks, "Хуки", 62, false, "Сколько методов игры патчит мод через Harmony"),
        (ModColumn.Update, "Обновление", 0, false, "Новая версия в «Загрузках», Библиотеке или своих папках"),
        (ModColumn.Size, "Размер", 90, true, "Размер файла DLL"),
    };

    /// Наименьшая ширина колонки «Компактного» стиля: название, автор, совместимость и обновление сначала ужимаются
    /// (автор — с многоточием), и только если и так не влезает — колонки прячутся по HideOrder.
    private int FlatMin(ModColumn col, int w) => col switch
    {
        ModColumn.Name => Px(NameMin),
        ModColumn.Author => Px(110),
        ModColumn.Compat => Px(160),
        ModColumn.Update => Px(90),
        _ => Px(w),
    };

    private List<Column> FlatColumns(int w)
    {
        var set = FlatColDefs.ToList();
        foreach (var h in HideOrder)
        {
            if (set.Sum(d => FlatMin(d.Col, d.W)) <= w) break;
            set.RemoveAll(d => d.Col == h);
        }
        // от минимумов: остаток — названию (до четверти ширины, не больше 330), совместимости и автору до обычной ширины,
        // всё прочее — «Обновлению» (а без него — названию)
        var width = set.ToDictionary(d => d.Col, d => FlatMin(d.Col, d.W));
        int spare = w - width.Values.Sum();
        void Grow(ModColumn col, int target)
        {
            if (spare <= 0 || !width.TryGetValue(col, out int cur)) return;
            int add = Math.Min(spare, Math.Max(0, target - cur));
            width[col] = cur + add;
            spare -= add;
        }
        Grow(ModColumn.Name, Math.Clamp(w / 4, Px(NameMin), Px(330)));
        Grow(ModColumn.Compat, Px(190));
        Grow(ModColumn.Author, Px(170));
        if (spare > 0) Grow(width.ContainsKey(ModColumn.Update) ? ModColumn.Update : ModColumn.Name, int.MaxValue);
        var res = new List<Column>();
        int x = 0;
        foreach (var d in set)
        {
            int cw = width[d.Col];
            res.Add(new Column { Col = d.Col, Title = d.Title, Short = d.Title, Tip = d.Tip, X = x, W = cw, Right = d.Right });
            x += cw;
        }
        return res;
    }

    /// Строка «Компактного» стиля (29 px, как в редакторе кода): флажок, аватар и название, версия, автор,
    /// совместимость со значком, число хуков, обновление, размер. Конфликт хуков — янтарная подложка строки.
    private void LayoutFlatRow(CardInfo c)
    {
        var m = c.M;
        int body = c.H - Math.Max(1, Px(1));   // снизу — линия-разделитель
        bool on = c.On && !c.Avail;
        Color soft = on ? Theme.NavySoft : Theme.Hint;
        c.Conflict = !c.Avail && on && _m.CompatFor(m).Any(i => i.Code == "hook-conflict");
        foreach (var col in Columns(c.W))
        {
            var cr = new Rectangle(col.X, 0, col.W, body);
            switch (col.Col)
            {
                case ModColumn.Check:
                {
                    int cs = Px(14);
                    c.Tg = c.Avail
                        ? new Rectangle(cr.X + (cr.Width - Px(26)) / 2, (body - Px(20) + 1) / 2, Px(26), Px(20))
                        : new Rectangle(cr.X + Px(8), (body - cs + 1) / 2, cs, cs);
                    break;
                }
                case ModColumn.Name:
                {
                    int isz = Px(18);
                    c.ShowIcon = true;
                    c.Ico = new Rectangle(cr.X + Px(6), (body - isz + 1) / 2, isz, isz);
                    LoadIcon(c, isz);
                    int tx = cr.X + Px(31), right = cr.Right - Px(8);
                    c.Title = m.DisplayName;
                    int tw = Math.Min(Theme.Measure(c.Title, on ? Theme.ButtonFont : Theme.Body).Width, Math.Max(10, right - tx));
                    c.TitleR = new Rectangle(tx, 0, tw + 2, body);
                    c.TitleMarks = Marks(c.Title);
                    int x = tx + tw + Px(8);
                    if (m.Kind == "Plugin" && x + Px(56) < right)
                    {
                        var t = new TagPill { Text = "плагин", Bg = Theme.BlueTint, Fg = Theme.BlueText, Tip = "Плагин MelonLoader (папка Plugins): загружается раньше модов" };
                        int pw = Theme.PillWidth(t.Text, Theme.SmallBold, Px(16), false);
                        t.R = new Rectangle(x, (body - Px(16) + 1) / 2, pw, Px(16));
                        c.Tags.Add(t);
                    }
                    break;
                }
                case ModColumn.Version:
                    AddFlatCell(c, col, cr, string.IsNullOrEmpty(m.Version) ? "—" : m.Version, string.IsNullOrEmpty(m.Version) ? Theme.Hint : soft, Theme.MonoCode, marks: Marks(m.Version));
                    break;
                case ModColumn.Author:
                    AddFlatCell(c, col, cr, string.IsNullOrWhiteSpace(m.Author) ? "—" : m.Author, string.IsNullOrWhiteSpace(m.Author) ? Theme.Hint : soft, Theme.Body, marks: Marks(m.Author));
                    break;
                case ModColumn.Compat:
                {
                    var (text, fg, icon, tip) = FlatCompatCell(m, c.St.Tip);
                    AddFlatCell(c, col, cr, text, fg, Theme.Body, icon, tip);
                    break;
                }
                case ModColumn.Hooks:
                {
                    var (text, _, _, tip) = HooksCell(m);
                    bool num = text.Length > 0 && char.IsDigit(text[0]);
                    if (num) { int sp = text.IndexOf(' '); if (sp > 0) text = text.Substring(0, sp); }
                    else if (text == "нет") text = "0";
                    AddFlatCell(c, col, cr, text, num || text == "0" ? soft : Theme.Hint, Theme.MonoCode, tip: tip);
                    break;
                }
                case ModColumn.Update:
                {
                    var u = _m.UpdateFor(m);
                    if (u == null) AddFlatCell(c, col, cr, "—", Theme.Hint, Theme.Body);
                    else if (u.Kind == "outdated") AddFlatCell(c, col, cr, "устарел?", Theme.Orange, Theme.Body, VIcon.Warning, WrapTip("По журналу игры: " + u.Reason));
                    else AddFlatCell(c, col, cr, u.Version, Theme.Blue, Theme.Body, VIcon.CircleDown, WrapTip("Новая версия v" + u.Version + ": " + u.Where + "\nОбновить — в подробностях мода (вкладка «Детали») или в разделе «Каталог и обновления»"));
                    break;
                }
                case ModColumn.Size:
                    AddFlatCell(c, col, cr, m.Size > 0 ? Theme.FileSize(m.Size) : "—", m.Size > 0 ? soft : Theme.Hint, Theme.MonoCode);
                    break;
            }
        }
    }

    private static void AddFlatCell(CardInfo c, Column col, Rectangle cr, string text, Color fg, Font font, VIcon icon = VIcon.None, string? tip = null,
        List<(int Start, int Length)>? marks = null)
        => c.Cells.Add(new Cell { Col = col.Col, R = cr, Text = text, Fg = fg, Font = font, Icon = icon, Right = col.Right, Marks = marks, Tip = tip });

    /// Совместимость: значок и слово — худшая проблема, предупреждения в журнале, «работает» у запущенной игры или «совместим».
    private (string Text, Color Fg, VIcon Icon, string? Tip) FlatCompatCell(ModEntry m, string statusTip)
    {
        if (m.Available) return ("в «" + m.SourceLabel + "»", Theme.Blue, VIcon.CircleDown, "Не установлен — найден в «" + m.SourceLabel + "». Enter — установить.");
        if (!m.Readable) return ("не читается", Theme.Coral, VIcon.CircleX, statusTip);
        var probs = _m.Problems(m);
        if (probs.Count > 0)
        {
            var worst = probs.FirstOrDefault(p => p.Tone == Tone.Error);
            if (worst.Text == null) worst = probs[0];
            bool err = worst.Tone == Tone.Error, hooks = !err && worst.Text == "конфликт хуков";
            var tips = _m.CompatBadges(m).Select(CompatTip).Concat(_m.BaseProblems(m).Select(p => "• " + p.Text)).ToList();
            return (worst.Text + (probs.Count > 1 ? "  +" + (probs.Count - 1) : ""), err ? Theme.Coral : Theme.Orange,
                err ? VIcon.CircleX : hooks ? VIcon.Branch : VIcon.Warning, string.Join("\n\n", tips));
        }
        var ls = _m.LogFor(m);
        if (ls != null && ls.Warnings > 0)
            return (ls.Warnings + " предупр. в журнале", Theme.Orange, VIcon.Warning,
                "В журнале прошлого запуска " + Theme.Count(ls.Warnings, "предупреждение", "предупреждения", "предупреждений")
                + (ls.LastWarning.Length > 0 ? ", последнее:\n" + WrapTip(OneLine(ls.LastWarning)) : "") + "\nВсе строки — вкладка «Журнал игры» внизу.");
        bool on = _m.On(m.Id);
        if (_m.GameRunning && on && m.Enabled && _m.Log != null)
            return _m.Log.WasLoaded(m.Id)
                ? ("работает", Theme.Green, VIcon.CircleCheck, "Загружен в запущенную игру")
                : ("не загружен", Theme.Orange, VIcon.Warning, statusTip);
        if (_m.Compat == null) return _m.CompatBusy ? ("проверка…", Theme.Hint, VIcon.None, "Идёт проверка совместимости") : ("—", Theme.Hint, VIcon.None, null);
        var info = _m.CompatFor(m).Where(i => i.Level == CompatLevel.Info).ToList();
        return ("совместим", on ? Theme.Green : Theme.Mix(Theme.Green, Theme.Cream, 0.3f), VIcon.CircleCheck,
            info.Count > 0 ? string.Join("\n\n", info.Select(CompatTip)) : "Проверка совместимости не нашла проблем");
    }

    /// Флажок «Компактного» стиля: 14 px, бирюзовый с тёмной галочкой или тонкая серая рамка. Ждёт применения — точка в углу.
    private void FlatCheckBox(Graphics g, Rectangle r, bool on, bool hover, bool dim, bool pending, Color bg)
    {
        float rad = Px(3);
        if (on)
        {
            Color fill = hover ? Theme.TealDark : Theme.Teal;
            if (dim) fill = Theme.Mix(fill, bg, 0.5f);
            Theme.FillRound(g, r, rad, fill);
            Theme.Icon(g, VIcon.Check, r, Theme.OnAccent, Math.Max(1.5f, 1.9f * S));
        }
        else
        {
            float w = Math.Max(1f, S);
            Theme.StrokeRound(g, new RectangleF(r.X + w / 2f, r.Y + w / 2f, r.Width - w, r.Height - w), rad, hover ? Theme.Teal : dim ? Theme.Line : Theme.Hint, w);
        }
        if (pending)
        {
            float cx = r.Right - Px(1), cy = r.Y + Px(1);
            Theme.Dot(g, cx, cy, Px(4.5f), bg);
            Theme.Dot(g, cx, cy, Px(3.2f), on ? Theme.Blue : Theme.Orange);
        }
    }

    private void DrawFlatRow(Graphics g, CardInfo c, int ox, int oy)
    {
        var r = new Rectangle(ox, oy, c.W, c.H);
        Rectangle O(Rectangle q) => new(q.X + ox, q.Y + oy, q.Width, q.Height);
        int one = Math.Max(1, Px(1));
        Color baseBg = c.Conflict ? Theme.OrangeTint : c.Avail ? Theme.Mix(Theme.Cream, Theme.HeaderBg, 0.5f) : Theme.Cream;
        Color bg = c.Sel ? Theme.Selected : c.Hover ? (c.Conflict || c.Avail ? Theme.Mix(baseBg, Theme.Navy, 0.03f) : Theme.RowHover) : baseBg;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r.X, r.Y, r.Width, r.Height - one);
        using (var b = new SolidBrush(Theme.Mix(Theme.Cream, Theme.Line, 0.43f))) g.FillRectangle(b, r.X, r.Bottom - one, r.Width, one);
        if (c.Sel) using (var b = new SolidBrush(Theme.Teal)) g.FillRectangle(b, r.X, r.Y, Px(2), r.Height - one);

        var tg = O(c.Tg);
        if (c.Avail) DrawInstallButton(g, c, tg);
        else FlatCheckBox(g, tg, c.On, c.HTgl, !c.Readable, c.Pending, bg);

        bool on = c.On && !c.Avail;
        if (c.ShowIcon) DrawAvatar(g, c, O(c.Ico), Theme.Tiny, Px(4));
        Theme.TextMarked(g, c.Title, on ? Theme.ButtonFont : Theme.Body, O(c.TitleR), on ? Theme.Navy : Theme.NavySoft, c.TitleMarks);
        foreach (var t in c.Tags) Theme.Pill(g, t.R.X + ox, t.R.Y + oy, t.R.Height, t.Text, Theme.SmallBold, t.Bg, t.Fg);

        foreach (var x in c.Cells)
        {
            var cr = O(x.R);
            int pad = Px(8);
            var tr = new Rectangle(cr.X + pad, cr.Y, cr.Width - pad * 2, cr.Height);
            if (x.Icon != VIcon.None)
            {
                int isz = Px(12);
                Theme.Icon(g, x.Icon, new RectangleF(tr.X, cr.Y + (cr.Height - isz) / 2f, isz, isz), x.Fg);
                int shift = x.Col == ModColumn.Update ? Px(17) : Px(19);
                tr = new Rectangle(tr.X + shift, tr.Y, tr.Width - shift, tr.Height);
            }
            var f = x.Font ?? Theme.Body;
            if (x.Right) Theme.Text(g, x.Text, f, tr, x.Fg, TextFormatFlags.Right);
            else Theme.TextMarked(g, x.Text, f, tr, x.Fg, x.Marks);
        }
    }

    /// Шапка таблицы «Компактного» стиля: тёмная полоса, серые подписи без разделителей; у «Мод» — шеврон, пока порядок
    /// по умолчанию; у колонки сортировки — стрелка вверх или вниз.
    private void PaintFlatHeader(Graphics g, int w)
    {
        int h = TopH, one = Math.Max(1, Px(1));
        using (var b = new SolidBrush(Theme.HeaderBg)) g.FillRectangle(b, 0, 0, Width, h - one);
        using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, 0, h - one, Width, one);
        var font = Theme.SmallBold;
        foreach (var col in Columns(w))
        {
            var cr = new Rectangle(Pad + col.X, 0, col.W, h - one);
            string key = "col:" + col.Col;
            bool hv = IsHover(key), dn = IsDown(key), active = SortColumn == col.Col;
            if (hv || dn) using (var hb = new SolidBrush(dn ? Theme.Beige : Theme.Mix(Theme.HeaderBg, Theme.Navy, 0.04f))) g.FillRectangle(hb, cr);
            Color fg = active ? Theme.Navy : hv ? Theme.NavySoft : Theme.Hint;
            bool chev = active || col.Col == ModColumn.Name && SortColumn == ModColumn.None;
            int cw = Px(11), pad = col.Col == ModColumn.Name ? Px(6) : Px(8);
            if (col.Col == ModColumn.Check)
            {
                if (active) Theme.Icon(g, SortDesc ? VIcon.ChevronDown : VIcon.ChevronUp, new RectangleF(cr.X + (cr.Width - cw) / 2f, (cr.Height - cw) / 2f, cw, cw), Theme.TealText);
                continue;
            }
            int extra = chev ? cw + Px(3) : 0;
            int tw = Math.Min(Theme.Measure(col.Title, font).Width + 2, Math.Max(0, cr.Width - pad * 2 - extra));
            int tx = col.Right ? cr.Right - pad - tw - extra : cr.X + pad;
            Theme.Text(g, col.Title, font, new Rectangle(tx, 0, tw, cr.Height), fg);
            if (chev)
                Theme.Icon(g, active && !SortDesc ? VIcon.ChevronUp : VIcon.ChevronDown, new RectangleF(tx + tw + Px(2), (cr.Height - cw) / 2f + Px(0.5f), cw, cw),
                    active ? Theme.TealText : Theme.Hint);
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

    /// Ячейка таблицы.
    private sealed class Cell
    {
        public ModColumn Col;
        public Rectangle R;
        public string Text = "";
        public Color Fg;
        public Color? Dot;        // цветная точка перед текстом (совместимость, хуки)
        public Color? PillBg;     // текст в капсуле (новая версия)
        public bool Right;
        public List<(int Start, int Length)>? Marks;
        public string? Tip;
        public VIcon Icon;        // значок перед текстом («Компактный»: совместимость, обновление)
        public Font? Font;        // шрифт ячейки (null — мелкий)
    }

    /// Всё, что видно на карточке (координаты — от левого верхнего угла карточки).
    private sealed class CardInfo
    {
        public ModEntry M = null!;
        public ListMode Mode;
        public int W, H, Index;
        public bool Sel, Avail, On, Hover, HTgl, HInst, DInst, Readable, ShowIcon, NoDesc, Alt, Pending, AuthorDot, Conflict;
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
        public readonly List<Cell> Cells = new();
        public string? More, MoreTip;
        public Rectangle MoreR;
        public string Sig = "";
    }

    private CardInfo Info(ModEntry m, int w, int index)
    {
        string id = m.Id;
        var c = new CardInfo { M = m, W = w, H = CardH, Index = index, Mode = Mode };
        string? hid = HoverKey == null ? null : KeyId(HoverKey);
        c.Sel = string.Equals(id, SelectedId, StringComparison.OrdinalIgnoreCase);
        c.Avail = m.Available;
        c.HTgl = IsHover("tgl:" + id);
        c.HInst = IsHover("inst:" + id);
        c.DInst = IsDown("inst:" + id);
        c.Hover = hid != null && hid.Equals(id, StringComparison.OrdinalIgnoreCase);
        c.On = _m.On(id);
        c.Pending = !c.Avail && c.On != m.Enabled;
        c.Readable = m.Readable;
        c.Accent = _m.Accent(m);
        c.St = _m.Status(m);
        c.Alt = index % 2 == 1;
        switch (c.Mode)
        {
            case ListMode.Table: if (Flat) LayoutFlatRow(c); else LayoutRow(c); break;
            case ListMode.Cards: LayoutFluent(c); break;
            default: LayoutCapsule(c); break;
        }
        c.Sig = Signature(c);
        return c;
    }

    private void LoadIcon(CardInfo c, int isz)
    {
        if (Icons != null) c.Img = Icons.Get(c.M, isz, out _, out c.ImgSerial);
        if (c.Img == null)
        {
            c.Initials = IconCache.Initials(c.M.DisplayName);
            if (Flat) c.Initials = c.Initials.ToUpperInvariant();
            (c.AvBg, c.AvFg) = IconCache.AvatarColors(c.M.Id);
        }
    }

    private void SetDescription(CardInfo c, Rectangle r, Font f)
    {
        var m = c.M;
        c.DescR = r;
        c.NoDesc = string.IsNullOrWhiteSpace(m.Description);
        if (c.NoDesc) { c.Desc = m.Readable ? "Описания нет" : "Не удалось прочитать метаданные DLL"; c.DescColor = Theme.Hint; }
        else
        {
            var (desc, dm) = Snippet(OneLine(m.Description), f, r.Width);
            c.Desc = desc; c.DescMarks = dm; c.DescColor = Theme.NavySoft;
        }
    }

    /// Капсулы («Слаймовый»): переключатель, значок 40 px, название и версия, описание, автор и метки, статус справа.
    private void LayoutCapsule(CardInfo c)
    {
        var m = c.M;
        int w = c.W, h = c.H, oy = (h - Px(76)) / 2;
        int side = Px(24);                       // поля шире — у капсулы скруглённые края
        c.Tg = new Rectangle(side, (h - Px(24)) / 2, Px(44), Px(24));
        c.ShowIcon = w >= Px(440);
        int tx;
        if (c.ShowIcon)
        {
            int isz = Px(40);
            c.Ico = new Rectangle(c.Tg.Right + Px(14), (h - isz) / 2, isz, isz);
            tx = c.Ico.Right + Px(12);
            LoadIcon(c, isz);
        }
        else tx = c.Tg.Right + Px(16);
        int right = w - side;

        // статус справа
        int pw = Theme.PillWidth(c.St.Text, Theme.SmallBold, Px(22), false);
        c.Pill = new Rectangle(right - pw, oy + Px(10), pw, Px(22));

        // название + версия
        int titleRight = c.Pill.X - Px(10);
        c.Title = m.DisplayName;
        int tw = Math.Min(Theme.Measure(c.Title, Theme.Strong).Width, Math.Max(10, titleRight - tx));
        c.TitleR = new Rectangle(tx, oy + Px(8), tw + 2, Px(24));
        c.TitleMarks = Marks(c.Title);
        int vx = tx + tw + Px(8);
        if (!string.IsNullOrEmpty(m.Version) && vx + Px(30) < titleRight)
        {
            c.Ver = "v" + m.Version;
            c.VerR = new Rectangle(vx, oy + Px(9), titleRight - vx, Px(24));
            c.VerMarks = Marks(c.Ver);
        }

        // описание (при поиске — фрагмент с совпадением)
        SetDescription(c, new Rectangle(tx, oy + Px(31), right - tx, Px(20)), Theme.Body);

        // строка меток: автор, затем метки (первыми — совместимость)
        int ty = oy + Px(53), th = Px(18), x = tx, limit = right;
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
    }

    /// Карточки («Fluent», ~58 px): переключатель, значок 32 px; первая строка — название, версия, автор и статус;
    /// вторая — описание и бейджи справа.
    private void LayoutFluent(CardInfo c)
    {
        var m = c.M;
        int w = c.W, h = c.H;
        c.Tg = new Rectangle(Px(16), (h - Px(20)) / 2, Px(40), Px(20));
        c.ShowIcon = w >= Px(420);
        int tx;
        if (c.ShowIcon)
        {
            int isz = Px(32);
            c.Ico = new Rectangle(c.Tg.Right + Px(14), (h - isz) / 2, isz, isz);
            tx = c.Ico.Right + Px(12);
            LoadIcon(c, isz);
        }
        else tx = c.Tg.Right + Px(14);
        int right = w - Px(14);
        int line1 = Math.Max(Px(4), (h - Px(42)) / 2), line2 = line1 + Px(23);

        // статус — справа в первой строке
        int ph = Px(20);
        int pw = Theme.PillWidth(c.St.Text, Theme.SmallBold, ph, false);
        c.Pill = new Rectangle(right - pw, line1 + Px(1), pw, ph);

        // название · версия · автор
        int titleRight = c.Pill.X - Px(12);
        c.Title = m.DisplayName;
        int tw = Math.Min(Theme.Measure(c.Title, Theme.Strong).Width, Math.Max(10, titleRight - tx));
        c.TitleR = new Rectangle(tx, line1, tw + 2, Px(22));
        c.TitleMarks = Marks(c.Title);
        int x = tx + tw + Px(8);
        if (!string.IsNullOrEmpty(m.Version) && x + Px(30) < titleRight)
        {
            c.Ver = "v" + m.Version;
            int vw = Math.Min(Theme.Measure(c.Ver, Theme.Small).Width + 2, titleRight - x);
            c.VerR = new Rectangle(x, line1 + Px(1), vw, Px(22));
            c.VerMarks = Marks(c.Ver);
            x += vw + Px(6);
        }
        string author = string.IsNullOrWhiteSpace(m.Author) ? "" : m.Author;
        if (author.Length > 0 && x + Px(50) < titleRight)
        {
            c.Author = author;
            c.AuthorDot = true;
            int aw = Math.Min(Theme.Measure(author, Theme.Small).Width + 2, titleRight - x - Px(12));
            c.AuthorR = new Rectangle(x + Px(12), line1 + Px(1), aw, Px(22));
            c.AuthorMarks = Marks(author);
        }

        // вторая строка: бейджи прижаты вправо, описанию — остальное
        int th = Px(18), ty = line2 + Px(1);
        var tags = BuildTags(m, c.On);
        int gap = Px(6), minDesc = Math.Min(Px(170), (right - tx) / 2);
        int budget = right - tx - minDesc;
        var ws = tags.Select(t => Theme.PillWidth(t.Text, Theme.SmallBold, th, false)).ToList();
        int MoreW(int n) => Theme.PillWidth("+" + n, Theme.SmallBold, th, false);
        int k = 0, used = 0;
        while (k < tags.Count && used + ws[k] + (k > 0 ? gap : 0) <= budget) { used += ws[k] + (k > 0 ? gap : 0); k++; }
        if (k < tags.Count)
        {
            while (k > 0 && used + gap + MoreW(tags.Count - k) > budget) { k--; used -= ws[k] + (k > 0 ? gap : 0); }
            if (MoreW(tags.Count - k) <= budget) c.More = "+" + (tags.Count - k);
        }
        int total = used + (c.More != null ? (k > 0 ? gap : 0) + MoreW(tags.Count - k) : 0);
        int x0 = right - total;
        for (int i = 0; i < k; i++)
        {
            tags[i].R = new Rectangle(x0, ty, ws[i], th);
            c.Tags.Add(tags[i]);
            x0 += ws[i] + gap;
        }
        if (c.More != null)
        {
            c.MoreR = new Rectangle(right - MoreW(tags.Count - k), ty, MoreW(tags.Count - k), th);
            c.MoreTip = string.Join("\n", tags.Skip(k).Select(z => "• " + (z.Tip ?? z.Text).Split('\n')[0]));
        }
        int descRight = total > 0 ? right - total - Px(12) : right;
        SetDescription(c, new Rectangle(tx, line2, Math.Max(Px(40), descRight - tx), Px(20)), Theme.Body);
    }

    /// Строка таблицы («Компактный», ~30 px): отметка, значок и название, версия, автор, совместимость, хуки, обновление, размер.
    private void LayoutRow(CardInfo c)
    {
        var m = c.M;
        int h = c.H;
        foreach (var col in Columns(c.W))
        {
            var cr = new Rectangle(col.X, 0, col.W, h);
            switch (col.Col)
            {
                case ModColumn.Check:
                    c.Tg = c.Avail
                        ? new Rectangle(cr.X + (cr.Width - Px(28)) / 2, (h - Px(20)) / 2, Px(28), Px(20))
                        : new Rectangle(cr.X + (cr.Width - Px(16)) / 2, (h - Px(16)) / 2, Px(16), Px(16));
                    break;
                case ModColumn.Name:
                {
                    int isz = Math.Min(Px(20), h - Px(8));
                    c.ShowIcon = true;
                    c.Ico = new Rectangle(cr.X + Px(4), (h - isz) / 2, isz, isz);
                    LoadIcon(c, isz);
                    int tx = c.Ico.Right + Px(9), right = cr.Right - Px(10);
                    c.Title = m.DisplayName;
                    int tw = Math.Min(Theme.Measure(c.Title, Theme.ButtonFont).Width, Math.Max(10, right - tx));
                    c.TitleR = new Rectangle(tx, 0, tw + 2, h);
                    c.TitleMarks = Marks(c.Title);
                    int x = tx + tw + Px(8);
                    if (m.Kind == "Plugin" && x + Px(60) < right)
                    {
                        var t = new TagPill { Text = "плагин", Bg = Theme.BlueTint, Fg = Theme.BlueText, Tip = "Плагин MelonLoader (папка Plugins): загружается раньше модов" };
                        int pw = Theme.PillWidth(t.Text, Theme.SmallBold, Px(16), false);
                        t.R = new Rectangle(x, (h - Px(16)) / 2, pw, Px(16));
                        c.Tags.Add(t);
                        x += pw + Px(8);
                    }
                    if (right - x >= Px(60))
                    {
                        SetDescription(c, new Rectangle(x, 0, right - x, h), Theme.Small);
                        c.DescColor = c.NoDesc ? Theme.Alpha(Theme.Hint, 0) : Theme.Hint;   // «Описания нет» в таблице не показываем
                        if (c.NoDesc && !m.Readable) c.DescColor = Theme.CoralText;
                    }
                    break;
                }
                case ModColumn.Version:
                    AddCell(c, col, cr, string.IsNullOrEmpty(m.Version) ? "—" : m.Version, string.IsNullOrEmpty(m.Version) ? Theme.Hint : Theme.NavySoft, marks: Marks(m.Version));
                    break;
                case ModColumn.Author:
                    AddCell(c, col, cr, string.IsNullOrWhiteSpace(m.Author) ? "—" : m.Author, string.IsNullOrWhiteSpace(m.Author) ? Theme.Hint : Theme.NavySoft, marks: Marks(m.Author));
                    break;
                case ModColumn.Compat:
                {
                    var (text, fg, dot, tip) = CompatCell(m, c.St.Tip);
                    AddCell(c, col, cr, text, fg, dot, tip: tip);
                    break;
                }
                case ModColumn.Hooks:
                {
                    var (text, fg, dot, tip) = HooksCell(m);
                    AddCell(c, col, cr, text, fg, dot, tip: tip);
                    break;
                }
                case ModColumn.Update:
                {
                    var u = _m.UpdateFor(m);
                    if (u == null) AddCell(c, col, cr, "—", Theme.Hint);
                    else if (u.Kind == "outdated") AddCell(c, col, cr, "устарел?", Theme.OrangeText, Theme.Orange, tip: WrapTip("По журналу игры: " + u.Reason));
                    else AddCell(c, col, cr, "v" + u.Version, Color.White, pill: Theme.Blue, tip: WrapTip("Новая версия v" + u.Version + ": " + u.Where + "\nОбновить — в подробностях мода (вкладка «Детали»)"));
                    break;
                }
                case ModColumn.Size:
                    AddCell(c, col, cr, m.Size > 0 ? Theme.FileSize(m.Size) : "—", m.Size > 0 ? Theme.NavySoft : Theme.Hint, right: true);
                    break;
            }
        }
    }

    private static void AddCell(CardInfo c, Column col, Rectangle cr, string text, Color fg, Color? dot = null, Color? pill = null, bool right = false,
        List<(int Start, int Length)>? marks = null, string? tip = null)
        => c.Cells.Add(new Cell { Col = col.Col, R = cr, Text = text, Fg = fg, Dot = dot, PillBg = pill, Right = right || col.Right, Marks = marks, Tip = tip });

    /// Совместимость в таблице: худшая проблема (ошибки первыми) или «совместим»; у запущенной игры — загрузился ли.
    private (string Text, Color Fg, Color? Dot, string? Tip) CompatCell(ModEntry m, string statusTip)
    {
        if (m.Available) return ("в «" + m.SourceLabel + "»", Theme.BlueText, Theme.Blue, "Не установлен — найден в «" + m.SourceLabel + "». Enter — установить.");
        if (!m.Readable) return ("не читается", Theme.CoralText, Theme.Coral, statusTip);
        var probs = _m.Problems(m);
        if (probs.Count > 0)
        {
            var worst = probs.FirstOrDefault(p => p.Tone == Tone.Error);
            if (worst.Text == null) worst = probs[0];
            bool err = worst.Tone == Tone.Error;
            var tips = _m.CompatBadges(m).Select(CompatTip).Concat(_m.BaseProblems(m).Select(p => "• " + p.Text)).ToList();
            return (worst.Text + (probs.Count > 1 ? "  +" + (probs.Count - 1) : ""), err ? Theme.CoralText : Theme.OrangeText, err ? Theme.Coral : Theme.Orange,
                string.Join("\n\n", tips));
        }
        bool on = _m.On(m.Id);
        if (_m.GameRunning && on && m.Enabled && _m.Log != null)
            return _m.Log.WasLoaded(m.Id)
                ? ("работает", Theme.TealText, Theme.Teal, "Загружен в запущенную игру")
                : ("не загружен", Theme.OrangeText, Theme.Orange, statusTip);
        if (_m.Compat == null) return _m.CompatBusy ? ("проверка…", Theme.Hint, null, "Идёт проверка совместимости") : ("—", Theme.Hint, null, null);
        var info = _m.CompatFor(m).Where(i => i.Level == CompatLevel.Info).ToList();
        return ("совместим", Theme.TealText, Theme.Teal, info.Count > 0 ? string.Join("\n\n", info.Select(CompatTip)) : "Проверка совместимости не нашла проблем");
    }

    private ModManifest? ManifestOf(ModEntry m)
    {
        if (_m.Manifests != null && _m.Manifests.TryGetValue(m.Id, out var mf)) return mf;
        return _m.Compat?.Manifest(m.Id);
    }

    /// Хуки Harmony в таблице: сколько методов игры патчит мод и пересекается ли с другими включёнными.
    private (string Text, Color Fg, Color? Dot, string? Tip) HooksCell(ModEntry m)
    {
        if (m.Available) return ("—", Theme.Hint, null, null);
        var mf = ManifestOf(m);
        if (mf == null) return (_m.CompatBusy ? "…" : "—", Theme.Hint, null, null);
        int targets = mf.Hooks.Where(h => !h.Dynamic).Select(h => h.Key).Distinct().Count();
        bool dyn = mf.Hooks.Any(h => h.Dynamic) || mf.ManualPatching && mf.Hooks.Count == 0;
        if (targets == 0 && !dyn)
            return (mf.UsesHarmony ? "0" : "нет", Theme.Hint, null, mf.UsesHarmony ? "Ссылается на Harmony, но патчей не найдено" : "Не использует Harmony");
        string text = targets + (dyn ? "+" : "");
        string tip = HooksTip(mf);
        var issues = _m.CompatFor(m);
        var conflict = issues.FirstOrDefault(i => i.Code == "hook-conflict");
        if (conflict != null) return (text + " · конфликт", Theme.OrangeText, Theme.Orange, CompatTip(conflict) + "\n\n" + tip);
        var shared = issues.FirstOrDefault(i => i.Code == "hook-shared");
        if (shared != null) return (text + " · общие", Theme.BlueText, Theme.Blue, CompatTip(shared) + "\n\n" + tip);
        return (text, Theme.NavySoft, null, tip);
    }

    public static string HookLabel(HookTarget h) => h.Dynamic ? "цель вычисляется в коде" + (h.PatchClass.Length > 0 ? " (" + HookTarget.ShortType(h.PatchClass) + ")" : "") : h.Short;

    private static string HooksTip(ModManifest mf)
    {
        var groups = mf.Hooks.GroupBy(h => h.Dynamic ? "?" + h.PatchClass : h.Key).ToList();
        var lines = groups.Take(10).Select(g => "• " + HookLabel(g.First()) + " — " + string.Join(", ", g.Select(h => h.KindText).Distinct())).ToList();
        if (groups.Count > 10) lines.Add("и ещё " + (groups.Count - 10));
        if (lines.Count == 0) lines.Add("• патчи создаются в коде (harmony.Patch) — цели заранее не известны");
        return "Патчит методы игры (Harmony):\n" + string.Join("\n", lines) + "\nВсе хуки — вкладка «Хуки Harmony» внизу";
    }

    private static void Marks(StringBuilder sb, List<(int Start, int Length)>? marks)
    {
        if (marks == null) return;
        foreach (var (s, l) in marks) sb.Append('[').Append(s).Append(',').Append(l).Append(']');
    }

    private string Signature(CardInfo c)
    {
        var sb = new StringBuilder(320);
        sb.Append(c.M.Id).Append('|').Append((int)c.Mode).Append('#').Append(Theme.Generation).Append('|').Append(c.W).Append('x').Append(c.H).Append('@').Append(DeviceDpi).Append('|').Append(BackColor.ToArgb()).Append('|')
          .Append(c.Sel ? 'S' : '-').Append(c.Hover ? 'H' : '-').Append(c.HTgl ? 'T' : '-').Append(c.HInst ? 'I' : '-').Append(c.DInst ? 'D' : '-')
          .Append(c.Avail ? 'A' : '-').Append(c.On ? '1' : '0').Append(c.Readable ? 'R' : '-').Append(c.ShowIcon ? 'i' : '-').Append(c.Pending ? 'P' : '-')
          .Append(c.Mode == ListMode.Table && c.Alt ? 'z' : '-').Append(Flat ? 'F' : '-').Append(c.Conflict ? 'C' : '-').Append('|')
          .Append(c.Accent.ToArgb()).Append('|').Append(c.St.Text).Append(c.St.Bg.ToArgb()).Append(c.St.Fg.ToArgb()).Append('|');
        if (c.Img != null) sb.Append("img").Append(c.ImgSerial); else sb.Append(c.Initials).Append(c.AvBg.ToArgb());
        sb.Append('|').Append(c.Title).Append(c.TitleR.Width); Marks(sb, c.TitleMarks);
        sb.Append('|').Append(c.Ver); Marks(sb, c.VerMarks);
        sb.Append('|').Append(c.Desc).Append(c.DescColor.ToArgb()).Append(c.DescR.Width); Marks(sb, c.DescMarks);
        sb.Append('|').Append(c.Author).Append(c.AuthorR.Width); Marks(sb, c.AuthorMarks);
        foreach (var t in c.Tags) sb.Append('|').Append(t.Text).Append(t.Bg.ToArgb()).Append(t.Fg.ToArgb()).Append(t.R.X);
        foreach (var x in c.Cells)
        {
            sb.Append('|').Append((int)x.Col).Append(':').Append(x.Text).Append(x.Fg.ToArgb());
            if (x.Dot is Color d) sb.Append('d').Append(d.ToArgb());
            if (x.PillBg is Color pb) sb.Append('p').Append(pb.ToArgb());
            if (x.Icon != VIcon.None) sb.Append('i').Append((int)x.Icon);
            if (x.Font != null) sb.Append('f').Append(x.Font.Name).Append(x.Font.SizeInPoints);
            Marks(sb, x.Marks);
        }
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

    /// Таблица: фон — только там, где его не закроют строки, шапка колонок и заголовок группы
    /// (заливка всего окна под ними — лишняя работа на каждом кадре прокрутки).
    protected override void PaintBack(Graphics g, Rectangle clip)
    {
        if (!IsTable || View.Count == 0) { base.PaintBack(g, clip); return; }
        var area = Rectangle.Intersect(clip, new Rectangle(0, TopH, Width, ViewH));
        if (area.Width <= 0 || area.Height <= 0) return;
        int w = CardW, pad = Pad, bottom = Height;
        using var b = new SolidBrush(Flat ? Theme.Cream : Theme.Paper);
        void Fill(int x, int y, int fw, int fh)
        {
            var q = Rectangle.Intersect(new Rectangle(x, y, fw, fh), area);
            if (q.Width > 0 && q.Height > 0) g.FillRectangle(b, q);
        }
        Fill(0, TopH, pad, ViewH);                          // слева от строк
        Fill(pad + w, TopH, Width - pad - w, ViewH);         // справа: поле и полоса прокрутки
        int y = TopH;                                        // по высоте — промежутки между строками, сверху и снизу
        var (a, z) = VisibleRange();
        for (int i = a; i <= z && y < bottom; i++)
        {
            int top = TopH + CardTop(i) - _scroll - (i == _groupAt ? HeaderH : 0);
            if (top > y) Fill(pad, y, w, top - y);
            y = Math.Max(y, TopH + CardTop(i) - _scroll + CardH);
        }
        if (y < bottom) Fill(pad, y, w, bottom - y);
    }

    protected override void Draw(Graphics g)
    {
        if (_gen != Theme.Generation || _mode != Mode) { ClearCache(); _cacheW = -1; _colsW = -1; _gen = Theme.Generation; _mode = Mode; }
        if (View.Count == 0) { DrawEmpty(g); if (_drop) DrawDrop(g); return; }
        int w = CardW;
        if (w != _cacheW) { ClearCache(); _cacheW = w; }
        bool table = IsTable;
        var clip = Rectangle.Inflate(_clip, ShadowMargin, ShadowMargin);
        var state = g.Save();
        if (TopH > 0) g.SetClip(new Rectangle(0, TopH, Width, ViewH), CombineMode.Intersect);
        if (_groupAt >= 0)
        {
            var hr = new Rectangle(Pad, TopH + CardTop(_groupAt) - HeaderH - _scroll, w, HeaderH);
            if (hr.IntersectsWith(_clip)) DrawGroupHeader(g, hr);
        }
        var (a, b) = VisibleRange();
        for (int i = a; i <= b; i++)
        {
            var r = CardRect(i);
            var c = Info(View[i], w, i);
            if (r.Bottom > TopH) AddCardHits(c, Rectangle.Intersect(r, new Rectangle(0, TopH, Width, ViewH)), r); // зоны — у всех видимых, даже если рисуется одна карточка
            if (r.IntersectsWith(clip)) PaintCard(g, c, r);
        }
        g.Restore(state);
        if (table)
        {
            if (TopH > 0) DrawTableHeader(g, w, _clip.Top < TopH); // зоны колонок нужны всегда, рисунок — если шапку задели
            if (!Flat) DrawTableFrame(g);                           // «Компактный» — без рамки: таблица во всю ширину
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
        if (!CacheEnabled) { DrawItem(g, c, r.X, r.Y); return; }
        // тень выходит за края картинки — её рисуем вживую, а в углах картинки она уже есть
        if ((c.Sel || c.Hover) && Theme.Spec.Shadows) Theme.Shadow(g, r, CardRadius(c), Px(7), c.Sel ? 20 : 12);
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

    private Bitmap RenderCard(CardInfo c) => RenderDib(c.W, c.H, g => DrawItem(g, c, 0, 0));

    private Bitmap RenderDib(int width, int height, Action<Graphics> draw)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
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
                bg.Clear(BackColor); draw(bg);
                return bmp;
            }
            old = SelectObject(dc, dib);
            using (var g = Graphics.FromHdc(dc))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(BackColor);
                draw(g);
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

    private float CardRadius(CardInfo c) => c.Mode switch
    {
        ListMode.Table => 0f,
        ListMode.Capsules => c.H / 2f,
        _ => Px(Theme.Spec.CardRadius),
    };

    /// Нарисовать карточку с левым верхним углом в (ox, oy). TextRenderer не учитывает Transform,
    /// поэтому сдвиг — явный (в кэш карточка рисуется в (0, 0)).
    private void DrawItem(Graphics g, CardInfo c, int ox, int oy)
    {
        switch (c.Mode)
        {
            case ListMode.Table: if (Flat) DrawFlatRow(g, c, ox, oy); else DrawRow(g, c, ox, oy); break;
            case ListMode.Cards: DrawFluent(g, c, ox, oy); break;
            default: DrawCapsule(g, c, ox, oy); break;
        }
    }

    private static bool Same(Color a, Color b) => a.ToArgb() == b.ToArgb();

    /// Состояние, которое стоит подсветить полоской: ждёт применения, ошибка, доступен (а не просто «включён/выключен»).
    private static bool Notable(Color accent) => !Same(accent, Theme.Teal) && !Same(accent, Theme.LineDark);

    private void DrawAvatar(Graphics g, CardInfo c, Rectangle ir, Font f, float radius)
    {
        if (c.Img != null) { g.DrawImage(c.Img, ir, 0, 0, c.Img.Width, c.Img.Height, GraphicsUnit.Pixel); return; }
        bool avail = c.Avail;
        Theme.FillRound(g, ir, radius, avail ? Theme.Mix(c.AvBg, Theme.Paper, 0.35f) : c.AvBg);
        Theme.Text(g, c.Initials, f, ir, avail ? Theme.Mix(c.AvFg, Theme.Paper, 0.2f) : c.AvFg, TextFormatFlags.HorizontalCenter);
    }

    private void DrawInstallButton(Graphics g, CardInfo c, Rectangle tg)
    {
        Theme.FillRound(g, tg, Theme.Radius(tg.Height, S), c.DInst ? Theme.BlueText : c.HInst ? Theme.Blue : Theme.BlueTint);
        Theme.Glyph(g, Theme.GDownload, tg, c.HInst || c.DInst ? Color.White : Theme.BlueText, Theme.IconsSmall);
    }

    /// Флажок таблицы. Ждёт применения — точка в углу (синяя: включится, оранжевая: выключится).
    private void CheckBox(Graphics g, Rectangle r, bool on, bool hover, bool dim, bool pending, Color bg)
    {
        float rad = Px(4);
        if (on)
        {
            Color fill = hover ? Theme.TealDark : Theme.Teal;
            if (dim) fill = Theme.Mix(fill, bg, 0.5f);
            Theme.FillRound(g, r, rad, fill);
            Theme.Glyph(g, Theme.GCheck, r, Color.White, Theme.IconsSmall);
        }
        else
        {
            Theme.FillRound(g, r, rad, Theme.Dark ? Theme.Mix(bg, Color.Black, 0.25f) : Theme.Paper);
            Theme.StrokeRound(g, r, rad, hover ? Theme.Teal : dim ? Theme.Line : Theme.Mix(Theme.LineDark, Theme.NavySoft, 0.3f), Math.Max(1.2f, 1.5f * S));
        }
        if (pending)
        {
            float cx = r.Right - Px(1), cy = r.Y + Px(1);
            Theme.Dot(g, cx, cy, Px(4.5f), bg);
            Theme.Dot(g, cx, cy, Px(3.2f), on ? Theme.Blue : Theme.Orange);
        }
    }

    // ── таблица
    private void DrawRow(Graphics g, CardInfo c, int ox, int oy)
    {
        var r = new Rectangle(ox, oy, c.W, c.H);
        Rectangle O(Rectangle q) => new(q.X + ox, q.Y + oy, q.Width, q.Height);
        Color bg = c.Sel ? Theme.Selected : c.Hover ? Theme.RowHover : c.Avail ? Theme.Mix(Theme.Paper, Theme.Cream, 0.5f) : c.Alt ? Theme.RowAlt : Theme.Paper;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        if (c.Sel) using (var p = new Pen(Theme.Alpha(Theme.Teal, 90), Math.Max(1f, S))) { g.DrawLine(p, r.X, r.Y, r.Right, r.Y); g.DrawLine(p, r.X, r.Bottom - 1, r.Right, r.Bottom - 1); }
        // полоска слева: выбор — акцентом, иначе только заметные состояния
        if (c.Sel || Notable(c.Accent))
            using (var b = new SolidBrush(c.Sel ? Theme.Teal : c.Accent)) g.FillRectangle(b, r.X, r.Y, Px(c.Sel ? 3 : 2), r.Height);

        var tg = O(c.Tg);
        if (c.Avail) DrawInstallButton(g, c, tg);
        else CheckBox(g, tg, c.On, c.HTgl, !c.Readable, c.Pending, bg);

        if (c.ShowIcon) DrawAvatar(g, c, O(c.Ico), Theme.Section, Px(5));
        bool dimTitle = c.Avail || !c.On;
        Theme.TextMarked(g, c.Title, Theme.ButtonFont, O(c.TitleR), dimTitle ? Theme.NavySoft : Theme.Navy, c.TitleMarks);
        foreach (var t in c.Tags) Theme.Pill(g, t.R.X + ox, t.R.Y + oy, t.R.Height, t.Text, Theme.SmallBold, t.Bg, t.Fg);
        if (c.Desc.Length > 0 && c.DescColor.A > 0)
        {
            if (c.NoDesc) Theme.Text(g, c.Desc, Theme.Small, O(c.DescR), c.DescColor);
            else Theme.TextMarked(g, c.Desc, Theme.Small, O(c.DescR), c.DescColor, c.DescMarks);
        }

        foreach (var x in c.Cells)
        {
            int pad = Px(8);
            var cr = O(x.R);
            var tr = new Rectangle(cr.X + pad, cr.Y, cr.Width - pad * 2, cr.Height);
            if (x.PillBg is Color pb)
            {
                int ph = Px(18);
                Theme.Pill(g, tr.X, cr.Y + (cr.Height - ph) / 2, ph, x.Text, Theme.SmallBold, pb, x.Fg, Theme.GDownload);
                continue;
            }
            if (x.Dot is Color d)
            {
                Theme.Dot(g, tr.X + Px(3.5f), cr.Y + cr.Height / 2f, Px(3.5f), d);
                tr = new Rectangle(tr.X + Px(13), tr.Y, tr.Width - Px(13), tr.Height);
            }
            if (x.Right) Theme.Text(g, x.Text, Theme.Small, tr, x.Fg, TextFormatFlags.Right);
            else Theme.TextMarked(g, x.Text, Theme.Small, tr, x.Fg, x.Marks);
        }
    }

    // ── карточки Fluent
    private void DrawFluent(Graphics g, CardInfo c, int ox, int oy)
    {
        var r = new Rectangle(ox, oy, c.W, c.H);
        Rectangle O(Rectangle q) => new(q.X + ox, q.Y + oy, q.Width, q.Height);
        bool sel = c.Sel, avail = c.Avail, hover = c.Hover;
        float rad = Px(Theme.Spec.CardRadius);
        Color bg = sel ? Theme.Selected : hover ? Theme.Mix(Theme.Paper, Theme.Beige, 0.45f) : avail ? Theme.Mix(Theme.Paper, Theme.Cream, 0.5f) : Theme.Paper;
        Theme.FillRound(g, r, rad, bg);
        if (avail && !sel)
        {
            using var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), rad);
            using var pen = new Pen(hover ? Theme.Blue : Theme.Alpha(Theme.Blue, 110), Math.Max(1f, S)) { DashPattern = new[] { 4f, 3f } };
            g.DrawPath(pen, path);
        }
        else Theme.StrokeRound(g, r, rad, sel ? Theme.Alpha(Theme.Teal, 150) : hover ? Theme.LineDark : Theme.Line, Math.Max(1f, S));
        // индикатор слева (как у выбранного пункта в Windows 11): выбор или заметное состояние
        if (sel || Notable(c.Accent))
        {
            int ih = sel ? Px(20) : Px(14);
            Theme.FillRound(g, new RectangleF(r.X + Px(1), r.Y + (r.Height - ih) / 2f, Px(3), ih), Px(1.5f), sel ? Theme.Teal : c.Accent);
        }

        var tg = O(c.Tg);
        if (avail) DrawInstallButton(g, c, tg);
        else Theme.Toggle(g, tg, c.On, c.HTgl, !c.Readable);

        if (c.ShowIcon) DrawAvatar(g, c, O(c.Ico), Theme.ButtonFont, Px(6));

        Theme.Pill(g, c.Pill.X + ox, c.Pill.Y + oy, c.Pill.Height, c.St.Text, Theme.SmallBold, c.St.Bg, c.St.Fg);
        Theme.TextMarked(g, c.Title, Theme.Strong, O(c.TitleR), Theme.Navy, c.TitleMarks);
        if (c.Ver != null) Theme.TextMarked(g, c.Ver, Theme.Small, O(c.VerR), Theme.Hint, c.VerMarks);
        if (c.AuthorDot && c.Author.Length > 0)
        {
            var ar = O(c.AuthorR);
            Theme.Text(g, "·", Theme.Small, new Rectangle(ar.X - Px(10), ar.Y, Px(8), ar.Height), Theme.Hint);
            Theme.TextMarked(g, c.Author, Theme.Small, ar, Theme.Hint, c.AuthorMarks);
        }
        if (c.NoDesc) Theme.Text(g, c.Desc, Theme.Body, O(c.DescR), c.DescColor);
        else Theme.TextMarked(g, c.Desc, Theme.Body, O(c.DescR), c.DescColor, c.DescMarks);
        foreach (var t in c.Tags) Theme.Pill(g, t.R.X + ox, t.R.Y + oy, t.R.Height, t.Text, Theme.SmallBold, t.Bg, t.Fg);
        if (c.More != null) Theme.Pill(g, c.MoreR.X + ox, c.MoreR.Y + oy, c.MoreR.Height, c.More, Theme.SmallBold, Theme.Beige, Theme.NavySoft);
    }

    // ── капсулы «Слаймового» стиля
    private void DrawCapsule(Graphics g, CardInfo c, int ox, int oy)
    {
        var r = new Rectangle(ox, oy, c.W, c.H);
        Rectangle O(Rectangle q) => new(q.X + ox, q.Y + oy, q.Width, q.Height);
        bool sel = c.Sel, avail = c.Avail, hover = c.Hover;
        float rad = r.Height / 2f;
        if (sel || hover) Theme.Shadow(g, r, rad, Px(7), sel ? 20 : 12);
        Theme.FillRound(g, r, rad, sel ? Theme.Selected : avail && !hover ? Theme.Mix(Theme.Paper, Theme.Cream, 0.45f) : Theme.Paper);
        if (avail && !sel)
        {   // ещё не установлен — пунктирная рамка
            using var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f), rad);
            using var pen = new Pen(hover ? Theme.Blue : Theme.Alpha(Theme.Blue, 120), Math.Max(1f, S)) { DashPattern = new[] { 4f, 3f } };
            g.DrawPath(pen, path);
        }
        else Theme.StrokeRound(g, r, rad, sel ? Theme.Teal : hover ? Theme.LineDark : Theme.Mix(Theme.Line, c.Accent, 0.18f), sel ? Math.Max(1.5f, 1.8f * S) : Math.Max(1f, 1.2f * S));

        // полоска состояния (короче — в пределах скругления)
        Theme.FillRound(g, new RectangleF(r.X + Px(11), r.Y + Px(24), Px(4), r.Height - Px(48)), Px(2), c.Accent);

        var tg = O(c.Tg);
        if (avail) DrawInstallButton(g, c, tg);
        else Theme.Toggle(g, tg, c.On, c.HTgl, !c.Readable);

        if (c.ShowIcon) DrawAvatar(g, c, O(c.Ico), Theme.Strong, Px(20));   // круглый значок — под капсулу

        Theme.Pill(g, c.Pill.X + ox, c.Pill.Y + oy, c.Pill.Height, c.St.Text, Theme.SmallBold, c.St.Bg, c.St.Fg);
        Theme.TextMarked(g, c.Title, Theme.Strong, O(c.TitleR), Theme.Navy, c.TitleMarks);
        if (c.Ver != null) Theme.TextMarked(g, c.Ver, Theme.Small, O(c.VerR), Theme.Hint, c.VerMarks);
        if (c.NoDesc) Theme.Text(g, c.Desc, Theme.Body, O(c.DescR), c.DescColor);
        else Theme.TextMarked(g, c.Desc, Theme.Body, O(c.DescR), c.DescColor, c.DescMarks);
        Theme.TextMarked(g, c.Author, Theme.Small, O(c.AuthorR), Theme.Hint, c.AuthorMarks);
        foreach (var t in c.Tags) Theme.Pill(g, t.R.X + ox, t.R.Y + oy, t.R.Height, t.Text, Theme.SmallBold, t.Bg, t.Fg);
        if (c.More != null) Theme.Pill(g, c.MoreR.X + ox, c.MoreR.Y + oy, c.MoreR.Height, c.More, Theme.SmallBold, Theme.Beige, Theme.NavySoft);
    }

    /// visible — видимая часть строки (под шапкой таблицы зоны не нужны), r — вся строка.
    private void AddCardHits(CardInfo c, Rectangle visible, Rectangle r)
    {
        if (visible.Width <= 0 || visible.Height <= 0) return;
        string id = c.M.Id;
        Rectangle O(Rectangle q) => Rectangle.Intersect(new Rectangle(q.X + r.X, q.Y + r.Y, q.Width, q.Height), visible);
        void Menu(Point pt) { Select(id); var m = ById(id); if (m != null) MenuRequested?.Invoke(m, pt); }
        AddHit(visible, "card:" + id, () => Select(id), null, Menu, false);
        for (int i = 0; i < c.Tags.Count; i++)
            if (c.Tags[i].Tip != null) AddHit(O(c.Tags[i].R), "tag:" + id + ":" + i, () => Select(id), c.Tags[i].Tip, Menu, false);
        if (c.More != null) AddHit(O(c.MoreR), "tag:" + id + ":more", () => Select(id), c.MoreTip, Menu, false);
        foreach (var x in c.Cells)
            if (x.Tip != null) AddHit(O(x.R), "tag:" + id + ":" + x.Col, () => Select(id), x.Tip, Menu, false);
        var tg = O(Rectangle.Inflate(c.Tg, c.Mode == ListMode.Table ? Px(10) : Px(8), c.Mode == ListMode.Table ? Px(6) : Px(10)));
        string stTip = c.St.Text.Length > 0 ? char.ToUpperInvariant(c.St.Text[0]) + c.St.Text.Substring(1) + ": " + c.St.Tip + "\n" : "";
        if (c.Avail)
            AddHit(tg, "inst:" + id, () => { Select(id); var m = ById(id); if (m != null) InstallRequested?.Invoke(m); }, "Установить в Mods (Enter)", Menu);
        else
            AddHit(tg, "tgl:" + id, () => { Select(id); var m = ById(id); if (m != null) ToggleRequested?.Invoke(m); },
                (c.Mode == ListMode.Table ? stTip : "") + (c.On ? "Выключить (Пробел)" : "Включить (Пробел)"), Menu);
        if (c.Mode != ListMode.Table) AddHit(O(c.Pill), "st:" + id, () => Select(id), c.St.Tip, Menu, false);
    }

    private void DrawGroupHeader(Graphics g, Rectangle r)
    {
        if (r.Bottom < 0 || r.Top > Height) return;
        int n = View.Count - _groupAt;
        string title = "ДОСТУПНЫ ДЛЯ УСТАНОВКИ · " + n;
        bool table = IsTable;
        if (table)
        {
            using (var b = new SolidBrush(Theme.HeaderBg)) g.FillRectangle(b, r);
            using var lp = new Pen(Theme.Line, Math.Max(1f, S));
            g.DrawLine(lp, r.X, r.Y, r.Right, r.Y);
            g.DrawLine(lp, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
        }
        int x = r.X + Px(table ? 12 : 6), y = table ? r.Y + (r.Height - Px(18)) / 2 : r.Y + (_groupAt == 0 ? Px(2) : Mode == ListMode.Cards ? Px(6) : Px(8));
        var ts = Theme.Measure(title, Theme.Section);
        Theme.Glyph(g, Theme.GDownload, new Rectangle(x, y, Px(16), Px(18)), Theme.BlueText, Theme.IconsSmall);
        Theme.Text(g, title, Theme.Section, new Rectangle(x + Px(20), y, ts.Width + 2, Px(18)), Theme.BlueText);
        string hint = "не установлены — Enter или кнопка ↓ ставит в Mods";
        int hw = Theme.Measure(hint, Theme.Small).Width;
        int lineFrom = x + Px(20) + ts.Width + Px(10), lineTo = r.Right - Px(table ? 12 : 6);
        if (lineTo - lineFrom > hw + Px(40))
        {
            Theme.Text(g, hint, Theme.Small, new Rectangle(lineTo - hw, y, hw + 1, Px(18)), Theme.Hint);
            lineTo -= hw + Px(10);
        }
        using var p = new Pen(Theme.Alpha(Theme.Blue, 70), Math.Max(1f, S));
        if (lineTo > lineFrom) g.DrawLine(p, lineFrom, y + Px(9), lineTo, y + Px(9));
    }

    /// Закреплённая шапка таблицы: заголовки колонок, щелчок — сортировка (стрелка у активной).
    /// Картинка шапки (как у строк): меняется только от ширины, наведения, нажатия и сортировки.
    private Bitmap? _headBmp;
    private string _headSig = "";

    private void DrawTableHeader(Graphics g, int w, bool paint)
    {
        int h = TopH;
        foreach (var col in Columns(w))
        {
            var cr = new Rectangle(Pad + col.X, 0, col.W, h - 1);
            var c2 = col.Col;
            string dir = SortColumn == c2 ? (SortDesc ? " (по убыванию)" : " (по возрастанию)") : "";
            AddHit(cr, "col:" + c2, () => SortRequested?.Invoke(c2), col.Tip + "\nЩелчок — сортировать" + dir + "; ещё щелчок — обратный порядок, третий — как было");
        }
        if (!paint) return;
        if (!CacheEnabled) { PaintTableHeader(g, w); return; }
        static string Col(string? k) => k != null && k.StartsWith("col:", StringComparison.Ordinal) ? k : "";
        string sig = Width + "|" + w + "|" + h + "|" + DeviceDpi + "|" + Theme.Generation + "|" + Col(HoverKey) + "|" + Col(PressKey) + "|" + SortColumn + "|" + SortDesc;
        if (_headBmp == null || _headSig != sig)
        {
            _headBmp?.Dispose();
            _headBmp = RenderDib(Width, h, hg => PaintTableHeader(hg, w));
            _headSig = sig;
        }
        var im = g.InterpolationMode; var cm = g.CompositingMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_headBmp, new Rectangle(0, 0, _headBmp.Width, _headBmp.Height), 0, 0, _headBmp.Width, _headBmp.Height, GraphicsUnit.Pixel);
        g.InterpolationMode = im; g.CompositingMode = cm;
    }

    private void PaintTableHeader(Graphics g, int w)
    {
        if (Flat) { PaintFlatHeader(g, w); return; }
        int h = TopH;
        var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
        using (var b = new SolidBrush(Theme.HeaderBg)) g.FillRectangle(b, new Rectangle(0, 0, Width, h));
        g.SmoothingMode = sm;
        using (var lp = new Pen(Theme.Line, Math.Max(1f, S))) g.DrawLine(lp, 0, h - 1, Width, h - 1);
        foreach (var col in Columns(w))
        {
            var cr = new Rectangle(Pad + col.X, 0, col.W, h - 1);
            string key = "col:" + col.Col;
            bool hv = IsHover(key), dn = IsDown(key), active = SortColumn == col.Col;
            if (hv || dn) using (var hb = new SolidBrush(dn ? Theme.Beige : Theme.RowHover)) g.FillRectangle(hb, cr);
            Color fg = active ? Theme.Navy : hv ? Theme.NavySoft : Theme.Hint;
            int pad = Px(8), aw = active ? Px(14) : 0;
            if (col.Col == ModColumn.Check)
            {
                Theme.Glyph(g, Theme.GCheck, new Rectangle(cr.X, 0, cr.Width - aw / 2, h), fg, Theme.IconsSmall);
                if (active) Theme.Glyph(g, SortDesc ? Theme.GSortDown : Theme.GSortUp, new Rectangle(cr.Right - Px(14), 0, Px(12), h), Theme.TealText, Theme.IconsSmall);
            }
            else
            {
                string title = Theme.Measure(col.Title, Theme.Section).Width + aw <= cr.Width - pad * 2 ? col.Title : col.Short;
                int tw = Math.Min(Theme.Measure(title, Theme.Section).Width + 2, Math.Max(0, cr.Width - pad * 2 - aw));
                int tx = col.Right ? cr.Right - pad - tw - aw : cr.X + pad;
                Theme.Text(g, title, Theme.Section, new Rectangle(tx, 0, tw, h), fg);
                if (active) Theme.Glyph(g, SortDesc ? Theme.GSortDown : Theme.GSortUp, new Rectangle(tx + tw + Px(1), 0, Px(12), h), Theme.TealText, Theme.IconsSmall);
            }
            if (col.X > 0) using (var sp = new Pen(Theme.Line, Math.Max(1f, S))) g.DrawLine(sp, cr.X, Px(8), cr.X, h - Px(9));
        }
    }

    /// Таблица — панель со скруглёнными углами: углы снаружи — цветом фона, сверху — тонкая рамка.
    /// Заливаются только задетые перерисовкой углы (квадрат минус четверть круга), а не всё окно.
    private void DrawTableFrame(Graphics g)
    {
        var r = new RectangleF(0, 0, Width, Height);
        float rad = Px(Theme.Spec.PanelRadius);
        float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
        if (d > 1f)
        {
            float q = d / 2f;
            int qi = (int)Math.Ceiling(q) + 1;
            using var b = new SolidBrush(BackColor);
            foreach (var (x, y, angle) in new[] { (0f, 0f, 180f), (r.Right - d, 0f, 270f), (r.Right - d, r.Bottom - d, 0f), (0f, r.Bottom - d, 90f) })
            {
                float sx = angle is 180f or 90f ? x : x + q, sy = angle is 180f or 270f ? y : y + q;
                if (!_clip.IntersectsWith(new Rectangle((int)sx - 1, (int)sy - 1, qi + 1, qi + 1))) continue;
                using var path = new GraphicsPath(FillMode.Alternate);
                path.AddRectangle(new RectangleF(sx, sy, q, q));
                path.AddPie(x, y, d, d, angle, 90f);
                g.FillPath(b, path);
            }
        }
        Theme.StrokeRound(g, r, rad, Theme.Line, Math.Max(1f, S));
    }

    private void DrawEmpty(Graphics g)
    {
        var r = new Rectangle(Pad, Pad, Width - Pad * 2, Height - Pad * 2);
        float rad = Px(Theme.Spec.PanelRadius);
        if (!Flat)   // «Компактный» — прямо на фоне таблицы, без панели
        {
            Theme.FillRound(g, r, rad, Theme.Paper);
            Theme.StrokeRound(g, r, rad, Theme.Line, Math.Max(1f, S));
        }
        int sz = Px(64);
        int cy = r.Y + Math.Max(Px(20), r.Height / 2 - Px(84));
        if (Flat)
        {   // «Компактный»: значок в круге, как у пустых вкладок нижней панели
            int d = Px(46);
            var c = new Rectangle(r.X + (r.Width - d) / 2, cy + sz - d - Px(10), d, d);
            Theme.FillRound(g, c, d / 2f, EmptyHappy ? Theme.TealTint : Theme.Beige);
            var ic = EmptyIcon != VIcon.None ? EmptyIcon : EmptyHappy ? VIcon.Check : VIcon.CircleInfo;
            float isz = Px(18);
            Theme.Icon(g, ic, new RectangleF(c.X + (d - isz) / 2f, c.Y + (d - isz) / 2f, isz, isz), EmptyHappy ? Theme.TealText : Theme.NavySoft);
        }
        else Theme.Slime(g, new RectangleF(r.X + (r.Width - sz) / 2f, cy, sz, sz * 0.86f), EmptyHappy ? Theme.Teal : Theme.Hex(0xB9C2D0), EmptyHappy);
        Theme.Text(g, EmptyTitle, Theme.Strong, new Rectangle(r.X + Px(20), cy + sz, r.Width - Px(40), Px(28)), Theme.Navy, TextFormatFlags.HorizontalCenter);
        int tw = r.Width - Px(80);
        int th = Math.Min(Theme.WrapHeight(EmptyText, Theme.Body, tw), Px(80));
        Theme.Wrap(g, EmptyText, Theme.Body, new Rectangle(r.X + Px(40), cy + sz + Px(30), tw, th + Px(2)), Theme.Hint, true);
        if (EmptyAction.Length > 0)
        {
            int bh = Px(34), bw = Theme.Measure(EmptyAction, Theme.ButtonFont).Width + Px(36);
            var br = new Rectangle(r.X + (r.Width - bw) / 2, cy + sz + Px(30) + th + Px(16), bw, bh);
            bool h = IsHover("empty:act"), dn = IsDown("empty:act");
            Theme.FillRound(g, br, Theme.Radius(bh, S), dn ? Theme.TealDark : h ? Theme.Teal : Theme.TealTint);
            Theme.Text(g, EmptyAction, Theme.ButtonFont, br, h || dn ? Color.White : Theme.TealText, TextFormatFlags.HorizontalCenter);
            AddHit(br, "empty:act", () => EmptyActionClick?.Invoke());
        }
    }

    private void DrawDrop(Graphics g)
    {
        using (var b = new SolidBrush(Color.FromArgb(232, Theme.Cream))) g.FillRectangle(b, ClientRectangle);
        var r = new RectangleF(Px(6), Px(6), Width - Px(12), Height - Px(12));
        using (var p = Theme.RoundRect(r, Px(Math.Max(8, Theme.Spec.PanelRadius + 2))))
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

    private bool OnBar(Point p) => HasBar && p.X >= Width - BarW - Px(2) && p.Y >= TopH;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Focused) Focus();
        if (e.Button == MouseButtons.Left && OnBar(e.Location))
        {
            StopWheel();
            var tr = ThumbRect();
            if (tr.Contains(e.Location)) { _dragThumb = true; _dragOffset = e.Y - tr.Y; Capture = true; }
            else { _scroll += (e.Y < tr.Y ? -1 : 1) * Math.Max(Px(40), ViewH * 9 / 10); ClampScroll(); }
            InvalidateRows();
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragThumb)
        {
            var tr = ThumbRect();
            int track = ViewH - Px(6) - tr.Height;
            int ny = e.Y - _dragOffset - TopH - Px(3);
            int before = _scroll;
            _scroll = track <= 0 ? 0 : (int)((long)ny * MaxScroll / track);
            ClampScroll();
            if (_scroll != before) InvalidateRows();
            return;
        }
        bool hb = OnBar(e.Location);
        if (hb != _hoverBar) { _hoverBar = hb; Invalidate(new Rectangle(Width - BarW - Px(2), 0, BarW + Px(2), Height)); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragThumb) { _dragThumb = false; Capture = false; InvalidateRows(); return; }
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || OnBar(e.Location)) return;
        var k = HitAt(e.Location)?.Key;
        if (k == null || !(k.StartsWith("card:", StringComparison.Ordinal) || k.StartsWith("tag:", StringComparison.Ordinal) || k.StartsWith("st:", StringComparison.Ordinal))) return;
        var id = KeyId(k);
        var m = id == null ? null : ById(id);
        if (m != null) OpenRequested?.Invoke(m);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBar) { _hoverBar = false; Invalidate(new Rectangle(Width - BarW - Px(2), 0, BarW + Px(2), Height)); }
    }

    /// Шаг колеса: в таблице — три строки, у карточек — как раньше.
    private int WheelPx => IsTable ? Pitch * 3 : Px(66);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-e.Delta * WheelPx / 120);
    }

    /// Прокрутить на dy пикселей: плавно (~150 мс, ease-out), в снимке окна — сразу.
    public void ScrollBy(int dy, bool smooth = true)
    {
        int target = Math.Clamp((_wheel.Enabled ? _animTo : _scroll) + dy, 0, MaxScroll);
        if (!smooth || !Anim.Enabled || !IsHandleCreated)
        {
            StopWheel();
            if (target != _scroll) { _scroll = target; InvalidateRows(); Update(); UpdateHover(PointToClient(MousePosition)); }
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
            InvalidateRows();
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
        int per = Math.Max(1, ViewH / Math.Max(1, Pitch));
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
