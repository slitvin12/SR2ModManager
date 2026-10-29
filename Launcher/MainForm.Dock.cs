// MainForm.Dock.cs — дизайн окна и нижняя панель: выбор стиля (UserData\ModManager\ui.json, рядом с profiles.json),
// вкладки «Проблемы», «Хуки Harmony», «Журнал игры», «Изменения» и «Детали», высота и сворачивание панели, сортировка таблицы по колонкам.
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
    private readonly RowsView _changes = new() { TitleW = 190 };
    private UiPrefs _ui = new();
    private string _uiPath = "";
    private int _dockDragFrom;
    private string _dockBadgeSig = "", _problemsSig = "", _hooksSig = "", _logsSig = "", _changesSig = "";

    // хвост MelonLoader\Latest.log для вкладки «Журнал игры»
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
            SyncDesignChrome();
            _rootSig = ""; _chipsSig = ""; _filterSig = ""; _dockBadgeSig = "";
            _rootLayoutKey = default;
            RefreshUi(); // боковая панель, вкладки, строка состояния, панели — в размерах и цветах нового стиля
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
        Toast(Tone.Ok, "Дизайн: «" + Designs.Get(k).Name + "». Ctrl+Shift+D — следующий, все варианты — «Настройки» слева.");
    }

    private void OpenDesignPicker(Control? anchor = null)
    {
        anchor ??= _cmd;
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
            "Ошибки и предупреждения: окружение, зависимости, конфликты, совместимость, ошибки в журнале (Ctrl+1)", VIcon.Warning);
        _dock.Add(DockTab.Hooks, "Хуки Harmony", "Хуки", Theme.GPlug, _hooks,
            "Какие методы игры патчат включённые моды и где они пересекаются (Ctrl+2)", VIcon.Branch);
        _dock.Add(DockTab.Logs, "Журнал игры", "Журнал", Theme.GDoc, _logs, "Журнал MelonLoader последнего запуска — Latest.log (Ctrl+3)", VIcon.Doc);
        _dock.Add(DockTab.Changes, "Изменения", "Изм.", Theme.GUndo, _changes,
            "Что изменится после «Применить» или «Играть»: какие моды включатся, какие выключатся (Ctrl+4)", VIcon.Undo);
        _dock.Add(DockTab.Details, "Детали", "Детали", Theme.GInfo, _details,
            "Подробности выбранного мода: описание, совместимость, зависимости, журнал, файл, старые версии (Ctrl+5 или двойной щелчок по моду)", VIcon.CircleInfo);
        _dock.SetActive(_ui.DockTab);
        _dock.SetCollapsed(_ui.DockCollapsed);
        _dock.Picked += t => ShowDockTab(t);
        _dock.CollapseToggled += () => SetDockCollapsed(!_dock.Collapsed);
        _grip.DragStarted += () => _dockDragFrom = _dock.Collapsed ? 0 : _dock.Height;
        _grip.Dragged += DragDock;
        _grip.DragEnded += SaveUi;
        _grip.Toggle += () => SetDockCollapsed(!_dock.Collapsed);
        foreach (var v in new[] { _problems, _hooks, _logs, _changes })
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

    /// Высота панели в раскладке: сохранённая (по умолчанию — треть места под список, у карточек и капсул чуть меньше:
    /// они выше строк таблицы), не меньше 150 px, список — не меньше 140 px.
    private int DockHeightFor(int area)
    {
        if (_dock.Collapsed) return _dock.BarH;
        int P(float v) => _root.Px(v);
        int want = _ui.DockHeight > 0 ? P(_ui.DockHeight) : area * (Flat ? 336 : 310) / 1000;
        int max = area - P(10) - P(140);
        return Math.Max(_dock.BarH, Math.Min(Math.Max(want, P(150)), max));
    }

    /// Кнопки справа в полосе вкладок — свои у каждой вкладки.
    private void SyncDockTools()
    {
        var tools = new List<DockPanel.Tool>();
        string note = "", noteTip = "", noteShort = "";
        switch (_dock.Active)
        {
            case DockTab.Problems:
                tools.Add(new DockPanel.Tool { Key = "recheck", Text = "Проверить снова", Glyph = Theme.GRefresh, Tip = "Заново прочитать DLL модов и окружение (MelonLoader, Unity, .NET, библиотеки)", Act = () => { StartCompatCheck(); Toast(Tone.Info, "Проверка совместимости запущена."); } });
                if (_m.Env is { } env)
                {
                    note = "Проверено: " + env.Summary; noteTip = note;
                    noteShort = "Проверено: " + (env.LoaderInstalled ? "ML " + env.LoaderShort : "без MelonLoader") + (env.GameVersion.Length > 0 ? " · игра " + env.ShortGameVersion : "");
                }
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
            case DockTab.Changes:
            {
                bool run = _m.GameRunning, plan = PendingDiff().Count > 0;
                int n = _m.ChangeCount;
                if (run ? Dirty() : n > 0)
                    tools.Add(new DockPanel.Tool { Key = "capply", Text = run ? "Запланировать" : "Применить", Glyph = run ? Theme.GClock : Theme.GSave, Tip = _apply.Tip ?? "", Act = () => Commit(false) });
                if (n > 0 || plan)
                    tools.Add(new DockPanel.Tool { Key = "creset", Text = "Сбросить все", Glyph = Theme.GUndo, Tip = "Вернуть отметки как в папке Mods" + (plan ? " и отменить запланированное" : ""), Act = ResetChanges });
                if (n > 0)
                {
                    (note, noteShort) = run ? (plan && !Dirty() ? ("Запланировано на следующий запуск игры", "До следующего запуска")
                                                                : ("Игра запущена — изменения можно только запланировать", "Только запланировать"))
                                            : ("Применятся по «Применить» (Ctrl+S) или «Играть» (Ctrl+Enter)", "Ctrl+S — применить");
                }
                break;
            }
        }
        string sig = (int)_dock.Active + "|" + note + "|" + noteShort + "|" + noteTip + "|" + string.Join("|", tools.Select(t => t.Key + (t.On ? "+" : "-") + t.Tip));
        _dock.Tools.Clear();
        _dock.Tools.AddRange(tools);
        _dock.Note = note;
        _dock.NoteShort = noteShort;
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
        // «Компактный», как в макете: у вкладки хуков счётчика нет — конфликты и так видны в «Проблемах» и в таблице
        if (Flat) SetBadge(DockTab.Hooks, 0, Tone.Neutral);
        else if (shared > 0) SetBadge(DockTab.Hooks, shared, risky ? Tone.Warn : Tone.Info);
        else SetBadge(DockTab.Hooks, targets, Tone.Neutral);
        SetBadge(DockTab.Logs, _logErrors, Tone.Error);
        SetBadge(DockTab.Changes, _m.ChangeCount, Tone.Neutral);
        string bs = string.Join("|", _dock.Tabs.Select(t => t.Badge + "#" + (int)t.Tone));
        if (bs != _dockBadgeSig) { _dockBadgeSig = bs; _dock.Invalidate(); }
        if (_dock.Collapsed) return;
        switch (_dock.Active)
        {
            case DockTab.Problems: SetRowsIfChanged(_problems, probs, ref _problemsSig, false); SyncProblemsEmpty(); break;
            case DockTab.Hooks: SetRowsIfChanged(_hooks, hooks, ref _hooksSig, false); break;
            case DockTab.Logs: RefreshDockPage(DockTab.Logs); break;
            case DockTab.Changes: SetRowsIfChanged(_changes, BuildChangeRows(), ref _changesSig, false); SyncDockTools(); break;
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
            case DockTab.Changes:
                SetRowsIfChanged(_changes, BuildChangeRows(), ref _changesSig, resetScroll);
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
        else if (_dock.Active == DockTab.Changes) RefreshDockPage(DockTab.Changes);
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
        {
            sb.Append(r.Key).Append('\u0001').Append(r.Text).Append('\u0001').Append(r.Title).Append('\u0001').Append(r.Badge).Append(r.Mark ? '*' : '-').Append((int)r.Tone)
              .Append('\u0001').Append((int)r.Icon).Append(r.Rich ? 'R' : '-');
            if (r.Actions != null) foreach (var a in r.Actions) sb.Append('\u0003').Append(a.Text);
            sb.Append('\u0002');
        }
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

    // Проблемы — плоский список без заголовков разделов: у строки значок, о чём речь и кнопки «что сделать».
    // Конфликт хуков двух модов — одна строка «A ↔ B», а не по строке у каждого.

    private static readonly HashSet<string> WontLoad = new(StringComparer.Ordinal)
        { "unhollower", "legacy-info", "loader-newer", "verify-loader", "other-game", "mono-only", "platform", "process", "game-version", "dotnet-newer", "loader-older-broken" };
    private static readonly HashSet<string> MayBreak = new(StringComparer.Ordinal)
        { "loader-newer-patch", "netfx", "interop-older", "harmony-older", "lib-older", "desktop-lib", "framework-missing" };

    /// Коротко, что не так: заголовок проблемы и чем это грозит (подробности — в подсказке и во вкладке «Детали»).
    private static string FlatIssueText(CompatIssue i)
        => (i.Line.Length > 0 ? i.Line : i.Title) + (WontLoad.Contains(i.Code) ? " — не загрузится" : MayBreak.Contains(i.Code) ? " — может не работать" : "");

    private static string OneLine(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 160 ? s.Substring(0, 160) + "…" : s;
    }

    /// Действие над модом по Id — к моменту щелчка список модов мог перечитаться.
    private Action ForMod(string id, Action<ModEntry> f) => () => { var x = _m.Find(id); if (x != null) f(x); };

    private void ShowDetailsOf(ModEntry m)
    {
        SelectAnywhere(m.Id);
        ShowDockTab(DockTab.Details);
    }

    /// «Журнал игры» только со строками этого мода.
    private void ShowLogFor(ModEntry m)
    {
        SelectAnywhere(m.Id);
        if (!_ui.LogSelectedOnly) { _ui.LogSelectedOnly = true; SaveUi(); }
        ShowDockTab(DockTab.Logs, true);
    }

    /// «Хуки Harmony» только с методами этого мода.
    private void ShowHooksOf(ModEntry m)
    {
        SelectAnywhere(m.Id);
        if (!_ui.HooksSelectedOnly) { _ui.HooksSelectedOnly = true; SaveUi(); }
        ShowDockTab(DockTab.Hooks, true);
    }

    private void DisableOneMenu(string a, string b)
    {
        var menu = Menus.Create(this);
        foreach (var id in new[] { a, b })
        {
            var m = _m.Find(id);
            if (m == null || !_m.On(m.Id)) continue;
            Menus.Add(menu, "Выключить «" + m.DisplayName + "»", Theme.GCancel, ForMod(m.Id, Toggle));
        }
        if (menu.Items.Count == 0) { menu.Dispose(); return; }
        menu.Show(Cursor.Position);
    }

    private List<RowsView.Row> BuildProblemRows(out int errors)
    {
        int err = 0;
        string? sel = _list.SelectedId;
        bool IsSel(string? id) => sel != null && id != null && id.Equals(sel, StringComparison.OrdinalIgnoreCase);
        var items = new List<(int Rank, string Sort, RowsView.Row Row)>();
        void Add(int rank, string sort, string key, Tone tone, VIcon icon, string title, string text, string tip, string? modId,
            List<(string, Action)> acts, bool rich = false, bool mark = false)
        {
            if (tone == Tone.Error) err++;
            items.Add((rank, sort, new RowsView.Row
            {
                Key = key, Icon = icon, Glyph = tone == Tone.Error ? Theme.GError : Theme.GWarning, Tone = tone,
                Title = title, Text = text, Rich = rich, ModId = modId, Mark = mark || IsSel(modId),
                Tip = tip, Copy = title + ": " + text.Replace("**", "").Replace("`", ""), Actions = acts.Count > 0 ? acts : null,
            }));
        }

        // окружение — общее для всех модов
        foreach (var i in _m.Env?.Issues.Where(i => i.Level >= CompatLevel.Warning).OrderByDescending(i => i.Level) ?? Enumerable.Empty<CompatIssue>())
        {
            var tone = i.Level == CompatLevel.Error ? Tone.Error : Tone.Warn;
            var acts = new List<(string, Action)>();
            var issue = i;
            if (i.Code.StartsWith("prefs", StringComparison.Ordinal))
                acts.Add(("Показать файл", () => Report(Shell.Reveal(Path.Combine(_store.UserDataDir, "MelonPreferences.cfg")))));
            if (i.Code.StartsWith("game-asm", StringComparison.Ordinal) || i.Code.StartsWith("loader", StringComparison.Ordinal) && i.Code != "loader-missing")
                acts.Add(("Журнал", () => ShowDockTab(DockTab.Logs, true)));
            if (i.Fix.Length > 0) acts.Add(("Что сделать", () => Toast(Tone.Info, EnvArea(issue) + ": " + issue.Fix)));
            Add(tone == Tone.Error ? -2 : -1, EnvArea(i), "env:" + i.Code + ":" + i.Title, tone, tone == Tone.Error ? VIcon.CircleX : VIcon.Warning,
                EnvArea(i), i.Title + (i.Text.Length > 0 ? " — " + i.Text : ""), IssueTip(i), null, acts);
        }

        var pairs = new Dictionary<string, (ModEntry A, ModEntry B, CompatIssue I)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _m.Mods)
        {
            string id = m.Id, name = m.DisplayName;
            foreach (var (badge, text, tone) in _m.ProblemDetails(m))
            {
                var acts = new List<(string, Action)>();
                if (!m.Readable)
                {
                    acts.Add(("Показать файл", ForMod(id, x => Report(Shell.Reveal(x.Path)))));
                    acts.Add(("Удалить", ForMod(id, DeleteMod)));
                }
                else if (badge.StartsWith("нужен ", StringComparison.Ordinal)) acts.Add(("Включить нужные", FixDeps));
                else if (badge.StartsWith("нет «", StringComparison.Ordinal))
                {
                    var dep = m.DependsOn.Where(d => badge == $"нет «{d}»").Select(d => _m.FindAny(d)).FirstOrDefault(x => x != null && x.Available);
                    if (dep != null) { var dd = dep; acts.Add(("Установить " + dd.DisplayName, () => InstallAvailable(dd))); }
                    else acts.Add(("Подробнее", ForMod(id, ShowDetailsOf)));
                }
                else if (badge == "конфликт") acts.Add(("Выключить", ForMod(id, Toggle)));
                else if (badge.EndsWith(" в логе", StringComparison.Ordinal)) acts.Add(("Открыть журнал", ForMod(id, ShowLogFor)));
                else acts.Add(("Подробнее", ForMod(id, ShowDetailsOf)));
                Add(tone == Tone.Error ? 0 : 2, name, "p:" + id + ":" + badge, tone, tone == Tone.Error ? VIcon.CircleX : VIcon.Warning,
                    name, text, name + ": " + text, id, acts);
            }
            foreach (var i in _m.CompatBadges(m))
            {
                if (i.Code == "hook-conflict")
                {
                    bool paired = false;
                    foreach (var oid in i.Related)
                    {
                        var o = _m.Find(oid);
                        if (o == null || !HooksClash(m, o)) continue;
                        paired = true;
                        var (a, b) = string.Compare(m.Id, o.Id, StringComparison.OrdinalIgnoreCase) <= 0 ? (m, o) : (o, m);
                        string pk = a.Id + "|" + b.Id;
                        if (!pairs.ContainsKey(pk)) pairs[pk] = (a, b, i);
                    }
                    if (paired) continue;
                }
                var tone = i.Level == CompatLevel.Error ? Tone.Error : Tone.Warn;
                var acts = new List<(string, Action)>();
                if (tone == Tone.Error && WontLoad.Contains(i.Code))
                {
                    if (Uri.TryCreate(m.Link, UriKind.Absolute, out var lu) && (lu.Scheme == Uri.UriSchemeHttps || lu.Scheme == Uri.UriSchemeHttp))
                    {
                        string link = m.Link!;
                        acts.Add(("Найти новую версию", () => Report(Shell.OpenLink(link))));
                    }
                    else acts.Add(("Подробнее", ForMod(id, ShowDetailsOf)));
                    acts.Add(("Удалить", ForMod(id, DeleteMod)));
                }
                else acts.Add(("Подробнее", ForMod(id, ShowDetailsOf)));
                Add(tone == Tone.Error ? 0 : 2, name, "c:" + id + ":" + i.Code + ":" + i.Badge, tone, tone == Tone.Error ? VIcon.CircleX : VIcon.Warning,
                    name, FlatIssueText(i), name + ": " + IssueTip(i), id, acts);
            }
            // предупреждения в журнале прошлого запуска (ошибки уже есть среди проблем мода)
            var ls = _m.LogFor(m);
            if (ls != null && ls.Errors == 0 && ls.Warnings > 0)
            {
                string text = Theme.Count(ls.Warnings, "предупреждение", "предупреждения", "предупреждений") + " в прошлом запуске"
                    + (ls.LastWarning.Length > 0 ? ": " + OneLine(ls.LastWarning) : "");
                Add(2, name, "lw:" + id, Tone.Warn, VIcon.Warning, name, text, name + ": " + text + "\nПодробности — во вкладке «Журнал игры».", id,
                    new List<(string, Action)> { ("Открыть журнал", ForMod(id, ShowLogFor)) });
            }
        }
        foreach (var (a0, b0, i) in pairs.Values)
        {
            var (a, b) = ClashFirst(a0, b0);
            string aid = a.Id, bid = b.Id;
            Add(1, a.DisplayName + " " + b.DisplayName, "hc:" + aid + ":" + bid, Tone.Warn, VIcon.Branch, a.DisplayName + " ↔ " + b.DisplayName,
                ClashText(a, b), a.DisplayName + " и " + b.DisplayName + ": " + IssueTip(i), aid,
                new List<(string, Action)> { ("Показать хуки", ForMod(aid, ShowHooksOf)), ("Выключить один…", () => DisableOneMenu(aid, bid)) },
                rich: true, mark: IsSel(bid));
        }
        errors = err;
        return items.OrderBy(x => x.Rank).ThenBy(x => x.Sort, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Row).ToList();
    }

    private static bool RiskyHook(HookTarget h) => h.SkipsOriginal || h.Kind == "Transpiler";

    /// Первым в паре «A ↔ B» — тот, у кого на общем методе префикс с отменой или транспайлер: он и перехватывает вызов.
    private (ModEntry, ModEntry) ClashFirst(ModEntry a, ModEntry b)
    {
        var shared = SharedHooks(a, b);
        bool ra = shared.Any(s => s.A.Any(RiskyHook)), rb = shared.Any(s => s.B.Any(RiskyHook));
        return rb && !ra ? (b, a) : (a, b);
    }

    /// Общие методы двух модов: ключ → хуки каждого.
    private List<(string Key, List<HookTarget> A, List<HookTarget> B)> SharedHooks(ModEntry a, ModEntry b)
    {
        var fa = ManifestOf(a); var fb = ManifestOf(b);
        var res = new List<(string, List<HookTarget>, List<HookTarget>)>();
        if (fa == null || fb == null) return res;
        var byB = fb.Hooks.Where(h => !h.Dynamic).GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var g in fa.Hooks.Where(h => !h.Dynamic).GroupBy(h => h.Key))
            if (byB.TryGetValue(g.Key, out var hb)) res.Add((g.Key, g.ToList(), hb));
        return res;
    }

    /// Мешают ли моды друг другу: на общем методе у кого-то из них префикс с отменой или транспайлер (и они не связаны зависимостью).
    private bool HooksClash(ModEntry a, ModEntry b)
    {
        if (a.DependsOn.Contains(b.Id, StringComparer.OrdinalIgnoreCase) || b.DependsOn.Contains(a.Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (CompatChecker.Vouched(a, ManifestOf(a), b, ManifestOf(b))) return false; // автор проверил их вместе
        return CompatChecker.HooksClash(ManifestOf(a), ManifestOf(b)); // кооперативные отмены (HookPolicy) не мешают
    }

    /// «Оба патчат `Камера.LateUpdate` и ещё 2 метода: у A префикс с отменой».
    private string ClashText(ModEntry a, ModEntry b)
    {
        var shared = SharedHooks(a, b);
        if (shared.Count == 0) return "Патчат одни и те же методы игры";
        var main = shared.FirstOrDefault(s => s.A.Any(RiskyHook) || s.B.Any(RiskyHook));
        if (main.Key == null) main = shared[0];
        static string Kind(List<HookTarget> l) => l.Any(h => h.Kind == "Transpiler") ? "транспайлер" : l.Any(h => h.SkipsOriginal) ? "префикс с отменой" : "";
        string ka = Kind(main.A), kb = Kind(main.B);
        var who = new List<string>();
        if (ka.Length > 0 && ka == kb) who.Add("у обоих " + ka);
        else
        {
            if (ka.Length > 0) who.Add("у " + a.DisplayName + " " + ka);
            if (kb.Length > 0) who.Add("у " + b.DisplayName + " " + kb);
        }
        return "Оба патчат `" + main.A[0].Short + "`" + (shared.Count > 1 ? " и ещё " + Theme.Count(shared.Count - 1, "метод", "метода", "методов") : "")
            + (who.Count > 0 ? ": " + string.Join(", ", who) : "");
    }

    // ── «Изменения»

    /// Что изменится после «Применить» или «Играть»: моды, у которых отметка не совпадает с папкой Mods.
    private List<RowsView.Row> BuildChangeRows()
    {
        var rows = new List<RowsView.Row>();
        var pend = PendingDiff();
        string? sel = _list.SelectedId;
        foreach (var m in _m.Mods.Where(_m.IsChanged).OrderBy(x => _m.On(x.Id) ? 0 : 1).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            bool on = _m.On(m.Id), planned = pend.TryGetValue(m.Id, out var pv) && pv == on;
            string id = m.Id;
            string what = on ? "**включится** — DLL вернётся в Mods" : "**выключится** — DLL уедет в Mods\\Disabled";
            rows.Add(new RowsView.Row
            {
                Key = "ch:" + id, Icon = on ? VIcon.CircleCheck : VIcon.CircleX, Glyph = on ? Theme.GCheck : Theme.GCancel, Tone = on ? Tone.Ok : Tone.Neutral,
                Title = m.DisplayName, Text = what + (planned ? " · запланировано на следующий запуск" : ""), Rich = true, ModId = id,
                Mark = sel != null && id.Equals(sel, StringComparison.OrdinalIgnoreCase),
                Tip = m.DisplayName + (on ? ": включится" : ": выключится") + (planned ? " — запланировано, плагин применит при следующем запуске игры" : _m.GameRunning ? " — после «Запланировать»" : " — после «Применить» или «Играть»"),
                Copy = m.DisplayName + ": " + (on ? "включится" : "выключится"),
                Actions = new List<(string, Action)> { ("Отменить", ForMod(id, x => { _m.Sel[x.Id] = x.Enabled; RefreshUi(); })) },
            });
        }
        _changes.EmptyHappy = true;
        _changes.EmptyTitle = "Изменений нет";
        _changes.EmptyText = PendingDiff().Count > 0 ? "Запланированное совпадает с отметками — плагин применит его при следующем запуске игры."
            : "Отметки совпадают с папкой Mods. Включите или выключите моды — здесь появится, что изменится после «Применить» или «Играть».";
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
        bool Vouch(ModEntry a, ModEntry b) => CompatChecker.Vouched(a, ManifestOf(a), b, ManifestOf(b));
        // опасно, если префикс с отменой / транспайлер стоит рядом с модом, с которым автор совместимость не проверял
        bool Risky(List<(ModEntry M, HookTarget H)> g) => g.Any(x => CompatChecker.HardHook(ManifestOf(x.M), x.H)
            && g.Any(y => !y.M.Id.Equals(x.M.Id, StringComparison.OrdinalIgnoreCase) && !Vouch(x.M, y.M)));
        bool Agreed(List<(ModEntry M, HookTarget H)> g) => !Risky(g) && g.Any(x => x.H.SkipsOriginal || x.H.Kind == "Transpiler");
        // все «опасные на вид» хуки — кооперативные отмены (HookPolicy), а не проверка автора (CompatibleWith)
        bool CoopOnly(List<(ModEntry M, HookTarget H)> g) => g.Where(x => x.H.SkipsOriginal || x.H.Kind == "Transpiler")
            .All(x => x.H.Kind != "Transpiler" && CompatChecker.Cooperative(ManifestOf(x.M)));
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
                Tip = Tip(key, g) + (bad ? "\n\nПрефикс с отменой или транспайлер: моды могут мешать друг другу — порядок патчей зависит от приоритета (MelonPriority / HarmonyPriority)."
                    : isShared && Agreed(g) ? (CoopOnly(g) ? "\n\nПрефиксы с отменой объявлены кооперативными (SR2ModManager.HookPolicy): срабатывают только при включённой функции и не мешают чужим хукам."
                        : "\n\nАвтор мода проверил эти моды вместе (SR2ModManager.CompatibleWith): хуки согласованы.") : ""),
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

    // ── «Журнал игры»

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

    /// Под «Командами», по их правому краю — как всплывающая панель в обычном запуске.
    private void PlaceRenderPicker()
    {
        if (_renderPicker == null) return;
        Control a = _cmd;
        int x = Math.Max(_root.Px(8), a.Right - _renderPicker.Width);
        _renderPicker.Location = new Point(x, a.Bottom + _root.Px(6));
    }
}
