// MainForm.Dock.cs — дизайн окна и нижняя панель: выбор стиля (UserData\ModManager\ui.json, рядом с profiles.json),
// вкладки «Проблемы», «Хуки Harmony», «Логи» и «Детали», высота и сворачивание панели, сортировка таблицы по колонкам.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed partial class MainForm
{
    private readonly PillButton _designBtn = new("Дизайн", Look.Quiet, Theme.GPalette);
    private readonly DockGrip _grip = new();
    private readonly DockPanel _dock = new();
    private readonly RowsView _problems = new() { TitleW = 200 };
    private readonly RowsView _hooks = new();
    private readonly RowsView _logs = new() { Mono = true, StickToEnd = true, LeadW = 64 };
    private UiPrefs _ui = new();
    private string _uiPath = "";
    private int _dockDragFrom;
    private string _dockBadgeSig = "", _problemsSig = "", _hooksSig = "", _logsSig = "";

    // хвост MelonLoader\Latest.log для вкладки «Логи»
    private List<LogLine> _logLines = new();
    private (long Len, DateTime Stamp) _logTailKey = (-2, default);
    private int _logErrors, _logWarnings;
    private bool _logCut;

    // ───────── настройки вида (ui.json) ─────────

    /// В обычном запуске — из ui.json; в снимке и замерах — по умолчанию (с ключами --design, --dock, --sort), чтобы
    /// результат не зависел от того, что выбрано на этом ПК.
    private void LoadUi(ModStore store, RenderOptions? render)
    {
        _uiPath = UiPrefs.PathIn(store.ConfigDir);
        _ui = render != null ? new UiPrefs() : UiPrefs.Load(_uiPath);
        if (render == null) return;
        if (render.Design is DesignKind d) _ui.DesignKind = d;
        if (render.Dock is DockTab t) _ui.DockTab = t;
        _ui.DockCollapsed = render.DockCollapsed;
        if (!string.IsNullOrWhiteSpace(render.Sort))
        {
            // --sort колонка[:asc|:desc], например «compat:desc»
            var parts = render.Sort.Split(':');
            var col = UiPrefs.ParseColumn(parts[0]);
            string dir = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "";
            _ui.SortColumn = col;
            _ui.SortDesc = col != ModColumn.None && (dir == "desc" || dir != "asc" && ModSort.DefaultDesc(col));
        }
    }

    private void SaveUi()
    {
        if (_render != null) return;
        try { _ui.Save(_uiPath); }
        catch (Exception e) { Toast(Tone.Error, "Не удалось сохранить настройки вида (ui.json): " + e.Message); }
    }

    // ───────── дизайн ─────────

    /// Включить стиль: палитра, вид списка, размеры шапки и скругления — сразу, без перезапуска.
    private void ApplyDesign(DesignKind kind, bool save)
    {
        var before = Theme.P;
        if (Theme.Use(kind))
        {
            SuspendLayout();
            Theme.Retint(this, before, Theme.P);
            _list.ApplyDesign();
            _rootSig = ""; _chipsSig = ""; _filterSig = ""; _dockBadgeSig = "";
            _rootLayoutKey = default;
            ApplyView(true);
            ResumeLayout(false);
            DoLayout();
            _dock.Relayout();
            Invalidate(true);
            ApplyTitleBar();
        }
        if (save && _ui.DesignKind != kind)
        {
            _ui.DesignKind = kind;
            SaveUi();
        }
    }

    /// Снимок: окно уже показано в одном стиле — переключить на другой тем же путём, что и кнопка «Дизайн».
    public void SwitchDesignForRender()
    {
        if (_render?.SwitchTo is DesignKind k) ApplyDesign(k, false);
    }

    private void NextDesign()
    {
        var k = Designs.Next(Theme.Design);
        ApplyDesign(k, true);
        Toast(Tone.Ok, "Дизайн: «" + Designs.Get(k).Name + "». Ctrl+Shift+D — следующий, кнопка «Дизайн» — все варианты.");
    }

    private void OpenDesignPicker(Control? anchor = null)
    {
        anchor ??= _designBtn.Visible ? _designBtn : _more;
        DesignPicker.Show(anchor, k => ApplyDesign(k, true));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// Тёмный заголовок окна у тёмного стиля (Windows 10 1809+ / 11).
    private void ApplyTitleBar()
    {
        if (!IsHandleCreated || _render != null) return;
        try
        {
            int v = Theme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0) DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
        }
        catch { }
    }

    // ───────── сортировка таблицы ─────────

    private bool Sortable => Theme.Spec.Mode == ListMode.Table;

    private ModManifest? ManifestOf(ModEntry m)
    {
        if (_m.Manifests != null && _m.Manifests.TryGetValue(m.Id, out var mf)) return mf;
        return _m.Compat?.Manifest(m.Id);
    }

    private ModSortKeys SortKeys() => new()
    {
        IsOn = m => _m.On(m.Id),
        Compat = m =>
        {
            if (m.Available) return 0;
            var p = _m.Problems(m);
            if (p.Any(x => x.Tone == Tone.Error)) return 3;
            if (p.Count > 0) return 2;
            return _m.CompatWorst(m) == CompatLevel.Info ? 1 : 0;
        },
        Hooks = m =>
        {
            var mf = m.Available ? null : ManifestOf(m);
            if (mf == null || mf.Hooks.Count == 0 && !mf.ManualPatching) return -1;
            return mf.Hooks.Where(h => !h.Dynamic).Select(h => h.Key).Distinct().Count() + (mf.Hooks.Any(h => h.Dynamic) || mf.ManualPatching ? 1 : 0);
        },
        Update = m => _m.UpdateFor(m) is { } u ? (u.CanInstall ? 2 : 1) : 0,
    };

    /// Порядок строк: в таблице — по выбранной колонке (установленные и доступные — каждые в своей группе).
    private List<ModEntry> SortView(List<ModEntry> view)
    {
        var col = Sortable ? _ui.SortColumn : ModColumn.None;
        _list.SetSort(col, _ui.SortDesc);
        return col == ModColumn.None ? view : ModSort.Apply(view, col, _ui.SortDesc, SortKeys());
    }

    /// Щелчок по заголовку колонки: по возрастанию → по убыванию → без сортировки.
    private void OnSortRequested(ModColumn col)
    {
        var (c, d) = ModSort.Next(_ui.SortColumn, _ui.SortDesc, col);
        _ui.SortColumn = c;
        _ui.SortDesc = d;
        SaveUi();
        ApplyView(false);
    }

    // ───────── нижняя панель ─────────

    private void SetupDock()
    {
        _details.Framed = false;
        _details.BackColor = Theme.Paper;
        _dock.Add(DockTab.Problems, "Проблемы", "Проблемы", Theme.GWarning, _problems,
            "Ошибки и предупреждения: окружение, зависимости, конфликты, совместимость, ошибки в журнале (Ctrl+1)");
        _dock.Add(DockTab.Hooks, "Хуки Harmony", "Хуки", Theme.GPlug, _hooks,
            "Какие методы игры патчат включённые моды и где они пересекаются (Ctrl+2)");
        _dock.Add(DockTab.Logs, "Логи", "Логи", Theme.GDoc, _logs, "Журнал MelonLoader последнего запуска — Latest.log (Ctrl+3)");
        _dock.Add(DockTab.Details, "Детали", "Детали", Theme.GInfo, _details,
            "Подробности выбранного мода: описание, совместимость, зависимости, журнал, файл, старые версии (Ctrl+4 или двойной щелчок по моду)");
        _dock.SetActive(_ui.DockTab);
        _dock.SetCollapsed(_ui.DockCollapsed);
        _dock.Picked += t => ShowDockTab(t);
        _dock.CollapseToggled += () => SetDockCollapsed(!_dock.Collapsed);
        _grip.DragStarted += () => _dockDragFrom = _dock.Collapsed ? 0 : _dock.Height;
        _grip.Dragged += DragDock;
        _grip.DragEnded += SaveUi;
        _grip.Toggle += () => SetDockCollapsed(!_dock.Collapsed);
        foreach (var v in new[] { _problems, _hooks, _logs })
        {
            v.RowClicked += r => OnDockRow(r, false);
            v.RowActivated += r => OnDockRow(r, true);
            v.ModName = r => ModOfRow(r)?.DisplayName;
        }
        _list.OpenRequested += _ => ShowDockTab(DockTab.Details);
        _list.SortRequested += OnSortRequested;
        _designBtn.Tip = "Дизайн окна: «Компактный», «Fluent» или «Слаймовый» — меняется сразу (Ctrl+Shift+D — следующий)";
        _designBtn.Click += (_, _) => OpenDesignPicker(_designBtn);
        SyncDockTools();
    }

    private ModEntry? ModOfRow(RowsView.Row r)
    {
        if (r.ModId != null) return _m.FindAny(r.ModId);
        if (r.Tag is LogLine l) return _m.Mods.FirstOrDefault(m => MelonLogTail.Mentions(l, m));
        return null;
    }

    /// Щелчок по строке — выбрать её мод в списке; двойной щелчок или Enter — ещё и открыть «Детали».
    private void OnDockRow(RowsView.Row r, bool open)
    {
        var m = ModOfRow(r);
        if (m == null) return;
        if (!string.Equals(_list.SelectedId, m.Id, StringComparison.OrdinalIgnoreCase)) SelectAnywhere(m.Id);
        if (open) ShowDockTab(DockTab.Details);
    }

    private Control? PageOf(DockTab t) => _dock.TabOf(t)?.Page;

    private void ShowDockTab(DockTab t, bool focus = false)
    {
        bool layout = _dock.Collapsed;
        _dock.SetActive(t);
        _dock.SetCollapsed(false);
        if (layout) DoLayout();
        SyncDockTools();
        RefreshDockPage(t);
        if (_ui.DockTab != t || _ui.DockCollapsed)
        {
            _ui.DockTab = t;
            _ui.DockCollapsed = false;
            SaveUi();
        }
        if (focus) PageOf(t)?.Focus();
    }

    private void SetDockCollapsed(bool collapsed)
    {
        if (_dock.Collapsed == collapsed) return;
        bool focusList = collapsed && PageOf(_dock.Active)?.ContainsFocus == true;
        _dock.SetCollapsed(collapsed);
        _ui.DockCollapsed = collapsed;
        DoLayout();
        if (!collapsed) RefreshDockPage(_dock.Active);
        if (focusList) _list.Focus();
        SaveUi();
    }

    /// Ручку тянут: выше — панель выше; почти до низа — свернуть.
    private void DragDock(int dy)
    {
        int from = _dockDragFrom > 0 ? _dockDragFrom : _dock.BarH;
        int h = from - dy;
        if (h < _dock.BarH + _root.Px(56))
        {
            if (!_dock.Collapsed) { _dock.SetCollapsed(true); _ui.DockCollapsed = true; DoLayout(); }
            return;
        }
        if (_dock.Collapsed) { _dock.SetCollapsed(false); _ui.DockCollapsed = false; RefreshDockPage(_dock.Active); }
        _ui.DockHeight = Math.Max(1, (int)Math.Round(h / _root.S));
        DoLayout();
        _root.Update();
    }

    /// Высота панели в раскладке: сохранённая (по умолчанию ~38% места под список), не меньше 150 px, список — не меньше 140 px.
    private int DockHeightFor(int area)
    {
        if (_dock.Collapsed) return _dock.BarH;
        int P(float v) => _root.Px(v);
        int want = _ui.DockHeight > 0 ? P(_ui.DockHeight) : area * 38 / 100;
        int max = area - P(10) - P(140);
        return Math.Max(_dock.BarH, Math.Min(Math.Max(want, P(150)), max));
    }

    /// Кнопки справа в полосе вкладок — свои у каждой вкладки.
    private void SyncDockTools()
    {
        var tools = new List<DockPanel.Tool>();
        string note = "", noteTip = "";
        switch (_dock.Active)
        {
            case DockTab.Problems:
                tools.Add(new DockPanel.Tool { Key = "recheck", Text = "Проверить снова", Glyph = Theme.GRefresh, Tip = "Заново прочитать DLL модов и окружение (MelonLoader, Unity, .NET, библиотеки)", Act = () => { StartCompatCheck(); Toast(Tone.Info, "Проверка совместимости запущена."); } });
                if (_m.Env != null) { note = "Проверено: " + _m.Env.Summary; noteTip = note; }
                else if (_m.CompatBusy) note = "Идёт проверка совместимости…";
                break;
            case DockTab.Hooks:
                tools.Add(new DockPanel.Tool { Key = "hsel", Text = "Только выбранный мод", Glyph = Theme.GPin, IsToggle = true, On = _ui.HooksSelectedOnly, Tip = "Показать только методы, которые патчит выбранный мод", Act = () => { _ui.HooksSelectedOnly = !_ui.HooksSelectedOnly; SaveUi(); SyncDockTools(); RefreshDockPage(DockTab.Hooks); } });
                note = _hooksNote;
                break;
            case DockTab.Logs:
                tools.Add(new DockPanel.Tool { Key = "lerr", Text = "Только ошибки", Glyph = Theme.GWarning, IsToggle = true, On = _ui.LogErrorsOnly, Tip = "Только ошибки и предупреждения", Act = () => { _ui.LogErrorsOnly = !_ui.LogErrorsOnly; SaveUi(); SyncDockTools(); RefreshDockPage(DockTab.Logs, true); } });
                tools.Add(new DockPanel.Tool { Key = "lsel", Text = "Только выбранный мод", Glyph = Theme.GPin, IsToggle = true, On = _ui.LogSelectedOnly, Tip = "Только строки выбранного мода", Act = () => { _ui.LogSelectedOnly = !_ui.LogSelectedOnly; SaveUi(); SyncDockTools(); RefreshDockPage(DockTab.Logs, true); } });
                tools.Add(new DockPanel.Tool { Key = "lopen", Text = "Открыть файл", Glyph = Theme.GOpen, Tip = _store.MelonLogPath, Act = OpenLogs });
                (note, noteTip) = LogNote();
                break;
        }
        string sig = (int)_dock.Active + "|" + note + "|" + noteTip + "|" + string.Join("|", tools.Select(t => t.Key + (t.On ? "+" : "-") + t.Tip));
        _dock.Tools.Clear();
        _dock.Tools.AddRange(tools);
        _dock.Note = note;
        _dock.NoteTip = noteTip;
        if (sig != _dockToolsSig) { _dockToolsSig = sig; _dock.Invalidate(); }
    }

    private string _dockToolsSig = "";

    /// После изменения данных: счётчики на вкладках — всегда, строки — у видимой вкладки.
    private void RefreshDock()
    {
        var probs = BuildProblemRows(out int errors);
        int pc = probs.Count(r => !r.Header);
        var hooks = BuildHookRows(out int shared, out bool risky, out int targets);
        SetBadge(DockTab.Problems, pc, errors > 0 ? Tone.Error : Tone.Warn);
        if (shared > 0) SetBadge(DockTab.Hooks, shared, risky ? Tone.Warn : Tone.Info);
        else SetBadge(DockTab.Hooks, targets, Tone.Neutral);
        SetBadge(DockTab.Logs, _logErrors, Tone.Error);
        string bs = string.Join("|", _dock.Tabs.Select(t => t.Badge + "#" + (int)t.Tone));
        if (bs != _dockBadgeSig) { _dockBadgeSig = bs; _dock.Invalidate(); }
        if (_dock.Collapsed) return;
        switch (_dock.Active)
        {
            case DockTab.Problems: SetRowsIfChanged(_problems, probs, ref _problemsSig, false); SyncProblemsEmpty(); break;
            case DockTab.Hooks: SetRowsIfChanged(_hooks, hooks, ref _hooksSig, false); break;
            case DockTab.Logs: RefreshDockPage(DockTab.Logs); break;
        }
    }

    /// Строки видимой вкладки (после смены вкладки, фильтра или выбранного мода).
    private void RefreshDockPage(DockTab t, bool resetScroll = false)
    {
        if (_dock.Collapsed || _dock.Active != t) return;
        switch (t)
        {
            case DockTab.Problems:
                SetRowsIfChanged(_problems, BuildProblemRows(out _), ref _problemsSig, resetScroll);
                SyncProblemsEmpty();
                break;
            case DockTab.Hooks:
                SetRowsIfChanged(_hooks, BuildHookRows(out _, out _, out _), ref _hooksSig, resetScroll);
                break;
            case DockTab.Logs:
                SetRowsIfChanged(_logs, BuildLogRows(), ref _logsSig, resetScroll);
                break;
        }
    }

    /// Выбрали другой мод: метки «относится к выбранному» и фильтры «Только выбранный мод».
    private void OnDockSelection()
    {
        if (_dock.Collapsed) return;
        if (_dock.Active == DockTab.Hooks) RefreshDockPage(DockTab.Hooks, _ui.HooksSelectedOnly);
        else if (_dock.Active == DockTab.Logs) RefreshDockPage(DockTab.Logs, _ui.LogSelectedOnly);
        else if (_dock.Active == DockTab.Problems) RefreshDockPage(DockTab.Problems);
    }

    private void SetBadge(DockTab id, int n, Tone tone)
    {
        var t = _dock.TabOf(id);
        if (t == null) return;
        t.Badge = n <= 0 ? null : n > 99 ? "99+" : n.ToString();
        t.Tone = tone;
    }

    private static void SetRowsIfChanged(RowsView v, List<RowsView.Row> rows, ref string sig, bool resetScroll)
    {
        var sb = new StringBuilder(rows.Count * 24);
        foreach (var r in rows)
            sb.Append(r.Key).Append('\u0001').Append(r.Text).Append('\u0001').Append(r.Title).Append('\u0001').Append(r.Badge).Append(r.Mark ? '*' : '-').Append((int)r.Tone).Append('\u0002');
        string s = sb.ToString();
        if (s == sig && !resetScroll) return;
        sig = s;
        v.SetRows(rows, resetScroll);
    }

    // ── «Проблемы»

    private static string EnvArea(CompatIssue i)
    {
        string c = i.Code;
        if (c.StartsWith("loader", StringComparison.Ordinal) || c.StartsWith("support", StringComparison.Ordinal)) return "MelonLoader";
        if (c.StartsWith("dotnet", StringComparison.Ordinal)) return ".NET";
        if (c.StartsWith("harmony", StringComparison.Ordinal)) return "Harmony";
        if (c.StartsWith("interop", StringComparison.Ordinal)) return "Il2CppInterop";
        if (c.StartsWith("tomlet", StringComparison.Ordinal)) return "Tomlet";
        if (c.StartsWith("game-asm", StringComparison.Ordinal)) return "Сборки игры";
        if (c.StartsWith("prefs", StringComparison.Ordinal)) return "MelonPreferences";
        return "Окружение";
    }

    private static string IssueTip(CompatIssue i)
        => i.Title + "\n" + i.Text + (i.Fix.Length > 0 ? "\nЧто сделать: " + i.Fix : "");

    private List<RowsView.Row> BuildProblemRows(out int errors)
    {
        var rows = new List<RowsView.Row>();
        errors = 0;
        string? sel = _list.SelectedId;
        var env = _m.Env?.Issues.Where(i => i.Level >= CompatLevel.Warning).OrderByDescending(i => i.Level).ToList() ?? new List<CompatIssue>();
        if (env.Count > 0)
        {
            rows.Add(new RowsView.Row { Key = "h:env", Header = true, Title = "ОКРУЖЕНИЕ", Right = env.Count.ToString(), Tip = "MelonLoader, .NET, библиотеки и сборки игры — общие для всех модов" });
            foreach (var i in env)
            {
                var tone = i.Level == CompatLevel.Error ? Tone.Error : Tone.Warn;
                if (tone == Tone.Error) errors++;
                rows.Add(new RowsView.Row
                {
                    Key = "env:" + i.Code + ":" + i.Title, Glyph = tone == Tone.Error ? Theme.GError : Theme.GWarning, Tone = tone,
                    Title = EnvArea(i), Text = i.Title + (i.Text.Length > 0 ? " — " + i.Text : ""), Badge = i.Badge, BadgeTone = tone,
                    Tip = IssueTip(i), Copy = EnvArea(i) + ": " + i.Title + ". " + i.Text + (i.Fix.Length > 0 ? " Что сделать: " + i.Fix : ""),
                });
            }
        }
        var items = new List<(ModEntry M, string Badge, string Text, Tone Tone, string Tip, string Key)>();
        foreach (var m in _m.Mods)
        {
            foreach (var (badge, text, tone) in _m.ProblemDetails(m)) items.Add((m, badge, text, tone, text, "p:" + m.Id + ":" + badge));
            foreach (var i in _m.CompatBadges(m))
                items.Add((m, i.Badge, i.Title + (i.Text.Length > 0 ? " — " + i.Text : ""), i.Level == CompatLevel.Error ? Tone.Error : Tone.Warn, IssueTip(i), "c:" + m.Id + ":" + i.Code + ":" + i.Badge));
        }
        if (items.Count > 0)
        {
            rows.Add(new RowsView.Row { Key = "h:mods", Header = true, Title = "МОДЫ", Right = items.Count.ToString(), Tip = "Зависимости, конфликты, совместимость с MelonLoader и игрой, ошибки в журнале" });
            foreach (var it in items.OrderByDescending(x => x.Tone == Tone.Error).ThenBy(x => x.M.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                if (it.Tone == Tone.Error) errors++;
                rows.Add(new RowsView.Row
                {
                    Key = it.Key, Glyph = it.Tone == Tone.Error ? Theme.GError : Theme.GWarning, Tone = it.Tone,
                    Title = it.M.DisplayName, Text = it.Text, Badge = it.Badge, BadgeTone = it.Tone, ModId = it.M.Id,
                    Mark = sel != null && it.M.Id.Equals(sel, StringComparison.OrdinalIgnoreCase),
                    Tip = it.M.DisplayName + ": " + it.Tip, Copy = it.M.DisplayName + ": " + it.Text,
                });
            }
        }
        return rows;
    }

    private void SyncProblemsEmpty()
    {
        bool checking = _m.Env == null && _m.CompatBusy;
        _problems.EmptyHappy = !checking;
        _problems.EmptyTitle = checking ? "Идёт проверка совместимости…" : "Проблем нет";
        _problems.EmptyText = checking
            ? "Менеджер читает DLL модов и проверяет MelonLoader, Unity, .NET и библиотеки."
            : _m.Mods.Count == 0 ? "Модов пока нет — перетащите .dll или .zip в окно." : "Зависимости на месте, конфликтов и ошибок совместимости нет, в журнале прошлого запуска ошибок нет.";
    }

    // ── «Хуки Harmony»

    private string _hooksNote = "";

    private List<RowsView.Row> BuildHookRows(out int shared, out bool risky, out int targets)
    {
        var rows = new List<RowsView.Row>();
        shared = 0; risky = false; targets = 0;
        var selMod = _list.Selected;
        string? sel = selMod != null && !selMod.Available ? selMod.Id : null;
        bool onlySel = _ui.HooksSelectedOnly && sel != null;
        var groups = new Dictionary<string, List<(ModEntry M, HookTarget H)>>(StringComparer.Ordinal);
        var dyn = new List<(ModEntry M, HookTarget? H)>();
        int harmonyMods = 0;
        bool any = false;
        foreach (var m in _m.Mods)
        {
            if (!_m.On(m.Id)) continue;
            var mf = ManifestOf(m);
            if (mf == null) continue;
            any = true;
            if (mf.Hooks.Count > 0 || mf.ManualPatching) harmonyMods++;
            foreach (var h in mf.Hooks)
            {
                if (h.Dynamic) { dyn.Add((m, h)); continue; }
                if (!groups.TryGetValue(h.Key, out var l)) groups[h.Key] = l = new List<(ModEntry, HookTarget)>();
                l.Add((m, h));
            }
            if (mf.ManualPatching && mf.Hooks.Count == 0) dyn.Add((m, null));
        }
        targets = groups.Count;
        bool Has(List<(ModEntry M, HookTarget H)> g, string id) => g.Any(x => x.M.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        bool Risky(List<(ModEntry M, HookTarget H)> g) => g.Any(x => x.H.SkipsOriginal || x.H.Kind == "Transpiler");
        string Who(List<(ModEntry M, HookTarget H)> g) => string.Join("  ·  ", g.GroupBy(x => x.M.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First().M.DisplayName + " — " + string.Join(", ", x.Select(y => y.H.KindText).Distinct())));
        string Tip(string key, List<(ModEntry M, HookTarget H)> g) => key.Replace("::", ".") + "\n" + string.Join("\n", g.Select(x =>
            "• " + x.M.DisplayName + " — " + x.H.KindText + (x.H.PatchClass.Length > 0 ? " (" + HookTarget.ShortType(x.H.PatchClass) + ")" : "") + (x.H.Manual ? ", harmony.Patch в коде" : "")));
        string Ns(HookTarget h) { int i = Math.Max(h.Type.LastIndexOf('.'), h.Type.LastIndexOf('+')); return i > 0 ? h.Type.Substring(0, i) : ""; }

        var sharedG = new List<KeyValuePair<string, List<(ModEntry M, HookTarget H)>>>();
        var single = new List<KeyValuePair<string, List<(ModEntry M, HookTarget H)>>>();
        foreach (var kv in groups)
        {
            int n = kv.Value.Select(x => x.M.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (n >= 2) { shared++; if (Risky(kv.Value)) risky = true; sharedG.Add(kv); }
            else single.Add(kv);
        }
        RowsView.Row GroupRow(string key, List<(ModEntry M, HookTarget H)> g, bool isShared)
        {
            int n = g.Select(x => x.M.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            bool bad = isShared && Risky(g);
            var h0 = g[0].H;
            return new RowsView.Row
            {
                Key = "k:" + key, Glyph = bad ? Theme.GWarning : Theme.GPlug, Tone = bad ? Tone.Warn : isShared ? Tone.Info : Tone.Neutral,
                Title = h0.Short, Text = Who(g), Right = Ns(h0),
                Badge = isShared ? Theme.Count(n, "мод", "мода", "модов") : "", BadgeTone = bad ? Tone.Warn : Tone.Info,
                Mark = sel != null && !onlySel && Has(g, sel), ModId = sel != null && Has(g, sel) ? sel : g[0].M.Id,
                Tip = Tip(key, g) + (bad ? "\n\nПрефикс с отменой или транспайлер: моды могут мешать друг другу — порядок патчей зависит от приоритета (MelonPriority / HarmonyPriority)." : ""),
                Copy = key.Replace("::", ".") + ": " + Who(g),
            };
        }
        var shownShared = sharedG.Where(kv => !onlySel || Has(kv.Value, sel!)).OrderByDescending(kv => Risky(kv.Value))
            .ThenByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Value[0].H.Short, StringComparer.OrdinalIgnoreCase).ToList();
        var shownSingle = single.Where(kv => !onlySel || Has(kv.Value, sel!))
            .OrderBy(kv => kv.Value[0].M.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(kv => kv.Value[0].H.Short, StringComparer.OrdinalIgnoreCase).ToList();
        var shownDyn = dyn.Where(d => !onlySel || d.M.Id.Equals(sel, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.M.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (shownShared.Count > 0)
        {
            rows.Add(new RowsView.Row { Key = "h:shared", Header = true, Title = "ОБЩИЕ МЕТОДЫ", Right = shownShared.Count.ToString(), Tip = "Методы игры, которые патчат два мода и больше" });
            foreach (var kv in shownShared) rows.Add(GroupRow(kv.Key, kv.Value, true));
        }
        if (shownSingle.Count > 0)
        {
            rows.Add(new RowsView.Row { Key = "h:single", Header = true, Title = shownShared.Count > 0 ? "ОСТАЛЬНЫЕ" : "МЕТОДЫ ИГРЫ", Right = shownSingle.Count.ToString() });
            foreach (var kv in shownSingle) rows.Add(GroupRow(kv.Key, kv.Value, false));
        }
        if (shownDyn.Count > 0)
        {
            rows.Add(new RowsView.Row { Key = "h:dyn", Header = true, Title = "ЦЕЛЬ ВЫЧИСЛЯЕТСЯ В КОДЕ", Right = shownDyn.Count.ToString(), Tip = "TargetMethod() или harmony.Patch(…) — какой метод патчится, станет известно только в игре" });
            int k = 0;
            foreach (var (m, h) in shownDyn)
                rows.Add(new RowsView.Row
                {
                    Key = "d:" + m.Id + ":" + (k++), Glyph = Theme.GPlug, Tone = Tone.Neutral, Title = m.DisplayName,
                    Text = h == null ? "патчи создаются в коде (harmony.Patch) — цели заранее не известны" : ModList.HookLabel(h) + " — " + h.KindText,
                    ModId = m.Id, Mark = sel != null && !onlySel && m.Id.Equals(sel, StringComparison.OrdinalIgnoreCase),
                });
        }
        _hooksNote = !any ? "" : Theme.Count(targets, "метод", "метода", "методов") + " игры · " + Theme.Count(harmonyMods, "мод", "мода", "модов") + " с Harmony";
        _hooks.EmptyHappy = any;
        if (!any)
        {
            _hooks.EmptyTitle = _m.CompatBusy ? "Идёт проверка совместимости…" : _m.OnCount == 0 ? "Нет включённых модов" : "Хуки ещё не проверены";
            _hooks.EmptyText = _m.OnCount == 0 ? "Включите моды — здесь появятся методы игры, которые они патчат через Harmony." : "Список появится, когда менеджер прочитает DLL модов.";
        }
        else if (onlySel)
        {
            _hooks.EmptyTitle = "У «" + selMod!.DisplayName + "» хуков нет";
            _hooks.EmptyText = _m.On(sel!) ? "Он не патчит методы игры через Harmony. Снимите «Только выбранный мод», чтобы увидеть хуки всех включённых модов." : "Мод выключен — хуки показываются только у включённых модов.";
        }
        else
        {
            _hooks.EmptyTitle = "Хуков Harmony нет";
            _hooks.EmptyText = "Включённые моды не патчат методы игры через Harmony — пересекаться им негде.";
        }
        return rows;
    }

    // ── «Логи»

    /// Перечитать хвост журнала, если файл изменился (размер или время). true — изменился.
    private bool LoadLogTail(bool force = false)
    {
        string path = _store.MelonLogPath;
        long len = -1; DateTime stamp = default;
        try { var fi = new FileInfo(path); if (fi.Exists) { len = fi.Length; stamp = fi.LastWriteTimeUtc; } } catch { }
        if (!force && (len, stamp) == _logTailKey) return false;
        _logTailKey = (len, stamp);
        var raw = len < 0 ? new List<string>() : MelonLogTail.ReadTail(path);
        _logCut = raw.Count >= 3000 || len > 768 * 1024;
        _logLines = MelonLogTail.ParseAll(raw);
        _logErrors = _logLines.Count(l => !l.Continuation && l.Level == LogLevel.Error);
        _logWarnings = _logLines.Count(l => !l.Continuation && l.Level == LogLevel.Warning);
        _logsSig = "";
        return true;
    }

    private (string Note, string Tip) LogNote()
    {
        if (_logTailKey.Len < 0) return ("Latest.log не найден", _store.MelonLogPath);
        var t = _logTailKey.Stamp.ToLocalTime();
        string when = (DateTime.Now - t).TotalHours < 20 ? t.ToString("HH:mm") : t.ToString("dd.MM HH:mm");
        int n = _logLines.Count(l => !l.Continuation);
        string note = "Latest.log · " + when + " · " + (_logCut ? "последние " : "") + Theme.Count(n, "строка", "строки", "строк")
            + (_logErrors > 0 ? " · " + Theme.Count(_logErrors, "ошибка", "ошибки", "ошибок") : "")
            + (_logWarnings > 0 ? " · " + Theme.Count(_logWarnings, "предупреждение", "предупреждения", "предупреждений") : "");
        return (note, _store.MelonLogPath + "\nизменён " + t.ToString("dd.MM.yyyy HH:mm:ss"));
    }

    private List<RowsView.Row> BuildLogRows()
    {
        var sel = _list.Selected;
        bool hasSel = sel != null && !sel.Available;
        bool errorsOnly = _ui.LogErrorsOnly, selOnly = _ui.LogSelectedOnly && hasSel;
        var rows = new List<RowsView.Row>(Math.Min(_logLines.Count, 4000));
        for (int i = 0; i < _logLines.Count; i++)
        {
            var l = _logLines[i];
            if (errorsOnly && l.Level == LogLevel.Info) continue;
            bool mine = hasSel && MelonLogTail.Mentions(l, sel!);
            if (selOnly && !mine) continue;
            var tone = l.Level == LogLevel.Error ? Tone.Error : l.Level == LogLevel.Warning ? Tone.Warn : Tone.Neutral;
            string time = l.Time.Length >= 8 ? l.Time.Substring(0, 8) : l.Time;
            rows.Add(new RowsView.Row
            {
                Key = "l:" + i,
                Lead = l.Continuation ? "" : time,
                Glyph = l.Continuation ? "" : tone == Tone.Error ? Theme.GError : tone == Tone.Warn ? Theme.GWarning : "",
                Tone = tone,
                Title = l.Continuation || l.Section.Length == 0 ? "" : "[" + l.Section + "]",
                TitleTone = tone == Tone.Neutral ? Tone.Info : tone,
                Text = l.Continuation ? l.Text.Trim() : l.Text,
                TextTone = tone,
                Dim = l.Continuation,
                Indent = l.Continuation ? 1 : 0,
                Mark = mine && !selOnly,
                Tag = l,
                Copy = l.Raw,
                Tip = l.Raw.Length > 150 ? WrapLog(l.Raw) : null,
            });
        }
        if (_logTailKey.Len < 0)
        {
            _logs.EmptyHappy = false;
            _logs.EmptyTitle = "Журнала пока нет";
            _logs.EmptyText = "MelonLoader\\Latest.log появится после первого запуска игры с MelonLoader.";
        }
        else if (_logLines.Count == 0)
        {
            _logs.EmptyHappy = false;
            _logs.EmptyTitle = "Журнал пуст";
            _logs.EmptyText = "Latest.log есть, но в нём пока ничего не записано.";
        }
        else
        {
            _logs.EmptyHappy = errorsOnly && !selOnly;
            _logs.EmptyTitle = selOnly ? "Про «" + sel!.DisplayName + "» в журнале ничего" + (errorsOnly ? " тревожного нет" : " нет")
                : "Ошибок и предупреждений нет";
            _logs.EmptyText = selOnly ? "Снимите «Только выбранный мод», чтобы увидеть весь журнал." : "В журнале прошлого запуска нет ни ошибок, ни предупреждений.";
        }
        return rows;
    }

    private static string WrapLog(string s)
    {
        if (s.Length > 1200) s = s.Substring(0, 1200) + "…";
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i += 110) sb.Append(s, i, Math.Min(110, s.Length - i)).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    // ───────── снимок: панель выбора дизайна поверх окна (--render --picker) ─────────

    private DesignPicker? _renderPicker;

    private void AddPickerForRender()
    {
        _renderPicker = new DesignPicker { Standalone = true };
        _root.Controls.Add(_renderPicker);
        _renderPicker.BringToFront();
        PlaceRenderPicker();
    }

    /// Под кнопкой «Дизайн», по её правому краю — как всплывающая панель в обычном запуске.
    private void PlaceRenderPicker()
    {
        if (_renderPicker == null) return;
        int x = Math.Max(_root.Px(8), _designBtn.Right - _renderPicker.Width);
        _renderPicker.Location = new Point(x, _designBtn.Bottom + _root.Px(6));
    }
}
