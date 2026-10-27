// Тесты логики включения/выключения на временной копии папки игры.
// Запуск: dotnet run --project Tests -- [папка Mods для копирования реальных DLL] [папка для демо-каталога снимков]
// (оба аргумента необязательны; без реальных DLL тесты каталога на настоящих модах пропускаются)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SR2ModManager.Core;

if (args.Length >= 2 && args[0] == "--scan-bench")
{   // замер сканирования каталога: dotnet ModManager.Tests.dll --scan-bench <папка> [дней]
    var src = new CatalogSource(args[1], "bench", args.Length > 2 && int.TryParse(args[2], out int days) ? days : 0, 1);
    var swAll = System.Diagnostics.Stopwatch.StartNew();
    var files = CatalogScanner.Files(src);
    long tList = swAll.ElapsedMilliseconds;
    var rows = new List<(long Open, long Read, string Kind, double Mb, int Dlls, int Mods)>();
    foreach (var f in files)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int dlls = -1;
        if (f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            try { using var z = ZipFile.OpenRead(f.FullName); dlls = ModStore.ZipDllEntries(z).Count; } catch { dlls = -2; }
        long tOpen = sw.ElapsedMilliseconds; sw.Restart();
        var found = CatalogScanner.ReadFile(f);
        rows.Add((tOpen, sw.ElapsedMilliseconds, f.Extension.ToLowerInvariant(), f.Length / 1048576.0, dlls, found.Count));
    }
    Console.WriteLine($"files: {files.Count} ({rows.Sum(r => r.Mb):0} MB), list {tList} ms, total {swAll.ElapsedMilliseconds} ms, open+list {rows.Sum(r => r.Open)} ms, read {rows.Sum(r => r.Read)} ms, mods: {rows.Sum(r => r.Mods)}");
    foreach (var r in rows.OrderByDescending(r => r.Open + r.Read).Take(12))
        Console.WriteLine($"  open {r.Open,5} ms  read {r.Read,5} ms  {r.Kind}  {r.Mb,7:0.0} MB  dlls={r.Dlls}  mods={r.Mods}");
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    foreach (var f in files) CatalogScanner.ReadFile(f);
    Console.WriteLine($"повторно (кэш): {sw2.ElapsedMilliseconds} ms");
    return 0;
}

int pass = 0, fail = 0;
void Check(bool ok, string what) { if (ok) { pass++; Console.WriteLine("PASS " + what); } else { fail++; Console.WriteLine("FAIL " + what); } }

string root = Path.Combine(Path.GetTempPath(), "sr2mm-test-" + Guid.NewGuid().ToString("N")[..8]);
string game = Path.Combine(root, "Slime Rancher 2");
string mods = Path.Combine(game, "Mods");
Directory.CreateDirectory(mods);
File.WriteAllText(Path.Combine(game, "SlimeRancher2.exe"), "fake");
string? realMods = args.Length > 0 ? args[0] : null;
try
{
    var ids = new List<string>();
    if (realMods != null && Directory.Exists(realMods))
    {
        // включённые (Mods) и выключенные (Mods\Disabled) — все кладём в корень тестовой Mods
        var srcDirs = new List<string> { realMods };
        if (Directory.Exists(Path.Combine(realMods, "Disabled"))) srcDirs.Add(Path.Combine(realMods, "Disabled"));
        foreach (var dir in srcDirs)
            foreach (var f in Directory.GetFiles(dir, "*.dll"))
            {
                string to = Path.Combine(mods, Path.GetFileName(f));
                if (File.Exists(to)) continue;
                File.Copy(f, to); ids.Add(Path.GetFileNameWithoutExtension(f));
            }
        foreach (var f in Directory.GetFiles(realMods, "*.bak*")) File.Copy(f, Path.Combine(mods, Path.GetFileName(f)));
        Console.WriteLine("Скопировано реальных DLL: " + ids.Count);
    }
    else
    {
        foreach (var n in new[] { "CreatorToolkit", "SlimeLive", "MinecraftHUD", "CreatorToolkitReviewDirector" })
        { File.WriteAllText(Path.Combine(mods, n + ".dll"), "not a PE file " + n); ids.Add(n); }
        File.WriteAllText(Path.Combine(mods, "SlimeLive.dll.bak"), "backup");
    }

    var store = new ModStore(game);
    var list = store.Scan();
    Check(list.Count == ids.Count, $"scan видит {ids.Count} модов (*.bak игнорируются): {list.Count}");
    Check(list.All(m => m.Enabled), "все изначально включены");
    foreach (var m in list) Console.WriteLine($"  {m.Id}: name='{m.Name}' ver={m.Version} author='{m.Author}' kind={m.Kind} readable={m.Readable} deps=[{string.Join(",", m.DependsOn)}]");
    var sl = list.First(m => m.Id == "SlimeLive");
    Check(sl.DependsOn.Contains("CreatorToolkit"), "SlimeLive зависит от CreatorToolkit");
    if (realMods != null)
    {
        Check(list.All(m => m.Readable), "метаданные всех реальных DLL прочитаны");
        Check(sl.Name == "SlimeLive" && sl.Version.StartsWith("0."), "MelonInfo SlimeLive прочитан: " + sl.Version);
        var ct = list.First(m => m.Id == "CreatorToolkit");
        Check(ct.Name == "Creator Toolkit", "MelonInfo CreatorToolkit: " + ct.Name + " " + ct.Version);
    }
    else Check(list.All(m => !m.Readable), "битые DLL не роняют сканирование");

    // 1. выключить SlimeLive
    var r = store.Apply(new Dictionary<string, bool> { ["SlimeLive"] = false });
    Check(r.Ok && r.Moved == 1, "выключение SlimeLive: " + r);
    Check(File.Exists(Path.Combine(mods, "Disabled", "SlimeLive.dll")) && !File.Exists(Path.Combine(mods, "SlimeLive.dll")), "файл в Mods\\Disabled");
    Check(File.Exists(Path.Combine(mods, "SlimeLive.dll.bak")), ".bak не тронут");
    list = store.Scan();
    Check(!list.First(m => m.Id == "SlimeLive").Enabled, "scan: SlimeLive выключен");

    // 2. повтор — идемпотентность
    r = store.Apply(new Dictionary<string, bool> { ["SlimeLive"] = false });
    Check(r.Ok && r.Moved == 0, "повторное выключение ничего не двигает");

    // 3. зависимости: SlimeLive включён, CreatorToolkit выключен
    var cfg = store.LoadConfig();
    var sel = list.ToDictionary(m => m.Id, m => true, StringComparer.OrdinalIgnoreCase);
    sel["CreatorToolkit"] = false;
    var warn = ModStore.CheckSelection(list, sel, cfg);
    Check(warn.Any(w => w.Contains("SlimeLive") || w.Contains("выключен")), "предупреждение о зависимости: " + string.Join(" | ", warn));
    var added = ModStore.AddMissingDependencies(list, sel);
    Check(added.Contains("CreatorToolkit") && sel["CreatorToolkit"], "авто-включение зависимости");
    Check(ModStore.CheckSelection(list, sel, cfg).Count == 0, "после авто-включения предупреждений нет");

    // 4. профили
    var vanilla = cfg.Profiles.First(p => p.Name.StartsWith("Ваниль"));
    r = store.Apply(ModStore.SelectionForProfile(list, vanilla));
    Check(r.Ok && store.Scan().All(m => !m.Enabled), "профиль «Ваниль» выключает всё");
    Check(Directory.GetFiles(mods, "*.dll").Length == 0, "в корне Mods нет DLL");
    var stream = cfg.Profiles.First(p => p.Name.StartsWith("Стрим"));
    r = store.Apply(ModStore.SelectionForProfile(store.Scan(), stream));
    var on = store.Scan().Where(m => m.Enabled).Select(m => m.Id).OrderBy(x => x).ToList();
    Check(r.Ok && on.SequenceEqual(new[] { "CreatorToolkit", "SlimeLive" }), "профиль «Стрим»: " + string.Join(",", on));
    var all = cfg.Profiles.First(p => p.All);
    r = store.Apply(ModStore.SelectionForProfile(store.Scan(), all));
    Check(r.Ok && store.Scan().All(m => m.Enabled), "профиль «Все моды» включает всё");

    // 5. дубликат: новая сборка установлена в Mods, а старая лежит в Disabled
    store.Apply(new Dictionary<string, bool> { ["MinecraftHUD"] = false });
    File.WriteAllText(Path.Combine(mods, "MinecraftHUD.dll"), "new build");
    list = store.Scan();
    var mh = list.First(m => m.Id == "MinecraftHUD");
    Check(mh.Enabled && mh.DuplicateInDisabled && list.Count(m => m.Id == "MinecraftHUD") == 1, "дубликат: видна копия из Mods, флаг DuplicateInDisabled");
    r = store.Apply(new Dictionary<string, bool> { ["MinecraftHUD"] = false });
    Check(r.Ok && File.ReadAllText(Path.Combine(mods, "Disabled", "MinecraftHUD.dll")) == "new build", "при выключении новая копия заменяет старую");
    Check(Directory.GetFiles(Path.Combine(mods, "Disabled", "_replaced")).Length == 1, "старая копия сохранена в _replaced, ничего не удалено");
    r = store.Apply(new Dictionary<string, bool> { ["MinecraftHUD"] = true });
    Check(r.Ok, "включение обратно");

    // 6. pending (как пишет плагин в игре)
    store.SavePending(new PendingChanges { Source = "test", Desired = new() { ["SlimeLive"] = false, ["Unknown"] = true } });
    Check(store.LoadPending()?.Desired.Count == 2, "pending.json записан и прочитан");
    var pr = store.ApplyPending();
    Check(pr != null && pr.Ok && !File.Exists(store.PendingPath), "pending применён и удалён: " + pr);
    Check(!store.Scan().First(m => m.Id == "SlimeLive").Enabled, "pending выключил SlimeLive");
    Check(store.ApplyPending() == null, "без pending — ничего не делается");

    // 7. ошибка переноса (файл заблокирован) -> pending сохраняется
    store.SavePending(new PendingChanges { Desired = new() { ["SlimeLive"] = true } });
    string lockedPath = Path.Combine(mods, "Disabled", "SlimeLive.dll");
    if (OperatingSystem.IsWindows())
    {
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            pr = store.ApplyPending();
            Check(pr != null && !pr.Ok && File.Exists(store.PendingPath), "заблокированный файл: ошибка, pending сохранён");
        }
    }
    pr = store.ApplyPending();
    Check(pr != null && pr.Ok && store.Scan().All(m => m.Enabled), "после разблокировки pending применён, всё снова включено");

    // 8. конфиг
    store.EnsureConfig();
    var c2 = store.LoadConfig();
    c2.Profiles.Add(new Profile { Name = "Мой", Mods = new() { "MinecraftHUD" } });
    store.SaveConfig(c2);
    Check(store.LoadConfig().Profiles.Any(p => p.Name == "Мой"), "сохранение своего профиля (кириллица)");
    Check(File.ReadAllText(store.ConfigPath).Contains("Мой"), "JSON читаемый (без \\u-экранирования)");
    Check(File.Exists(store.LogPath), "лог действий пишется");

    // итог: тот же набор, что был
    var finalOn = store.Scan().Where(m => m.Enabled).Select(m => m.Id).OrderBy(x => x);
    Check(finalOn.SequenceEqual(ids.OrderBy(x => x)), "в конце включены те же моды, что в начале");

    // 9. какой профиль выбран сейчас
    list = store.Scan();
    var selAll = list.ToDictionary(m => m.Id, m => true, StringComparer.OrdinalIgnoreCase);
    Check(ModStore.FindMatchingProfile(list, selAll, cfg, "Все моды")?.All == true, "FindMatchingProfile: «Все моды»");
    var selNone = list.ToDictionary(m => m.Id, m => false, StringComparer.OrdinalIgnoreCase);
    Check(ModStore.FindMatchingProfile(list, selNone, cfg)?.Name.StartsWith("Ваниль") == true, "FindMatchingProfile: «Ваниль»");
    var selOdd = list.ToDictionary(m => m.Id, m => m.Id == "SlimeLive", StringComparer.OrdinalIgnoreCase);
    Check(ModStore.FindMatchingProfile(list, selOdd, cfg) == null, "FindMatchingProfile: SlimeLive без CreatorToolkit — ни один профиль");

    // 10. журнал MelonLoader
    var logMods = new List<ModEntry>
    {
        new() { Id = "CreatorToolkit", FileName = "CreatorToolkit.dll", Name = "Creator Toolkit", AssemblyName = "CreatorToolkit" },
        new() { Id = "SlimeLive", FileName = "SlimeLive.dll", Name = "SlimeLive", AssemblyName = "SlimeLive" },
    };
    var logLines = new[]
    {
        "[16:54:21.349] MelonLoader v0.7.3 Open-Beta\r",
        "[16:54:21.360] Game Version: 1.3.0 [abc]",
        "[16:54:26.352] Melon Assembly loaded: '.\\Mods\\CreatorToolkit.dll'",
        "[16:54:28.834] [WARNING] [Il2CppInterop] Class::Init signatures have been exhausted, using a substitute!",
        "[10:58:33.923] [WARNING] [Creator_Toolkit] HUD / map overlay: CultureNotFoundException",
        "[10:58:34.000] [ERROR] [Creator_Toolkit] Boom",
        "   at Some.Stack.Trace()",
        "[10:58:35.000] [Creator_Toolkit] [ERROR] Boom 2",
        "[10:58:36.000] [ERROR] Failed to load Melon Assembly from '.\\Mods\\SlimeLive.dll'",
        "[10:58:37.000] [SlimeLive] обычное сообщение",
    };
    var li = ModStore.ParseMelonLog(logLines, logMods);
    Check(li.LoaderVersion == "0.7.3 Open-Beta" && li.GameVersion == "1.3.0", $"лог: версии ML={li.LoaderVersion} game={li.GameVersion}");
    Check(li.WasLoaded("CreatorToolkit") && !li.WasLoaded("SlimeLive"), "лог: загруженные DLL");
    Check(li.For("CreatorToolkit")?.Errors == 2 && li.For("CreatorToolkit")?.Warnings == 1 && li.For("CreatorToolkit")!.LastError == "Boom 2", "лог: ошибки/предупреждения Creator Toolkit");
    Check(li.For("SlimeLive")?.Errors == 1, "лог: ошибка загрузки привязана к SlimeLive по имени файла");
    Check(li.Other.Warnings == 1 && li.Other.Errors == 0 && li.TotalErrors == 3, "лог: Il2CppInterop — в «прочих»");

    // 11. установка: .NET-библиотека без MelonInfo → UserLibs
    string selfDll = typeof(ModEntry).Assembly.Location;
    var ir = store.Install(new[] { selfDll });
    Check(ir.Ok && File.Exists(Path.Combine(game, "UserLibs", Path.GetFileName(selfDll))), "установка обычной .NET DLL → UserLibs: " + ir.Summary());
    ir = store.Install(new[] { selfDll });
    ir = store.Install(new[] { selfDll }); // два раза подряд в ту же секунду — имена копий не конфликтуют
    Check(ir.Ok && Directory.GetFiles(store.ReplacedDir, Path.GetFileName(selfDll) + ".*").Length == 2, "повторная установка: старые копии в _replaced без конфликтов имён");
    string junk = Path.Combine(root, "junk.dll"); File.WriteAllText(junk, "not a dll");
    ir = store.Install(new[] { junk, Path.Combine(root, "notes.txt") });
    Check(!ir.Ok && ir.Errors.Count == 2 && !File.Exists(Path.Combine(mods, "junk.dll")), "не-.NET DLL и .txt не ставятся: " + string.Join(" | ", ir.Errors));

    // 12. установка из .zip
    string zipPath = Path.Combine(root, "ZipMod-1.0.zip");
    using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        void Put(string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
        Put("ZipMod-1.0/Mods/ZipMod.dll", "zip v1");
        Put("ZipMod-1.0/UserData/ZipMod/config.cfg", "default");
        Put("ZipMod-1.0/README.txt", "readme");
        Put("ZipMod-1.0/UserData/../../evil.txt", "evil");
    }
    ir = store.Install(new[] { zipPath });
    Check(File.ReadAllText(Path.Combine(mods, "ZipMod.dll")) == "zip v1", "zip: мод из Mods/ установлен (общая папка архива отброшена)");
    Check(File.ReadAllText(Path.Combine(game, "UserData", "ZipMod", "config.cfg")) == "default", "zip: UserData распакован");
    Check(!File.Exists(Path.Combine(root, "evil.txt")) && !File.Exists(Path.Combine(game, "evil.txt")) && ir.Errors.Count == 1, "zip: путь с .. отклонён: " + string.Join(" | ", ir.Errors));
    File.WriteAllText(Path.Combine(game, "UserData", "ZipMod", "config.cfg"), "mine");
    using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Update))
    {
        z.GetEntry("ZipMod-1.0/Mods/ZipMod.dll")!.Delete();
        z.GetEntry("ZipMod-1.0/UserData/../../evil.txt")!.Delete();
        using var w = new StreamWriter(z.CreateEntry("ZipMod-1.0/Mods/ZipMod.dll").Open()); w.Write("zip v2");
    }
    ir = store.Install(new[] { zipPath });
    Check(ir.Ok && File.ReadAllText(Path.Combine(mods, "ZipMod.dll")) == "zip v2", "zip: обновление мода: " + ir.Summary());
    Check(File.ReadAllText(Path.Combine(game, "UserData", "ZipMod", "config.cfg")) == "mine", "zip: существующие настройки не перезаписаны");

    // 13. старые копии и откат
    var zm = store.Scan().First(m => m.Id == "ZipMod");
    var backups = store.ListBackups(zm);
    Check(backups.Count == 1 && backups[0].Where == "_replaced", "ListBackups: найдена прошлая версия");
    var rr = store.RestoreBackup(zm, backups[0].Path);
    Check(rr.Ok && File.ReadAllText(Path.Combine(mods, "ZipMod.dll")) == "zip v1", "RestoreBackup: вернулась v1");
    var after = store.ListBackups(store.Scan().First(m => m.Id == "ZipMod"));
    Check(after.Count == 1 && File.ReadAllText(after[0].Path) == "zip v2", "после отката v2 тоже можно вернуть");
    File.WriteAllText(Path.Combine(mods, "ZipMod.dll.bak_old"), "user backup");
    Check(store.ListBackups(zm).Any(b => b.Where == "Mods" && b.FileName == "ZipMod.dll.bak_old"), "ListBackups видит пользовательские .bak рядом с модом");

    // 14. установка в выключенный мод сохраняет «выключен»
    store.Apply(new Dictionary<string, bool> { ["ZipMod"] = false });
    File.WriteAllText(Path.Combine(root, "ZipMod.dll"), "plain v3");
    using (var z = ZipFile.Open(Path.Combine(root, "v3.zip"), ZipArchiveMode.Create))
        z.CreateEntryFromFile(Path.Combine(root, "ZipMod.dll"), "Mods/ZipMod.dll");
    ir = store.Install(new[] { Path.Combine(root, "v3.zip") });
    Check(ir.Ok && File.ReadAllText(Path.Combine(mods, "Disabled", "ZipMod.dll")) == "plain v3" && !File.Exists(Path.Combine(mods, "ZipMod.dll")), "обновление выключенного мода остаётся выключенным");

    // 15. отложенное обновление плагина
    Directory.CreateDirectory(Path.GetDirectoryName(store.StagedPluginPath)!);
    File.Copy(selfDll, store.StagedPluginPath, true);
    if (!ModStore.IsGameRunning())
    {
        bool did = store.TryApplyStagedPluginUpdate(out var msg);
        Check(did && File.Exists(store.PluginPath) && !File.Exists(store.StagedPluginPath), "обновление плагина из ModManager\\update: " + msg);
    }
    else
    {
        bool did = store.TryApplyStagedPluginUpdate(out _);
        Check(!did && File.Exists(store.StagedPluginPath), "пока игра запущена, обновление плагина ждёт");
    }

    // 16. версии
    Check(VersionUtil.Compare("1.10.0", "1.9.2") > 0 && VersionUtil.Compare("v0.6", "0.6.0.0") == 0, "версии: 1.10.0 > 1.9.2, v0.6 == 0.6.0.0");
    Check(VersionUtil.Compare("1.0.0-beta", "1.0.0") < 0 && VersionUtil.Compare("0.5.0a", "0.5.0") > 0 && VersionUtil.Compare("1.0.0+build5", "1.0.0") == 0,
        "версии: beta < релиз < 0.5.0a, +сборка не влияет");
    Check(VersionUtil.IsNewer("0.7.0", "0.6.0") && !VersionUtil.IsNewer("", "1.0") && !VersionUtil.IsNewer("0.6.0", "0.6.0"), "IsNewer");
    Check(CatalogScanner.CleanId("SlimeLive (1)", "SlimeLive") == "SlimeLive" && CatalogScanner.CleanId("SlimeLive-0.7.0", "SlimeLive") == "SlimeLive"
          && CatalogScanner.CleanId("SlimeLive_v0.7", "SlimeLive") == "SlimeLive" && CatalogScanner.CleanId("SlimeLiveExtras", "SlimeLive") == "SlimeLiveExtras",
        "имя мода из файла: без « (1)» и версии в хвосте");

    // 17. поиск: раскладка, ё, порядок, подсветка
    ModEntry Fake(string id, string name, string author, string desc, string ver = "1.0.0") => new() { Id = id, FileName = id + ".dll", Name = name, Author = author, Description = desc, Version = ver, Readable = true, Kind = "Mod", Enabled = true };
    var ct0 = Fake("CreatorToolkit", "Creator Toolkit", "slitvin12", "Меню создателя: игрок, спавн, ресурсы, погода", "0.6.0");
    var sl0 = Fake("SlimeLive", "SlimeLive", "slitvin12", "Интерактив для TikTok LIVE: гонки слаймов", "0.5.1");
    var mc0 = Fake("MinecraftHUD", "Minecraft HUD", "Steve", "Живой Minecraft внутри SR2 (F7 — HUD)");
    var rb0 = Fake("Rainbow", "Rainbow", "Ann", "Makes every slime rainbow");
    var yo0 = Fake("Yolka", "Ёлочная игрушка", "Дед Мороз", "Новогодний декор");
    var pool = new List<ModEntry> { ct0, sl0, mc0, rb0, yo0 };
    List<string> Find(string text) => CatalogView.Build(pool, null, CatalogFilter.All, ModSearch.Parse(text), null, null, null).Select(m => m.Id).ToList();
    Check(Find("ckfqv").SequenceEqual(new[] { "SlimeLive" }), "поиск в другой раскладке: «ckfqv» → слайм: " + string.Join(",", Find("ckfqv")));
    Check(Find("ьштускфае").SequenceEqual(new[] { "MinecraftHUD" }), "поиск «ьштускфае» → minecraft");
    Check(Find("елочная").SequenceEqual(new[] { "Yolka" }) && Find("ЁЛКА").Count == 0 && Find("ёлочная").SequenceEqual(new[] { "Yolka" }), "ё = е");
    Check(Find("creator tool").SequenceEqual(new[] { "CreatorToolkit" }) && Find("tool creator").SequenceEqual(new[] { "CreatorToolkit" }), "несколько слов — все должны найтись, порядок не важен");
    var slime = Find("slime");
    Check(slime.Count == 2 && slime[0] == "SlimeLive" && slime[1] == "Rainbow", "сначала совпадение в названии, потом в описании: " + string.Join(",", slime));
    Check(Find("slitvin12").Count == 2 && Find("мороз").SequenceEqual(new[] { "Yolka" }), "поиск по автору");
    Check(Find("v0.6").SequenceEqual(new[] { "CreatorToolkit" }) && Find("CreatorToolkit.dll").SequenceEqual(new[] { "CreatorToolkit" }), "поиск по версии и имени файла");
    Check(Find("zzz").Count == 0 && Find("   ").Count == 5, "нет совпадений → пусто; пустой запрос → все");
    var hl = ModSearch.Highlights("Creator Toolkit", ModSearch.Parse("tool"));
    Check(hl.Count == 1 && hl[0] == (8, 4), "подсветка: «tool» в «Creator Toolkit»");
    var hl2 = ModSearch.Highlights("SlimeLive", ModSearch.Parse("ыдшьу"));
    Check(hl2.Count == 1 && hl2[0] == (0, 5), "подсветка совпадения в другой раскладке");
    var hl3 = ModSearch.Highlights("Creator", ModSearch.Parse("cre crea"));
    Check(hl3.Count == 1 && hl3[0] == (0, 4), "пересекающиеся совпадения склеиваются");
    Check(ModSearch.Normalize("Ёж-Крот (v1)").Length == "Ёж-Крот (v1)".Length, "нормализация не меняет длину (подсветка по исходному тексту)");

    // 18. скорость: 5000 модов, запрос набирается по букве — без подвисаний
    var rnd = new Random(7);
    string[] words = { "slime", "ranch", "toolkit", "map", "hud", "live", "погода", "слайм", "карта", "меню", "creator", "minecraft", "фермы", "rainbow", "gordo", "largo" };
    string W() => words[rnd.Next(words.Length)];
    var big = Enumerable.Range(0, 5000).Select(i => Fake("Mod" + i, W() + " " + W() + " " + i, "Author" + (i % 50), W() + " " + W() + " " + W() + " " + W() + " " + W())).ToList();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    CatalogView.Build(big, null, CatalogFilter.All, ModSearch.Parse("x"), null, null, null); // первый проход строит ключи
    long warm = sw.ElapsedMilliseconds;
    string typed = "creator toolkit меню";
    long worst = 0, sum = 0;
    for (int i = 1; i <= typed.Length; i++)
    {
        var t0 = sw.ElapsedTicks;
        CatalogView.Build(big, null, CatalogFilter.All, ModSearch.Parse(typed.Substring(0, i)), null, null, null);
        long ms = (sw.ElapsedTicks - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        worst = Math.Max(worst, ms); sum += ms;
    }
    Console.WriteLine($"  скорость поиска: ключи 5000 модов — {warm} мс, на нажатие: худшее {worst} мс, всего за {typed.Length} нажатий {sum} мс");
    Check(worst < 60, $"фильтрация 5000 модов на каждое нажатие быстрее 60 мс (худшее {worst} мс)");

    // 19. фильтры и обновления на синтетике
    var avail0 = new List<ModEntry> { Fake("NewMod", "New Mod", "Bob", "Brand new") };
    avail0[0].Available = true; avail0[0].Enabled = false;
    var offSel = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["CreatorToolkit"] = true, ["SlimeLive"] = false, ["MinecraftHUD"] = true, ["Rainbow"] = false, ["Yolka"] = true };
    Func<string, bool> isOn = id => offSel.TryGetValue(id, out var v) && v;
    var upd0 = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase) { ["SlimeLive"] = new UpdateInfo { Id = "SlimeLive", Version = "0.9.1", Kind = "catalog" } };
    Func<ModEntry, bool> hasUpd = m => upd0.ContainsKey(m.Id);
    int cAll = CatalogView.Count(pool, avail0, CatalogFilter.All, isOn, hasUpd, null), cInst = CatalogView.Count(pool, avail0, CatalogFilter.Installed, isOn, hasUpd, null),
        cUpd = CatalogView.Count(pool, avail0, CatalogFilter.Updates, isOn, hasUpd, null), cOff = CatalogView.Count(pool, avail0, CatalogFilter.Disabled, isOn, hasUpd, null);
    Check(cAll == 6 && cInst == 5 && cUpd == 1 && cOff == 2, $"счётчики фильтров: все {cAll}, установленные {cInst}, обновления {cUpd}, отключенные {cOff}");
    var allView = CatalogView.Build(pool, avail0, CatalogFilter.All, ModSearch.Parse(""), isOn, hasUpd, null);
    Check(allView.Last().Id == "NewMod" && CatalogView.Build(pool, avail0, CatalogFilter.Installed, null, isOn, hasUpd, null).All(m => !m.Available), "доступные — после установленных, в «Установленных» их нет");
    Check(CatalogView.Build(pool, avail0, CatalogFilter.Disabled, ModSearch.Parse("rain"), isOn, hasUpd, null).Select(m => m.Id).SequenceEqual(new[] { "Rainbow" }), "фильтр + поиск вместе");
    int cOn = CatalogView.Count(pool, avail0, CatalogFilter.Enabled, isOn, hasUpd, null);
    var catView = CatalogView.Build(pool, avail0, CatalogFilter.Catalog, null, isOn, hasUpd, null).Select(m => m.Id).ToList();
    Check(cOn == 3 && cOn + cOff == cInst && catView.SequenceEqual(new[] { "SlimeLive", "NewMod" }),
        $"«Вкл» + «Выкл» = установленные ({cOn} + {cOff}); «Каталог и обновления» — моды с новой версией и доступные: {string.Join(", ", catView)}");

    // 20. каталог на реальных DLL: новые версии в «Загрузках», новый мод, чужая игра, мусор, старые файлы
    string? RealDll(string name)
    {
        if (realMods == null) return null;
        foreach (var d in new[] { realMods, Path.Combine(realMods, "Disabled") })
            if (File.Exists(Path.Combine(d, name + ".dll"))) return Path.Combine(d, name + ".dll");
        return null;
    }
    byte[] Patch(byte[] data, string from, string to)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(from); var b = System.Text.Encoding.UTF8.GetBytes(to);
        if (a.Length != b.Length) throw new Exception("patch length");
        var res = (byte[])data.Clone(); int n = 0;
        for (int i = 0; i + a.Length <= res.Length; i++)
        {
            int j = 0; while (j < a.Length && res[i + j] == a[j]) j++;
            if (j == a.Length) { Array.Copy(b, 0, res, i, b.Length); n++; i += a.Length - 1; }
        }
        if (n == 0) throw new Exception("нет «" + from + "» в DLL");
        return res;
    }
    string Bump(string v)
    {
        var c = v.ToCharArray();
        int dot = v.IndexOf('.');
        int k = dot >= 0 && dot + 1 < c.Length && char.IsDigit(c[dot + 1]) && c[dot + 1] < '9' ? dot + 1 : Array.FindIndex(c, ch => ch >= '0' && ch < '9');
        c[k]++;
        return new string(c);
    }
    string Alter(string s) => s.EndsWith("HUD") ? s[..^3] + "MAP" : new string(s.Select((ch, i) => i >= s.Length - 3 && ch is >= 'a' and < 'z' or >= 'A' and < 'Z' ? (char)(ch + 1) : ch).ToArray());

    string? ctReal = RealDll("CreatorToolkit"), slReal = RealDll("SlimeLive"), mhReal = RealDll("MinecraftHUD");
    if (ctReal != null && slReal != null && mhReal != null)
    {
        string game2 = Path.Combine(root, "Game2"), mods2 = Path.Combine(game2, "Mods"), dl = Path.Combine(root, "Downloads");
        Directory.CreateDirectory(Path.Combine(mods2, "Disabled")); Directory.CreateDirectory(dl);
        File.WriteAllText(Path.Combine(game2, "SlimeRancher2.exe"), "fake");
        File.Copy(ctReal, Path.Combine(mods2, "CreatorToolkit.dll"));
        File.Copy(slReal, Path.Combine(mods2, "Disabled", "SlimeLive.dll"));
        var store2 = new ModStore(game2);
        var inst2 = store2.Scan();
        var ctE = inst2.First(m => m.Id == "CreatorToolkit"); var slE = inst2.First(m => m.Id == "SlimeLive");
        string ctNew = Bump(ctE.Version), slNew = Bump(slE.Version);
        // новая версия Creator Toolkit — как её сохраняет браузер: «CreatorToolkit (1).dll»
        File.WriteAllBytes(Path.Combine(dl, "CreatorToolkit (1).dll"), Patch(File.ReadAllBytes(ctReal), ctE.Version, ctNew));
        // новая версия SlimeLive — в архиве с общей папкой и Mods/ внутри
        string slPatched = Path.Combine(root, "SlimeLive.dll");
        File.WriteAllBytes(slPatched, Patch(File.ReadAllBytes(slReal), slE.Version, slNew));
        string slZip = Path.Combine(dl, $"SlimeLive-{slNew}.zip");
        using (var z = ZipFile.Open(slZip, ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(slPatched, $"SlimeLive-{slNew}/Mods/SlimeLive.dll");
            using var w = new StreamWriter(z.CreateEntry($"SlimeLive-{slNew}/README.md").Open()); w.Write("readme");
        }
        // новый мод (не установлен): Minecraft HUD под другим именем
        var mhE = ModStore.Read(mhReal, false);
        var mhBytes = File.ReadAllBytes(mhReal);
        if (!string.IsNullOrEmpty(mhE.Name) && !mhE.Name.Equals(mhE.AssemblyName, StringComparison.OrdinalIgnoreCase) && mhE.Name.Length >= 3) mhBytes = Patch(mhBytes, mhE.Name, Alter(mhE.Name));
        mhBytes = Patch(mhBytes, mhE.AssemblyName, Alter(mhE.AssemblyName));
        string newId = Alter(mhE.AssemblyName);
        File.WriteAllBytes(Path.Combine(dl, newId + "-1.0.0.dll"), mhBytes);
        // мод для другой игры
        bool gameCheck = mhE.Games.Count > 0 && !string.IsNullOrEmpty(mhE.Games[0]);
        string otherGame = Path.Combine(dl, "OtherGameMod.dll");
        if (gameCheck)
        {
            string g0 = mhE.Games[0];
            File.WriteAllBytes(otherGame, Patch(mhBytes, g0, g0[..^1] + (g0[^1] == '3' ? '4' : '3')));
        }
        // мусор: «dll» не .NET, битый zip, zip без модов, старый файл
        File.WriteAllText(Path.Combine(dl, "junk.dll"), "not a dll");
        File.WriteAllText(Path.Combine(dl, "broken.zip"), "not a zip");
        using (var z = ZipFile.Open(Path.Combine(dl, "photos.zip"), ZipArchiveMode.Create))
        { using var w = new StreamWriter(z.CreateEntry("img/readme.txt").Open()); w.Write("x"); }
        string oldFile = Path.Combine(dl, "OldCopy.dll");
        File.Copy(mhReal, oldFile);
        File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-100));

        var srcs = new[] { new CatalogSource(store2.LibraryDir, "Библиотека", 0, 1), new CatalogSource(dl, "Загрузки", 60, 1) };
        var swScan = System.Diagnostics.Stopwatch.StartNew();
        var found = CatalogScanner.Scan(srcs);
        long scan1 = swScan.ElapsedMilliseconds; swScan.Restart();
        var found2 = CatalogScanner.Scan(srcs);
        long scan2 = swScan.ElapsedMilliseconds;
        Console.WriteLine($"  каталог: найдено {found.Count}: " + string.Join(", ", found.Select(f => $"{f.Id} v{f.Version} [{Path.GetFileName(f.Path)}{(f.EntryName.Length > 0 ? ":" + f.EntryName : "")}]")) + $"; скан {scan1} мс, повторный {scan2} мс");
        var fct = found.FirstOrDefault(f => f.Id == "CreatorToolkit");
        var fsl = found.FirstOrDefault(f => f.Id == "SlimeLive");
        Check(fct != null && fct.Version == ctNew && fct.FileName == "CreatorToolkit.dll", $"каталог: «CreatorToolkit (1).dll» → CreatorToolkit v{ctNew}");
        Check(fsl != null && fsl.Version == slNew && fsl.EntryName.EndsWith("Mods/SlimeLive.dll"), $"каталог: SlimeLive v{slNew} внутри zip");
        Check(found.Any(f => f.Id == newId), "каталог: новый мод найден, имя без версии: " + newId);
        Check(!found.Any(f => f.Path == oldFile), "каталог: файлы старше 60 дней в «Загрузках» не берутся");
        Check(!found.Any(f => f.Path.EndsWith("junk.dll") || f.Path.EndsWith("broken.zip") || f.Path.EndsWith("photos.zip")), "каталог: мусор не мешает и не попадает в список");
        if (gameCheck) Check(!found.Any(f => f.Path == otherGame), "каталог: мод для другой игры пропущен");
        else Console.WriteLine("  (MelonGame в DLL нет — проверка «другой игры» пропущена)");
        Check(found2.Count == found.Count && scan2 <= scan1 + 5, "повторный скан — из кэша");

        var stored2 = store2.ListAllBackups(inst2);
        var ups = Catalog.FindUpdates(inst2, found, stored2, null);
        Check(ups.TryGetValue("CreatorToolkit", out var uct) && uct.Kind == "catalog" && uct.Version == ctNew && uct.CanInstall, "обновление CreatorToolkit найдено: " + uct?.Where);
        Check(ups.TryGetValue("SlimeLive", out var usl) && usl.Version == slNew, "обновление SlimeLive (выключенного) найдено");
        var av = Catalog.AvailableOnly(inst2, found, store2.LoadConfig());
        Check(av.Count == 1 && av[0].Id == newId && av[0].Available, "доступные: только новый мод (обновления установленных — не «доступные»)");
        Func<string, bool> on2 = id => inst2.FirstOrDefault(m => m.Id == id)?.Enabled ?? false;
        Check(CatalogView.Count(inst2, av, CatalogFilter.All, on2, m => ups.ContainsKey(m.Id), null) == 3
              && CatalogView.Count(inst2, av, CatalogFilter.Updates, on2, m => ups.ContainsKey(m.Id), null) == 2
              && CatalogView.Count(inst2, av, CatalogFilter.Disabled, on2, m => ups.ContainsKey(m.Id), null) == 1, "фильтры на реальном каталоге: все 3, обновления 2, отключенные 1");
        Check(CatalogView.Build(inst2, av, CatalogFilter.All, ModSearch.Parse(newId.Substring(0, 5).ToLowerInvariant()), on2, null, null).Any(m => m.Id == newId), "поиск находит доступный мод");

        // установка из каталога
        var r1 = store2.InstallFromCatalog(av[0]);
        Check(r1.Ok && File.Exists(Path.Combine(mods2, newId + ".dll")) && !File.Exists(Path.Combine(mods2, newId + "-1.0.0.dll")), "установка доступного: " + newId + ".dll в Mods (имя без версии)");
        var r2 = store2.InstallFromCatalog(fct!, ctE);
        var after2 = store2.Scan();
        Check(r2.Ok && after2.First(m => m.Id == "CreatorToolkit").Version == ctNew && !after2.Any(m => m.Id.StartsWith("CreatorToolkit (")), "обновление: CreatorToolkit.dll заменён, копии «(1)» нет");
        Check(store2.ListBackups(after2.First(m => m.Id == "CreatorToolkit")).Any(b => b.Version == ctE.Version && b.Where == "_replaced"), "старая версия CreatorToolkit сохранена в _replaced");
        var r3 = store2.InstallFromCatalog(fsl!, slE);
        after2 = store2.Scan();
        var sl2 = after2.First(m => m.Id == "SlimeLive");
        Check(r3.Ok && !sl2.Enabled && sl2.Version == slNew && !File.Exists(Path.Combine(mods2, "SlimeLive.dll")), "обновление выключенного SlimeLive из zip: остался выключенным");
        Check(after2.Count == 3 && File.Exists(Path.Combine(dl, "CreatorToolkit (1).dll")) && File.Exists(slZip), "в Mods ровно 3 мода; файлы в «Загрузках» на месте");
        var ups2 = Catalog.FindUpdates(after2, found, store2.ListAllBackups(after2), null);
        Check(ups2.Count == 0 && Catalog.AvailableOnly(after2, found).Count == 0, "после установки: обновлений и доступных нет");
        // откат на старую → новая лежит среди копий → «обновление из сохранённой копии»
        var ctNow = after2.First(m => m.Id == "CreatorToolkit");
        var oldCopy = store2.ListBackups(ctNow).First(b => b.Version == ctE.Version);
        store2.RestoreBackup(ctNow, oldCopy.Path);
        after2 = store2.Scan();
        var ups3 = Catalog.FindUpdates(after2, new List<ModEntry>(), store2.ListAllBackups(after2), null);
        Check(ups3.TryGetValue("CreatorToolkit", out var ust) && ust.Kind == "stored" && ust.Version == ctNew && ust.CanInstall, "новее среди сохранённых копий — тоже обновление: " + ust?.Where);
        // «устарел» по журналу игры
        var outLog = ModStore.ParseMelonLog(new[]
        {
            "[10:00:00.000] [ERROR] [SlimeLive] System.MissingMethodException: Method not found: 'Void Il2Cpp.Foo.Bar()'",
        }, after2);
        var ups4 = Catalog.FindUpdates(after2, new List<ModEntry>(), null, outLog);
        Check(outLog.For("SlimeLive")?.Outdated == true && ups4.TryGetValue("SlimeLive", out var uo) && uo.Kind == "outdated" && !uo.CanInstall, "MissingMethodException в журнале → «устарел»");

        // демо-каталог для снимков окна (второй аргумент)
        if (args.Length > 1)
        {
            string demo = args[1];
            Directory.CreateDirectory(demo);
            foreach (var f in Directory.GetFiles(demo)) File.Delete(f);
            File.Copy(Path.Combine(dl, "CreatorToolkit (1).dll"), Path.Combine(demo, "CreatorToolkit (1).dll"));
            File.Copy(slZip, Path.Combine(demo, Path.GetFileName(slZip)));
            File.Copy(Path.Combine(dl, newId + "-1.0.0.dll"), Path.Combine(demo, newId + "-1.0.0.dll"));
            Console.WriteLine("  демо-каталог: " + demo);
        }
    }
    else Console.WriteLine("  (реальных DLL нет — тесты каталога на настоящих модах пропущены)");

    // совместимость модов, бэкапы сохранений, пресеты (Compat.cs, Backups.cs)
    ExtraTests.Run(realMods, root, Check);

    // настройки вида лаунчера (ui.json), сортировка таблицы, журнал для вкладки «Логи» (UiPrefs.cs)
    UiTests.Run(root, Check);
}
catch (Exception e) { fail++; Console.WriteLine("FAIL exception " + e); }
finally { try { Directory.Delete(root, true); } catch { } }
Console.WriteLine($"ИТОГ: {pass} PASS, {fail} FAIL");
return fail == 0 ? 0 : 1;



