// MainForm.cs — окно лаунчера: наборы модов, поиск и быстрые фильтры, список модов (таблица, карточки или капсулы —
// по выбранному дизайну), нижняя панель (MainForm.Dock.cs: «Проблемы», «Хуки Harmony», «Логи», «Детали»),
// каталог доступных модов и обновлений (ModManager\Library, «Загрузки»), установка перетаскиванием,
// планирование изменений при запущенной игре, «Играть».
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed class RenderOptions
{
    public bool? AssumeRunning;
    public string? Select;
    public int Filter = -1;
    public string? Search;
    public string? Toast;
    public bool Drop;
    public string? CatalogDir;   // --catalog: папка, которая в снимке играет роль «Загрузок»
    public DesignKind? Design;   // --design compact|fluent|slime (по умолчанию — «Компактный»; ui.json в снимке не читается)
    public DockTab? Dock;        // --dock problems|hooks|logs|changes|details: открытая вкладка нижней панели
    public bool DockCollapsed;   // --dock-collapsed: панель свёрнута до полосы вкладок
    public string? Sort;         // --sort колонка[:asc|:desc], например «compat:desc»
    public bool Picker;          // --picker: открыт выбор дизайна (как после кнопки «Дизайн»)
    public DesignKind? SwitchTo; // --switch-design: после показа окна сменить стиль на лету (проверка смены без перезапуска)
    public string? Toggle;       // --toggle id[,id…]: переключить отметки этих модов (только в памяти) — видно «не применено»
    public bool Bench { get; set; } // --bench: замеры — проверка совместимости и значки грузятся в фоне, как в обычном запуске
}

internal sealed partial class MainForm : Form
{
    private sealed class RootView : Canvas
    {
        public Action<Graphics, RootView>? Painter;
        protected override void Draw(Graphics g) => Painter?.Invoke(g, this);
        public void AddTip(Rectangle r, string key, string tip) => AddHit(r, key, null, tip, null, false);
    }

    private readonly ModStore _store;
    private readonly Model _m;
    private readonly RenderOptions? _render;
    private readonly RootView _root = new() { Dock = DockStyle.Fill };
    private readonly ChipBar _chips = new();
    private readonly TextField _search = new("Найти мод: название, автор, описание", Theme.GSearch) { Shortcut = "Ctrl+F", PlaceholderShort = "Найти мод…" };
    private readonly Segmented _filter = new();
    private CatalogFilter _filterKind = CatalogFilter.All;
    private readonly List<CatalogFilter> _filterKinds = new();
    private bool _searchQueued;
    private string _lastQuery = "";
    private Action? _emptyAct;
    private int _lastFilterW = -1;
    private readonly PillButton _allOn = new("Включить все", Look.Ghost, Theme.GCheck);
    private readonly PillButton _allOff = new("Выключить все", Look.Ghost, Theme.GCancel);
    private readonly StatusLine _banner = new();
    private readonly StatusLine _status = new();
    private readonly ModList _list;
    private readonly DetailsView _details;
    private readonly Segmented _launchVia = new();
    private readonly PillButton _more = new("", Look.Quiet) { IconOnly = true };
    private readonly PillButton _reset = new("Сбросить", Look.Ghost, Theme.GUndo);
    private readonly PillButton _apply = new("Применить", Look.Secondary, Theme.GSave);
    private readonly PillButton _play = new("Играть", Look.Primary, Theme.GPlay);
    private readonly PillButton _openMods = new("Mods", Look.Quiet, Theme.GFolder);
    private readonly PillButton _openLogs = new("Логи", Look.Quiet, Theme.GDoc);
    private readonly PillButton _saveBackup = new("Бэкап сейвов", Look.Quiet, Theme.GSave);
    private readonly IconCache _icons;
    private int _compatGen;
    public double CompatMs;                 // сколько заняла последняя фоновая проверка совместимости
    public long CompatAllocBytes;           // сколько памяти она выделила (для замеров)
    private string _rootSig = "", _chipsSig = "", _filterSig = "", _envHidden = "";
    private string? _saveRoot;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 800 };
    private FileSystemWatcher? _watcher;
    private readonly List<FileSystemWatcher> _catalogWatchers = new();
    private readonly System.Windows.Forms.Timer _catalogDebounce = new() { Interval = 1500 };
    private System.Threading.CancellationTokenSource? _catalogCts;
    private int _catalogGen;
    private readonly Dictionary<string, string?> _userDataCache = new(StringComparer.OrdinalIgnoreCase);
    private ContextMenuStrip? _menu;
    private string _toast = "";
    private Tone _toastTone;
    private DateTime _toastUntil;
    private (string, Action)[] _toastActions = Array.Empty<(string, Action)>();
    private int _tick;
    private bool _lastBanner;
    private (Size, int) _rootLayoutKey;
    private DateTime _logStamp;
    private bool _busy, _closingAfterLaunch;
    private string? _backupsFor;
    private List<BackupInfo> _backups = new();
    private string? _userData;
    private readonly string[] _startupInstall;
    private readonly string? _startupMessage;

    private static readonly string AppVersion = FormatVersion(typeof(MainForm).Assembly.GetName().Version);

    private static string FormatVersion(Version? v) => v == null ? "1.1" : v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    public MainForm(ModStore store, string[]? install = null, string? startupMessage = null, RenderOptions? render = null)
    {
        _store = store;
        _render = render;
        // стиль — до создания списка и панелей: они берут размеры и цвета активного дизайна
        var paletteBefore = Theme.P;
        LoadUi(store, render);
        Theme.Use(_ui.DesignKind);
        if (render != null) Anim.Enabled = false; // снимок — сразу конечные кадры анимаций
        _startupInstall = install ?? Array.Empty<string>();
        _startupMessage = startupMessage;
        _m = new Model(store);
        _list = new ModList(_m);
        _details = new DetailsView(_m);
        _icons = new IconCache(_list, store) { Sync = render != null && !render.Bench };
        _list.Icons = _icons;
        _icons.Ready += id => _list.InvalidateCard(id);
        SetupDock();

        Text = "SR2 Mod Manager — Slime Rancher 2";
        Font = Theme.Body;
        BackColor = Theme.Cream;
        KeyPreview = true;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        float s = DeviceDpi / 96f;
        MinimumSize = new Size((int)(900 * s), (int)(620 * s));
        // с нижней панелью окну нужна высота: по умолчанию 1180×840, но не больше рабочей области экрана
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Size = new Size(Math.Max(MinimumSize.Width, Math.Min((int)(1180 * s), wa.Width)), Math.Max(MinimumSize.Height, Math.Min((int)(840 * s), wa.Height)));
        try
        {
            // своя иконка-слайм (вшита в exe со всеми размерами); если её нет — иконка игры
            using var ico = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
            Icon = ico != null ? new Icon(ico) : Icon.ExtractAssociatedIcon(store.GameExe);
        }
        catch { }

        _launchVia.Items.Add(new Segmented.Item("Steam", null, "Запуск через Steam (steam://rungameid) — как обычно"));
        _launchVia.Items.Add(new Segmented.Item("Напрямую", null, "Запуск SlimeRancher2.exe из папки игры"));
        _more.Glyph = Theme.GMore;
        _more.Tip = "Ещё: установить мод, наборы (экспорт/импорт), бэкапы сейвов, папки, журналы";
        _allOn.Tip = "Отметить все моды";
        _allOff.Tip = "Снять отметку со всех модов (чистая игра)";
        _reset.Tip = "Вернуть отметки как сейчас на диске";
        _openMods.Tip = "Открыть папку Mods в проводнике";
        _openLogs.Tip = "Журнал MelonLoader последнего запуска (Latest.log). Правый клик — папка Logs и другие журналы";
        _saveBackup.Tip = "Сохранить бэкап сейвов игры сейчас (zip с проверкой SHA-256). Правый клик — восстановить из бэкапа";
        foreach (var c in new Control[] { _launchVia, _more, _reset, _apply, _play, _openMods, _openLogs, _saveBackup }) c.BackColor = Theme.Paper;
        _banner.Height = _root.Px(42);
        _status.Height = _root.Px(40);

        _root.Painter = PaintRoot;
        _root.Controls.AddRange(new Control[] { _chips, _search, _filter, _allOn, _allOff, _designBtn, _banner, _list, _grip, _dock, _status, _launchVia, _more, _openMods, _openLogs, _saveBackup, _reset, _apply, _play,
            _side, _bar, _cmd, _split });
        SetupCompact();
        Controls.Add(_root);
        _root.Layout += (_, _) => DoLayout();

        // события
        _chips.Picked += PickProfile;
        _chips.MenuRequested += ProfileMenu;
        _chips.AddRequested += SaveProfileAs;
        _search.Changed += QueueSearch;
        _search.Submitted += () => FocusList();
        _filter.Changed += i => { if (i >= 0 && i < _filterKinds.Count) SetFilter(_filterKinds[i]); };
        _allOn.Click += (_, _) => SetAll(true);
        _allOff.Click += (_, _) => SetAll(false);
        _list.SelectionChanged += () => { ShowDetails(false); OnDockSelection(); };
        _list.ToggleRequested += Toggle;
        _list.MenuRequested += ModMenu;
        _list.DeleteRequested += DeleteMod;
        _list.InstallRequested += InstallAvailable;
        _list.EmptyActionClick += () => _emptyAct?.Invoke();
        _list.TypeRequested += TypeToSearch;
        _details.ToggleRequested += Toggle;
        _details.SelectRequested += SelectAnywhere;
        _details.InstallRequested += InstallAvailable;
        _details.UpdateRequested += UpdateMod;
        _details.LinkRequested += url => Report(Shell.OpenLink(url));
        _details.EnableRequested += id => { _m.Sel[id] = true; var add = ModStore.AddMissingDependencies(_m.Mods, _m.Sel); Toast(Tone.Ok, "Включён " + _m.NameOf(id) + (add.Count > 0 ? " и " + Names(add) : "") + "."); RefreshUi(); };
        _details.RestoreRequested += RestoreBackup;
        _details.RevealRequested += m => Report(Shell.Reveal(m.Path));
        _details.SettingsRequested += m => { var d = _store.UserDataDirFor(m); if (d != null) Report(Shell.OpenFolder(d)); };
        _details.DeleteRequested += DeleteMod;
        _details.OpenLogRequested += () => { _ui.LogSelectedOnly = true; SaveUi(); ShowDockTab(DockTab.Logs); };
        _launchVia.Changed += i => { _m.Cfg.LaunchVia = i == 1 ? "exe" : "steam"; SaveCfg(); };
        _more.Click += (_, _) => MoreMenu();
        _openMods.Click += (_, _) => Report(Shell.OpenFolder(_store.ModsDir));
        _openLogs.Click += (_, _) => OpenLogs();
        _openLogs.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) LogsMenu(); };
        _saveBackup.Click += (_, _) => BackupSavesNow();
        _saveBackup.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) SavesMenu(); };
        _reset.Click += (_, _) => ResetChanges();
        _apply.Click += (_, _) => Commit(false);
        _play.Click += (_, _) => Commit(true);
        _timer.Tick += (_, _) => Tick();
        _debounce.Tick += (_, _) => OnDebounce();
        _catalogDebounce.Tick += (_, _) => { _catalogDebounce.Stop(); StartCatalogScan(); };

        // перетаскивание файлов модов в окно
        foreach (Control c in new Control[] { this, _root, _chips, _filter, _banner, _list, _grip, _dock, _details, _problems, _hooks, _logs, _changes, _status, _side, _bar })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragOver += OnDragEnter;
            c.DragLeave += (_, _) => _list.DropHint = false;
            c.DragDrop += OnDragDrop;
        }
        // элементы, созданные до выбора стиля (поля формы), — в цвета активного дизайна
        Theme.Retint(this, paletteBefore, Theme.P);
        SyncDesignChrome();
        HandleCreated += (_, _) => ApplyTitleBar();

        _m.GameRunning = render?.AssumeRunning ?? ModStore.IsGameRunning();
        _m.GameStarted = _m.GameRunning && render == null ? GameStartTime() : render?.AssumeRunning == true ? DateTime.Now.AddMinutes(-83) : null;
        Reload(false);
        _launchVia.Selected = _m.Cfg.LaunchVia == "exe" ? 1 : 0;

        if (render != null)
        {
            if (render.Filter >= 0) _filterKind = (CatalogFilter)render.Filter;
            StartCatalogScan(); // в снимке — синхронно: ModManager\Library и папка --catalog
            if (render.Search != null) _search.Text = render.Search;
            foreach (var id in (render.Toggle ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (_m.Find(id) is { } tm) _m.Sel[tm.Id] = !_m.On(tm.Id);
            RefreshUi();
            if (render.Select != null) _list.Select(render.Select);
            if (render.Toast != null) Toast(Tone.Ok, render.Toast);
            _list.DropHint = render.Drop;
            ActiveControl = _list; // как в обычном запуске: фокус на списке, у поиска видна подсказка
            if (render.Bench) Shown += (_, _) => StartCompatCheck(); // как в обычном запуске — после показа окна, в фоне
            if (render.Picker) AddPickerForRender();
        }
        else
        {
            RestorePlacement();
            Shown += (_, _) => OnShownFirst();
            FormClosing += OnClosingForm;
            // вернулись в окно (например, из браузера со скачанным модом) — пересмотреть каталог
            Activated += (_, _) => { if (_catalogGen > 0 && !_m.CatalogBusy && (DateTime.Now - _m.CatalogAt).TotalSeconds > 20) StartCatalogScan(); };
        }
    }

    protected override bool ShowWithoutActivation => _render != null;

    private void OnShownFirst()
    {
        _list.Focus();
        _timer.Start();
        try
        {
            if (Directory.Exists(_store.ModsDir))
            {
                _watcher = new FileSystemWatcher(_store.ModsDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    SynchronizingObject = this,
                };
                _watcher.Created += OnFs; _watcher.Deleted += OnFs; _watcher.Changed += OnFs; _watcher.Renamed += OnFs;
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch { _watcher = null; }
        WatchCatalog();
        StartCatalogScan();
        StartCompatCheck();
        if (_startupMessage != null) Toast(_startupMessage.StartsWith("Не удалось") ? Tone.Error : Tone.Ok, _startupMessage);
        if (_startupInstall.Length > 0) BeginInvoke(new Action(() => InstallFiles(_startupInstall)));
    }

    // ───────── данные ─────────

    private void Reload(bool keepSel)
    {
        var prev = keepSel ? new Dictionary<string, bool>(_m.Sel, StringComparer.OrdinalIgnoreCase) : null;
        var prevDisk = keepSel ? _m.CurrentOnDisk() : null;
        try { _m.Cfg = _store.LoadConfig(); } catch { _m.Cfg = ManagerConfig.CreateDefault(); }
        _m.Mods = _store.Scan();
        _m.Pending = _store.LoadPending();
        var sel = _m.CurrentOnDisk();
        if (_m.Pending != null)
            foreach (var kv in _m.Pending.Desired) if (sel.ContainsKey(kv.Key)) sel[kv.Key] = kv.Value;
        if (prev != null && prevDisk != null)
            foreach (var kv in prev)
                if (sel.ContainsKey(kv.Key) && prevDisk.TryGetValue(kv.Key, out var was) && was != kv.Value) sel[kv.Key] = kv.Value;
        _m.Sel = sel;
        _m.Log = _store.ReadMelonLog(_m.Mods);
        _logStamp = _m.Log?.Updated ?? default;
        LoadLogTail(true);
        _m.Stored = _store.ListAllBackups(_m.Mods);
        _m.RecomputeCatalog();
        _userDataCache.Clear();
        try { _m.PluginVersion = File.Exists(_store.PluginPath) ? ModStore.Read(_store.PluginPath, true).Version : ""; } catch { _m.PluginVersion = "?"; }
        _m.PluginStaged = File.Exists(_store.StagedPluginPath);
        _backupsFor = null;
        if (IsHandleCreated || _render != null && !_render.Bench) StartCompatCheck();
        RefreshUi();
        ShowDetails(true);
    }

    /// Обновить окно после изменения данных. Перерисовывается только то, что изменилось: в списке —
    /// карточки с новым видом (ModList.RefreshCards), строка статуса и баннер — при новом тексте,
    /// шапка и наборы — по своим подписям.
    private void RefreshUi()
    {
        _m.Warnings = CheckSelection();
        RecheckCompat();
        // переключили другой мод, а в панели справа ничего не поменялось (отметка, статус, зависимости, совместимость) —
        // панель не перерисовывается
        bool keepDetails = _detailsSigBefore != null && _detailsSigBefore == DetailsSig();
        _detailsSigBefore = null;
        SyncFilterItems();
        ApplyView(!keepDetails);

        _chips.Profiles = _m.Cfg.Profiles;
        _chips.Active = ModStore.FindMatchingProfile(_m.Mods, _m.Sel, _m.Cfg, _m.Cfg.LastProfile)?.Name;
        string cs = _chips.Active + "|" + string.Join("|", _m.Cfg.Profiles.Select(p => p.Name));
        if (cs != _chipsSig) { _chipsSig = cs; _chips.Invalidate(); }
        UpdateBanner();
        UpdateStatus();
        UpdateFooter();
        SyncShellChrome();
        SyncDockTools();
        RefreshDock();
        if (!keepDetails) _details.Invalidate();
        string rs = RootSig();
        if (rs != _rootSig) { _rootSig = rs; _root.Invalidate(); }
        // наборы — в боковой панели: раскладка зависит только от баннера, ширины вкладок и боковой панели
        int sidePx = Flat ? SidePx() : SidePxSoft(), cw = Flat ? _root.ClientSize.Width - sidePx : SoftContentW(sidePx);
        if (_banner.Visible != _lastBanner || _filter.PreferredWidth() != _lastFilterW || _banner.Visible && BannerHeight(cw) != _banner.Height || sidePx != _side.Width) DoLayout();
    }

    // ───────── быстрые фильтры и поиск ─────────

    private int CountIn(CatalogFilter f) => CatalogView.Count(_m.Mods, _m.Available, f, _m.On, _m.HasUpdate, _m.HasProblem);

    private static string FilterName(CatalogFilter f) => f switch
    {
        CatalogFilter.Installed => "Установленные",
        CatalogFilter.Updates => "Требуют обновления",
        CatalogFilter.Disabled => "Отключенные",
        CatalogFilter.Problems => "Проблемы",
        CatalogFilter.Enabled => "Включённые",
        CatalogFilter.Catalog => "Каталог и обновления",
        _ => "Все",
    };

    /// Вкладки-фильтры со счётчиками «Все / Вкл / Выкл / Проблемы / Обновления»; «Каталог и обновления» — раздел
    /// боковой панели (тогда ни одна вкладка не выбрана).
    private void SyncFilterItems()
    {
        int inst = _m.Mods.Count, avail = _m.Available.Count, upd = CountIn(CatalogFilter.Updates),
            off = CountIn(CatalogFilter.Disabled), prob = CountIn(CatalogFilter.Problems);
        int ready = _m.Updates.Values.Count(u => u.CanInstall);
        if (_filterKind == CatalogFilter.Installed) _filterKind = CatalogFilter.All;
        var kinds = new List<CatalogFilter> { CatalogFilter.All, CatalogFilter.Enabled, CatalogFilter.Disabled, CatalogFilter.Problems, CatalogFilter.Updates };
        string updTip = upd == 0 ? "Новых версий не найдено: менеджер ищет их в ModManager\\Library, «Загрузках» и среди сохранённых копий"
            : ready > 0 ? "Есть версия новее — её можно поставить одной кнопкой" + (upd > ready ? "; остальные выглядят устаревшими по журналу игры" : "")
            : "По журналу игры похоже, что мод собран под другую версию игры или MelonLoader";
        string offTip = "Выключенные: лежат в Mods\\Disabled (или выключатся после «Применить») — игра их не загружает";
        string probTip = "Несовместимость с MelonLoader/игрой, не хватает зависимостей, ошибки в журнале игры, лишние копии";
        string allTip = avail > 0 ? $"Установленные ({inst}) и доступные для установки ({avail}) — из ModManager\\Library и «Загрузок»"
                                  : "Все установленные моды. Доступные для установки появятся, когда в ModManager\\Library или «Загрузках» найдутся файлы модов";
        _filter.Items.Clear();
        foreach (var k in kinds)
            _filter.Items.Add(k switch
            {
                CatalogFilter.Enabled => new Segmented.Item("Вкл", (inst - off).ToString(), "Включённые моды — их загрузит игра (с учётом ещё не применённых отметок)"),
                CatalogFilter.Disabled => new Segmented.Item("Выкл", off.ToString(), offTip),
                CatalogFilter.Problems => new Segmented.Item("Проблемы", prob.ToString(), probTip, Tone.Warn) { Icon = VIcon.Warning },
                CatalogFilter.Updates => new Segmented.Item("Обновления", upd.ToString(), updTip, Tone.Info) { Icon = VIcon.CircleDown },
                _ => new Segmented.Item("Все", (inst + avail).ToString(), allTip),
            });
        _filterKinds.Clear();
        _filterKinds.AddRange(kinds);
        if (!kinds.Contains(_filterKind) && _filterKind != CatalogFilter.Catalog) _filterKind = CatalogFilter.All;
        _filter.Selected = kinds.IndexOf(_filterKind);
        string fs = _filter.Selected + "|" + string.Join("|", _filter.Items.Select(i => i.Text + "#" + i.Badge + "#" + (int)i.Tone + "#" + i.Tip + "#" + i.Icon));
        if (fs != _filterSig) { _filterSig = fs; _filter.Invalidate(); }
    }

    private void SetFilter(CatalogFilter f)
    {
        if (_filterKind == f && _filter.Selected == _filterKinds.IndexOf(f)) return;
        _filterKind = f;
        RefreshUi();
    }

    /// Поиск — на каждое нажатие, но не чаще раза за проход очереди сообщений: при вставке текста,
    /// Ctrl+Backspace или зажатой клавише список перестраивается один раз, и ввод не подтормаживает.
    private void QueueSearch()
    {
        if (_render != null || !IsHandleCreated) { ApplyView(); return; }
        if (_searchQueued) return;
        _searchQueued = true;
        BeginInvoke(new Action(() => { if (_searchQueued) ApplyView(); }));
    }

    /// Видимый список по фильтру и запросу. Только память (ключи поиска закэшированы) — диск не трогается.
    private void ApplyView(bool repaintDetails = true)
    {
        _searchQueued = false;
        string text = _search.Text;
        var q = ModSearch.Parse(text);
        var view = CatalogView.Build(_m.Mods, _m.Available, _filterKind, q, _m.On, _m.HasUpdate, _m.HasProblem);
        int total = CountIn(_filterKind);
        _list.Query = q.IsEmpty ? null : q;
        _search.SetCount(q.IsEmpty ? null : view.Count + " из " + total, view.Count == 0);
        SetEmptyState(q, text.Trim(), total);
        bool newQuery = !q.IsEmpty && !string.Equals(text.Trim(), _lastQuery, StringComparison.OrdinalIgnoreCase);
        _lastQuery = text.Trim();
        string? best = view.FirstOrDefault()?.Id;   // лучшее совпадение — до сортировки по колонке
        view = SortView(view);
        _list.SetView(view);
        string? keep = _list.SelectedId;
        bool visible = keep != null && view.Any(v => v.Id.Equals(keep, StringComparison.OrdinalIgnoreCase));
        // новый запрос — выбрать лучшее совпадение; иначе оставить выбранный мод, если он виден
        if (newQuery || !visible) _list.Select(newQuery ? best : view.FirstOrDefault()?.Id);
        else _list.EnsureVisible();
        ShowDetails(false, repaintDetails);
    }

    private void SetEmptyState(SearchQuery q, string text, int total)
    {
        _list.EmptyHappy = false;
        _list.EmptyAction = "";
        _list.EmptyIcon = VIcon.None;
        _emptyAct = null;
        if (!q.IsEmpty)
        {
            _list.EmptyTitle = "Ничего не найдено";
            _list.EmptyIcon = VIcon.Search;
            int elsewhere = _filterKind == CatalogFilter.All ? 0 : CatalogView.Build(_m.Mods, _m.Available, CatalogFilter.All, q, _m.On, _m.HasUpdate, _m.HasProblem).Count;
            if (elsewhere > 0)
            {
                _list.EmptyText = $"В фильтре «{FilterName(_filterKind)}» нет модов по запросу «{text}», а среди всех есть {Theme.Count(elsewhere, "совпадение", "совпадения", "совпадений")}.";
                _list.EmptyAction = $"Показать в «Все» ({elsewhere})";
                _emptyAct = () => SetFilter(CatalogFilter.All);
            }
            else
            {
                _list.EmptyText = $"Нет модов по запросу «{text}» ни среди установленных, ни среди доступных. Ищется по названию, автору, описанию и имени файла; раскладка и «ё» не важны.";
                _list.EmptyAction = "Очистить поиск";
                _emptyAct = () => { _search.Text = ""; _search.Box.Focus(); };
            }
            return;
        }
        switch (_filterKind)
        {
            case CatalogFilter.Updates:
                _list.EmptyHappy = true;
                _list.EmptyTitle = "Все моды актуальны";
                _list.EmptyText = "Новых версий не найдено. Скачайте новую версию мода (.dll или .zip) в «Загрузки» или положите в ModManager\\Library — менеджер сам предложит обновить.";
                _list.EmptyAction = "Открыть Library";
                _emptyAct = () => Report(Shell.OpenFolder(_store.LibraryDir));
                break;
            case CatalogFilter.Disabled:
                _list.EmptyHappy = true;
                _list.EmptyTitle = "Отключенных модов нет";
                _list.EmptyText = "Все установленные моды включены и загрузятся при запуске игры.";
                break;
            case CatalogFilter.Problems:
                _list.EmptyHappy = true;
                _list.EmptyTitle = "Проблем нет";
                _list.EmptyText = "Зависимости на месте, конфликтов нет, в журнале прошлого запуска ошибок нет.";
                break;
            case CatalogFilter.Enabled when _m.Mods.Count > 0:
                _list.EmptyTitle = "Включённых модов нет";
                _list.EmptyText = "Все моды выключены — игра запустится без модов (ваниль). Отметьте нужные во вкладке «Все» или выберите набор слева.";
                _list.EmptyAction = "Показать все";
                _emptyAct = () => SetFilter(CatalogFilter.All);
                break;
            case CatalogFilter.Catalog:
                _list.EmptyHappy = true;
                _list.EmptyTitle = "Каталог пуст";
                _list.EmptyIcon = VIcon.Download;
                _list.EmptyText = "Новых версий и модов для установки не найдено. Скачайте мод (.dll или .zip) в «Загрузки» или положите в ModManager\\Library — он появится здесь.";
                _list.EmptyAction = "Открыть Library";
                _emptyAct = () => Report(Shell.OpenFolder(_store.LibraryDir));
                break;
            default:
                if (_m.Mods.Count == 0)
                {
                    _list.EmptyTitle = "В папке Mods пока нет модов";
                    _list.EmptyIcon = VIcon.Download;
                    _list.EmptyText = "Перетащите сюда .dll или .zip — менеджер сам разложит файлы: моды в Mods, плагины в Plugins, библиотеки в UserLibs.";
                    _list.EmptyAction = "Установить из файла…";
                    _emptyAct = PickInstall;
                }
                else
                {
                    _list.EmptyTitle = "Здесь пусто";
                    _list.EmptyText = "В этом фильтре сейчас нет модов.";
                }
                break;
        }
    }

    private void FocusSearch()
    {
        _search.Box.Focus();
        _search.Box.SelectAll();
    }

    /// Из поиска — к списку (Enter, ↓): выбрано лучшее совпадение, дальше Пробел/Enter.
    private void FocusList()
    {
        if (_list.View.Count > 0 && (_list.SelectedId == null || !_list.View.Any(v => v.Id.Equals(_list.SelectedId, StringComparison.OrdinalIgnoreCase))))
            _list.Select(_list.View[0].Id);
        _list.Focus();
    }

    /// Начали печатать, стоя в списке, — буквы уходят в поиск.
    private void TypeToSearch(char ch)
    {
        _search.Box.Focus();
        _search.Box.SelectionStart = _search.Box.TextLength;
        _search.Box.SelectedText = ch.ToString();
    }

    /// Выбрать мод (установленный или доступный); если он скрыт фильтром или поиском — показать всё.
    private void SelectAnywhere(string id)
    {
        if (!_list.View.Any(v => v.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            _filterKind = CatalogFilter.All;
            if (_search.Text.Length > 0) _search.Text = "";
            RefreshUi();
        }
        _list.Select(id);
    }

    private void ShowDetails(bool reloadBackups, bool repaint = true)
    {
        var m = _list.Selected;
        if (m == null) { _details.ShowMod(null, new List<BackupInfo>(), null); return; }
        if (m.Available) { _backupsFor = null; _details.ShowMod(m, new List<BackupInfo>(), null); return; }
        if (reloadBackups || !string.Equals(_backupsFor, m.Id, StringComparison.OrdinalIgnoreCase))
        {
            // копии и папка настроек — из памяти (обновляются при перечитывании папки), чтобы выбор был мгновенным
            _backups = _m.Stored.TryGetValue(m.Id, out var b) ? b : new List<BackupInfo>();
            if (!_userDataCache.TryGetValue(m.Id, out var ud)) _userDataCache[m.Id] = ud = _store.UserDataDirFor(m);
            _userData = ud;
            _backupsFor = m.Id;
        }
        _details.ShowMod(m, _backups, _userData, repaint);
    }

    /// Всё, что в панели справа зависит от отметок модов: выбранный мод, его отметка и статус, зависимости и те, кому он
    /// нужен, замечания совместимости. Toggle сравнивает до и после — если совпало, панель не перерисовывается.
    private string? _detailsSigBefore;
    private string DetailsSig()
    {
        var m = _list.Selected;
        if (m == null) return "-";
        var sb = new System.Text.StringBuilder(m.Id);
        sb.Append('|').Append(_m.On(m.Id)).Append('|').Append(_m.Status(m).Text).Append('|').Append(_m.UpdateFor(m)?.Version)
          .Append('|').Append(_m.GameRunning);
        foreach (var d in m.DependsOn) sb.Append("|d:").Append(d).Append(_m.Find(d) is { } dm && _m.On(dm.Id));
        foreach (var u in _m.UsersOf(m)) sb.Append("|u:").Append(u.Id).Append(_m.On(u.Id));
        if (_m.Compat != null) foreach (var i in _m.Compat.For(m.Id)) sb.Append("|c:").Append(i);
        return sb.ToString();
    }

    private string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(_m.NameOf));

    private Dictionary<string, bool> Diff() => MapUtil.ToMap(_m.Mods.Where(_m.IsChanged), m => m.Id, m => _m.On(m.Id));

    private string DiffText(IDictionary<string, bool> d)
    {
        var on = d.Where(kv => kv.Value).Select(kv => _m.NameOf(kv.Key)).ToList();
        var off = d.Where(kv => !kv.Value).Select(kv => _m.NameOf(kv.Key)).ToList();
        var parts = new List<string>();
        if (on.Count > 0) parts.Add("включить " + string.Join(", ", on));
        if (off.Count > 0) parts.Add("выключить " + string.Join(", ", off));
        return string.Join("; ", parts);
    }

    private Dictionary<string, bool> PendingDiff()
    {
        var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (_m.Pending == null) return d;
        foreach (var kv in _m.Pending.Desired)
        {
            var m = _m.Find(kv.Key);
            if (m != null && m.Enabled != kv.Value) d[m.Id] = kv.Value;
        }
        return d;
    }

    /// Есть ли отметки, которые не применены и не запланированы.
    private bool Dirty()
    {
        var a = Diff(); var b = PendingDiff();
        return a.Count != b.Count || a.Any(kv => !b.TryGetValue(kv.Key, out var v) || v != kv.Value);
    }

    // ───────── строка статуса, баннер, кнопки ─────────

    private void Toast(Tone tone, string msg, params (string, Action)[] actions)
    {
        _toast = msg; _toastTone = tone; _toastActions = actions;
        _toastUntil = DateTime.Now.AddSeconds(actions.Length > 0 ? 12 : 7);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (DateTime.Now < _toastUntil && _toast.Length > 0)
        {
            var acts = _toastActions.Select(a => (a.Item1, (Action)(() => { _toastUntil = default; a.Item2(); UpdateStatus(); }))).ToArray();
            _status.Set(_toastTone, _toast, null, acts);
            SetBarMessage(true);
            return;
        }
        if (_m.Warnings.Count > 0)
        {
            bool fixable = _m.Warnings.Any(w => w.Contains("выключен"));
            string msg = string.Join("   •   ", _m.Warnings);
            if (fixable) _status.Set(Tone.Warn, msg, null, ("Включить нужные", FixDeps));
            else _status.Set(Tone.Warn, msg);
            SetBarMessage(true);
            return;
        }
        var diff = Diff();
        if (diff.Count > 0)
        {
            bool planned = !Dirty();
            string tail = _m.GameRunning
                ? (planned ? " — запланировано, применится при следующем запуске игры." : " — нажмите «Запланировать», чтобы применить при следующем запуске.")
                : (planned ? " — запланировано из игры, нажмите «Применить» или «Играть»." : " — нажмите «Применить» или «Играть».");
            _status.Set(Tone.Info, "Изменения: " + DiffText(diff) + tail, Theme.GSync, ("Сбросить", ResetChanges));
            SetBarMessage(false);
            return;
        }
        int all = _m.Mods.Count, on = _m.OnCount;
        string text = all == 0
            ? "Модов нет. Перетащите .dll или .zip в окно, чтобы установить."
            : $"Включено {on} из {Theme.Count(all, "мода", "модов", "модов")}" + (_m.GameRunning ? " — так же, как в запущенной игре." : " — изменений нет.") + "  Пробел — вкл/выкл, Ctrl+Enter — играть.";
        _status.Set(Tone.Neutral, text, Theme.GInfo);
        SetBarMessage(false);
    }

    private void UpdateBanner()
    {
        var pend = PendingDiff();
        string? msg = null; Tone tone = Tone.Info; string glyph = Theme.GClock;
        var acts = new List<(string, Action)>();
        // «игра запущена» без плана — только в строке состояния; новые версии — во вкладке «Обновления» и разделе
        // «Каталог и обновления», «Обновить все» — в «Командах»
        if (_m.GameRunning && (pend.Count > 0 || _m.PluginStaged))
        {
            msg = pend.Count > 0
                ? "Игра запущена. Запланировано: " + DiffText(pend) + " — применится при следующем запуске."
                : "Игра запущена — DLL модов заняты. Изменения можно запланировать: они применятся сами при следующем запуске игры.";
            if (_m.PluginStaged) msg += " Новая версия плагина менеджера встанет после выхода из игры.";
            if (pend.Count > 0) acts.Add(("Отменить план", CancelPlan));
        }
        else if (pend.Count > 0 && !_m.GameRunning)
        {
            msg = "Запланировано" + (_m.Pending?.Source == "in-game" ? " из игры" : "") + ": " + DiffText(pend) + ". Применится при «Применить», «Играть» или следующем запуске игры.";
            acts.Add(("Отменить план", CancelPlan));
        }
        else if (TryEnvBanner(out var emsg, out var etone, out var eacts))
        {
            msg = emsg; tone = etone; glyph = etone == Tone.Error ? Theme.GError : Theme.GWarning;
            acts.AddRange(eacts);
        }
        else if (string.IsNullOrEmpty(_m.PluginVersion) && Directory.Exists(_store.GameDir))
        {
            msg = "Плагин менеджера не найден в Plugins — панель модов в игре (F5) и запланированные изменения работать не будут. Переустановите менеджер (build.ps1 -Install).";
            tone = Tone.Warn; glyph = Theme.GWarning;
        }
        _banner.Visible = msg != null;
        if (msg != null) _banner.Set(tone, msg, glyph, acts.ToArray());
    }

    private void UpdateFooter()
    {
        bool run = _m.GameRunning;
        int changes = _m.ChangeCount;
        _apply.Text = run ? "Запланировать" : "Применить";
        _apply.Glyph = run ? Theme.GClock : Theme.GSave;
        _apply.Enabled = run ? Dirty() : changes > 0;
        _apply.Tip = run ? "Записать изменения — их применит плагин при следующем запуске игры (Ctrl+S)" : "Перенести DLL без запуска игры (Ctrl+S)";
        _play.Text = run ? "Игра запущена" : "Играть";
        _play.Enabled = !run;
        _play.Tip = run ? "Сначала закройте игру" : (changes > 0 ? "Применить изменения и запустить игру (Ctrl+Enter)" : "Запустить игру (Ctrl+Enter)");
        _split.Set(run ? "Игра запущена" : "Играть", run ? "" : _m.Cfg.LaunchVia == "exe" ? "напрямую" : "Steam", !run, _play.Tip);
        if (_split.PreferredWidth() != _lastSplitW && _root.Width > 0) DoLayout();
    }

    // ───────── раскладка ─────────

    /// Раскладка окна: «Компактный» — MainForm.Compact.cs, Fluent и «Слаймовый» — MainForm.Shell.cs.
    private void DoLayout()
    {
        if (_root.Width <= 0) return;
        if (Flat) LayoutCompact(); else LayoutSoft();
    }

    /// Баннер в две строки, если текст рядом с кнопками не помещается.
    private int BannerHeight(int width)
    {
        int actW = _banner.Actions.Sum(a => Theme.Measure(a.Text, Theme.ButtonFont).Width + _root.Px(30));
        bool two = Theme.Measure(_banner.Message, Theme.Body).Width > width - _root.Px(56) - actW;
        if (Flat) return two ? _root.Px(52) : _root.Px(38);
        return two ? _root.Px(54) : _root.Px(42);
    }

    // ───────── фон окна (рисуется корнем) ─────────

    private void PaintRoot(Graphics g, RootView v)
    {
        if (Flat)
        {   // «Компактный»: корень виден только как фон верхней полосы — остальное закрывают панели
            int th = v.Px(TopBarH), one = Math.Max(1, v.Px(1));
            using (var b = new SolidBrush(Theme.HeaderBg)) g.FillRectangle(b, 0, 0, v.Width, th);
            using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, 0, th - one, v.Width, one);
            return;
        }
        PaintRootSoft(g, v);
    }

    // ───────── действия с модами ─────────

    private void Toggle(ModEntry m)
    {
        if (m.Available) { InstallAvailable(m); return; }
        string before = DetailsSig();
        bool on = !_m.On(m.Id);
        _m.Sel[m.Id] = on;
        if (on)
        {
            var added = ModStore.AddMissingDependencies(_m.Mods, _m.Sel);
            if (added.Count > 0)
            {
                var undo = added.ToList();
                Toast(Tone.Info, $"Заодно включён {Names(added)} — без него «{m.DisplayName}» не работает.",
                    ("Не нужно", () => { foreach (var id in undo) _m.Sel[id] = false; RefreshUi(); }));
            }
        }
        else
        {
            var users = DependentsOn(m).Where(u => _m.On(u.Id)).ToList();
            if (users.Count > 0)
                Toast(Tone.Warn, $"«{m.DisplayName}» нужен для: {string.Join(", ", users.Select(u => u.DisplayName))}. Без него они не заработают.",
                    ("Выключить и их", () => { foreach (var u in users) _m.Sel[u.Id] = false; RefreshUi(); }),
                    ("Оставить включённым", () => { _m.Sel[m.Id] = true; RefreshUi(); }));
        }
        _detailsSigBefore = before;
        RefreshUi();
    }

    /// Все моды, которые (прямо или через других) зависят от m.
    private List<ModEntry> DependentsOn(ModEntry m)
    {
        var res = new List<ModEntry>();
        var queue = new Queue<ModEntry>(); queue.Enqueue(m);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { m.Id };
        while (queue.Count > 0)
            foreach (var u in _m.UsersOf(queue.Dequeue()))
                if (seen.Add(u.Id)) { res.Add(u); queue.Enqueue(u); }
        return res;
    }

    private void SetAll(bool on)
    {
        foreach (var m in _m.Mods) _m.Sel[m.Id] = on;
        Toast(Tone.Info, on ? "Отмечены все моды." : "Все моды сняты — игра запустится без модов (ваниль).");
        RefreshUi();
    }

    private void FixDeps()
    {
        var added = ModStore.AddMissingDependencies(_m.Mods, _m.Sel);
        if (added.Count > 0) Toast(Tone.Ok, "Включено: " + Names(added) + ".");
        RefreshUi();
    }

    private void OnlyThis(ModEntry m)
    {
        foreach (var x in _m.Mods) _m.Sel[x.Id] = false;
        _m.Sel[m.Id] = true;
        var added = ModStore.AddMissingDependencies(_m.Mods, _m.Sel);
        Toast(Tone.Info, $"Оставлен только «{m.DisplayName}»" + (added.Count > 0 ? " и нужные ему: " + Names(added) : "") + ".");
        RefreshUi();
    }

    private void ResetChanges()
    {
        bool hadPlan = PendingDiff().Count > 0;
        if (hadPlan) _store.ClearPending();
        _m.Pending = null;
        _m.Sel = _m.CurrentOnDisk();
        Toast(Tone.Ok, hadPlan ? "Отметки сброшены, план отменён — всё как сейчас на диске." : "Отметки сброшены — всё как сейчас на диске.");
        RefreshUi();
    }

    private void CancelPlan()
    {
        _store.ClearPending();
        _m.Pending = null;
        _m.Sel = _m.CurrentOnDisk();
        Toast(Tone.Ok, "План отменён — при следующем запуске моды останутся как сейчас.");
        RefreshUi();
    }

    /// Предупреждение о зависимостях перед применением. false — пользователь передумал.
    private bool ConfirmDeps()
    {
        var warn = CheckSelection();
        if (warn.Count == 0) return true;
        int a = Dialogs.Ask(this, "Не хватает зависимостей",
            string.Join("\n", warn.Select(w => "• " + w)) + "\n\nБез них моды могут не заработать или выдать ошибки при запуске.",
            new[] { "Включить нужные", "Оставить как есть", "Отмена" }, 0, Tone.Warn, 2);
        if (a == 0) { ModStore.AddMissingDependencies(_m.Mods, _m.Sel); RefreshUi(); }
        return a == 0 || a == 1;
    }

    private void Commit(bool launch)
    {
        if (_busy) return;
        _m.GameRunning = ModStore.IsGameRunning();
        if (_m.GameRunning)
        {
            if (!launch) Plan(); else RefreshUi();
            return;
        }
        if (!ConfirmDeps()) return;
        if (launch && !ConfirmCompat()) return;
        ApplyResult r;
        _busy = true;
        try
        {
            r = _store.Apply(new Dictionary<string, bool>(_m.Sel, StringComparer.OrdinalIgnoreCase));
            if (r.Ok) _store.ClearPending();
        }
        finally { _busy = false; }
        Reload(false);
        if (!r.Ok)
        {
            Dialogs.Info(this, "Не всё получилось", r.ToString(), Tone.Error);
            return;
        }
        if (launch)
        {
            try { _store.LaunchGame(_m.Cfg.LaunchVia); }
            catch (Exception e) { Dialogs.Info(this, "Не удалось запустить игру", e.Message, Tone.Error); return; }
            _closingAfterLaunch = true;
            Close();
            return;
        }
        Toast(Tone.Ok, r.Moved > 0 ? "Готово: " + r.Summary() + "." : "Ничего менять не нужно — всё уже так.");
    }

    private void Plan()
    {
        if (!ConfirmDeps()) return;
        var diff = Diff();
        try
        {
            if (diff.Count == 0) { _store.ClearPending(); Toast(Tone.Ok, "План пуст — при следующем запуске всё останется как сейчас."); }
            else
            {
                _store.SavePending(new PendingChanges { Source = "launcher", Desired = diff });
                Toast(Tone.Ok, "Запланировано: " + DiffText(diff) + ". Применится при следующем запуске игры.");
            }
        }
        catch (Exception e) { Dialogs.Info(this, "Не удалось сохранить план", e.Message, Tone.Error); }
        _m.Pending = _store.LoadPending();
        RefreshUi();
    }

    private void InstallFiles(IEnumerable<string> files)
    {
        var list = files.Where(File.Exists).ToList();
        if (list.Count == 0) return;
        ApplyResult r;
        _busy = true;
        try { r = _store.Install(list); }
        finally { _busy = false; }
        AfterInstall(r);
    }

    /// После установки: перечитать папку, выбрать новый мод (показав его, если он скрыт фильтром), сообщить итог.
    private void AfterInstall(ApplyResult r, string? okText = null, params (string, Action)[] actions)
    {
        Reload(true); // доступные и обновления пересчитаются по уже найденному каталогу
        if (!r.Ok) StartCatalogScan(); // вдруг файл в каталоге пропал — пересмотреть
        if (r.InstalledIds.Count > 0) SelectAnywhere(r.InstalledIds[^1]);
        if (!r.Ok)
            Dialogs.Info(this, r.Installed.Count > 0 ? "Установлено не всё" : "Не удалось установить",
                (r.Installed.Count > 0 ? "Установлено: " + string.Join(", ", r.Installed) + "\n\n" : "") + string.Join("\n", r.Errors.Select(e => "• " + e)), Tone.Error);
        else if (okText != null) Toast(Tone.Ok, okText + (_m.GameRunning ? " Заработает после перезапуска игры." : ""), actions);
        else if (r.Installed.Count > 0)
            Toast(Tone.Ok, "Установлено: " + string.Join(", ", r.Installed) + (_m.GameRunning ? ". Заработает после перезапуска игры." : "."), actions);
        else Toast(Tone.Ok, "Готово: " + r.Summary() + ".");
    }

    private void PickInstall()
    {
        using var d = new OpenFileDialog
        {
            Title = "Установить мод",
            Filter = "Моды MelonLoader (*.dll; *.zip)|*.dll;*.zip|Все файлы (*.*)|*.*",
            Multiselect = true,
        };
        if (d.ShowDialog(this) == DialogResult.OK) InstallFiles(d.FileNames);
    }

    private void DeleteMod(ModEntry m)
    {
        if (m.Available) return; // файл в «Загрузках» не наш — удалять его менеджеру незачем
        var users = _m.UsersOf(m);
        string extra = users.Count > 0 ? "\n\nОн нужен для: " + string.Join(", ", users.Select(u => u.DisplayName)) + " — они перестанут работать." : "";
        if (_m.GameRunning && m.Enabled) extra += "\n\nИгра запущена — если файл занят, удалить не получится до её закрытия.";
        if (Dialogs.Ask(this, $"Удалить «{m.DisplayName}»?", $"Файл {m.FileName} уйдёт в корзину — оттуда его можно восстановить.{extra}",
                new[] { "В корзину", "Отмена" }, 0, Tone.Warn, 1, Look.DangerFill) != 0) return;
        string? err;
        _busy = true;
        try { err = Shell.Recycle(m.Path); }
        finally { _busy = false; }
        if (err != null) { Dialogs.Info(this, "Не удалось удалить", m.FileName + ": " + err, Tone.Error); return; }
        try
        {
            var r = new ApplyResult { Moved = 1 };
            r.Log.Add("в корзину: " + m.FileName);
            _store.AppendLog("delete", r);
        }
        catch { }
        Reload(true);
        Toast(Tone.Ok, $"«{m.DisplayName}» удалён в корзину." + (m.DuplicateInDisabled ? " В Mods\\Disabled осталась его старая копия." : ""));
    }

    private void RestoreBackup(ModEntry m, BackupInfo b)
    {
        string ver = string.IsNullOrEmpty(b.Version) ? b.FileName : "v" + b.Version;
        if (Dialogs.Ask(this, $"Вернуть {m.DisplayName} {ver}?",
                $"Копия от {b.Date:dd.MM.yyyy HH:mm} встанет вместо текущей версии{(string.IsNullOrEmpty(m.Version) ? "" : " v" + m.Version)}. Текущая не удалится — она появится в списке старых версий, и её можно будет вернуть обратно.",
                new[] { "Вернуть", "Отмена" }, 0, Tone.Info, 1) != 0) return;
        ApplyResult r;
        _busy = true;
        try { r = _store.RestoreBackup(m, b.Path); }
        finally { _busy = false; }
        Reload(true);
        if (!r.Ok) Dialogs.Info(this, "Не удалось вернуть версию", string.Join("\n", r.Errors), Tone.Error);
        else Toast(Tone.Ok, $"{m.DisplayName}: возвращена {ver}." + (_m.GameRunning ? " Заработает после перезапуска игры." : ""));
    }


    // ───────── каталог: ModManager\Library, «Загрузки», свои папки ─────────

    private List<CatalogSource> CatalogSources()
    {
        var list = new List<CatalogSource> { new(_store.LibraryDir, "Библиотека", 0, 1) };
        if (_render != null)
        {
            if (!string.IsNullOrEmpty(_render.CatalogDir)) list.Add(new CatalogSource(_render.CatalogDir, "Загрузки", 0, 1));
            return list;
        }
        if (_m.Cfg.ScanDownloads != false)
        {
            string? dl = Shell.DownloadsDir();
            if (dl != null) list.Add(new CatalogSource(dl, "Загрузки", 60, 1)); // только свежие: за 60 дней
        }
        foreach (var f in _m.Cfg.CatalogFolders ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            string dir = Environment.ExpandEnvironmentVariables(f.Trim());
            string label = Path.GetFileName(dir.TrimEnd('\\', '/'));
            list.Add(new CatalogSource(dir, label.Length > 0 ? label : dir, 0, 1));
        }
        return list;
    }

    /// Пересмотреть каталог в фоне (файлы читаются один раз, дальше — из кэша), результат — в окно.
    private void StartCatalogScan()
    {
        var sources = CatalogSources();
        if (_render != null)
        {   // снимок — синхронно и без фоновых потоков
            try { ApplyCatalog(CatalogScanner.Scan(sources)); } catch { }
            return;
        }
        _catalogCts?.Cancel();
        var cts = _catalogCts = new System.Threading.CancellationTokenSource();
        int gen = ++_catalogGen;
        _m.CatalogBusy = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            List<ModEntry>? found = null;
            try { found = CatalogScanner.Scan(sources, cts.Token); } catch { }
            if (cts.IsCancellationRequested) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (gen != _catalogGen) return;
                    if (found != null) ApplyCatalog(found);
                    else { _m.CatalogBusy = false; _m.CatalogAt = DateTime.Now; }
                }));
            }
            catch { /* окно уже закрыто */ }
        });
    }

    private void ApplyCatalog(List<ModEntry> found)
    {
        _m.CatalogBusy = false;
        _m.CatalogAt = DateTime.Now;
        _m.Found = found;
        _m.RecomputeCatalog();
        RefreshUi();
    }

    /// Следить за Библиотекой, «Загрузками» и своими папками: появился .dll/.zip — пересмотреть каталог.
    private void WatchCatalog()
    {
        try { Directory.CreateDirectory(_store.LibraryDir); } catch { }
        foreach (var src in CatalogSources())
        {
            try
            {
                if (!Directory.Exists(src.Dir)) continue;
                var w = new FileSystemWatcher(src.Dir)
                {
                    IncludeSubdirectories = src.Depth > 0 && src.MaxAgeDays == 0, // «Загрузки» — только верхний уровень
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    SynchronizingObject = this,
                };
                void On(object? _, FileSystemEventArgs e)
                {
                    string f = e.FullPath;
                    bool mod = f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    bool was = e is RenamedEventArgs re && (re.OldFullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || re.OldFullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    if (mod || was || Directory.Exists(f)) { _catalogDebounce.Stop(); _catalogDebounce.Start(); }
                }
                w.Created += On; w.Deleted += On; w.Renamed += (o, e) => On(o, e);
                w.EnableRaisingEvents = true;
                _catalogWatchers.Add(w);
            }
            catch { }
        }
    }

    /// Доступные моды, которые нужны item и ещё не установлены (с их собственными зависимостями).
    private void CollectDeps(ModEntry item, List<ModEntry> acc)
    {
        foreach (var d in item.DependsOn)
        {
            if (_m.Find(d) != null) continue;
            var a = _m.FindAny(d);
            if (a == null || !a.Available || acc.Contains(a) || ReferenceEquals(a, item)) continue;
            acc.Add(a);
            CollectDeps(a, acc);
        }
    }

    private static void Merge(ApplyResult into, ApplyResult r)
    {
        into.Log.AddRange(r.Log); into.Errors.AddRange(r.Errors); into.Moved += r.Moved;
        into.Installed.AddRange(r.Installed); into.InstalledIds.AddRange(r.InstalledIds);
    }

    /// Поставить доступный мод из каталога (и недостающие зависимости, если они тоже нашлись). Можно отменить.
    private void InstallAvailable(ModEntry item)
    {
        if (_busy || item == null || !item.Available) return;
        var deps = new List<ModEntry>();
        CollectDeps(item, deps);
        var all = new ApplyResult();
        _busy = true;
        try
        {
            foreach (var x in deps.AsEnumerable().Reverse().Append(item)) Merge(all, _store.InstallFromCatalog(x));
        }
        finally { _busy = false; }
        var missing = item.DependsOn.Where(d => _m.FindAny(d) == null).ToList();
        string text = $"Установлен «{item.DisplayName}»" + (string.IsNullOrEmpty(item.Version) ? "" : " v" + item.Version)
            + (deps.Count > 0 ? " и нужные ему " + string.Join(", ", deps.Select(d => d.DisplayName)) : "") + " из папки «" + item.SourceLabel + "»."
            + (missing.Count > 0 ? " Не найдены зависимости: " + string.Join(", ", missing) + "." : "");
        var ids = all.InstalledIds.ToList();
        AfterInstall(all, text, ("Отменить", () => UndoInstall(ids)));
    }

    /// Отмена свежей установки: DLL — в корзину (из «Загрузок» ничего не пропадает).
    private void UndoInstall(List<string> ids)
    {
        var errors = new List<string>();
        foreach (var id in ids)
        {
            var m = _m.Find(id);
            if (m == null) continue;
            string? err = Shell.Recycle(m.Path);
            if (err != null) errors.Add(m.FileName + ": " + err);
        }
        Reload(true);
        if (errors.Count > 0) Dialogs.Info(this, "Не удалось отменить", string.Join("\n", errors), Tone.Error);
        else Toast(Tone.Ok, "Установка отменена — DLL в корзине, мод снова в списке доступных.");
    }

    /// Обновить мод: новый файл из каталога или более новая сохранённая копия. Текущая версия уходит в _replaced.
    private void UpdateMod(ModEntry m, UpdateInfo u)
    {
        if (_busy || m == null || m.Available || u == null || !u.CanInstall) return;
        string id = m.Id, name = m.DisplayName, old = m.Version;
        if (!BackupSavesBefore("обновление " + name + (string.IsNullOrEmpty(old) ? "" : " v" + old) + " → v" + u.Version, out string saved)) return;
        ApplyResult r;
        _busy = true;
        try { r = u.Stored != null ? _store.RestoreBackup(m, u.Stored.Path) : _store.InstallFromCatalog(u.Source, m); }
        finally { _busy = false; }
        if (r.InstalledIds.Count == 0) r.InstalledIds.Add(id);
        string text = $"{name} обновлён" + (string.IsNullOrEmpty(old) ? "" : $": v{old} → v{u.Version}") + (string.IsNullOrEmpty(old) ? $" до v{u.Version}." : ".") + saved;
        var undo = string.IsNullOrEmpty(old) ? Array.Empty<(string, Action)>() : new (string, Action)[] { ("Вернуть v" + old, () => RevertTo(id, old)) };
        AfterInstall(r, text, undo);
    }

    private void RevertTo(string id, string version)
    {
        var m = _m.Find(id);
        var b = m == null ? null : _m.Stored.TryGetValue(m.Id, out var l) ? l.FirstOrDefault(x => x.Version == version) : null;
        if (m == null || b == null) { Toast(Tone.Warn, "Прошлая версия не найдена — посмотрите «Старые версии» в подробностях мода."); return; }
        RestoreBackup(m, b);
    }

    private void UpdateAll()
    {
        var ups = _m.Updates.Values.Where(u => u.CanInstall && _m.Find(u.Id) != null).OrderBy(u => _m.NameOf(u.Id)).ToList();
        if (ups.Count == 0) return;
        if (ups.Count == 1) { UpdateMod(_m.Find(ups[0].Id)!, ups[0]); return; }
        string list = string.Join("\n", ups.Select(u => $"• {_m.NameOf(u.Id)}: " + (string.IsNullOrEmpty(u.Current) ? "" : $"v{u.Current} → ") + $"v{u.Version} ({u.Where})"));
        if (Dialogs.Ask(this, "Обновить " + Theme.Count(ups.Count, "мод", "мода", "модов") + "?",
                list + "\n\nТекущие версии сохранятся в Mods\\Disabled\\_replaced — любую можно вернуть в подробностях мода (раздел «Старые версии»). Перед обновлением сейвы игры сохранятся в бэкап.",
                new[] { "Обновить все", "Отмена" }, 0, Tone.Info, 1) != 0) return;
        if (!BackupSavesBefore("обновление: " + string.Join(", ", ups.Select(u => _m.NameOf(u.Id) + " → v" + u.Version)), out string saved)) return;
        var all = new ApplyResult();
        _busy = true;
        try
        {
            foreach (var u in ups)
            {
                var m = _m.Find(u.Id);
                if (m == null) continue;
                var r = u.Stored != null ? _store.RestoreBackup(m, u.Stored.Path) : _store.InstallFromCatalog(u.Source, m);
                Merge(all, r);
                if (r.Ok && r.InstalledIds.Count == 0) all.InstalledIds.Add(m.Id);
            }
        }
        finally { _busy = false; }
        AfterInstall(all, "Обновлено: " + string.Join(", ", ups.Select(u => _m.NameOf(u.Id) + " v" + u.Version)) + "." + saved);
    }

    // ───────── профили (наборы) ─────────

    private void PickProfile(Profile p)
    {
        var sel = ModStore.SelectionForProfile(_m.Mods, p);
        var added = ModStore.AddMissingDependencies(_m.Mods, sel);
        _m.Sel = sel;
        _m.Cfg.LastProfile = p.Name;
        SaveCfg();
        int n = _m.ChangeCount;
        string t = $"Набор «{p.Name}»: " + (n == 0 ? "уже действует." : Theme.Count(n, "изменение", "изменения", "изменений") + $" — нажмите «{(_m.GameRunning ? "Запланировать" : "Применить")}» или «Играть».");
        if (added.Count > 0) t += " Добавлены нужные зависимости: " + Names(added) + ".";
        Toast(Tone.Info, t);
        RefreshUi();
    }

    private string UniqueName(string baseName)
    {
        string n = baseName;
        for (int i = 2; _m.Cfg.Profiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++) n = baseName + " " + i;
        return n;
    }

    private static string CleanName(string s) => s.Replace("\"", "'").Trim();

    private void SaveProfileAs()
    {
        string? name = Dialogs.Prompt(this, "Новый набор модов", "Название набора (сохранятся отмеченные сейчас моды):", UniqueName("Мой набор"));
        if (name == null) return;
        name = CleanName(name);
        if (name.Length == 0) return;
        var mods = _m.Mods.Where(m => _m.On(m.Id)).Select(m => m.Id).OrderBy(x => x).ToList();
        var existing = _m.Cfg.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (Dialogs.Ask(this, $"Перезаписать «{existing.Name}»?", "Набор с таким названием уже есть. Заменить его текущим выбором модов?", new[] { "Перезаписать", "Отмена" }, 0, Tone.Warn, 1) != 0) return;
            existing.All = false; existing.Mods = mods;
        }
        else _m.Cfg.Profiles.Add(new Profile { Name = name, Mods = mods });
        _m.Cfg.LastProfile = name;
        SaveCfg();
        Toast(Tone.Ok, $"Набор «{name}» сохранён: " + (mods.Count == 0 ? "без модов." : Names(mods) + "."));
        RefreshUi();
    }

    private void ProfileMenu(Profile p, Point pt)
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        Menus.Add(menu, "Выбрать этот набор", Theme.GCheck, () => PickProfile(p));
        Menus.Add(menu, "Перезаписать текущим выбором", Theme.GSave, () =>
        {
            if (Dialogs.Ask(this, $"Перезаписать «{p.Name}»?", "В набор попадут моды, отмеченные сейчас: " + (_m.OnCount == 0 ? "ни одного (ваниль)." : Names(_m.Mods.Where(m => _m.On(m.Id)).Select(m => m.Id)) + "."), new[] { "Перезаписать", "Отмена" }, 0, Tone.Warn, 1) != 0) return;
            p.All = false;
            p.Mods = _m.Mods.Where(m => _m.On(m.Id)).Select(m => m.Id).OrderBy(x => x).ToList();
            _m.Cfg.LastProfile = p.Name;
            SaveCfg(); Toast(Tone.Ok, $"Набор «{p.Name}» обновлён."); RefreshUi();
        });
        Menus.Add(menu, "Переименовать…", Theme.GRename, () =>
        {
            string? n = Dialogs.Prompt(this, "Переименовать набор", "Новое название:", p.Name);
            if (n == null) return;
            n = CleanName(n);
            if (n.Length == 0 || n == p.Name) return;
            if (_m.Cfg.Profiles.Any(x => x != p && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) { Dialogs.Info(this, "Такое название уже есть", $"Набор «{n}» уже существует — выберите другое название.", Tone.Warn); return; }
            if (_m.Cfg.LastProfile == p.Name) _m.Cfg.LastProfile = n;
            p.Name = n; SaveCfg(); RefreshUi();
        });
        Menus.Add(menu, "Дублировать", Theme.GCopy, () =>
        {
            _m.Cfg.Profiles.Insert(_m.Cfg.Profiles.IndexOf(p) + 1, new Profile { Name = UniqueName(p.Name + " (копия)"), All = p.All, Mods = new List<string>(p.Mods ?? new()) });
            SaveCfg(); RefreshUi();
        });
        Menus.Add(menu, "Экспорт набора в файл (JSON или zip с модами)…", Theme.GOpen, () => ExportPreset(p));
        Menus.Separator(menu);
        Menus.Add(menu, "Ярлык «играть с этим набором» на рабочем столе", Theme.GPin, () => ProfileShortcut(p));
        Menus.Separator(menu);
        Menus.Add(menu, "Удалить набор", Theme.GDelete, () =>
        {
            if (Dialogs.Ask(this, $"Удалить набор «{p.Name}»?", "Удаляется только набор — сами моды остаются на месте.", new[] { "Удалить", "Отмена" }, 0, Tone.Warn, 1, Look.DangerFill) != 0) return;
            _m.Cfg.Profiles.Remove(p); SaveCfg(); RefreshUi();
        }, true, Theme.CoralText);
        menu.Show(_side, pt);
    }

    private void ProfileShortcut(Profile p)
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string lnk = Path.Combine(desktop, "SR2 — " + Shell.SafeFileName(p.Name) + ".lnk");
        string? err = Shell.CreateShortcut(lnk, exe, $"--profile \"{p.Name}\" --launch", Path.GetDirectoryName(exe) ?? "", _store.GameExe + ",0", $"Slime Rancher 2 с набором модов «{p.Name}»");
        if (err != null) Dialogs.Info(this, "Ярлык не создан", err, Tone.Error);
        else Toast(Tone.Ok, $"На рабочем столе появился ярлык «{Path.GetFileNameWithoutExtension(lnk)}» — он включит набор и запустит игру.");
    }

    // ───────── меню ─────────

    private void ModMenu(ModEntry m, Point pt)
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        bool web = Uri.TryCreate(m.Link, UriKind.Absolute, out var lu) && (lu.Scheme == Uri.UriSchemeHttps || lu.Scheme == Uri.UriSchemeHttp);
        if (m.Available)
        {
            Menus.Add(menu, "Установить", Theme.GDownload, () => InstallAvailable(m), true, null, "Enter");
            Menus.Separator(menu);
            Menus.Add(menu, "Показать файл в папке", Theme.GFolder, () => Report(Shell.Reveal(m.Path)));
            if (web) Menus.Add(menu, "Страница мода", Theme.GOpen, () => Report(Shell.OpenLink(m.Link)));
            menu.Show(_list, pt);
            return;
        }
        bool on = _m.On(m.Id);
        Menus.Add(menu, on ? "Выключить" : "Включить", on ? Theme.GCancel : Theme.GCheck, () => Toggle(m), true, null, "Пробел");
        Menus.Add(menu, "Оставить только этот мод", Theme.GPin, () => OnlyThis(m));
        var upd = _m.UpdateFor(m);
        if (upd != null && upd.CanInstall) Menus.Add(menu, "Обновить до v" + upd.Version, Theme.GSync, () => UpdateMod(m, upd), true, Theme.BlueText);
        Menus.Separator(menu);
        Menus.Add(menu, "Показать файл в папке", Theme.GFolder, () => Report(Shell.Reveal(m.Path)));
        string? ud = _store.UserDataDirFor(m);
        Menus.Add(menu, "Открыть настройки мода (UserData)", Theme.GSettings, () => { if (ud != null) Report(Shell.OpenFolder(ud)); }, ud != null);
        if (web) Menus.Add(menu, "Страница мода", Theme.GOpen, () => Report(Shell.OpenLink(m.Link)));
        Menus.Separator(menu);
        Menus.Add(menu, "Удалить в корзину…", Theme.GDelete, () => DeleteMod(m), true, Theme.CoralText, "Del");
        menu.Show(_list, pt);
    }

    private void MoreMenu()
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        AddMoreItems(menu);
        _menuAnchor = null;
        ShowMenu(menu);
    }

    private void AddMoreItems(ContextMenuStrip menu)
    {
        Menus.Add(menu, "Установить мод из файла…", Theme.GDownload, PickInstall, true, null, "Ctrl+O");
        Menus.Add(menu, "Обновить список", Theme.GRefresh, () => { Reload(true); StartCatalogScan(); Toast(Tone.Ok, "Список обновлён."); }, true, null, "F5");
        Menus.Separator(menu);
        Menus.Add(menu, "Дизайн окна: «" + Theme.Spec.Name + "»…", Theme.GPalette, () => BeginInvoke(new Action(() => OpenDesignPicker())));
        Menus.Add(menu, "Следующий дизайн", Theme.GSync, NextDesign, true, null, "Ctrl+Shift+D");
        Menus.Add(menu, _dock.Collapsed ? "Развернуть нижнюю панель" : "Свернуть нижнюю панель", _dock.Collapsed ? Theme.GChevronUp : Theme.GChevronDown,
            () => SetDockCollapsed(!_dock.Collapsed), true, null, "Ctrl+J");
        Menus.Separator(menu);
        Menus.Add(menu, "Экспорт отмеченных модов как набор (JSON / zip)…", Theme.GOpen, () => ExportPreset(null));
        Menus.Add(menu, "Импорт набора из файла…", Theme.GDownload, ImportPreset);
        Menus.Add(menu, "Сохранить бэкап сейвов", Theme.GSave, BackupSavesNow);
        Menus.Add(menu, "Восстановить сейвы из бэкапа…", Theme.GHistory, () => BeginInvoke(new Action(SavesMenu)));
        Menus.Separator(menu);
        Menus.Add(menu, "Папка Mods", Theme.GFolder, () => Report(Shell.OpenFolder(_store.ModsDir)));
        Menus.Add(menu, "Библиотека модов (ModManager\\Library)", Theme.GFolder, () => Report(Shell.OpenFolder(_store.LibraryDir)));
        Menus.Add(menu, "Папка игры", Theme.GFolder, () => Report(Shell.OpenFolder(_store.GameDir)));
        Menus.Add(menu, "Настройки модов (UserData)", Theme.GFolder, () => Report(Shell.OpenFolder(_store.UserDataDir)));
        Menus.Separator(menu);
        Menus.Add(menu, "MelonPreferences.cfg — настройки и клавиши", Theme.GSettings, () => Report(Shell.Open(_store.PreferencesPath)), File.Exists(_store.PreferencesPath));
        Menus.Add(menu, "Журнал MelonLoader (Latest.log)", Theme.GDoc, () => Report(Shell.Open(_store.MelonLogPath)), File.Exists(_store.MelonLogPath));
        Menus.Add(menu, "Папка журналов MelonLoader\\Logs", Theme.GFolder, () => Report(Shell.OpenFolder(MelonLogsDir)), Directory.Exists(MelonLogsDir));
        Menus.Add(menu, "Журнал менеджера", Theme.GHistory, () => Report(Shell.Open(_store.LogPath)), File.Exists(_store.LogPath));
        Menus.Separator(menu);
        Menus.Add(menu, "О менеджере и горячие клавиши", Theme.GInfo, About);
    }

    private void About()
    {
        Dialogs.Info(this, "SR2 Mod Manager v" + AppVersion,
            "Включение и выключение = перенос DLL между Mods и Mods\\Disabled — ничего не удаляется.\n\n" +
            "Пробел / Enter — включить или выключить выбранный мод\n" +
            "↑ ↓ — выбор мода, Del — удалить в корзину\n" +
            "Ctrl+F или / — поиск (Esc — очистить, Enter или ↓ — к списку), можно просто начать печатать\n" +
            "Ctrl+K — все команды (применить, сбросить, включить или выключить все, установка, наборы, папки)\n" +
            "F5 — обновить список, Ctrl+O — установить мод из файла\n" +
            "Ctrl+S — применить, Ctrl+Enter — применить и играть\n" +
            "Перетащите .dll или .zip в окно — мод установится сам\n" +
            "Доступные моды и новые версии ищутся в ModManager\\Library и «Загрузках» (за 60 дней);\n" +
            "  свои папки — «catalogFolders» в profiles.json, «scanDownloads»: false — не смотреть «Загрузки»\n" +
            "Правая кнопка по набору — переименовать, перезаписать, ярлык\n\n" +
            "Дизайн: «Компактный», «Fluent» или «Слаймовый» — «Настройки» слева или Ctrl+K → «Дизайн окна…», Ctrl+Shift+D — следующий;\n" +
            "  выбор, вкладка и высота нижней панели хранятся в UserData\\ModManager\\ui.json\n" +
            "В таблице щелчок по заголовку колонки — сортировка (ещё щелчок — обратно, третий — без сортировки)\n" +
            "Нижняя панель: Ctrl+1 — «Проблемы», Ctrl+2 — «Хуки Harmony», Ctrl+3 — «Журнал игры», Ctrl+4 — «Изменения»,\n" +
            "  Ctrl+5 — «Детали»" +
            " (или двойной щелчок по моду), Ctrl+J — свернуть или развернуть, высота — потянуть за полоску над панелью\n\n" +
            "В игре: F5 — панель модов (" + (string.IsNullOrEmpty(_m.PluginVersion) ? "плагин не установлен" : "плагин v" + _m.PluginVersion) + ").", Tone.Neutral);
    }

    private void Report(string? error)
    {
        if (error != null) Toast(Tone.Error, "Не получилось: " + error);
    }

    // ───────── фон: состояние игры, журнал, папка Mods ─────────

    private static DateTime? GameStartTime()
    {
        try
        {
            var ps = Process.GetProcessesByName(ModStore.GameProcessName);
            DateTime? t = null;
            foreach (var p in ps) { try { t ??= p.StartTime; } catch { } p.Dispose(); }
            return t;
        }
        catch { return null; }
    }

    private void Tick()
    {
        _tick++;
        bool running = ModStore.IsGameRunning();
        if (running != _m.GameRunning)
        {
            _m.GameRunning = running;
            _m.GameStarted = running ? GameStartTime() : null;
            string? msg = null;
            if (!running && _store.TryApplyStagedPluginUpdate(out var m)) msg = m;
            Reload(true);
            if (running) Toast(Tone.Info, "Игра запущена — моды заняты до выхода. Изменения можно запланировать.");
            else Toast(Tone.Ok, "Игра закрыта — изменения можно применять." + (msg != null ? " " + msg : ""));
            return;
        }
        if (running && _tick % 4 == 0)
        {
            try
            {
                var fi = new FileInfo(_store.MelonLogPath);
                if (fi.Exists && fi.LastWriteTime != _logStamp)
                {
                    _m.Log = _store.ReadMelonLog(_m.Mods);
                    _logStamp = _m.Log?.Updated ?? fi.LastWriteTime;
                    _m.RecomputeCatalog(); // «устарел» определяется по журналу
                    RefreshUi();
                }
            }
            catch { }
        }
        // вкладка «Логи» — хвост Latest.log, пока игра пишет журнал (проверка — размер и время файла)
        if (_tick % 2 == 0 && LoadLogTail())
        {
            RefreshDock();
            if (_dock.Active == DockTab.Logs) SyncDockTools();
        }
        if (_toastUntil != default && DateTime.Now >= _toastUntil) { _toastUntil = default; UpdateStatus(); }
        if (_tick % 4 == 0 || running) SyncShellChrome();
    }

    private void OnFs(object? sender, FileSystemEventArgs e)
    {
        if (_busy) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounce()
    {
        _debounce.Stop();
        if (_busy || Application.OpenForms.Count > 1 || (_menu != null && _menu.Visible)) { _debounce.Start(); return; }
        Reload(true);
    }

    // ───────── перетаскивание ─────────

    private static string[] DropFiles(DragEventArgs e)
    {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        return files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool ok = DropFiles(e).Length > 0;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        _list.DropHint = ok;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        _list.DropHint = false;
        var files = DropFiles(e);
        if (files.Length > 0) BeginInvoke(new Action(() => InstallFiles(files)));
    }

    // ───────── клавиши, окно ─────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.F: FocusSearch(); return true;
            case Keys.F5: Reload(true); StartCatalogScan(); Toast(Tone.Ok, "Список обновлён."); return true;
            case Keys.Control | Keys.Enter: Commit(true); return true;
            case Keys.Control | Keys.S: if (_apply.Enabled) Commit(false); return true;
            case Keys.Control | Keys.O: PickInstall(); return true;
            case Keys.Control | Keys.Shift | Keys.D: NextDesign(); return true;
            case Keys.Control | Keys.J: SetDockCollapsed(!_dock.Collapsed); return true;
            case Keys.Control | Keys.D1: case Keys.Control | Keys.NumPad1: ShowDockTab(DockTab.Problems, true); return true;
            case Keys.Control | Keys.D2: case Keys.Control | Keys.NumPad2: ShowDockTab(DockTab.Hooks, true); return true;
            case Keys.Control | Keys.D3: case Keys.Control | Keys.NumPad3: ShowDockTab(DockTab.Logs, true); return true;
            case Keys.Control | Keys.D4: case Keys.Control | Keys.NumPad4: ShowDockTab(DockTab.Changes, true); return true;
            case Keys.Control | Keys.D5: case Keys.Control | Keys.NumPad5: ShowDockTab(DockTab.Details, true); return true;
            case Keys.Control | Keys.K: CommandsMenu(); return true;
            case Keys.Escape:
                // в поиске: первый Esc стирает запрос, второй — возвращает к списку; вне поиска — просто стирает
                if (_search.IsFocused) { if (_search.Text.Length > 0) _search.Text = ""; else FocusList(); return true; }
                if (_search.Text.Length > 0) { _search.Text = ""; return true; }
                break;
            case Keys.Down:
                if (_search.IsFocused) { FocusList(); return true; }
                break;
            case Keys.OemQuestion:
            case Keys.Divide:
                if (!_search.IsFocused && ActiveControl is not TextBoxBase) { FocusSearch(); return true; }
                break;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnClosingForm(object? sender, FormClosingEventArgs e)
    {
        if (!_closingAfterLaunch && e.CloseReason == CloseReason.UserClosing && _m.Mods.Count > 0 && Dirty())
        {
            bool run = ModStore.IsGameRunning();
            _m.GameRunning = run;
            int a = Dialogs.Ask(this, "Сохранить изменения?", "Есть отметки, которые ещё не " + (run ? "запланированы" : "применены") + ": " + DiffText(Diff()) + ".",
                new[] { run ? "Запланировать" : "Применить", "Не сохранять", "Отмена" }, 0, Tone.Warn, 2);
            if (a == 2 || a < 0) { e.Cancel = true; return; }
            if (a == 0)
            {
                if (run) Plan(); else Commit(false);
                if (Dirty()) { e.Cancel = true; return; }
            }
        }
        _timer.Stop();
        try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
        foreach (var w in _catalogWatchers) try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        _catalogCts?.Cancel();
        _compatGen++;
        _icons.Dispose();
        SavePlacement();
    }

    private void RestorePlacement()
    {
        var w = _m.Cfg.Window;
        if (w == null || w.W < 400 || w.H < 300) return;
        var r = new Rectangle(w.X, w.Y, w.W, w.H);
        if (!Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, r).Width > 200 && Rectangle.Intersect(s.WorkingArea, r).Height > 100)) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (w.Maximized) WindowState = FormWindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (_render != null) return;
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var c = _store.LoadConfig();
            c.Window = new WindowPlacement { X = b.X, Y = b.Y, W = b.Width, H = b.Height, Maximized = WindowState == FormWindowState.Maximized };
            c.LaunchVia = _m.Cfg.LaunchVia;
            _store.SaveConfig(c);
        }
        catch { }
    }

    private void SaveCfg()
    {
        if (_render != null) return;
        try
        {
            var disk = _store.LoadConfig();
            _m.Cfg.Window = disk.Window;
            _store.SaveConfig(_m.Cfg);
        }
        catch (Exception e) { Toast(Tone.Error, "Не удалось сохранить настройки: " + e.Message); }
    }

    // ───────── совместимость: MelonLoader, Unity, .NET, библиотеки, хуки Harmony ─────────

    /// В снимке (--render) кэш хэша GameAssembly.dll — во временной папке, чтобы ничего не писать в папку игры.
    private string? CompatCacheDir => _render != null ? Path.Combine(Path.GetTempPath(), "SR2ModManager-render") : null;

    private string CompatKeyNow() => KeyOf(_m.Sel);

    private static string KeyOf(IReadOnlyDictionary<string, bool> sel)
        => string.Join("|", sel.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    /// Предупреждения о выборе (зависимости, конфликты). Зависимость-библиотека, которая уже есть
    /// (UserLibs, MelonLoader, сборки игры), — не «нет такого мода»: так её описывает проверка совместимости.
    private List<string> CheckSelection()
    {
        var warn = ModStore.CheckSelection(_m.Mods, _m.Sel, _m.Cfg);
        if (_m.Env == null || warn.Count == 0) return warn;
        var ids = new HashSet<string>(_m.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var libs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in _m.Mods)
        {
            if (!_m.On(m.Id)) continue;
            foreach (var d in m.DependsOn)
                if (!ids.Contains(d) && _m.IsKnownLib(d)) libs.Add($"{m.DisplayName}: нужен «{d}», но такого мода нет в папке Mods.");
        }
        return libs.Count == 0 ? warn : warn.Where(w => !libs.Contains(w)).ToList();
    }

    /// Пересчитать совместимость под текущие отметки (хуки и дубликаты зависят от того, что включено).
    /// Окружение и манифесты берутся из последней проверки — без диска, несколько миллисекунд.
    private void RecheckCompat()
    {
        if (_m.Env == null || _m.Manifests == null) return;
        string key = CompatKeyNow();
        if (_m.Compat != null && key == _m.CompatKey) return;
        try
        {
            _m.Compat = CompatChecker.Check(_m.Mods, _m.Manifests, _m.On, _m.Env, _m.Log);
            _m.CompatKey = key;
        }
        catch { }
    }

    private static (LoaderEnv Env, Dictionary<string, ModManifest> Manifests, CompatReport Report) RunCompat(
        ModStore store, List<ModEntry> mods, Dictionary<string, bool> sel, MelonLogInfo? log, string? cacheDir)
    {
        var env = LoaderEnv.Detect(store, true, cacheDir);
        var mfs = CompatChecker.ReadManifests(mods);
        var rep = CompatChecker.Check(mods, mfs, id => sel.TryGetValue(id, out var v) && v, env, log);
        return (env, mfs, rep);
    }

    /// Проверка совместимости: окружение (MelonLoader, Unity, .NET, Il2CppInterop, Harmony, MelonPreferences,
    /// сборки игры) и атрибуты модов. В окне — в фоне (окно не ждёт), в снимке — сразу.
    private void StartCompatCheck()
    {
        int gen = ++_compatGen;
        var mods = _m.Mods.ToList();
        var sel = new Dictionary<string, bool>(_m.Sel, StringComparer.OrdinalIgnoreCase);
        var log = _m.Log;
        string? cache = CompatCacheDir;
        var store = _store;
        if (_render != null && !_render.Bench)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var (env, mfs, rep) = RunCompat(store, mods, sel, log, cache);
                CompatMs = sw.Elapsed.TotalMilliseconds;
                ApplyCompat(gen, env, mfs, rep, sel, false);
            }
            catch { _m.CompatBusy = false; }
            return;
        }
        _m.CompatBusy = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            LoaderEnv? env = null; Dictionary<string, ModManifest>? mfs = null; CompatReport? rep = null;
            try { (env, mfs, rep) = RunCompat(store, mods, sel, log, cache); } catch { }
            double ms = sw.Elapsed.TotalMilliseconds;
            CompatAllocBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
            try
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(() => { if (gen == _compatGen) CompatMs = ms; ApplyCompat(gen, env, mfs, rep, sel, true); }));
            }
            catch { }
        });
    }

    private void ApplyCompat(int gen, LoaderEnv? env, Dictionary<string, ModManifest>? mfs, CompatReport? rep, Dictionary<string, bool> sel, bool refresh)
    {
        if (gen != _compatGen || IsDisposed) return;   // за это время папку перечитали — придёт более свежий результат
        _m.CompatBusy = false;
        if (env != null && mfs != null && rep != null)
        {
            _m.Env = env; _m.Manifests = mfs; _m.Compat = rep;
            _m.CompatKey = KeyOf(sel);
        }
        if (refresh) RefreshUi();   // отметки могли поменяться — RecheckCompat пересчитает под текущие
    }

    /// Перед «Играть»: свежая проверка включённых модов и окружения. false — пользователь передумал.
    private bool ConfirmCompat()
    {
        CompatReport rep;
        try
        {
            var env = LoaderEnv.Detect(_store, true, CompatCacheDir);
            var mfs = CompatChecker.ReadManifests(_m.Mods);
            rep = CompatChecker.Check(_m.Mods, mfs, _m.On, env, _m.Log);
            _compatGen++;              // фоновая проверка, если ещё идёт, уже не нужна
            _m.CompatBusy = false;
            _m.Env = env; _m.Manifests = mfs; _m.Compat = rep; _m.CompatKey = CompatKeyNow();
        }
        catch { return true; }         // сама проверка не удалась — играть она не мешает
        var block = rep.Blocking(_m.Mods, _m.On);
        if (block.Count == 0) return true;

        var bad = block.Where(b => b.Mod != null).Select(b => b.Mod!).Distinct().ToList();
        var deps = bad.SelectMany(b => DependentsOn(b)).Where(d => _m.On(d.Id) && !bad.Contains(d)).Distinct().ToList();
        var lines = new List<string> { "Проверено: " + rep.Env.Summary + "." , "" };
        foreach (var (mod, i) in block.Take(4))
        {
            lines.Add("• " + (mod != null ? mod.DisplayName + ": " : "") + i.Title + ". " + i.Text);
            if (i.Fix.Length > 0) lines.Add("   Что сделать: " + i.Fix);
        }
        if (block.Count > 4) lines.Add($"…и ещё {block.Count - 4} — см. подробности модов.");
        if (bad.Count > 0)
        {
            lines.Add("");
            lines.Add("«Выключить и играть» — запустить без " + (bad.Count == 1 ? "этого мода" : "этих модов")
                + (deps.Count > 0 ? " и зависящих от них (" + string.Join(", ", deps.Select(d => d.DisplayName)) + ")" : "")
                + ": DLL уйдут в Mods\\Disabled, вернуть можно в любой момент.");
        }
        string title = bad.Count == 0 ? "Моды не загрузятся" : bad.Count == 1 ? $"«{bad[0].DisplayName}» не загрузится" : $"{Theme.Count(bad.Count, "мод", "мода", "модов")} не загрузятся";
        string[] buttons = bad.Count > 0 ? new[] { "Выключить и играть", "Играть как есть", "Отмена" } : new[] { "Играть как есть", "Отмена" };
        int a = Dialogs.Ask(this, title, string.Join("\n", lines), buttons, 0, Tone.Error, buttons.Length - 1);
        if (a < 0 || a == buttons.Length - 1) return false;
        if (bad.Count > 0 && a == 0)
        {
            foreach (var m in bad.Concat(deps)) _m.Sel[m.Id] = false;
            RefreshUi();
        }
        return true;
    }

    /// Баннер о проблеме окружения (MelonLoader, .NET, Il2CppInterop, сборки игры, MelonPreferences.cfg).
    private bool TryEnvBanner(out string msg, out Tone tone, out List<(string, Action)> acts)
    {
        msg = ""; tone = Tone.Warn; acts = new List<(string, Action)>();
        var i = _m.Env?.Issues.Where(x => x.Level >= CompatLevel.Warning).OrderByDescending(x => x.Level).FirstOrDefault();
        if (i == null) return false;
        string key = i.Code + "|" + i.Title;
        if (_envHidden == key) return false;
        tone = i.Level == CompatLevel.Error ? Tone.Error : Tone.Warn;
        msg = i.Title + ": " + i.Text + (i.Fix.Length > 0 ? " " + i.Fix : "");
        if (i.Code.StartsWith("prefs", StringComparison.Ordinal) && File.Exists(_store.PreferencesPath))
            acts.Add(("Открыть файл", () => Report(Shell.Open(_store.PreferencesPath))));
        else if (i.Code.StartsWith("game-asm", StringComparison.Ordinal) || i.Code.StartsWith("loader", StringComparison.Ordinal))
            acts.Add(("Логи", OpenLogs));
        acts.Add(("Скрыть", () => { _envHidden = key; RefreshUi(); }));
        return true;
    }

    /// Подпись того, что рисует корень (у Fluent — заголовок страницы): перерисовывать его, только когда она изменилась.
    private string RootSig() => Flat ? "flat|" + Theme.Generation : "soft|" + Theme.Generation + "|" + PageTitle() + "|" + PageSub();

    // ───────── журналы MelonLoader ─────────

    private string MelonLogsDir => Path.Combine(_store.GameDir, "MelonLoader", "Logs");

    private void OpenLogs()
    {
        if (File.Exists(_store.MelonLogPath)) Report(Shell.Open(_store.MelonLogPath));
        else if (Directory.Exists(MelonLogsDir)) Report(Shell.OpenFolder(MelonLogsDir));
        else Toast(Tone.Warn, "Журналов MelonLoader пока нет — они появятся после первого запуска игры с MelonLoader.");
    }

    private void LogsMenu()
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        bool latest = File.Exists(_store.MelonLogPath);
        Menus.Add(menu, "Журнал последнего запуска (Latest.log)", Theme.GDoc, () => Report(Shell.Open(_store.MelonLogPath)), latest);
        Menus.Add(menu, "Показать Latest.log в папке", Theme.GFolder, () => Report(Shell.Reveal(_store.MelonLogPath)), latest);
        Menus.Add(menu, "Папка журналов MelonLoader\\Logs", Theme.GFolder, () => Report(Shell.OpenFolder(MelonLogsDir)), Directory.Exists(MelonLogsDir));
        try
        {
            if (Directory.Exists(MelonLogsDir))
            {
                var logs = new DirectoryInfo(MelonLogsDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).Take(5).ToList();
                if (logs.Count > 0) Menus.Separator(menu);
                foreach (var f in logs)
                {
                    string p = f.FullName;
                    Menus.Add(menu, f.LastWriteTime.ToString("dd.MM.yyyy HH:mm") + " · " + f.Name, Theme.GHistory, () => Report(Shell.Open(p)));
                }
            }
        }
        catch { }
        Menus.Separator(menu);
        Menus.Add(menu, "MelonPreferences.cfg — настройки модов", Theme.GSettings, () => Report(Shell.Open(_store.PreferencesPath)), File.Exists(_store.PreferencesPath));
        Menus.Add(menu, "Журнал менеджера", Theme.GHistory, () => Report(Shell.Open(_store.LogPath)), File.Exists(_store.LogPath));
        ShowMenu(menu);
    }

    // ───────── бэкапы сейвов ─────────

    private string SaveBackupDir => SaveBackups.DefaultDir(_store);

    /// Папка сохранений игры (LocalLow\MonomiPark\SlimeRancher2); null — сейвов на этом ПК нет.
    private string? SaveRoot()
    {
        if (_saveRoot == null || !Directory.Exists(_saveRoot)) _saveRoot = SaveBackups.FindSaveRoot();
        return _saveRoot;
    }

    private string GameVersionText() => _m.Env?.ShortGameVersion is { Length: > 0 } v ? v : _m.Log?.GameVersion ?? "";

    /// Моды, с которыми игрались эти сейвы (включённые на диске).
    private List<string> EnabledModsText() => _m.Mods.Where(m => m.Enabled)
        .Select(m => m.DisplayName + (string.IsNullOrEmpty(m.Version) ? "" : " " + m.Version)).ToList();

    private void Busy(string text, string glyph)
    {
        _status.Set(Tone.Info, text, glyph);
        _status.Update();
        SetBarMessage(true); _bar.Invalidate(); _bar.Update();
    }

    /// Бэкап сейвов перед изменением модов (обновление, импорт набора). Если сейвы не менялись с прошлого
    /// бэкапа — новый не нужен. false — бэкап не получился и пользователь решил не продолжать.
    private bool BackupSavesBefore(string note, out string saved, string reason = "before-update")
    {
        saved = "";
        string? root = SaveRoot();
        if (root == null) return true;   // сейвов нет — беречь нечего
        try
        {
            if (SaveBackups.Collect(root).Count == 0) return true;
            var same = SaveBackups.UpToDate(root, SaveBackupDir);
            if (same != null) { saved = $" Сейвы не менялись с бэкапа {same.Created:dd.MM HH:mm} — он на месте."; return true; }
            Busy("Сохраняю бэкап сейвов…", Theme.GSave);
            var b = SaveBackups.Create(root, SaveBackupDir, reason, note, GameVersionText(), EnabledModsText());
            saved = $" Сейвы сохранены в бэкап ({Theme.Count(b.Files, "файл", "файла", "файлов")}, {Theme.FileSize(b.ZipSize)}).";
            UpdateStatus();
            return true;
        }
        catch (Exception e)
        {
            UpdateStatus();
            return Dialogs.Ask(this, "Бэкап сейвов не сохранён", "Перед изменением модов не получилось сохранить сейвы: " + e.Message
                + "\n\nПродолжить без бэкапа? Сейвы останутся как есть, но откатиться к ним будет не из чего.",
                new[] { "Продолжить без бэкапа", "Отмена" }, 1, Tone.Warn, 1) == 0;
        }
    }

    /// «Бэкап сейвов» в одно нажатие: zip с описью и SHA-256 каждого файла, сразу проверяется.
    private void BackupSavesNow()
    {
        string? root = SaveRoot();
        if (root == null) { Toast(Tone.Warn, "Сейвы Slime Rancher 2 не найдены (" + SaveBackups.CandidateRoots()[0] + ") — сохранять нечего."); return; }
        try
        {
            var same = SaveBackups.UpToDate(root, SaveBackupDir);
            if (same != null && !same.Auto)
            {
                Toast(Tone.Ok, $"Сейвы не менялись с бэкапа {same.Created:dd.MM.yyyy HH:mm} — новый не нужен ({same.FileName}).",
                    ("Показать", () => Report(Shell.Reveal(same.Path))), ("Восстановить…", () => SavesMenu()));
                return;
            }
            Busy("Сохраняю бэкап сейвов…", Theme.GSave);
            var b = SaveBackups.Create(root, SaveBackupDir, "manual", "", GameVersionText(), EnabledModsText());
            var chk = SaveBackups.Verify(b.Path);
            if (!chk.Ok)
            {
                Toast(Tone.Error, "Бэкап записан, но проверка не прошла: " + chk.Summary + ". Попробуйте ещё раз.", ("Показать", () => Report(Shell.Reveal(b.Path))));
                return;
            }
            Toast(Tone.Ok, $"Бэкап сейвов сохранён и проверен: {Theme.Count(b.Files, "файл", "файла", "файлов")}, {Theme.FileSize(b.ZipSize)}"
                + (_m.GameRunning ? " (игра запущена — в бэкап попало то, что она уже записала на диск)." : "."),
                ("Показать", () => Report(Shell.Reveal(b.Path))), ("Восстановить…", () => SavesMenu()));
        }
        catch (Exception e) { Toast(Tone.Error, "Бэкап сейвов не сохранён: " + e.Message); }
    }

    private void SavesMenu()
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        menu.ShowItemToolTips = true;
        string? root = SaveRoot();
        List<SaveBackupInfo> list;
        try { list = SaveBackups.List(SaveBackupDir); } catch { list = new List<SaveBackupInfo>(); }
        Menus.Add(menu, "Сохранить бэкап сейвов сейчас", Theme.GSave, BackupSavesNow, root != null);
        Menus.Separator(menu);
        if (list.Count == 0) Menus.Add(menu, "Бэкапов пока нет — восстанавливать не из чего", Theme.GInfo, () => { }, false);
        else Menus.Add(menu, "Восстановить сейвы из бэкапа:", Theme.GHistory, () => { }, false);
        foreach (var b in list.Take(10))
        {
            var bb = b;
            string text = b.Readable ? $"{b.Created:dd.MM.yyyy HH:mm} · {b.ReasonText} · {Theme.FileSize(b.ZipSize)}" : b.FileName + " · не читается";
            var item = Menus.Add(menu, "   " + text, Theme.GHistory, () => RestoreSaves(bb), b.Readable && root != null);
            item.ToolTipText = b.Readable
                ? (b.Note.Length > 0 ? b.Note + "\n" : "") + Theme.Count(b.Files, "файл", "файла", "файлов") + ", " + Theme.FileSize(b.Bytes)
                  + (b.Game.Length > 0 ? " · игра " + b.Game : "") + (b.Manifest?.Mods.Count > 0 ? "\nМоды: " + string.Join(", ", b.Manifest!.Mods) : "") + "\n" + b.FileName
                : b.Error;
        }
        if (list.Count > 10) Menus.Add(menu, $"…и ещё {list.Count - 10} — в папке бэкапов", Theme.GFolder, () => Report(Shell.OpenFolder(SaveBackupDir)));
        Menus.Separator(menu);
        Menus.Add(menu, "Проверить целостность бэкапов (SHA-256)", Theme.GCheck, VerifySaveBackups, list.Count > 0);
        Menus.Add(menu, "Папка бэкапов сейвов", Theme.GFolder, () => { try { Directory.CreateDirectory(SaveBackupDir); } catch { } Report(Shell.OpenFolder(SaveBackupDir)); });
        Menus.Add(menu, "Папка сейвов игры", Theme.GFolder, () => Report(Shell.OpenFolder(root!)), root != null);
        ShowMenu(menu);
    }

    private void VerifySaveBackups()
    {
        List<SaveBackupInfo> list;
        try { list = SaveBackups.List(SaveBackupDir); } catch (Exception e) { Report(e.Message); return; }
        if (list.Count == 0) { Toast(Tone.Info, "Бэкапов сейвов пока нет."); return; }
        Busy($"Проверяю {Theme.Count(list.Count, "бэкап", "бэкапа", "бэкапов")}…", Theme.GSync);
        var sw = Stopwatch.StartNew();
        var bad = new List<string>();
        foreach (var b in list)
        {
            var c = SaveBackups.Verify(b.Path);
            if (!c.Ok) bad.Add(b.FileName + ": " + c.Summary);
        }
        if (bad.Count == 0)
            Toast(Tone.Ok, $"Все бэкапы целые: {list.Count} шт., размеры и SHA-256 каждого файла совпали с описью ({sw.ElapsedMilliseconds} мс).",
                ("Папка", () => Report(Shell.OpenFolder(SaveBackupDir))));
        else
        {
            UpdateStatus();
            Dialogs.Info(this, "Есть повреждённые бэкапы", $"Целых: {list.Count - bad.Count} из {list.Count}. Повреждённые восстанавливать нельзя — менеджер их не предложит:\n\n"
                + string.Join("\n", bad.Take(6).Select(x => "• " + x)) + (bad.Count > 6 ? $"\n…и ещё {bad.Count - 6}" : ""), Tone.Warn);
        }
    }

    /// Восстановление: только из целого бэкапа и только при закрытой игре; текущие сейвы сначала уходят
    /// в страховочный бэкап, файлы распаковываются рядом и сверяются по SHA-256, при сбое — откат.
    private void RestoreSaves(SaveBackupInfo b)
    {
        string? root = SaveRoot();
        if (root == null) { Toast(Tone.Warn, "Папка сейвов игры не найдена."); return; }
        if (ModStore.IsGameRunning())
        {
            Dialogs.Info(this, "Сначала закройте игру", "Пока Slime Rancher 2 запущен, он держит сейвы в памяти и перезапишет восстановленные файлы. Закройте игру и повторите.", Tone.Warn);
            return;
        }
        Busy("Проверяю бэкап…", Theme.GSync);
        var chk = SaveBackups.Verify(b.Path);
        UpdateStatus();
        if (!chk.Ok)
        {
            Dialogs.Info(this, "Бэкап повреждён", "Восстанавливать из него нельзя — текущие сейвы не тронуты.\n\n" + chk.Summary, Tone.Error);
            return;
        }
        string text = $"Сейвы из бэкапа от {b.Created:dd.MM.yyyy HH:mm} ({b.ReasonText}{(b.Note.Length > 0 ? ": " + b.Note : "")}) заменят текущие в папке\n{root}\n\n"
            + $"Бэкап {chk.Summary}.\n"
            + "Текущие сейвы сначала сохранятся в отдельный бэкап «перед восстановлением» — вернуть их можно из этого же меню.\n\n"
            + "Steam Cloud: если при запуске Steam спросит о конфликте сохранений, выберите файлы на этом компьютере.";
        if (Dialogs.Ask(this, "Восстановить сейвы?", text, new[] { "Восстановить", "Отмена" }, 0, Tone.Warn, 1) != 0) return;
        Busy("Восстанавливаю сейвы…", Theme.GHistory);
        RestoreResult r;
        try { r = SaveBackups.Restore(b.Path, root, SaveBackupDir, ModStore.IsGameRunning, GameVersionText(), EnabledModsText()); }
        catch (Exception e) { r = new RestoreResult { Error = e.Message }; }
        if (r.Ok)
            Toast(Tone.Ok, $"Сейвы восстановлены из бэкапа от {b.Created:dd.MM HH:mm}: {Theme.Count(r.Files, "файл", "файла", "файлов")}, сверены по SHA-256."
                + (r.SafetyBackup.Length > 0 ? " Прежние — в бэкапе «перед восстановлением»." : ""),
                ("Папка сейвов", () => Report(Shell.OpenFolder(root))));
        else
        {
            UpdateStatus();
            Dialogs.Info(this, "Сейвы не восстановлены", r.Error + (r.RolledBack ? "\n\nВсё вернулось как было — текущие сейвы на месте." : "")
                + (r.SafetyBackup.Length > 0 ? "\n\nСтраховочный бэкап: " + Path.GetFileName(r.SafetyBackup) : ""), Tone.Error);
        }
    }

    // ───────── наборы модов в файле (пресеты) ─────────

    /// Экспорт набора: .zip — вместе с DLL модов (можно поставить на другом ПК), .json — только список с версиями и SHA-256.
    private void ExportPreset(Profile? p)
    {
        var sel = p != null ? ModStore.SelectionForProfile(_m.Mods, p) : _m.Sel;
        var on = _m.Mods.Where(m => sel.TryGetValue(m.Id, out var v) && v).ToList();
        string name = p?.Name ?? _chips.Active ?? "Мой набор";
        string? dir = Shell.DownloadsDir();
        using var d = new SaveFileDialog
        {
            Title = "Экспорт набора модов",
            Filter = "Zip с DLL модов — для другого ПК (*.zip)|*.zip|Только список модов (*.json)|*.json",
            FileName = Shell.SafeFileName("SR2 набор — " + name),
            DefaultExt = "zip",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dir != null && Directory.Exists(dir)) d.InitialDirectory = dir;
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string path = d.FileName;
        bool json = d.FilterIndex == 2;
        if (json && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = Path.ChangeExtension(path, ".json");
        if (!json && !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) path = Path.ChangeExtension(path, ".zip");
        try
        {
            var preset = Presets.Build(name, on, GameVersionText(), _m.Env?.LoaderText is { Length: > 0 } lt ? lt : _m.Log?.LoaderVersion ?? "");
            Presets.Save(preset, path, json ? null : on);
            int bundled = preset.Mods.Count(x => x.Bundled);
            string what = on.Count == 0 ? "без модов (ваниль)" : json ? Theme.Count(on.Count, "мод", "мода", "модов") + " списком" : Theme.Count(on.Count, "мод", "мода", "модов") + (bundled == on.Count ? " вместе с DLL" : $", DLL вложены у {bundled}");
            Toast(Tone.Ok, $"Набор «{name}» сохранён: {what}, {Theme.FileSize(new FileInfo(path).Length)} — {Path.GetFileName(path)}.", ("Показать файл", () => Report(Shell.Reveal(path))));
        }
        catch (Exception e) { Dialogs.Info(this, "Набор не сохранён", e.Message, Tone.Error); }
    }

    /// Импорт набора: показать, что изменится, поставить недостающие моды (из архива набора или из каталога),
    /// отметить моды как в наборе и добавить его в список наборов. Применяется как обычно — «Применить»/«Играть».
    private void ImportPreset()
    {
        if (_busy) return;
        using var d = new OpenFileDialog { Title = "Импорт набора модов", Filter = "Наборы SR2 Mod Manager (*.zip; *.json)|*.zip;*.json|Все файлы (*.*)|*.*" };
        string? dir = Shell.DownloadsDir();
        if (dir != null && Directory.Exists(dir)) d.InitialDirectory = dir;
        if (d.ShowDialog(this) != DialogResult.OK) return;
        PresetLoad l;
        try { l = Presets.Load(d.FileName); }
        catch (Exception e) { Dialogs.Info(this, "Это не набор модов", e.Message, Tone.Error); return; }

        var plan = Presets.Plan(l, _m.Mods);
        var fromCatalog = plan.Missing.Select(pm => (Want: pm, Item: _m.Available.FirstOrDefault(a => a.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Item != null).Select(x => (x.Want, Item: x.Item!)).ToList();
        var missing = plan.Missing.Where(pm => !fromCatalog.Any(x => x.Want == pm)).ToList();
        string name = CleanName(l.Preset.Name.Length > 0 ? l.Preset.Name : Path.GetFileNameWithoutExtension(d.FileName));
        if (name.Length == 0) name = "Импорт";
        static string V(string v) => v.Length > 0 ? " v" + v : "";

        var lines = new List<string>
        {
            $"«{name}»: {(l.Preset.Mods.Count == 0 ? "без модов (ваниль)" : Theme.Count(l.Preset.Mods.Count, "мод", "мода", "модов"))}"
            + (l.Preset.Game.Length > 0 ? ", игра " + l.Preset.Game : "") + (l.Preset.Loader.Length > 0 ? ", MelonLoader " + l.Preset.Loader : "") + ".",
        };
        if (plan.Enable.Count > 0) lines.Add("• Включить: " + string.Join(", ", plan.Enable.Select(m => m.DisplayName)));
        if (plan.Disable.Count > 0) lines.Add("• Выключить: " + string.Join(", ", plan.Disable.Select(m => m.DisplayName)));
        if (plan.Install.Count > 0) lines.Add("• Установить из файла набора: " + string.Join(", ", plan.Install.Select(pm => pm.Name + V(pm.Version))));
        if (fromCatalog.Count > 0) lines.Add("• Установить из каталога: " + string.Join(", ", fromCatalog.Select(x => x.Item.DisplayName + V(x.Item.Version ?? "") + " («" + x.Item.SourceLabel + "»)")));
        if (plan.OtherVersion.Count > 0) lines.Add("• Другая версия: " + string.Join(", ", plan.OtherVersion.Select(x => $"{x.Have.DisplayName}{V(x.Have.Version ?? "")} (в наборе{V(x.Want.Version)})")) + " — останется ваша");
        if (missing.Count > 0) lines.Add("• Не найдены, скачайте отдельно: " + string.Join(", ", missing.Select(pm => pm.Name + V(pm.Version))));
        if (l.Problems.Count > 0) lines.Add("• Архив: " + string.Join("; ", l.Problems.Take(3)));
        int installs = plan.Install.Count + fromCatalog.Count;
        lines.Add("");
        lines.Add(installs > 0 ? "Перед установкой сейвы сохранятся в бэкап. Отметки поменяются как в наборе — применятся по «Применить» или «Играть»."
            : plan.Enable.Count + plan.Disable.Count > 0 ? "Отметки поменяются как в наборе — применятся по «Применить» или «Играть»."
            : "Все моды набора уже стоят и включены так же — набор просто добавится в список.");
        if (Dialogs.Ask(this, "Импортировать набор?", string.Join("\n", lines), new[] { "Импортировать", "Отмена" }, 0,
                missing.Count > 0 || l.Problems.Count > 0 ? Tone.Warn : Tone.Info, 1) != 0) return;

        var all = new ApplyResult();
        string saved = "";
        if (installs > 0)
        {
            if (!BackupSavesBefore("импорт набора «" + name + "»", out saved, "before-preset")) return;
            string tmp = Path.Combine(Path.GetTempPath(), "sr2mm-preset-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _busy = true;
            try
            {
                foreach (var (_, item) in fromCatalog) Merge(all, _store.InstallFromCatalog(item));
                if (plan.Install.Count > 0)
                {
                    var files = Presets.ExtractBundled(l.Path, plan.Install, tmp);
                    if (files.Count > 0) Merge(all, _store.Install(files));
                }
            }
            catch (Exception e) { all.Errors.Add(e.Message); }
            finally
            {
                _busy = false;
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            Reload(true);
        }

        var want = new HashSet<string>(l.Preset.Mods.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var m in _m.Mods) _m.Sel[m.Id] = want.Contains(m.Id);
        var added = ModStore.AddMissingDependencies(_m.Mods, _m.Sel);
        var ids = want.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var same = _m.Cfg.Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        string pname = name;
        if (same == null || same.All || !new HashSet<string>(same.Mods ?? new(), StringComparer.OrdinalIgnoreCase).SetEquals(ids))
        {
            if (same != null) pname = UniqueName(name);
            _m.Cfg.Profiles.Add(new Profile { Name = pname, Mods = ids });
        }
        _m.Cfg.LastProfile = pname;
        SaveCfg();
        RefreshUi();
        if (all.InstalledIds.Count > 0) SelectAnywhere(all.InstalledIds[^1]);
        int n = _m.ChangeCount;
        string t = $"Набор «{pname}» импортирован" + (all.Installed.Count > 0 ? ", установлено: " + string.Join(", ", all.Installed) : "") + ". "
            + (n == 0 ? "Всё уже как в наборе." : Theme.Count(n, "изменение", "изменения", "изменений") + $" — нажмите «{(_m.GameRunning ? "Запланировать" : "Применить")}» или «Играть».")
            + (added.Count > 0 ? " Добавлены нужные зависимости: " + Names(added) + "." : "")
            + (missing.Count > 0 ? " Не хватает: " + string.Join(", ", missing.Select(pm => pm.Name)) + "." : "") + saved;
        if (!all.Ok)
            Dialogs.Info(this, "Набор импортирован не полностью", "Не установлены:\n" + string.Join("\n", all.Errors.Select(e => "• " + e)), Tone.Error);
        Toast(missing.Count > 0 || !all.Ok ? Tone.Warn : Tone.Ok, t);
    }


    // ───────── снимок окна для проверки (--render) ─────────

    public void RenderTo(string png)
    {
        DoLayout();
        _root.PerformLayout();
        Application.DoEvents();
        using var bmp = new Bitmap(_root.Width, _root.Height, PixelFormat.Format24bppRgb);
        _root.DrawToBitmap(bmp, new Rectangle(0, 0, _root.Width, _root.Height));
        // DrawToBitmap рисует дочерние элементы в обратном порядке слоёв: то, что лежит поверх соседа
        // (поиск на боковой панели Fluent, всплывающая панель дизайна), дорисовать сверху — снизу вверх
        var kids = _root.Controls.Cast<Control>().Where(c => c.Visible).ToList();
        for (int i = kids.Count - 1; i >= 0; i--)
            if (kids.Skip(i + 1).Any(d => d.Bounds.IntersectsWith(kids[i].Bounds)))
                kids[i].DrawToBitmap(bmp, kids[i].Bounds);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png)) ?? ".");
        bmp.Save(png, ImageFormat.Png);
    }
}


