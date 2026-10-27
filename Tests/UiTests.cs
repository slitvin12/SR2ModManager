// UiTests.cs — настройки вида (ui.json), сортировка таблицы модов по колонкам и разбор журнала MelonLoader
// для вкладки «Логи» (Shared\UiPrefs.cs). Всё пишется только во временной папке теста.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SR2ModManager.Core;

static class UiTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        Console.WriteLine("── настройки вида (ui.json)");
        Prefs(root, check);
        Console.WriteLine("── сортировка таблицы модов");
        Sorting(check);
        Console.WriteLine("── журнал MelonLoader для вкладки «Логи»");
        LogTail(root, check);
    }

    // ───────────── ui.json ─────────────

    static void Prefs(string root, Action<bool, string> check)
    {
        string dir = Path.Combine(root, "ui-prefs");
        Directory.CreateDirectory(dir);
        string path = UiPrefs.PathIn(dir);
        check(Path.GetFileName(path) == "ui.json" && Path.GetDirectoryName(path) == dir, "ui.json — рядом с profiles.json (в папке настроек менеджера)");

        var d = UiPrefs.Load(path);
        check(!File.Exists(path) && d.DesignKind == DesignKind.Compact && d.SortColumn == ModColumn.None && d.DockTab == DockTab.Details
              && !d.DockCollapsed && d.DockHeight == 0, "нет ui.json — «Компактный», без сортировки, вкладка «Детали», файл не создаётся");

        var p = new UiPrefs
        {
            DesignKind = DesignKind.Slime, SortColumn = ModColumn.Version, SortDesc = true, DockTab = DockTab.Logs,
            DockHeight = 260, DockCollapsed = true, LogErrorsOnly = true, LogSelectedOnly = true, HooksSelectedOnly = true,
        };
        p.Save(path);
        var r = UiPrefs.Load(path);
        check(r.DesignKind == DesignKind.Slime && r.SortColumn == ModColumn.Version && r.SortDesc && r.DockTab == DockTab.Logs
              && r.DockHeight == 260 && r.DockCollapsed && r.LogErrorsOnly && r.LogSelectedOnly && r.HooksSelectedOnly,
            "ui.json: сохранение и чтение — все поля на месте");
        string json = File.ReadAllText(path);
        check(json.Contains("\"design\": \"slime\"") && json.Contains("\"sort\": \"version\"") && json.Contains("\"dockTab\": \"logs\""),
            "ui.json: понятные ключи (design, sort, dockTab) и значения словами");
        check(!File.Exists(path + ".tmp") && Directory.GetFiles(dir).Length == 1, "ui.json: запись атомарная — временный файл не остаётся");

        p.DesignKind = DesignKind.Fluent;
        p.Save(path);
        check(UiPrefs.Load(path).DesignKind == DesignKind.Fluent, "ui.json: перезапись существующего файла");

        File.WriteAllText(path, "{ \"design\": \"slime\", \"dockTab\": ");
        var broken = UiPrefs.Load(path);
        check(broken.DesignKind == DesignKind.Compact && broken.DockTab == DockTab.Details, "битый ui.json — значения по умолчанию, без исключения");

        File.WriteAllText(path, "// свой комментарий\n{ \"Design\": \"Слаймовый\", \"sort\": \"нет-такой\", \"sortDesc\": true, \"dockTab\": \"хуки\", \"dockHeight\": 99999, }");
        var odd = UiPrefs.Load(path);
        check(odd.DesignKind == DesignKind.Slime && odd.DockTab == DockTab.Hooks, "ui.json: регистр ключей, комментарии, лишняя запятая и русские названия");
        check(odd.SortColumn == ModColumn.None && !odd.SortDesc && odd.DockHeight == 4000 && odd.Sort == "" && odd.Design == "slime",
            "ui.json: неизвестная колонка — без сортировки, высота панели ограничена, значения приведены к ключам");

        File.WriteAllText(path, "{ \"design\": \"какой-то\", \"dockTab\": 5 }");
        check(UiPrefs.Load(path).DesignKind == DesignKind.Compact, "ui.json: неизвестный стиль или неверный тип — по умолчанию");

        check(UiPrefs.ParseDesign("fluent") == DesignKind.Fluent && UiPrefs.ParseDesign(" A ") == DesignKind.Fluent && UiPrefs.ParseDesign("b") == DesignKind.Slime
              && UiPrefs.ParseDesign("Компактный") == DesignKind.Compact && UiPrefs.ParseDesign("2") == DesignKind.Slime && UiPrefs.ParseDesign("x") == null && UiPrefs.ParseDesign(null) == null,
            "названия стилей: по-английски, по-русски, буквой варианта и числом");
        check(UiPrefs.ParseColumn("Compat") == ModColumn.Compat && UiPrefs.ParseColumn("size") == ModColumn.Size && UiPrefs.ParseColumn("") == ModColumn.None
              && UiPrefs.ParseColumn("42") == ModColumn.None && UiPrefs.ParseDock("Логи") == DockTab.Logs && UiPrefs.ParseDock("harmony") == DockTab.Hooks && UiPrefs.ParseDock("?") == null,
            "названия колонок и вкладок");

        var c = p.Clone();
        c.DesignKind = DesignKind.Compact;
        check(p.DesignKind == DesignKind.Fluent, "Clone — независимая копия");
    }

    // ───────────── сортировка ─────────────

    static ModEntry M(string id, string version = "1.0.0", string author = "a", long size = 1000, bool avail = false, string? name = null) => new()
    {
        Id = id, FileName = id + ".dll", Path = @"C:\fake\Mods\" + id + ".dll", Enabled = true, Readable = true,
        AssemblyName = id, Name = name ?? id, Version = version, Author = author, Kind = "Mod", Size = size, Available = avail,
    };

    static string Ids(IEnumerable<ModEntry> l) => string.Join(",", l.Select(m => m.Id));

    static void Sorting(Action<bool, string> check)
    {
        // щелчки по заголовку
        var s1 = ModSort.Next(ModColumn.None, false, ModColumn.Name);
        var s2 = ModSort.Next(s1.Column, s1.Desc, ModColumn.Name);
        var s3 = ModSort.Next(s2.Column, s2.Desc, ModColumn.Name);
        check(s1 == (ModColumn.Name, false) && s2 == (ModColumn.Name, true) && s3 == (ModColumn.None, false), "заголовок «Мод»: А→Я, Я→А, без сортировки");
        var z1 = ModSort.Next(ModColumn.Name, false, ModColumn.Size);
        var z2 = ModSort.Next(z1.Column, z1.Desc, ModColumn.Size);
        var z3 = ModSort.Next(z2.Column, z2.Desc, ModColumn.Size);
        check(z1 == (ModColumn.Size, true) && z2 == (ModColumn.Size, false) && z3 == (ModColumn.None, false), "заголовок «Размер»: сначала крупные, потом мелкие, потом без сортировки");
        check(ModSort.DefaultDesc(ModColumn.Compat) && ModSort.DefaultDesc(ModColumn.Update) && ModSort.DefaultDesc(ModColumn.Hooks) && !ModSort.DefaultDesc(ModColumn.Author),
            "направление по умолчанию: проблемы, обновления и хуки — сначала важное");

        // версии
        check(ModSort.CompareVersions("1.10.0", "1.9.3") > 0 && ModSort.CompareVersions("v2.0", "1.99") > 0 && ModSort.CompareVersions("1.0", "1.0.0") < 0
              && ModSort.CompareVersions("1.0.1", "1.0.beta") > 0 && ModSort.CompareVersions("01.2", "1.2") == 0 && ModSort.CompareVersions("1.0.0-beta2", "1.0.0-beta10") < 0,
            "сравнение версий: числа как числа, «v» в начале, хвосты beta");

        var view = new List<ModEntry>
        {
            M("Zeta", "1.10.0", "Bob", 500), M("alpha", "1.9.0", "", 3000), M("Beta", "", "anna", 1500), M("Gamma", "2.0", "Carl", 1500),
            M("Lib", "0.5", "Zed", 100, avail: true), M("Amber", "3.0", "Ann", 9000, avail: true),
        };
        var byName = ModSort.Apply(view, ModColumn.Name, false);
        check(Ids(byName) == "alpha,Beta,Gamma,Zeta,Amber,Lib", "по названию: без учёта регистра, доступные — своей группой ниже установленных");
        check(Ids(ModSort.Apply(view, ModColumn.Name, true)) == "Zeta,Gamma,Beta,alpha,Lib,Amber", "по названию Я→А — группы на местах");
        check(Ids(ModSort.Apply(view, ModColumn.Version, false)) == "alpha,Zeta,Gamma,Beta,Lib,Amber"
              && Ids(ModSort.Apply(view, ModColumn.Version, true)) == "Gamma,Zeta,alpha,Beta,Amber,Lib",
            "по версии: 1.9 < 1.10 < 2.0, мод без версии — в конце при любом направлении");
        check(Ids(ModSort.Apply(view, ModColumn.Author, false)) == "Beta,Zeta,Gamma,alpha,Amber,Lib", "по автору: пустой автор — в конце");
        check(Ids(ModSort.Apply(view, ModColumn.Size, true)) == "alpha,Beta,Gamma,Zeta,Amber,Lib", "по размеру: равные — по названию (порядок не скачет)");
        check(ReferenceEquals(view[0], ModSort.Apply(view, ModColumn.None, false)[0]) && Ids(ModSort.Apply(view, ModColumn.None, true)) == Ids(view)
              && Ids(view) == "Zeta,alpha,Beta,Gamma,Lib,Amber", "без сортировки — порядок каталога; исходный список не меняется");

        var on = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Gamma", "Zeta" };
        var compat = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Beta"] = 3, ["Zeta"] = 2, ["alpha"] = 1 };
        var hooks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Gamma"] = 12, ["Zeta"] = 3, ["Beta"] = -1 };
        var keys = new ModSortKeys
        {
            IsOn = m => on.Contains(m.Id),
            Compat = m => compat.TryGetValue(m.Id, out var v) ? v : 0,
            Hooks = m => hooks.TryGetValue(m.Id, out var v) ? v : 0,
            Update = m => m.Id == "alpha" ? 2 : m.Id == "Gamma" ? 1 : 0,
        };
        check(Ids(ModSort.Apply(view, ModColumn.Compat, true, keys)) == "Beta,Zeta,alpha,Gamma,Amber,Lib", "по совместимости: сначала ошибки, потом предупреждения");
        check(Ids(ModSort.Apply(view, ModColumn.Check, true, keys)) == "Gamma,Zeta,alpha,Beta,Amber,Lib", "по отметке: включённые сверху");
        check(Ids(ModSort.Apply(view, ModColumn.Hooks, true, keys)) == "Gamma,Zeta,alpha,Beta,Amber,Lib", "по хукам Harmony: больше патчей — выше, без Harmony — ниже всех");
        check(Ids(ModSort.Apply(view, ModColumn.Update, true, keys)) == "alpha,Gamma,Beta,Zeta,Amber,Lib", "по обновлениям: новая версия, затем «устарел»");
        check(ModSort.Apply(new List<ModEntry>(), ModColumn.Name, false).Count == 0 && ModSort.Apply(new[] { M("x") }, ModColumn.Size, true).Count == 1,
            "сортировка пустого списка и одного мода");
    }

    // ───────────── журнал ─────────────

    static void LogTail(string root, Action<bool, string> check)
    {
        var a = MelonLogTail.Parse("[12:34:56.789] [Creator_Toolkit] Loaded 12 items");
        check(!a.Continuation && a.Time == "12:34:56.789" && a.Section == "Creator_Toolkit" && a.Text == "Loaded 12 items" && a.Level == LogLevel.Info,
            "строка журнала: время, раздел, текст");
        var e = MelonLogTail.Parse("[12:34:57.001] [ERROR] [SlimeLive] Failed to patch method");
        var w = MelonLogTail.Parse("[12:34:57.100] [MelonLoader] [WARNING] Some Melons are missing dependencies");
        check(e.Level == LogLevel.Error && e.Section == "SlimeLive" && e.Text == "Failed to patch method"
              && w.Level == LogLevel.Warning && w.Section == "MelonLoader", "уровень [ERROR] / [WARNING] — до или после раздела");
        var plain = MelonLogTail.Parse("[12:34:58.000] Plain message [not a section");
        check(!plain.Continuation && plain.Section == "" && plain.Text == "Plain message [not a section", "строка без раздела");
        var cont = MelonLogTail.Parse("  at SlimeLive.Patch.Prefix () [0x00000] in <abc>:0");
        check(cont.Continuation && cont.Time == "", "стек вызовов — продолжение сообщения");

        var all = MelonLogTail.ParseAll(new[]
        {
            "[10:00:00.000] [MelonLoader] Loading Mods...",
            "[10:00:01.000] [ERROR] [SlimeLive] System.NullReferenceException",
            "  at SlimeLive.Main.OnUpdate ()",
            "",
            "   ",
            "[10:00:02.000] [Creator_Toolkit] ok",
        });
        check(all.Count == 4 && all[2].Continuation && all[2].Level == LogLevel.Error && all[2].Section == "SlimeLive" && all[3].Level == LogLevel.Info,
            "продолжения получают уровень и раздел своего сообщения, пустые строки пропускаются");

        var mod = M("SlimeLive", name: "Slime Live");
        mod.AssemblyName = "SlimeLiveAsm";
        check(MelonLogTail.Mentions(MelonLogTail.Parse("[10:00:00.000] [Slime_Live] hi"), mod)
              && MelonLogTail.Mentions(MelonLogTail.Parse("[10:00:00.000] [slimelive] hi"), mod)
              && MelonLogTail.Mentions(MelonLogTail.Parse("[10:00:00.000] [SlimeLiveAsm] hi"), mod)
              && MelonLogTail.Mentions(MelonLogTail.Parse("[10:00:00.000] [MelonLoader] Loaded SlimeLive.dll"), mod)
              && !MelonLogTail.Mentions(MelonLogTail.Parse("[10:00:00.000] [Other] hello"), mod),
            "строка относится к моду: имя (с «_» вместо пробелов), Id, имя сборки или файл");

        string dir = Path.Combine(root, "ui-log");
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "Latest.log");
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append("[10:00:00.000] [Test] line ").Append(i).Append("\r\n");
        File.WriteAllText(log, sb.ToString());
        List<string> tail;
        // игра держит журнал открытым на запись — чтение не должно мешать и не должно падать
        using (var writer = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            tail = MelonLogTail.ReadTail(log, 300);
        check(tail.Count == 300 && tail[^1] == "[10:00:00.000] [Test] line 4999" && tail[0] == "[10:00:00.000] [Test] line 4700",
            "хвост журнала: последние строки, пока файл открыт игрой на запись");
        var small = MelonLogTail.ReadTail(log, 3000, 4096);
        check(small.Count > 0 && small.Count < 200 && small.All(l => l.StartsWith("[10:00:00.000] [Test] line ")) && small[^1].EndsWith(" 4999"),
            "хвост журнала: ограничение по байтам, обрезанная первая строка отброшена");
        check(MelonLogTail.ReadTail(Path.Combine(dir, "нет-такого.log")).Count == 0, "нет журнала — пустой хвост без исключения");
        File.WriteAllText(log, "");
        check(MelonLogTail.ReadTail(log).Count == 0, "пустой журнал — пустой хвост");
    }
}
