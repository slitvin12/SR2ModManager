// MainForm.Compact.cs — «Компактный» стиль в духе редактора кода. Вместо шапки с наборами и подвала с кнопками:
//  • верхняя полоса — фильтр, вкладки-фильтры, «Команды» (Ctrl+K) и «Играть» со стрелкой (способ запуска, применить без запуска);
//  • боковая панель — наборы модов, разделы (моды, каталог, бэкапы сейвов, журнал, настройки) и сведения об окружении;
//  • строка состояния — игра, сколько включено, что не применено, сообщения и подсказки по клавишам.
// Fluent и «Слаймовый» этих элементов не показывают — у них шапка и подвал из MainForm.cs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed partial class MainForm
{
    private readonly PaintBox _side = new();
    private readonly PaintBox _bar = new();
    private readonly PillButton _cmd = new("Команды", Look.Outline) { Icon = VIcon.Terminal, Keycap = "Ctrl+K" };
    private readonly SplitPlay _split = new();
    private bool _barMessage;                 // в строке состояния — сообщение (_status), а не «не применено»
    private (int N, long At) _savesCount = (-1, 0);
    private string _sideSig = "", _barSig = "";
    private int _lastSplitW = -1;
    private (Control C, Point P)? _menuAnchor; // где открыть меню, вызванное из боковой панели

    private const int SideW = 206, TopBarH = 46, StatusBarH = 24;
    private static readonly Color[] ProfileDots = { Theme.Hex(0xFF9BB5), Theme.Hex(0xFFD28A), Theme.Hex(0x7FB5FF), Theme.Hex(0xC4A7FF), Theme.Hex(0x5BD08A) };

    private static bool Flat => Theme.Spec.Flat;

    private void SetupCompact()
    {
        _cmd.Font = Theme.Body;
        _cmd.Tip = "Все команды: применить, сбросить, включить или выключить все, установить мод, наборы, бэкапы, папки, дизайн (Ctrl+K)";
        _cmd.Click += (_, _) => CommandsMenu();
        _split.Play += () => Commit(true);
        _split.MenuRequested += LaunchMenu;
        _split.MenuTip = "Способ запуска (Steam или напрямую), применить без запуска, сбросить изменения";
        _side.Painter = PaintSide;
        _bar.Painter = PaintBar;
        _status.Changed += () => { if (Flat) _bar.Invalidate(); };
    }

    /// Элементы, которые есть только у одного вида окна: у «Компактного» — верхняя полоса, боковая панель и строка
    /// состояния; у Fluent и «Слаймового» — шапка с наборами, подвал с кнопками и строка статуса над ним.
    private void SyncDesignChrome()
    {
        bool flat = Flat;
        foreach (var c in new Control[] { _chips, _designBtn, _launchVia, _more, _apply, _play, _status }) c.Visible = !flat;
        if (flat) foreach (var c in new Control[] { _allOn, _allOff, _reset, _openMods, _openLogs, _saveBackup }) c.Visible = false;
        foreach (var c in new Control[] { _side, _bar, _cmd, _split }) c.Visible = flat;
        foreach (var c in new Control[] { _side, _bar, _cmd, _split }) c.BackColor = Theme.HeaderBg;
        _filter.Tabs = flat;
        _filter.BackColor = flat ? Theme.HeaderBg : Theme.Cream;
        _search.Flat = flat;
        _search.BackColor = flat ? Theme.HeaderBg : Theme.Cream;
        _search.SetFill(flat ? Theme.Cream : null);
        _search.Placeholder = flat ? "Фильтр: имя, автор, хук..." : "Найти мод: название, автор, описание";
        _search.PlaceholderShort = flat ? "Фильтр…" : "Найти мод…";
        _search.Shortcut = flat ? "/" : "Ctrl+F";
        _banner.Square = flat;
        foreach (var v in new[] { _problems, _hooks, _logs, _changes }) { v.BackColor = flat ? Theme.HeaderBg : Theme.Paper; v.Tall = flat && (v == _problems || v == _changes); }
        _problems.TitleW = flat ? 190 : 200;
        _details.BackColor = flat ? Theme.HeaderBg : Theme.Paper;
        _sideSig = _barSig = "";
        _lastSplitW = -1;
        UpdateFooter();
    }

    // ───────── раскладка «Компактного» стиля ─────────

    /// Ширина боковой панели, px: как в макете (206), но не уже, чем нужно подписи «Каталог и обновления» со счётчиком —
    /// Segoe UI при 125 % чуть шире макетного шрифта, и подпись иначе обрезалась бы.
    private int SidePx()
    {
        int P(float v) => _root.Px(v);
        int cat = CountIn(CatalogFilter.Catalog);
        int need = P(39) + Theme.Measure("Каталог и обновления", Theme.Body).Width + P(2)
                 + (cat > 0 ? Theme.Measure(cat.ToString(), Theme.Small).Width + P(4) : 0) + P(16);
        return Math.Max(P(SideW), need);
    }

    private void LayoutCompact()
    {
        int P(float v) => _root.Px(v);
        int W = _root.ClientSize.Width, H = _root.ClientSize.Height;
        int top = P(TopBarH), side = SidePx(), barH = P(StatusBarH);

        // верхняя полоса: фильтр, вкладки, «Команды», «Играть»
        int sw = _split.PreferredWidth();
        _lastSplitW = sw;
        _split.SetBounds(W - P(10) - sw, P(7), sw, P(32));
        bool cmdWasIcon = _cmd.IconOnly, cmdHadKey = _cmd.ShowKeycap;
        _cmd.Height = P(30);
        _cmd.IconOnly = false;
        _cmd.ShowKeycap = true;
        int fw = _filter.PreferredWidth();
        _lastFilterW = fw;
        int searchW = P(250), cmdW = _cmd.PreferredWidth();
        int Room() => _split.Left - P(8) - cmdW - P(8) - (P(10) + searchW + P(8) + fw);
        if (Room() < 0) searchW = Math.Max(P(170), searchW + Room());
        if (Room() < 0) { _cmd.ShowKeycap = false; cmdW = _cmd.PreferredWidth(); }
        if (Room() < 0) { _cmd.IconOnly = true; cmdW = P(30); }
        _search.SetBounds(P(10), P(8), searchW, P(30));
        _filter.SetBounds(_search.Right + P(8), P(8), Math.Max(0, Math.Min(fw, _split.Left - P(16) - cmdW - _search.Right - P(8))), P(30));
        _cmd.SetBounds(_split.Left - P(8) - cmdW, P(8), cmdW, P(30));
        if (_cmd.IconOnly != cmdWasIcon || _cmd.ShowKeycap != cmdHadKey) _cmd.Invalidate();
        PlaceRenderPicker();

        // слева — боковая панель, снизу — строка состояния
        _side.SetBounds(0, top, side, Math.Max(0, H - top - barH));
        _bar.SetBounds(0, H - barH, W, barH);

        // справа: баннер (если есть), таблица, ручка и нижняя панель
        int y = top, cw = W - side;
        _lastBanner = _banner.Visible;
        if (_banner.Visible)
        {
            int bh = BannerHeight(cw);
            _banner.SetBounds(side, y, cw, bh);
            y += bh;
        }
        int bottom = H - barH;
        int gripH = P(5);
        int dockH = DockHeightFor(bottom - y);
        int listBottom = Math.Max(y + P(60), bottom - dockH - gripH);
        _list.SetBounds(side, y, cw, listBottom - y);
        _grip.SetBounds(side, listBottom, cw, gripH);
        _dock.SetBounds(side, listBottom + gripH, cw, Math.Max(_dock.BarH, bottom - listBottom - gripH));

        var rk = (_root.ClientSize, _root.DeviceDpi);
        if (rk != _rootLayoutKey) { _rootLayoutKey = rk; _root.Invalidate(); }
    }

    /// Перерисовать боковую панель и строку состояния, если то, что на них видно, изменилось.
    private void SyncCompactChrome()
    {
        if (!Flat) return;
        string ss = SideSig();
        if (ss != _sideSig) { _sideSig = ss; _side.Invalidate(); }
        string bs = BarSig();
        if (bs != _barSig) { _barSig = bs; _bar.Invalidate(); }
    }

    private void SetBarMessage(bool on)
    {
        if (_barMessage == on) return;
        _barMessage = on;
        if (Flat) _bar.Invalidate();
    }

    // ───────── боковая панель ─────────

    private int SavesCount()
    {
        long now = Environment.TickCount64;
        if (_savesCount.N >= 0 && now - _savesCount.At < 10000) return _savesCount.N;
        int n = 0;
        try { if (Directory.Exists(SaveBackupDir)) n = Directory.GetFiles(SaveBackupDir, "*.zip").Length; } catch { }
        _savesCount = (n, now);
        return n;
    }

    private int ProfileCount(Profile p) => p.All ? _m.Mods.Count : (p.Mods ?? new()).Count(id => _m.Find(id) != null);

    private static Color ProfileDot(Profile p)
    {
        if (p.All || p.Mods == null || p.Mods.Count == 0) return Theme.Hint;
        uint h = 2166136261;
        foreach (char c in p.Name.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return ProfileDots[h % (uint)ProfileDots.Length];
    }

    /// Набор, подсвеченный в боковой панели: тот, что даёт ровно текущий выбор, иначе последний выбранный — с меткой «изменён».
    private (string? Name, bool Modified) SideProfile()
    {
        string? active = _chips.Active;
        if (active != null) return (active, false);
        var last = _m.Cfg.Profiles.FirstOrDefault(p => p.Name.Equals(_m.Cfg.LastProfile, StringComparison.OrdinalIgnoreCase));
        return last == null ? (null, false) : (last.Name, _m.Mods.Count > 0);
    }

    private List<string> EnvLines()
    {
        var res = new List<string>();
        var env = _m.Env;
        string loader = env?.LoaderText is { Length: > 0 } lt ? lt : _m.Log?.LoaderVersion ?? "";
        if (loader.Length > 0) res.Add("MelonLoader " + loader);
        else if (env != null && !env.LoaderInstalled) res.Add("MelonLoader не установлен");
        var l2 = new List<string>();
        if (env?.UnityVersion is { Length: > 0 } u) l2.Add("Unity " + u);
        string gv = GameVersionText();
        if (gv.Length > 0) l2.Add("игра " + gv);
        if (l2.Count > 0) res.Add(string.Join(" · ", l2));
        var l3 = new List<string> { _m.PluginStaged ? "Плагин обновится после игры" : string.IsNullOrEmpty(_m.PluginVersion) ? "Плагин не установлен" : "Плагин v" + _m.PluginVersion };
        if (env?.DotNet != null) l3.Add(".NET " + env.DotNet.Major);
        res.Add(string.Join(" · ", l3));
        return res;
    }

    private string SideSig()
    {
        var (name, modified) = SideProfile();
        var sb = new System.Text.StringBuilder();
        sb.Append(Theme.Generation).Append('|').Append(name).Append(modified ? '*' : '-').Append('|').Append(_filterKind == CatalogFilter.Catalog ? 'c' : 'm')
          .Append('|').Append(_m.Mods.Count).Append('|').Append(CountIn(CatalogFilter.Catalog)).Append('|').Append(SavesCount());
        foreach (var p in _m.Cfg.Profiles) sb.Append('|').Append(p.Name).Append('#').Append(ProfileCount(p)).Append(p.All ? 'a' : '-').Append(p.Mods?.Count ?? 0);
        foreach (var l in EnvLines()) sb.Append('|').Append(l);
        return sb.ToString();
    }

    private void PaintSide(Graphics g, PaintBox v)
    {
        int P(float x) => v.Px(x);
        float s = v.S;
        int W = v.Width, H = v.Height, one = Math.Max(1, P(1));
        using (var lb = new SolidBrush(Theme.Line)) g.FillRectangle(lb, W - one, 0, one, H);
        int left = P(8), rowW = W - P(17), rowH = P(28), pitch = P(29), tx = P(33), countR = W - P(16);
        Color hoverBg = Theme.Mix(Theme.HeaderBg, Theme.Navy, 0.05f);
        void Head(string text, float cy) => Theme.Text(g, text, Theme.Section, new Rectangle(P(17), (int)Math.Round(cy - P(9)), P(150), P(18)), Theme.Hint);
        int Count(string text, Rectangle r)
        {
            if (text.Length == 0) return 0;
            int cw = Theme.Measure(text, Theme.Small).Width;
            Theme.Text(g, text, Theme.Small, new Rectangle(countR - cw - P(2), r.Y, cw + P(2), r.Height), Theme.Hint, TextFormatFlags.Right);
            return cw;
        }

        // ── наборы
        Head("НАБОРЫ", P(24));
        var plus = new Rectangle(W - P(34), P(14), P(20), P(20));
        bool hp = v.Hot("add");
        if (hp) Theme.FillRound(g, plus, P(4), hoverBg);
        Theme.Icon(g, VIcon.Plus, new RectangleF(plus.X + P(5), plus.Y + P(5), P(10), P(10)), hp ? Theme.Navy : Theme.Hint, Math.Max(1f, 1.3f * s));
        v.Zone(plus, "add", SaveProfileAs, "Сохранить текущий выбор модов как новый набор");

        var profiles = _m.Cfg.Profiles;
        var (shown, modified) = SideProfile();
        // наборы не помещаются вместе с разделами — последние уходят в «Ещё…»
        int sectionsH = P(25.5f) + P(27.5f) + 4 * pitch + rowH / 2 + P(10);
        int fit = Math.Max(1, (H - P(38) - sectionsH) / pitch);
        int rows = profiles.Count <= fit ? profiles.Count : Math.Max(0, fit - 1);
        int y = P(38);
        for (int i = 0; i < rows; i++)
        {
            var p = profiles[i];
            var r = new Rectangle(left, y, rowW, rowH);
            string key = "p:" + i;
            bool hl = p.Name == shown, hot = v.Hot(key);
            if (hl) Theme.FillRound(g, r, P(5), Theme.TealTint);
            else if (hot) Theme.FillRound(g, r, P(5), hoverBg);
            Theme.Dot(g, P(19.5f), r.Y + r.Height / 2f, P(3.5f), hl ? Theme.Teal : ProfileDot(p));
            int cw = Count(ProfileCount(p).ToString(), r);
            int maxR = countR - cw - P(8);
            Font f = hl ? Theme.ButtonFont : Theme.Body;
            int nameW = Theme.Measure(p.Name, f).Width + P(2);
            int bw = hl && modified ? Theme.Measure("изменён", Theme.TagFont).Width + P(12) : 0;
            if (bw > 0 && maxR - tx - bw - P(9) < Math.Min(nameW, P(50))) bw = 0;
            int tw = Math.Max(0, Math.Min(nameW, maxR - tx - (bw > 0 ? bw + P(9) : 0)));
            Theme.Text(g, p.Name, f, new Rectangle(tx, r.Y, tw, r.Height), hl || hot ? Theme.Navy : Theme.NavySoft);
            if (bw > 0)
            {
                var br = new Rectangle(tx + tw + P(7), r.Y + (r.Height - P(16)) / 2, bw, P(16));
                Theme.FillRound(g, br, br.Height / 2f, Theme.OrangeTint);
                Theme.Text(g, "изменён", Theme.TagFont, br, Theme.OrangeText, TextFormatFlags.HorizontalCenter);
            }
            var pp = p;
            string what = p.All ? "все моды" : (p.Mods == null || p.Mods.Count == 0) ? "без модов (ваниль)" : Names(p.Mods);
            string tip = p.Name + ": " + what + (hl && modified ? "\nОтметки уже отличаются от набора — правый щелчок → «Перезаписать текущим выбором» или «+» — новый набор" : "")
                + "\nЩелчок — включить набор; правый щелчок — переименовать, перезаписать, ярлык, экспорт";
            v.Zone(r, key, () => PickProfile(pp), tip, pt => ProfileMenu(pp, pt));
            y += pitch;
        }
        if (rows < profiles.Count)
        {
            var r = new Rectangle(left, y, rowW, rowH);
            bool hot = v.Hot("p:more");
            if (hot) Theme.FillRound(g, r, P(5), hoverBg);
            Theme.Text(g, "Ещё " + Theme.Count(profiles.Count - rows, "набор", "набора", "наборов") + "…", Theme.Body, new Rectangle(tx, r.Y, countR - tx, r.Height), hot ? Theme.Navy : Theme.Hint);
            int from = rows;
            v.Zone(r, "p:more", () => MoreProfilesMenu(from, new Point(r.Right + P(4), r.Y)), "Остальные наборы");
            y += pitch;
        }
        else if (profiles.Count == 0)
        {
            var r = new Rectangle(left, y, rowW, rowH);
            bool hot = v.Hot("p:new");
            if (hot) Theme.FillRound(g, r, P(5), hoverBg);
            Theme.Text(g, "Сохранить выбор как набор…", Theme.Body, new Rectangle(P(17), r.Y, countR - P(17), r.Height), hot ? Theme.Navy : Theme.Hint);
            v.Zone(r, "p:new", SaveProfileAs, "Набор — список включённых модов: переключайтесь между ними одним щелчком");
            y += pitch;
        }
        float h2 = y - pitch + rowH + P(25.5f);

        // ── разделы
        Head("РАЗДЕЛЫ", h2);
        int cat = CountIn(CatalogFilter.Catalog), saves = SavesCount();
        bool inCat = _filterKind == CatalogFilter.Catalog;
        var secs = new (string Key, VIcon Icon, string Text, string Count, bool Sel, Action<Rectangle> Click, string Tip, Action<Rectangle>? Right)[]
        {
            ("s:mods", VIcon.Grid, "Моды", _m.Mods.Count.ToString(), !inCat, _ => SetFilter(CatalogFilter.All),
                "Установленные моды; вкладки сверху — все, включённые, выключенные, с проблемами, с обновлениями", null),
            ("s:cat", VIcon.Download, "Каталог и обновления", cat > 0 ? cat.ToString() : "", inCat, _ => SetFilter(CatalogFilter.Catalog),
                "Моды, доступные для установки, и новые версии установленных — из ModManager\\Library, «Загрузок» и своих папок", null),
            ("s:saves", VIcon.Archive, "Бэкапы сейвов", saves > 0 ? saves.ToString() : "", false, r => { _menuAnchor = (_side, new Point(r.Right + P(4), r.Y)); SavesMenu(); },
                "Сохранить сейвы игры в zip (с проверкой SHA-256) или восстановить из бэкапа", null),
            ("s:log", VIcon.Doc, "Журнал игры", "", false, _ => ShowDockTab(DockTab.Logs),
                "Журнал MelonLoader последнего запуска (Latest.log) — вкладка внизу (Ctrl+3). Правый щелчок — файлы журналов",
                r => { _menuAnchor = (_side, new Point(r.Right + P(4), r.Y)); LogsMenu(); }),
            ("s:set", VIcon.Gear, "Настройки", "", false, r => { _menuAnchor = (_side, new Point(r.Right + P(4), r.Y)); SettingsMenu(); },
                "Дизайн окна, MelonPreferences.cfg, папки игры и модов, горячие клавиши", null),
        };
        int secBottom = 0;
        for (int i = 0; i < secs.Length; i++)
        {
            var sc = secs[i];
            float cy = h2 + P(27.5f) + i * pitch;
            var r = new Rectangle(left, (int)Math.Round(cy - rowH / 2f), rowW, rowH);
            bool hot = v.Hot(sc.Key);
            if (hot) Theme.FillRound(g, r, P(5), hoverBg);
            Color fg = sc.Sel || hot ? Theme.Navy : Theme.NavySoft;
            Theme.Icon(g, sc.Icon, new RectangleF(P(17), cy - P(6), P(12), P(12)), fg);
            int cw = Count(sc.Count, r);
            int textR = cw > 0 ? countR - cw - P(4) : countR;
            // выбранный раздел — полужирным, если так помещается (иначе обычным: лучше целиком, чем с «…»)
            Font sf = sc.Sel && Theme.Measure(sc.Text, Theme.ButtonFont).Width <= textR - P(39) ? Theme.ButtonFont : Theme.Body;
            Theme.Text(g, sc.Text, sf, new Rectangle(P(39), r.Y, Math.Max(0, textR - P(39)), r.Height), fg);
            var rr = r;
            var click = sc.Click; var right = sc.Right;
            v.Zone(r, sc.Key, () => click(rr), sc.Tip, right == null ? null : _ => right(rr));
            secBottom = r.Bottom;
        }

        // ── окружение — внизу, если не наезжает на разделы
        var lines = EnvLines();
        float pe = 18.5f * s;
        float headCy = H - P(26.5f) - lines.Count * pe;
        if (lines.Count > 0 && headCy - P(10) > secBottom + P(12))
        {
            Theme.Text(g, "Окружение", Theme.SmallBold, new Rectangle(P(16), (int)Math.Round(headCy - P(9)), W - P(28), P(18)), Theme.NavySoft);
            for (int j = 0; j < lines.Count; j++)
            {
                float cy = headCy + (j + 1) * pe;
                Theme.Text(g, lines[j], Theme.Small, new Rectangle(P(16), (int)Math.Round(cy - P(9)), W - P(28), P(18)), Theme.Hint);
            }
            var er = new Rectangle(left, (int)(headCy - P(12)), rowW, (int)(H - P(12) - (headCy - P(12))));
            string tip = _m.Env != null ? "Проверено: " + _m.Env.Summary + "\nЩелчок — вкладка «Проблемы»" : _m.CompatBusy ? "Идёт проверка окружения…" : "Окружение ещё не проверено";
            v.Zone(er, "env", () => ShowDockTab(DockTab.Problems), tip, null, false);
        }
    }

    private void MoreProfilesMenu(int from, Point at)
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        var (shown, _) = SideProfile();
        foreach (var p in _m.Cfg.Profiles.Skip(from))
        {
            var pp = p;
            Menus.Add(menu, p.Name + "  ·  " + ProfileCount(p), p.Name == shown ? Theme.GCheck : "", () => PickProfile(pp));
        }
        menu.Show(_side, at, ToolStripDropDownDirection.BelowRight);
    }

    // ───────── строка состояния ─────────

    private const string BarHints = "Ctrl+K — все команды · Пробел — вкл/выкл · Ctrl+Enter — играть";

    private string BarSig()
    {
        var d = Diff();
        return string.Join("|", Theme.Generation, _m.GameRunning, _m.GameStarted.HasValue ? Theme.Ago(DateTime.Now - _m.GameStarted.Value) : "",
            _m.OnCount, _m.Mods.Count, d.Count, d.Count > 0 && Dirty(), _barMessage,
            _barMessage ? (int)_status.Tone + _status.Message + string.Join(",", _status.Actions.Select(a => a.Text)) : "");
    }

    private static VIcon ToneIcon(Tone t) => t switch
    {
        Tone.Ok => VIcon.CircleCheck,
        Tone.Warn => VIcon.Warning,
        Tone.Error => VIcon.CircleX,
        _ => VIcon.CircleInfo,
    };

    private static Color ToneColor(Tone t) => t switch
    {
        Tone.Ok => Theme.Teal,
        Tone.Warn => Theme.Orange,
        Tone.Error => Theme.Coral,
        Tone.Info => Theme.Blue,
        _ => Theme.Hint,
    };

    private void PaintBar(Graphics g, PaintBox v)
    {
        int P(float x) => v.Px(x);
        float s = v.S;
        int W = v.Width, H = v.Height, one = Math.Max(1, P(1));
        using (var lb = new SolidBrush(Theme.Line)) g.FillRectangle(lb, 0, 0, W, one);
        var row = new Rectangle(0, one, W, H - one);
        float cy = row.Y + row.Height / 2f;
        var font = Theme.Small;
        int x = P(10);

        // игра
        bool run = _m.GameRunning;
        Theme.Dot(g, x + P(3), cy, P(3), run ? Theme.Teal : Theme.Hint);
        x += P(13);
        string game = run ? "Игра запущена" + (_m.GameStarted.HasValue ? " · " + Theme.Ago(DateTime.Now - _m.GameStarted.Value) : "") : "Игра не запущена";
        int gw = Theme.Measure(game, font).Width;
        Theme.Text(g, game, font, new Rectangle(x, row.Y, gw + P(2), row.Height), run ? Theme.TealText : Theme.Hint);
        v.Zone(new Rectangle(0, row.Y, x + gw + P(4), row.Height), "game", null,
            run ? "Пока игра запущена, DLL модов заняты — изменения можно только запланировать" : "Можно менять моды и запускать игру", null, false);
        x += gw + P(16);

        // сколько включено
        int all = _m.Mods.Count;
        string en = all == 0 ? "Модов нет — перетащите .dll или .zip в окно" : $"Включено **{_m.OnCount}** из {all}";
        int ew = Theme.Rich(null, en, font, new Rectangle(0, 0, 10000, row.Height), Theme.NavySoft, s);

        // третий участок: сообщение (тост, предупреждение, «сохраняю…») или не применённые изменения
        bool msg = _barMessage && _status.Message.Length > 0;
        string third = "";
        var diff = msg ? new Dictionary<string, bool>() : Diff();
        bool planned = diff.Count > 0 && !Dirty();
        if (!msg && diff.Count > 0)
            third = (planned ? "Запланировано: " : run ? "Запланировать: " : "Не применено: ") + "**" + Theme.Count(diff.Count, "изменение", "изменения", "изменений") + "**" + (planned ? "" : " · Ctrl+S");
        var acts = msg ? _status.Actions.ToList() : new List<(string Text, Action Act)>();
        int ActW(string t) => Theme.Measure(t, Theme.SmallBold).Width + P(14);
        int actsW = acts.Sum(a => ActW(a.Text) + P(6));
        int thirdW = msg ? P(18) + Theme.Measure(_status.Message, font).Width + (acts.Count > 0 ? P(4) + actsW : 0)
            : third.Length > 0 ? Theme.Rich(null, third, font, new Rectangle(0, 0, 10000, row.Height), Theme.NavySoft, s) : 0;

        // подсказки справа — если всё помещается
        int right = W - P(11);
        int hw = Theme.Measure(BarHints, font).Width;
        int need = x + ew + (thirdW > 0 ? P(16) + thirdW : 0) + P(24) + hw;
        int limit = right;
        if (need <= right)
        {
            Theme.Text(g, BarHints, font, new Rectangle(right - hw - P(2), row.Y, hw + P(2), row.Height), Theme.Hint, TextFormatFlags.Right);
            limit = right - hw - P(24);
        }

        int ex = Math.Min(ew, Math.Max(0, limit - x));
        Theme.Rich(g, en, font, new Rectangle(x, row.Y, ex + P(2), row.Height), Theme.NavySoft, s, Theme.Navy);
        if (all > 0) v.Zone(new Rectangle(x, row.Y, ex, row.Height), "on", null, "Отмечено модов: " + _m.OnCount + " из " + all + ". Пробел — включить или выключить выбранный", null, false);
        x += ex + P(16);
        if (thirdW == 0 || x >= limit - P(20)) return;

        if (msg)
        {
            var tone = _status.Tone;
            int isz = P(12);
            Theme.Icon(g, ToneIcon(tone), new RectangleF(x, cy - isz / 2f, isz, isz), ToneColor(tone));
            x += isz + P(6);
            int mw = Math.Max(0, Math.Min(Theme.Measure(_status.Message, font).Width + P(2), limit - x - (acts.Count > 0 ? P(4) + actsW : 0)));
            Theme.Text(g, _status.Message, font, new Rectangle(x, row.Y, mw, row.Height), Theme.Navy);
            if (mw < Theme.Measure(_status.Message, font).Width) v.Zone(new Rectangle(x, row.Y, mw, row.Height), "msg", null, _status.Message, null, false);
            x += mw + P(4);
            for (int i = 0; i < acts.Count; i++)
            {
                int aw = ActW(acts[i].Text);
                if (x + aw > limit) break;
                var ar = new Rectangle(x, row.Y + (row.Height - P(18)) / 2, aw, P(18));
                string key = "act:" + i;
                bool hot = v.Hot(key), dn = v.Pressed(key);
                Theme.FillRound(g, ar, P(4), dn ? Theme.BeigeDark : hot ? Theme.Beige : Theme.Alpha(Theme.Beige, 0));
                Theme.StrokeRound(g, ar, P(4), hot ? Theme.Mix(Theme.LineDark, Theme.NavySoft, 0.3f) : Theme.LineDark, Math.Max(1f, s));
                Theme.Text(g, acts[i].Text, Theme.SmallBold, ar, Theme.Navy, TextFormatFlags.HorizontalCenter);
                var act = acts[i].Act;
                v.Zone(ar, key, () => act());
                x += aw + P(6);
            }
            return;
        }
        int tw = Math.Min(thirdW, limit - x);
        Theme.Rich(g, third, font, new Rectangle(x, row.Y, tw + P(2), row.Height), Theme.NavySoft, s, Theme.Navy);
        v.Zone(new Rectangle(x, row.Y, tw, row.Height), "diff", () => ShowDockTab(DockTab.Changes),
            (planned ? "Запланировано: " : "Изменения: ") + DiffText(diff) + "\nЩелчок — вкладка «Изменения»" + (planned ? "" : "; Ctrl+S — " + (run ? "запланировать" : "применить")));
    }

    // ───────── меню «Команды», «Играть ▾», «Настройки» ─────────

    /// Открыть меню: у пункта боковой панели (если его вызвали оттуда), в «Компактном» — под «Командами»,
    /// в остальных стилях — над кнопкой classic в подвале.
    private void ShowMenu(ContextMenuStrip menu, Control classic)
    {
        if (_menuAnchor is { } a) { _menuAnchor = null; menu.Show(a.C, a.P, ToolStripDropDownDirection.BelowRight); }
        else if (Flat) menu.Show(_cmd, new Point(_cmd.Width, _cmd.Height + _root.Px(4)), ToolStripDropDownDirection.BelowLeft);
        else menu.Show(classic, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
    }

    private void CommandsMenu()
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        bool run = _m.GameRunning;
        int changes = _m.ChangeCount;
        bool plan = PendingDiff().Count > 0;
        Menus.Add(menu, run ? "Запланировать изменения" : "Применить изменения", run ? Theme.GClock : Theme.GSave, () => Commit(false), run ? Dirty() : changes > 0, null, "Ctrl+S");
        Menus.Add(menu, "Сбросить отметки", Theme.GUndo, ResetChanges, changes > 0 || plan);
        if (plan) Menus.Add(menu, "Отменить план", Theme.GCancel, CancelPlan);
        Menus.Add(menu, run ? "Игра запущена" : changes > 0 ? "Применить и играть" : "Играть", Theme.GPlay, () => Commit(true), !run, null, "Ctrl+Enter");
        Menus.Separator(menu);
        Menus.Add(menu, "Включить все", Theme.GCheck, () => SetAll(true), _m.Mods.Count > 0);
        Menus.Add(menu, "Выключить все", Theme.GCancel, () => SetAll(false), _m.Mods.Count > 0);
        Menus.Separator(menu);
        AddMoreItems(menu);
        _menuAnchor = null;
        ShowMenu(menu, _more);
    }

    private void LaunchMenu()
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        bool run = _m.GameRunning, exe = _m.Cfg.LaunchVia == "exe";
        Menus.Add(menu, "Запускать через Steam", exe ? "" : Theme.GCheck, () => SetLaunchVia(false));
        Menus.Add(menu, "Запускать напрямую (SlimeRancher2.exe)", exe ? Theme.GCheck : "", () => SetLaunchVia(true));
        Menus.Separator(menu);
        Menus.Add(menu, run ? "Запланировать без запуска" : "Применить без запуска", run ? Theme.GClock : Theme.GSave, () => Commit(false),
            run ? Dirty() : _m.ChangeCount > 0, null, "Ctrl+S");
        Menus.Add(menu, "Сбросить отметки", Theme.GUndo, ResetChanges, _m.ChangeCount > 0 || PendingDiff().Count > 0);
        if (PendingDiff().Count > 0) Menus.Add(menu, "Отменить план", Theme.GCancel, CancelPlan);
        menu.Show(_split, new Point(_split.Width, _split.Height + _root.Px(4)), ToolStripDropDownDirection.BelowLeft);
    }

    private void SetLaunchVia(bool exe)
    {
        _m.Cfg.LaunchVia = exe ? "exe" : "steam";
        _launchVia.Selected = exe ? 1 : 0;
        SaveCfg();
        UpdateFooter();
        Toast(Tone.Ok, exe ? "«Играть» запускает SlimeRancher2.exe из папки игры." : "«Играть» запускает игру через Steam.");
    }

    private void SettingsMenu()
    {
        _menu?.Dispose();
        var menu = _menu = Menus.Create(this);
        Menus.Add(menu, "Дизайн окна: «" + Theme.Spec.Name + "»…", Theme.GPalette, () => BeginInvoke(new Action(() => OpenDesignPicker())));
        Menus.Add(menu, "Следующий дизайн", Theme.GSync, NextDesign, true, null, "Ctrl+Shift+D");
        Menus.Add(menu, _dock.Collapsed ? "Развернуть нижнюю панель" : "Свернуть нижнюю панель", _dock.Collapsed ? Theme.GChevronUp : Theme.GChevronDown,
            () => SetDockCollapsed(!_dock.Collapsed), true, null, "Ctrl+J");
        Menus.Separator(menu);
        Menus.Add(menu, "MelonPreferences.cfg — настройки и клавиши", Theme.GSettings, () => Report(Shell.Open(_store.PreferencesPath)), File.Exists(_store.PreferencesPath));
        Menus.Add(menu, "Наборы и настройки менеджера (profiles.json)", Theme.GDoc, () => Report(Shell.Reveal(_store.ConfigPath)), File.Exists(_store.ConfigPath));
        Menus.Add(menu, "Настройки модов (UserData)", Theme.GFolder, () => Report(Shell.OpenFolder(_store.UserDataDir)));
        Menus.Separator(menu);
        Menus.Add(menu, "Папка Mods", Theme.GFolder, () => Report(Shell.OpenFolder(_store.ModsDir)));
        Menus.Add(menu, "Библиотека модов (ModManager\\Library)", Theme.GFolder, () => Report(Shell.OpenFolder(_store.LibraryDir)));
        Menus.Add(menu, "Папка игры", Theme.GFolder, () => Report(Shell.OpenFolder(_store.GameDir)));
        Menus.Separator(menu);
        Menus.Add(menu, "О менеджере и горячие клавиши", Theme.GInfo, About);
        ShowMenu(menu, _more);
    }
}
