// ExtraTests.cs — проверка совместимости (Compat.cs), бэкапы сохранений и пресеты (Backups.cs).
// Реальные папки (игра, сохранения) только читаются; всё, что пишется, — во временной папке теста.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using SR2ModManager.Core;

static class ExtraTests
{
    public static void Run(string? realMods, string root, Action<bool, string> check)
    {
        Console.WriteLine("── совместимость: реальные моды и окружение");
        CompatReal(realMods, check);
        Console.WriteLine("── совместимость: правила на синтетических модах");
        CompatRules(check);
        Console.WriteLine("── справочник модов Nexus / Ranching Together и пары хуков");
        KnownAndPairs(check);
        Console.WriteLine("── пул модов Nexus (_work\\pool\\game\\Mods)");
        PoolReal(check);
        Console.WriteLine("── бэкапы сохранений");
        Backups(root, check);
        Console.WriteLine("── пресеты набора модов");
        PresetsTest(realMods, root, check);
    }

    // ───────────── совместимость ─────────────

    static ModEntry Mod(string id, bool on = true, params string[] deps) => new()
    {
        Id = id, FileName = id + ".dll", Path = @"C:\fake\Mods\" + id + ".dll", Enabled = on, Readable = true,
        AssemblyName = id, Name = id, Version = "1.0.0", Kind = "Mod", DependsOn = deps.ToList(),
    };

    static ModManifest Mf(string id, Version? ml = null) => new()
    {
        Path = @"C:\fake\Mods\" + id + ".dll", Readable = true, IsMelon = true, AssemblyName = id, AssemblyVersion = new Version(1, 0, 0, 0),
        MelonKind = "Mod", MelonName = id, MelonVersion = "1.0.0", MelonAuthor = "test", LoaderRef = ml ?? new Version(0, 7, 3, 0),
        TargetFramework = ".NETCoreApp,Version=v6.0", Games = { ("MonomiPark", "SlimeRancher2") },
        References = { ["MelonLoader"] = ml ?? new Version(0, 7, 3, 0), ["System.Runtime"] = new Version(6, 0, 0, 0), ["Il2CppInterop.Runtime"] = new Version(1, 0, 0, 0), ["Il2CppSystem"] = new Version(0, 0, 0, 0) },
        UsesIl2CppInterop = true,
    };

    static LoaderEnv FakeEnv(Version? loader = null)
    {
        var e = new LoaderEnv
        {
            GameDir = @"C:\fake", LoaderInstalled = true, Loader = loader ?? new Version(0, 7, 3, 0), OpenBeta = true, Runtime = "net6", GameType = "Il2cpp", GameArch = "x64",
            UnityVersion = "6000.0.73f1", GameVersion = "1.3.0 [20260704030000-113492]", GameDeveloper = "MonomiPark", GameName = "SlimeRancher2",
            Il2CppInterop = new Version(1, 5, 1, 0), Harmony = new Version(2, 10, 2, 0), Tomlet = true, SupportModule = true, GameAssemblies = 121,
            DotNet = new Version(6, 0, 16), DotNetDir = @"C:\fake\dotnet\shared\Microsoft.NETCore.App\6.0.16", RollForward = "LatestMinor",
        };
        e.LoaderText = CompatChecker.V3(e.Loader!) + (loader == null ? " Open-Beta" : "");
        e.Known["Il2CppSystem"] = @"C:\fake\MelonLoader\Il2CppAssemblies\Il2CppSystem.dll"; e.KnownWhere["Il2CppSystem"] = "game";
        foreach (var n in new[] { "MelonLoader", "Il2CppInterop.Runtime", "Il2CppInterop.Common", "0Harmony", "Tomlet" }) { e.Known[n] = @"C:\fake\MelonLoader\net6\" + n + ".dll"; e.KnownWhere[n] = "MelonLoader"; }
        e.Known["System.Runtime"] = @"C:\fake\dotnet\System.Runtime.dll"; e.KnownWhere["System.Runtime"] = "framework";
        e.Issues = CompatChecker.EnvIssues(e);
        return e;
    }

    static void CompatReal(string? realMods, Action<bool, string> check)
    {
        if (realMods == null || !Directory.Exists(realMods)) { Console.WriteLine("  (нет папки Mods — пропущено)"); return; }
        string gameDir = Path.GetDirectoryName(Path.GetFullPath(realMods).TrimEnd('\\', '/'))!;
        var store = new ModStore(gameDir);
        if (!Directory.Exists(Path.Combine(gameDir, "MelonLoader"))) { Console.WriteLine("  (MelonLoader не установлен — пропущено)"); return; }
        var env = LoaderEnv.Detect(store, hashGameAssembly: false);
        Console.WriteLine($"  окружение: {env.Summary}; Il2CppInterop {env.Il2CppInterop}, Harmony {env.Harmony}, Tomlet {env.Tomlet}, модуль Il2Cpp {env.SupportModule}, сборок игры {env.GameAssemblies} (Unity генератора {env.GeneratorUnity}), .NET {env.DotNet} ({env.RollForward}; есть {string.Join(", ", env.DotNetInstalled)}), известных сборок {env.Known.Count}, {env.Took.TotalMilliseconds:0} мс");
        foreach (var i in env.Issues) Console.WriteLine("    окружение: " + i);
        check(env.LoaderInstalled && env.Loader is { Major: 0, Minor: >= 6 }, "MelonLoader найден: " + env.LoaderText);
        check(env.Loader is not { Major: 0, Minor: 7 } || env.OpenBeta, "0.7.x распознан как Open-Beta по журналу");
        check(env.UnityVersion.Length > 0 && env.GameName.Length > 0, $"Unity {env.UnityVersion}, игра {env.GameDeveloper}/{env.GameName} {env.ShortGameVersion}");
        check(env.Il2CppInterop != null && env.Harmony != null && env.Tomlet && env.SupportModule, "Il2CppInterop, Harmony, Tomlet (MelonPreferences) и модуль Il2Cpp на месте");
        check(env.DotNet is { Major: 6 }, ".NET для модов: " + env.DotNet);
        check(env.PrefsError == "", "MelonPreferences.cfg читается" + (env.PrefsError == "" ? "" : ": " + env.PrefsError));
        check(env.Issues.All(i => i.Level < CompatLevel.Error), "в окружении нет ошибок");
        var sw = Stopwatch.StartNew();
        var env2 = LoaderEnv.Detect(store, hashGameAssembly: true, cacheDir: Path.Combine(Path.GetTempPath(), "sr2mm-test-cache"));
        Console.WriteLine($"  с хэшем GameAssembly.dll: {(env2.HashChecked ? "сверен" : "не сверялся")}, устарели сборки: {env2.GeneratorStale} {env2.GeneratorNote} ({sw.ElapsedMilliseconds} мс)");
        check(!env2.HashChecked || !env2.GeneratorStale, "сборки игры сгенерированы для текущей GameAssembly.dll");

        var mods = store.Scan();
        sw.Restart();
        var mfs = CompatChecker.ReadManifests(mods);
        long tRead = sw.ElapsedMilliseconds;
        sw.Restart();
        CompatChecker.ReadManifests(mods);
        Console.WriteLine($"  манифесты: {mfs.Count} за {tRead} мс, повторно из кэша {sw.ElapsedMilliseconds} мс");
        foreach (var m in mods)
        {
            var mf = mfs[m.Id];
            Console.WriteLine($"  {m.FileName}{(m.Enabled ? "" : " (выкл)")}: melon={mf.IsMelon} {mf.MelonKind} «{mf.MelonName}» {mf.MelonVersion} ({mf.MelonAuthor}); ML {mf.LoaderRef}; {mf.FrameworkLabel}; " +
                $"игры [{string.Join(", ", mf.Games.Select(g => g.Developer + "/" + g.Name))}]; зависимости [{string.Join(", ", mf.Additional)}]; Harmony {mf.UsesHarmony} (хуков {mf.Hooks.Count}{(mf.ManualPatching ? ", Patch() в коде" : "")}); prefs {mf.UsesPreferences}; interop {mf.UsesIl2CppInterop}");
            foreach (var h in mf.Hooks.Take(12)) Console.WriteLine($"      хук {h.Kind}{(h.SkipsOriginal ? " (bool)" : "")}{(h.Manual ? " [код]" : "")} {h.Key} ← {h.PatchClass}");
            if (mf.Hooks.Count > 12) Console.WriteLine($"      … и ещё {mf.Hooks.Count - 12}");
            Console.WriteLine("      факты: " + string.Join(" · ", CompatChecker.Facts(mf)));
        }
        CoopReal(mods, mfs, env, check);
        var ct = mods.FirstOrDefault(m => m.Id == "CreatorToolkit");
        var rd = mods.FirstOrDefault(m => m.Id == "CreatorToolkitReviewDirector");
        if (ct == null || rd == null) { Console.WriteLine("  (нет CreatorToolkit/ReviewDirector — проверка на них пропущена)"); return; }
        var ctm = mfs[ct.Id];
        var rdm = mfs[rd.Id];
        check(ctm.IsMelon && !ctm.LegacyInfo && ctm.MelonVersion.StartsWith("0.6."), $"CreatorToolkit: MelonInfo «{ctm.MelonName}» {ctm.MelonVersion}");
        check(rdm.IsMelon && !rdm.LegacyInfo && rdm.MelonVersion.StartsWith("3.0.0"), $"ReviewDirector: MelonInfo «{rdm.MelonName}» {rdm.MelonVersion}");
        check(ctm.LoaderRef != null && rdm.LoaderRef != null, $"версия MelonLoader, под которую собраны: CT {ctm.LoaderRef}, RD {rdm.LoaderRef}");
        check(ctm.FrameworkKind == "core" && rdm.FrameworkKind == "core", $"оба под .NET Core: {ctm.FrameworkLabel} / {rdm.FrameworkLabel}");
        check(ctm.UsesIl2CppInterop, "CreatorToolkit ссылается на Il2CppInterop");
        check(rd.DependsOn.All(d => mods.Any(x => x.Id.Equals(d, StringComparison.OrdinalIgnoreCase))) && rdm.Additional.Count == 0,
              "ReviewDirector: зависимостей в DLL не объявлено, неразрешённых нет" + (rd.DependsOn.Count > 0 ? " (" + string.Join(", ", rd.DependsOn) + ")" : ""));
        check(!ctm.UsesHarmony || ctm.Hooks.Count > 0, $"хуки Harmony CreatorToolkit найдены (в том числе harmony.Patch в коде): {ctm.Hooks.Count}");

        var log = store.ReadMelonLog(mods);
        Func<string, bool> asOnDisk = id => mods.First(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Enabled;
        Func<string, bool> both = id => id is "CreatorToolkit" or "CreatorToolkitReviewDirector";
        var rep = CompatChecker.Check(mods, mfs, both, env, log);
        Console.WriteLine($"  проверка (CT + RD включены): {rep.Took.TotalMilliseconds:0.0} мс");
        foreach (var (id, l) in rep.ByMod) foreach (var i in l) Console.WriteLine($"    {id}: {i}");
        check(rep.Worst(ct.Id) < CompatLevel.Error && rep.Worst(rd.Id) < CompatLevel.Error, $"CreatorToolkit {ctm.MelonVersion} и ReviewDirector 3.0.0 совместимы с текущим MelonLoader/Unity");
        check(rep.Blocking(mods, both).Count == 0, "перед запуском нет блокирующих проблем");
        var repDisk = CompatChecker.Check(mods, mfs, asOnDisk, env, log);
        check(repDisk.Blocking(mods, asOnDisk).Count == 0, "текущий набор (как на диске) запускается без блокирующих проблем");

        // тот же набор на MelonLoader 0.6.5 (без Open-Beta): моды, собранные под 0.7, — ошибка с объяснением
        var e065 = LoaderEnv.Detect(store, false);
        e065.Loader = new Version(0, 6, 5, 0); e065.LoaderText = "0.6.5"; e065.OpenBeta = false;
        e065.Issues = CompatChecker.EnvIssues(e065);
        var rep065 = CompatChecker.Check(mods, mfs, both, e065, null);
        foreach (var i in rep065.For(ct.Id)) Console.WriteLine("    ML 0.6.5 → CT: " + i);
        bool ctFor07 = ctm.LoaderRef is { Major: 0, Minor: >= 7 };
        check(!ctFor07 || rep065.For(ct.Id).Any(i => i.Code == "loader-newer" && i.Level == CompatLevel.Error), "на ML 0.6.5 CreatorToolkit (собран под 0.7) помечен «нужен ML 0.7»");
        check(e065.Issues.Any(i => i.Code == "loader-unity6"), "ML 0.6.5 на Unity 6: предупреждение про 0.7.3");
        // окружение без .NET 6 и без Il2CppInterop — ошибки окружения блокируют запуск
        var eBad = LoaderEnv.Detect(store, false);
        eBad.DotNet = null; eBad.DotNetDir = ""; eBad.Il2CppInterop = null; eBad.PrefsError = "строка 3: не закрыты кавычки";
        eBad.Issues = CompatChecker.EnvIssues(eBad);
        check(eBad.Issues.Any(i => i.Code == "dotnet-missing" && i.Level == CompatLevel.Error) && eBad.Issues.Any(i => i.Code == "interop-missing")
              && eBad.Issues.Any(i => i.Code == "prefs-broken"), "нет .NET 6 / Il2CppInterop / битый MelonPreferences.cfg — видно в окружении");
        var repBad = CompatChecker.Check(mods, mfs, both, eBad, null);
        check(repBad.Blocking(mods, both).Count >= 2, "такие ошибки окружения блокируют «Играть»");
        check(!ctm.UsesPreferences || repBad.For(ct.Id).Any(i => i.Code == "prefs"), "мод с MelonPreferences предупреждает о сбросе настроек");

        // реальный MelonPreferences.cfg — без ложных срабатываний
        if (File.Exists(store.PreferencesPath))
        {
            string text = File.ReadAllText(store.PreferencesPath);
            check(TomlCheck.Problem(text) == "", $"TomlCheck: реальный MelonPreferences.cfg ({text.Length} символов) без ложных ошибок");
            var cut = text.Substring(0, text.LastIndexOf('"') >= 0 ? text.LastIndexOf('"') : text.Length);
            Console.WriteLine("    обрезанный файл: " + TomlCheck.Problem(cut + "\n[broken"));
            check(TomlCheck.Problem(cut + "\n[broken") != "", "TomlCheck: обрезанный файл распознан как повреждённый");
        }
    }

    /// Кооп: Ranching Together (SR2MP, расширение Starlight) + Starlight + CreatorToolkit — реальные DLL из Mods.
    static void CoopReal(List<ModEntry> mods, Dictionary<string, ModManifest> mfs, LoaderEnv env, Action<bool, string> check)
    {
        var mp = mods.FirstOrDefault(m => m.Id.Equals("SR2MP", StringComparison.OrdinalIgnoreCase));
        var sl = mods.FirstOrDefault(m => m.AssemblyName.Equals(StarlightInfo.AssemblyName, StringComparison.OrdinalIgnoreCase));
        if (mp == null || sl == null) { Console.WriteLine("  (нет SR2MP/Starlight — проверка коопа пропущена)"); return; }
        var mpm = mfs[mp.Id]; var slm = mfs[sl.Id];
        check(mp.Kind == "Expansion" && mpm.IsExpansion && !mpm.IsMelon, $"SR2MP — расширение Starlight: «{mp.Name}» {mp.Version} ({mp.Author})");
        check(mp.DependsOn.Contains(sl.Id, StringComparer.OrdinalIgnoreCase), "SR2MP зависит от Starlight: " + string.Join(", ", mp.DependsOn));
        check(mpm.MinStarlight.Length > 0, "SR2MP: минимальный Starlight (minimum_sce) " + mpm.MinStarlight + ", установлен " + sl.Version);
        var ct = mods.FirstOrDefault(m => m.Id == "CreatorToolkit");
        Func<string, bool> coop = id => id.Equals(mp.Id, StringComparison.OrdinalIgnoreCase) || id.Equals(sl.Id, StringComparison.OrdinalIgnoreCase) || id == "CreatorToolkit";
        var rep = CompatChecker.Check(mods, mfs, coop, env, null);
        foreach (var id in new[] { mp.Id, sl.Id, "CreatorToolkit" }) foreach (var i in rep.For(id)) Console.WriteLine($"    кооп {id}: [{i.Level}] {i.Badge} — {i.Title}");
        check(!rep.For(mp.Id).Any(i => i.Code == "not-melon"), "SR2MP без ложного «это библиотека, а не мод»");
        check(!rep.For(sl.Id).Any(i => i.Code == "dotnet-newer"), "Starlight без ложного «нужен .NET 11» (Microsoft.VisualBasic.Core 11.0 — сборка .NET 6)");
        check(!rep.For(mp.Id).Any(i => i.Code.StartsWith("starlight-")), "Starlight подходит под minimum_sce SR2MP");
        check(rep.Worst(mp.Id) < CompatLevel.Error && rep.Worst(sl.Id) < CompatLevel.Error, "SR2MP и Starlight без ошибок совместимости");
        if (ct != null && mfs[ct.Id].CompatibleWith.Count > 0)
        {
            check(mfs[ct.Id].CompatibleWith.Contains("SR2MP", StringComparer.OrdinalIgnoreCase), "CreatorToolkit объявляет CompatibleWith: " + string.Join(";", mfs[ct.Id].CompatibleWith));
            check(!rep.For(ct.Id).Any(i => i.Code == "hook-conflict") && !rep.For(mp.Id).Any(i => i.Code == "hook-conflict") && !rep.For(sl.Id).Any(i => i.Code == "hook-conflict"),
                "CT + SR2MP + Starlight: общие хуки согласованы — без «конфликта хуков»");
            check(rep.Blocking(mods, coop).Count == 0, "кооп-набор запускается без блокирующих проблем");
        }
        if (ct != null)
        {
            var ctm = mfs[ct.Id];
            Console.WriteLine($"    CreatorToolkit {ct.Version}: HookPolicy={(ctm.CooperativeHooks ? "cooperative" : "—")}, хуков {ctm.Hooks.Count(h => !h.Dynamic)} (вычисляемых {ctm.Hooks.Count(h => h.Dynamic)}), с отменой {ctm.Hooks.Count(h => h.SkipsOriginal)}");
            if (ctm.CooperativeHooks)
            {
                check(ctm.Hooks.Count(h => !h.Dynamic) >= 30 && !ctm.Hooks.Any(h => h.Kind is "Transpiler" or "Finalizer"), "CreatorToolkit: хуки распознаны из IL, без транспайлеров/финализаторов (" + ctm.Hooks.Count + ")");
                var all = CompatChecker.Check(mods, mfs, _ => true, env, null);
                check(!all.For(ct.Id).Any(i => i.Code == "hook-conflict"), "CreatorToolkit (HookPolicy=cooperative) без «конфликта хуков» со всеми установленными модами");
                foreach (var i in all.For(ct.Id)) Console.WriteLine($"    все моды, CT: [{i.Level}] {i.Badge} — {i.Title}");
            }
        }    }

    static void KnownAndPairs(Action<bool, string> check)
    {
        check(KnownMods.All.Count == 70 && KnownMods.All.Select(k => k.NexusId).Distinct().Count() == 70, "справочник: 70 модов Nexus, id без повторов");
        check(KnownMods.Find("Vacuum Modifications")?.NexusId == 45 && KnownMods.Find("BuildImprovements")?.NexusId == 179 && KnownMods.Find(null, "BetterDrones")?.NexusId == 177,
            "узнаёт по имени MelonInfo и по имени сборки");
        check(KnownMods.All.Where(k => k.RanchingTogether).Select(k => k.NexusId).OrderBy(x => x).SequenceEqual(new[] { 45, 177, 179 }),
            "официальная поддержка Ranching Together — ровно Vacuum Modifications, Better Drones, Placement Improvements");
        check(KnownMods.Find("Infinite health")?.NexusId == 57 && KnownMods.Find("SR2MP")?.NexusId == 118 && KnownMods.Find("Starlight")?.NexusId == 60
              && KnownMods.Find("ScuttleBugsBegone_.NET6.0")?.NexusId == 28 && KnownMods.Find("MoreVaccablesMod")?.NexusId == 42, "реальные имена из MelonInfo и сборок");
        check(KnownMods.Find("CreatorToolkit") == null && KnownMods.Find("") == null && KnownMods.Find("MelonLoader") == null, "чужие имена не узнаются");
        var keys = KnownMods.All.SelectMany(k => new[] { k.Name }.Concat(k.Aliases).Select(KnownMods.Norm).Distinct()).ToList();
        check(keys.All(k => k.Length > 0) && keys.GroupBy(x => x).All(g => g.Count() == 1), "имена и псевдонимы не пересекаются между модами");
        check(KnownMods.All.Count(k => k.CreatorToolkit.Length > 0) >= 30, "проверено с Creator Toolkit: " + KnownMods.All.Count(k => k.CreatorToolkit.Length > 0) + " модов");
        check(KnownMods.All.All(k => k.Url == "https://www.nexusmods.com/slimerancher2/mods/" + k.NexusId), "у каждого — ссылка на страницу Nexus");
        var vm = Mf("VacuumModifications"); vm.MelonName = "Vacuum Modifications";
        var facts = CompatChecker.Facts(vm);
        check(facts.Contains("Nexus Mods #45") && facts.Contains("официальная поддержка в Ranching Together") && facts.Any(x => x.StartsWith("проверен с Creator Toolkit")),
            "подробности: Nexus, Ranching Together, проверка с Creator Toolkit");
        check(!CompatChecker.Facts(Mf("ModA")).Any(x => x.StartsWith("Nexus")), "неизвестный мод — без фактов справочника");
        var bep = KnownMods.Find("BepInEx");
        check(bep?.NexusId == 53 && KnownMods.Facts(bep).Any(x => x.Contains("BepInEx")), "BepInEx помечен как другой загрузчик");

        // Пары: кооперативный мод (как Creator Toolkit) + чужой постфикс (как Cube Slimes) + чужая отмена (как Infinite Health).
        // Раньше конфликт с третьим модом втягивал в «Проблемы» и безобидную пару CT ↔ Cube.
        var env = FakeEnv();
        HookTarget H(string type, string method, string kind, bool skips, string cls) =>
            new HookTarget { Type = type, Method = method, Kind = kind, SkipsOriginal = skips, PatchClass = cls };
        var ct = Mod("CT"); var cube = Mod("Cube"); var ih = Mod("IH");
        var mct = Mf("CT"); var mcube = Mf("Cube"); var mih = Mf("IH");
        foreach (var x in new[] { mct, mcube, mih }) x.UsesHarmony = true;
        mct.CooperativeHooks = true;
        mct.Hooks.Add(H("Il2Cpp.TimeDirector", "Update", "Prefix", true, "CT.Freeze"));
        mct.Hooks.Add(H("Il2Cpp.PlayerModel", "LoseHealth", "Prefix", true, "CT.God"));
        mcube.Hooks.Add(H("Il2Cpp.TimeDirector", "Update", "Postfix", false, "Cube.Tick"));
        mih.Hooks.Add(H("Il2Cpp.PlayerModel", "LoseHealth", "Prefix", true, "IH.Block"));
        var pm = new Dictionary<string, ModManifest> { ["CT"] = mct, ["Cube"] = mcube, ["IH"] = mih };
        var trio = new List<ModEntry> { ct, cube, ih };
        var pr = CompatChecker.Check(trio, pm, _ => true, env, null);
        var w = pr.For("CT").FirstOrDefault(i => i.Code == "hook-conflict");
        check(w != null && w.Related.SequenceEqual(new[] { "IH" }) && !w.Text.Contains("Cube"), "«конфликт хуков» называет только мешающий мод (чужая отмена), не постфикс");
        check(pr.For("CT").Any(i => i.Code == "hook-shared" && i.Related.SequenceEqual(new[] { "Cube" }) && i.Text.Contains("HookPolicy")),
            "с модом-постфиксом — отдельная инфо-строка «общие хуки» (HookPolicy)");
        check(!pr.For("Cube").Any(i => i.Code == "hook-conflict") && pr.For("IH").Any(i => i.Code == "hook-conflict" && i.Related.SequenceEqual(new[] { "CT" })),
            "у мода-постфикса конфликта нет, у чужой отмены — только с CT");
        check(!CompatChecker.HooksClash(mct, mcube) && !CompatChecker.HooksClash(mcube, mct) && CompatChecker.HooksClash(mct, mih) && CompatChecker.HooksClash(mih, mct),
            "HooksClash: кооперативная отмена + постфикс — нет, + чужая отмена — да (симметрично)");
        check(CompatChecker.Cooperative(mct) && !CompatChecker.Cooperative(mih) && !CompatChecker.Cooperative(null)
              && !CompatChecker.HardHook(mct, mct.Hooks[0]) && CompatChecker.HardHook(mih, mih.Hooks[0]) && !CompatChecker.HardHook(mcube, mcube.Hooks[0]),
            "Cooperative / HardHook");
        mct.CompatibleWith.Add("IH");
        var pv = CompatChecker.Check(trio, pm, _ => true, env, null);
        check(trio.All(m => !pv.For(m.Id).Any(i => i.Code == "hook-conflict")), "CompatibleWith снимает конфликт одинаковых отмен (как Creator Toolkit + Infinite Health)");
        mct.CompatibleWith.Clear();
    }

    /// Пул реальных модов с Nexus/GitHub (_work\pool\setup-game.ps1): Creator Toolkit без «конфликта хуков» со всеми.
    static void PoolReal(Action<bool, string> check)
    {
        string? dir = Environment.GetEnvironmentVariable("SR2MM_POOL");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); dir == null && d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "_work", "pool", "game", "Mods");
            if (Directory.Exists(p)) dir = p;
        }
        if (dir == null || !Directory.Exists(dir)) { Console.WriteLine("  (нет пула — пропущено)"); return; }
        string gameDir = Path.GetDirectoryName(Path.GetFullPath(dir).TrimEnd('\\', '/'))!;
        var mods = new ModStore(gameDir).Scan();
        var mfs = CompatChecker.ReadManifests(mods);
        var ct = mods.FirstOrDefault(m => m.AssemblyName == "CreatorToolkit");
        if (ct == null) { Console.WriteLine("  (в пуле нет Creator Toolkit — пропущено)"); return; }
        var rep = CompatChecker.Check(mods, mfs, _ => true, FakeEnv(), null);
        var known = mods.Where(m => KnownMods.Find(m.Name, m.AssemblyName) != null).ToList();
        Console.WriteLine($"  модов в пуле: {mods.Count}, узнаны по справочнику: {known.Count}; не узнаны: " +
            string.Join(", ", mods.Except(known).Select(m => m.DisplayName)));
        foreach (var m in mods)
            foreach (var i in rep.For(m.Id).Where(i => i.Code == "hook-conflict"))
                Console.WriteLine($"    конфликт хуков: {m.DisplayName} ↔ {string.Join(", ", i.Related.Select(r => mods.FirstOrDefault(x => x.Id == r)?.DisplayName ?? r))}");
        check(!rep.For(ct.Id).Any(i => i.Code == "hook-conflict"), "пул: у Creator Toolkit нет «конфликта хуков» ни с одним модом");
        check(mods.All(m => !rep.For(m.Id).Any(i => i.Code == "hook-conflict" && i.Related.Contains(ct.Id))), "пул: ни один мод не считает, что Creator Toolkit ему мешает");
        // свои моды репозитория (Creator Toolkit и его директора, Minecraft HUD, SlimeLive) на Nexus не выложены
        static bool Own(ModEntry m) => m.AssemblyName.StartsWith("CreatorToolkit", StringComparison.OrdinalIgnoreCase) || m.AssemblyName is "MinecraftHUD" or "SlimeLive";
        var foreign = mods.Where(m => !Own(m)).ToList();
        check(foreign.Count > 0 && foreign.All(m => known.Contains(m)), $"пул: справочник узнаёт все сторонние моды ({foreign.Count(known.Contains)} из {foreign.Count})");
        check(known.All(m => m.Link.Length > 0), "пул: у узнанных модов есть ссылка (своя или на Nexus)");
    }

    static void CompatRules(Action<bool, string> check)
    {
        var env = FakeEnv();
        check(env.Issues.Count == 0, "эталонное окружение (ML 0.7.3 Open-Beta, Unity 6000.0.73f1, .NET 6.0.16) без замечаний: " + string.Join("; ", env.Issues));

        var a = Mod("ModA"); var b = Mod("ModB"); var c = Mod("ModC", true, "ModA");
        var ma = Mf("ModA"); var mb = Mf("ModB"); var mc = Mf("ModC");
        ma.UsesHarmony = mb.UsesHarmony = mc.UsesHarmony = true;
        ma.Hooks.Add(new HookTarget { Type = "Il2Cpp.PlayerState", Method = "Update", Kind = "Prefix", SkipsOriginal = true, PatchClass = "ModA.P1" });
        mb.Hooks.Add(new HookTarget { Type = "Il2Cpp.PlayerState", Method = "Update", Kind = "Postfix", PatchClass = "ModB.P1" });
        mb.Hooks.Add(new HookTarget { Type = "Il2Cpp.WeatherDirector", Method = "Start", Kind = "Postfix", PatchClass = "ModB.P2" });
        mc.Hooks.Add(new HookTarget { Type = "Il2Cpp.WeatherDirector", Method = "Start", Kind = "Postfix", PatchClass = "ModC.P1" });
        var mods = new List<ModEntry> { a, b, c };
        var mfs = new Dictionary<string, ModManifest> { ["ModA"] = ma, ["ModB"] = mb, ["ModC"] = mc };
        var rep = CompatChecker.Check(mods, mfs, _ => true, env, null);
        foreach (var (id, l) in rep.ByMod) foreach (var i in l) Console.WriteLine($"    {id}: {i}");
        var ia = rep.For("ModA").FirstOrDefault(i => i.Code == "hook-conflict");
        check(ia != null && ia.Level == CompatLevel.Warning && ia.Related.Contains("ModB") && ia.Text.Contains("PlayerState.Update"), "префикс с отменой на общем методе → «конфликт хуков» с объяснением");
        check(rep.For("ModB").Any(i => i.Code == "hook-conflict"), "конфликт виден и у второго мода");
        check(rep.For("ModC").Any(i => i.Code == "hook-shared" && i.Level == CompatLevel.Info), "два постфикса на одном методе → только «общие хуки» (инфо)");
        check(rep.Blocking(mods, _ => true).Count == 0, "предупреждения о хуках не блокируют запуск");
        var repOff = CompatChecker.Check(mods, mfs, id => id != "ModB", env, null);
        check(!repOff.For("ModA").Any(i => i.Code.StartsWith("hook")), "выключенный мод в пересечениях хуков не учитывается");

        // дубликат, несовместимость, чужая игра, Mono, старые/новые версии MelonLoader, .NET 8, нет библиотеки
        var d1 = Mod("Dup"); var d2 = Mod("Dup (1)"); var md1 = Mf("Dup"); var md2 = Mf("Dup");
        var x = Mod("Xenophobe"); var mx = Mf("Xenophobe"); mx.Incompatible.Add("ModA");
        var g = Mod("OtherGame"); var mg = Mf("OtherGame"); mg.Games.Clear(); mg.Games.Add(("TVGS", "Schedule I"));
        var mono = Mod("MonoOnly"); var mmono = Mf("MonoOnly"); mmono.Domain = 1;
        var u = Mod("Unhollowed"); var mu = Mf("Unhollowed", new Version(0, 5, 7, 0)); mu.UsesUnhollower = true; mu.References["UnhollowerBaseLib"] = new Version(0, 4, 18, 0);
        var n8 = Mod("NewLoader"); var mn8 = Mf("NewLoader", new Version(0, 8, 0, 0));
        var o6 = Mod("OldLoader"); var mo6 = Mf("OldLoader", new Version(0, 6, 1, 0));
        var dn = Mod("DotNet8"); var mdn = Mf("DotNet8"); mdn.TargetFramework = ".NETCoreApp,Version=v8.0"; mdn.References["System.Runtime"] = new Version(8, 0, 0, 0);
        var lib = Mod("NeedsLib"); var mlib = Mf("NeedsLib"); mlib.References["SomeLib"] = new Version(2, 1, 0, 0);
        var ver = Mod("Pinned"); var mver = Mf("Pinned"); mver.RequiredLoader = "0.6.4"; mver.RequiredIsMinimum = false;
        var gv = Mod("OldGame"); var mgv = Mf("OldGame"); mgv.GameVersions.Add("0.6.0");
        var nm = Mod("JustLib"); var mnm = new ModManifest { Path = @"C:\fake\Mods\JustLib.dll", Readable = true, AssemblyName = "JustLib", TargetFramework = ".NETStandard,Version=v2.0" };
        var all = new List<ModEntry> { a, d1, d2, x, g, mono, u, n8, o6, dn, lib, ver, gv, nm };
        var mfs2 = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase)
        {
            ["ModA"] = Mf("ModA"), ["Dup"] = md1, ["Dup (1)"] = md2, ["Xenophobe"] = mx, ["OtherGame"] = mg, ["MonoOnly"] = mmono, ["Unhollowed"] = mu,
            ["NewLoader"] = mn8, ["OldLoader"] = mo6, ["DotNet8"] = mdn, ["NeedsLib"] = mlib, ["Pinned"] = mver, ["OldGame"] = mgv, ["JustLib"] = mnm,
        };
        var r2 = CompatChecker.Check(all, mfs2, _ => true, env, null);
        foreach (var (id, l) in r2.ByMod) foreach (var i in l) Console.WriteLine($"    {id}: [{i.Level}] {i.Badge} — {i.Title}");
        bool Has(string id, string code, CompatLevel lvl) => r2.For(id).Any(i => i.Code == code && i.Level == lvl && i.Text.Length > 20 && i.Badge.Length > 0);
        check(Has("Dup", "duplicate", CompatLevel.Error) && Has("Dup (1)", "duplicate", CompatLevel.Error), "две копии одной сборки → «дубликат»");
        check(Has("Xenophobe", "incompatible", CompatLevel.Error) && Has("ModA", "incompatible", CompatLevel.Error), "MelonIncompatibleAssemblies → «несовместим» у обоих");
        check(Has("OtherGame", "other-game", CompatLevel.Error), "MelonGame другой игры → «для другой игры»");
        check(Has("MonoOnly", "mono-only", CompatLevel.Error), "MelonPlatformDomain(MONO) → «только Mono»");
        check(Has("Unhollowed", "unhollower", CompatLevel.Error), "мод для ML 0.5 (Unhollower) → ошибка");
        check(Has("NewLoader", "loader-newer", CompatLevel.Error), "собран под ML 0.8 → «нужен ML 0.8»");
        check(Has("OldLoader", "loader-older", CompatLevel.Info), "собран под ML 0.6 → только информация (обычно работает в 0.7)");
        check(Has("DotNet8", "dotnet-newer", CompatLevel.Error), "собран под .NET 8 → ошибка (MelonLoader запускает .NET 6)");
        check(Has("NeedsLib", "missing-lib", CompatLevel.Error), "нет библиотеки → «нет SomeLib» с подсказкой про UserLibs");
        check(Has("Pinned", "verify-loader", CompatLevel.Error), "VerifyLoaderVersion(0.6.4) на 0.7.3 → ошибка");
        check(Has("OldGame", "game-version", CompatLevel.Error), "MelonGameVersion другой версии SR2 → ошибка");
        check(Has("JustLib", "not-melon", CompatLevel.Warning), "DLL без MelonInfo → «не мод» (место в UserLibs)");
        check(r2.For("ModA").All(i => i.Code != "loader-newer"), "мод под ту же версию ML без лишних замечаний");
        // журнал прошлого запуска: MissingMethodException у мода, собранного под 0.6 → «не работает в 0.7»
        var log = ModStore.ParseMelonLog(new[] { "[10:00:00.000] [ERROR] [OldLoader] System.MissingMethodException: Method not found: 'Void MelonLoader.MelonMod.OnApplicationStart()'" }, all);
        var r3 = CompatChecker.Check(all, mfs2, _ => true, env, log);
        check(r3.For("OldLoader").Any(i => i.Code == "loader-older-broken" && i.Level == CompatLevel.Error), "по журналу: мод для 0.6 падает в 0.7 → ошибка с причиной из журнала");
        // окружение 0.6.5: тот же мод уже не «новее», но Unity 6 требует 0.7.3
        var e065 = FakeEnv(new Version(0, 6, 5, 0));
        check(e065.Issues.Any(i => i.Code == "loader-unity6" && i.Level == CompatLevel.Warning), "ML 0.6.5 + Unity 6 → предупреждение окружения");
        var r4 = CompatChecker.Check(new List<ModEntry> { a }, new Dictionary<string, ModManifest> { ["ModA"] = Mf("ModA") }, _ => true, e065, null);
        check(r4.For("ModA").Any(i => i.Code == "loader-newer"), "мод под 0.7.3 на 0.6.5 → «нужен ML 0.7»");
        var e055 = FakeEnv(new Version(0, 5, 7, 0));
        check(e055.Issues.Any(i => i.Code == "loader-old" && i.Level == CompatLevel.Error), "ML 0.5.7 → «устарел» (до Il2CppInterop)");

        // системные сборки с «чужой» нумерацией: Microsoft.VisualBasic.Core 11.0 входит в .NET 6
        check(CompatChecker.FrameworkNeeds("Microsoft.VisualBasic.Core", new Version(11, 0, 0, 0), new Version(11, 0, 0, 0), env.DotNet) == 0, "VisualBasic.Core 11.0 при рантайме с 11.0 → .NET не новее");
        check(CompatChecker.FrameworkNeeds("Microsoft.VisualBasic.Core", new Version(11, 0, 0, 0), null, env.DotNet) == 0, "VisualBasic.Core 11.0 без файла рантайма → не «.NET 11»");
        check(CompatChecker.FrameworkNeeds("Microsoft.VisualBasic.Core", new Version(13, 0, 0, 0), new Version(11, 0, 0, 0), env.DotNet) == 8, "VisualBasic.Core 13.0 при 11.0 в .NET 6 → нужен .NET 8");
        check(CompatChecker.FrameworkNeeds("System.Runtime", new Version(8, 0, 0, 0), new Version(6, 0, 0, 0), env.DotNet) == 8, "System.Runtime 8.0 → нужен .NET 8");
        var vb = Mod("UsesVb"); var mvb = Mf("UsesVb"); mvb.References["Microsoft.VisualBasic.Core"] = new Version(11, 0, 0, 0);
        var envVb = FakeEnv(); envVb.KnownWhere["Microsoft.VisualBasic.Core"] = "framework"; envVb.Known["Microsoft.VisualBasic.Core"] = @"C:\fake\dotnet\Microsoft.VisualBasic.Core.dll";
        var rvb = CompatChecker.Check(new List<ModEntry> { vb }, new Dictionary<string, ModManifest> { ["UsesVb"] = mvb }, _ => true, envVb, null);
        check(!rvb.For("UsesVb").Any(i => i.Code == "dotnet-newer"), "мод со ссылкой на Microsoft.VisualBasic.Core 11.0 → без «нужен .NET 11»");

        // расширение Starlight: без MelonInfo, но это мод (его запускает Starlight)
        ModEntry Exp(string id) { var e = Mod(id); e.Kind = "Expansion"; return e; }
        ModManifest ExpMf(string id, string min) { var f = Mf(id); f.IsMelon = false; f.MelonKind = f.MelonName = f.MelonVersion = f.MelonAuthor = ""; f.IsExpansion = true; f.MinStarlight = min; f.References["Starlight"] = new Version(4, 0, 4, 0); return f; }
        var ex = Exp("CoopExp"); var slOk = Mod("Starlight"); slOk.Version = "4.0.4";
        var rex = CompatChecker.Check(new List<ModEntry> { ex, slOk }, new Dictionary<string, ModManifest> { ["CoopExp"] = ExpMf("CoopExp", "4.0.4"), ["Starlight"] = Mf("Starlight") }, _ => true, env, null);
        check(!rex.For("CoopExp").Any(i => i.Code == "not-melon" || i.Code.StartsWith("starlight-")), "расширение Starlight + Starlight 4.0.4 → не «библиотека», без замечаний: " + string.Join("; ", rex.For("CoopExp")));
        var slOld = Mod("Starlight"); slOld.Version = "4.0.3";
        var rold = CompatChecker.Check(new List<ModEntry> { ex, slOld }, new Dictionary<string, ModManifest> { ["CoopExp"] = ExpMf("CoopExp", "4.0.4"), ["Starlight"] = Mf("Starlight") }, _ => true, env, null);
        check(rold.For("CoopExp").Any(i => i.Code == "starlight-older" && i.Level == CompatLevel.Error), "minimum_sce 4.0.4 при Starlight 4.0.3 → «нужен Starlight 4.0.4»");
        var rnone = CompatChecker.Check(new List<ModEntry> { ex }, new Dictionary<string, ModManifest> { ["CoopExp"] = ExpMf("CoopExp", "4.0.4") }, _ => true, env, null);
        check(rnone.For("CoopExp").Any(i => i.Code == "starlight-missing" && i.Level == CompatLevel.Error), "расширение без Starlight → «нужен Starlight»");

        // CompatibleWith: автор проверил моды вместе → общий префикс с отменой не «конфликт», а «общие хуки»
        var va = Mod("Vouching"); var vbm = Mod("Partner");
        var mva = Mf("Vouching"); var mvp = Mf("Partner");
        mva.UsesHarmony = mvp.UsesHarmony = true;
        mva.Hooks.Add(new HookTarget { Type = "Il2Cpp.PlayerModel", Method = "LoseHealth", Kind = "Prefix", SkipsOriginal = true, PatchClass = "Vouching.God" });
        mvp.Hooks.Add(new HookTarget { Type = "Il2Cpp.PlayerModel", Method = "LoseHealth", Kind = "Prefix", SkipsOriginal = true, PatchClass = "Partner.Hp" });
        var vmods = new List<ModEntry> { va, vbm };
        var vmfs = new Dictionary<string, ModManifest> { ["Vouching"] = mva, ["Partner"] = mvp };
        var rv0 = CompatChecker.Check(vmods, vmfs, _ => true, env, null);
        check(rv0.For("Vouching").Any(i => i.Code == "hook-conflict"), "без CompatibleWith два префикса с отменой → «конфликт хуков»");
        mva.CompatibleWith.Add("Partner");
        var rv1 = CompatChecker.Check(vmods, vmfs, _ => true, env, null);
        check(!rv1.For("Vouching").Any(i => i.Code == "hook-conflict") && !rv1.For("Partner").Any(i => i.Code == "hook-conflict")
              && rv1.For("Partner").Any(i => i.Code == "hook-shared" && i.Level == CompatLevel.Info && i.Text.Contains("CompatibleWith")),
              "CompatibleWith у одного из модов → у обоих только «общие хуки» (инфо) с пометкой о проверке");
        check(CompatChecker.Vouched(vbm, mvp, va, mva) && !CompatChecker.Vouched(vbm, mvp, a, Mf("ModA")), "Vouched симметричен и не распространяется на другие моды");
        // HookPolicy = cooperative: префикс с отменой срабатывает только при включённой функции, идёт последним,
        // а в HarmonyX чужие префиксы и постфиксы всё равно выполняются → это не конфликт. Чужие отмены и транспайлеры — конфликт.
        HookTarget H(string kind, bool skips, string cls, string method = "SpendEnergy") =>
            new HookTarget { Type = "Il2Cpp.PlayerModel", Method = method, Kind = kind, SkipsOriginal = skips, PatchClass = cls };
        var coopMod = Mod("CoopHooks"); var plainMod = Mod("PlainHooks"); var rudeMod = Mod("RudeHooks"); var ilMod = Mod("Rewriter");
        var mCoop = Mf("CoopHooks"); var mPlain = Mf("PlainHooks"); var mRude = Mf("RudeHooks"); var mIl = Mf("Rewriter");
        foreach (var cm in new[] { mCoop, mPlain, mRude, mIl }) cm.UsesHarmony = true;
        mCoop.Hooks.Add(H("Prefix", true, "CoopHooks.Energy"));
        mPlain.Hooks.Add(H("Prefix", false, "PlainHooks.Log")); mPlain.Hooks.Add(H("Postfix", false, "PlainHooks.Log"));
        mRude.Hooks.Add(H("Prefix", true, "RudeHooks.Block"));
        mIl.Hooks.Add(H("Transpiler", false, "Rewriter.IL"));
        var cmfs = new Dictionary<string, ModManifest> { ["CoopHooks"] = mCoop, ["PlainHooks"] = mPlain, ["RudeHooks"] = mRude, ["Rewriter"] = mIl };
        bool Conflict(ModEntry x, ModEntry y, string id) =>
            CompatChecker.Check(new List<ModEntry> { x, y }, cmfs, _ => true, env, null).For(id).Any(i => i.Code == "hook-conflict");
        check(Conflict(coopMod, plainMod, "CoopHooks"), "без HookPolicy префикс с отменой рядом с чужим префиксом → «конфликт хуков»");
        mCoop.CooperativeHooks = true;
        var rc = CompatChecker.Check(new List<ModEntry> { coopMod, plainMod }, cmfs, _ => true, env, null);
        check(!rc.For("CoopHooks").Any(i => i.Code == "hook-conflict") && !rc.For("PlainHooks").Any(i => i.Code == "hook-conflict")
              && rc.For("PlainHooks").Any(i => i.Code == "hook-shared" && i.Level == CompatLevel.Info && i.Text.Contains("HookPolicy"))
              && rc.For("CoopHooks").Any(i => i.Code == "hook-shared" && i.Text.Contains("HookPolicy")),
              "HookPolicy=cooperative + чужие префикс/постфикс → у обоих только «общие хуки» (инфо) с пометкой HookPolicy");
        check(Conflict(coopMod, rudeMod, "CoopHooks") && Conflict(coopMod, rudeMod, "RudeHooks"), "кооперативный мод + чужой некооперативный префикс с отменой → «конфликт хуков» остаётся");
        check(Conflict(coopMod, ilMod, "CoopHooks") && Conflict(coopMod, ilMod, "Rewriter"), "кооперативный мод + чужой транспайлер → «конфликт хуков» остаётся");
        mRude.CooperativeHooks = true;
        check(!Conflict(coopMod, rudeMod, "CoopHooks") && !Conflict(coopMod, rudeMod, "RudeHooks"), "два кооперативных префикса с отменой → без конфликта");
        mRude.CooperativeHooks = false;
        mCoop.Hooks.Add(H("Transpiler", false, "CoopHooks.IL", "Update"));
        check(Conflict(coopMod, plainMod, "CoopHooks"), "HookPolicy не действует, если у мода есть свой транспайлер");
        mCoop.Hooks.RemoveAt(mCoop.Hooks.Count - 1);
        check(CompatChecker.Facts(mCoop).Contains("кооперативные хуки") && !CompatChecker.Facts(mPlain).Contains("кооперативные хуки"), "подробности показывают «кооперативные хуки»");
    }

    // ───────────── бэкапы сохранений ─────────────

    static Dictionary<string, string> Snapshot(string dir) =>
        SaveBackups.Collect(dir).ToDictionary(f => f.Rel, f => SaveBackups.Sha256File(f.Full), StringComparer.OrdinalIgnoreCase);

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static void RewriteZip(string src, string dst, Action<ZipArchive> edit)
    {
        File.Copy(src, dst, true);
        using var z = ZipFile.Open(dst, ZipArchiveMode.Update);
        edit(z);
    }

    static void Backups(string root, Action<bool, string> check)
    {
        string? real = SaveBackups.FindSaveRoot();
        Dictionary<string, string>? realBefore = null;
        string saves = Path.Combine(root, "LocalLow", "MonomiPark", "SlimeRancher2");
        if (real != null && SaveBackups.Collect(real).Count > 0)
        {
            realBefore = Snapshot(real);
            CopyDir(real, saves);   // реальные сохранения только читаем: дальше работаем с копией
            Console.WriteLine($"  копия реальных сохранений: {real} → временная папка ({realBefore.Count} файлов)");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(saves, "Steam", "76561198000000000"));
            for (int i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(saves, "Steam", "76561198000000000", $"2026101315540{i}_{i}.sav"), Encoding.UTF8.GetBytes(new string((char)('a' + i), 50000 + i)));
            Console.WriteLine("  (реальных сохранений нет — синтетические)");
        }
        // мусор, который не должен попасть в бэкап и не должен трогаться при восстановлении
        Directory.CreateDirectory(Path.Combine(saves, "Unity", "cache"));
        File.WriteAllText(Path.Combine(saves, "Unity", "cache", "test-sr2mm.bin"), "unity cache");
        File.WriteAllText(Path.Combine(saves, "Player-sr2mm-test.log"), "log");
        var scope = SaveBackups.Collect(saves);
        check(scope.Count > 0 && scope.All(f => !f.Rel.StartsWith("Unity/") && !(f.Rel.EndsWith(".log") && !f.Rel.Contains('/'))), $"в бэкап входят сохранения ({scope.Count} файлов), без Unity\\ и Player.log");
        var orig = Snapshot(saves);
        string bdir = Path.Combine(root, "Backups", "Saves");

        var sw = Stopwatch.StartNew();
        var b1 = SaveBackups.Create(saves, bdir, "manual", "тест", "1.3.0", new[] { "CreatorToolkit 0.6.0" });
        long tCreate = sw.ElapsedMilliseconds;
        sw.Restart();
        var v1 = SaveBackups.Verify(b1.Path);
        Console.WriteLine($"  бэкап {b1.FileName}: файлов {b1.Files}, {b1.Bytes / 1024.0:0} КБ → zip {b1.ZipSize / 1024.0:0} КБ за {tCreate} мс; проверка {sw.ElapsedMilliseconds} мс: {v1.Summary}");
        check(File.Exists(b1.Path) && !File.Exists(b1.Path + ".part") && b1.Files == scope.Count, "бэкап создан одним действием, без .part");
        check(v1.Ok && v1.Files == scope.Count, "проверка целостности: все SHA-256 совпали");
        var listed = SaveBackups.List(bdir);
        check(listed.Count == 1 && listed[0].Readable && listed[0].Reason == "manual" && listed[0].Note == "тест", "бэкап виден в списке с причиной и заметкой");
        check(SaveBackups.UpToDate(saves, bdir)?.Path == b1.Path, "сохранения не менялись → новый бэкап перед обновлением не нужен");

        // порча архива
        string bad1 = Path.Combine(root, "bad-bytes.zip");
        File.Copy(b1.Path, bad1);
        using (var fs = new FileStream(bad1, FileMode.Open, FileAccess.ReadWrite))
        {
            long at = fs.Length / 3;
            for (int k = 0; k < 16; k++) { fs.Position = at + k * 97; int v = fs.ReadByte(); fs.Position = at + k * 97; fs.WriteByte((byte)(v ^ 0x5A)); }
        }
        var vb1 = SaveBackups.Verify(bad1);
        check(!vb1.Ok, "испорченные байты внутри → бэкап не прошёл проверку: " + vb1.Summary);
        string bad2 = Path.Combine(root, "bad-trunc.zip");
        File.WriteAllBytes(bad2, File.ReadAllBytes(b1.Path).Take((int)(new FileInfo(b1.Path).Length / 2)).ToArray());
        check(!SaveBackups.Verify(bad2).Ok, "обрезанный архив → не прошёл проверку");
        string bad3 = Path.Combine(root, "bad-hash.zip");
        RewriteZip(b1.Path, bad3, z =>
        {
            var e = z.GetEntry("manifest.json")!;
            SaveBackupManifest mf;
            using (var s = e.Open()) mf = JsonSerializer.Deserialize<SaveBackupManifest>(s)!;
            mf.Files[0].Sha256 = new string('0', 64);
            e.Delete();
            using var w = z.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(w, mf);
        });
        check(SaveBackups.Verify(bad3).Problems.Any(p => p.Contains("SHA-256")), "подменённый хэш в описи → «не совпадает SHA-256»");
        string bad4 = Path.Combine(root, "bad-extra.zip");
        RewriteZip(b1.Path, bad4, z => { using var w = new StreamWriter(z.CreateEntry("saves/../../evil.txt").Open()); w.Write("x"); });
        check(SaveBackups.Verify(bad4).Problems.Any(p => p.Contains("опасный")), "путь с .. в архиве → отклонён");
        string bad5 = Path.Combine(root, "bad-nomanifest.zip");
        RewriteZip(b1.Path, bad5, z => z.GetEntry("manifest.json")!.Delete());
        check(!SaveBackups.Verify(bad5).Ok, "архив без описи → не бэкап");

        // восстановление: меняем «игру» — правим, удаляем и добавляем файлы
        var files = SaveBackups.Collect(saves);
        var big = files.OrderByDescending(f => new FileInfo(f.Full).Length).First();
        File.AppendAllText(big.Full, "changed by test");
        if (files.Count > 1) File.Delete(files.First(f => f.Full != big.Full).Full);
        string extraDir = Path.GetDirectoryName(big.Full)!;
        File.WriteAllText(Path.Combine(extraDir, "20991231235959_9.sav"), "new save");
        var changed = Snapshot(saves);
        check(SaveBackups.UpToDate(saves, bdir) == null, "сохранения изменились → перед обновлением нужен свежий бэкап");

        var rGame = SaveBackups.Restore(b1.Path, saves, bdir, () => true);
        check(!rGame.Ok && Same(Snapshot(saves), changed), "игра запущена → восстановление отказано, файлы не тронуты");
        var rBad = SaveBackups.Restore(bad1, saves, bdir, () => false);
        check(!rBad.Ok && rBad.SafetyBackup.Length == 0 && Same(Snapshot(saves), changed), "повреждённый бэкап → отказ до любых изменений");

        // откат: файл в папке сохранений открыт без права на удаление/переименование → перенос падает → всё возвращается
        int before = SaveBackups.List(bdir).Count;
        using (var lockIt = new FileStream(Path.Combine(extraDir, "20991231235959_9.sav"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var rLock = SaveBackups.Restore(b1.Path, saves, bdir, () => false);
            Console.WriteLine($"  восстановление с занятым файлом: ok={rLock.Ok}, откат={rLock.RolledBack}, ошибка: {rLock.Error}");
            check(!rLock.Ok && rLock.RolledBack, "сбой посреди восстановления → откат");
        }
        check(Same(Snapshot(saves), changed), "после отката сохранения в точности как до попытки");
        check(!Directory.GetDirectories(Path.GetDirectoryName(saves)!).Any(d => Path.GetFileName(d).StartsWith(".sr2mm-")), "временные папки восстановления убраны");

        sw.Restart();
        var r = SaveBackups.Restore(b1.Path, saves, bdir, () => false);
        Console.WriteLine($"  восстановление: ok={r.Ok} за {sw.ElapsedMilliseconds} мс; {string.Join(" · ", r.Log)}{(r.Error.Length > 0 ? " · ошибка: " + r.Error : "")}");
        check(r.Ok && Same(Snapshot(saves), orig), "восстановление вернуло сохранения байт в байт (SHA-256)");
        check(File.Exists(Path.Combine(saves, "Unity", "cache", "test-sr2mm.bin")) && File.Exists(Path.Combine(saves, "Player-sr2mm-test.log")), "кэш Unity и Player.log не тронуты");
        check(r.SafetyBackup.Length > 0 && SaveBackups.Verify(r.SafetyBackup).Ok, "перед восстановлением сделан страховочный бэкап, он цел");
        var safety = SaveBackups.Verify(r.SafetyBackup).Manifest!;
        check(safety.Reason == "before-restore" && safety.Files.Any(f => f.Path.EndsWith("20991231235959_9.sav")), "в страховочном бэкапе — состояние до восстановления (с новым файлом)");
        var back = SaveBackups.Restore(r.SafetyBackup, saves, bdir, () => false);
        check(back.Ok && Same(Snapshot(saves), changed), "из страховочного бэкапа можно вернуться обратно");
        check(SaveBackups.List(bdir).Count >= before + 2, "все бэкапы на месте");

        // хранение: автоматических не больше 20, ручные не удаляются
        string small = Path.Combine(root, "small-saves");
        Directory.CreateDirectory(small);
        File.WriteAllText(Path.Combine(small, "a.sav"), "a");
        string pdir = Path.Combine(root, "prune");
        SaveBackups.Create(small, pdir, "manual");
        for (int i = 0; i < 23; i++) SaveBackups.Create(small, pdir, "before-update", "#" + i);
        var pl = SaveBackups.List(pdir);
        check(pl.Count(b => b.Auto) == SaveBackups.KeepAuto && pl.Count(b => !b.Auto) == 1 && pl.First(b => b.Auto).Note == "#22", $"хранится {SaveBackups.KeepAuto} последних автоматических + ручные ({pl.Count} всего)");

        if (real != null && realBefore != null)
            check(Same(Snapshot(real), realBefore), "реальные сохранения не изменились (только чтение)");
    }

    // ───────────── пресеты ─────────────

    static string b1Zip(string root) => Directory.GetFiles(Path.Combine(root, "Backups", "Saves"), "*.zip").First();

    static void PresetsTest(string? realMods, string root, Action<bool, string> check)
    {
        string game = Path.Combine(root, "preset-game");
        string mods = Path.Combine(game, "Mods");
        Directory.CreateDirectory(Path.Combine(mods, "Disabled"));
        File.WriteAllText(Path.Combine(game, "SlimeRancher2.exe"), "fake");
        int copied = 0;
        if (realMods != null && Directory.Exists(realMods))
        {
            foreach (var f in Directory.GetFiles(realMods, "*.dll")) { File.Copy(f, Path.Combine(mods, Path.GetFileName(f))); copied++; }
            string dis = Path.Combine(realMods, "Disabled");
            if (Directory.Exists(dis)) foreach (var f in Directory.GetFiles(dis, "*.dll")) { File.Copy(f, Path.Combine(mods, "Disabled", Path.GetFileName(f))); copied++; }
        }
        if (copied < 2)
        {   // без реальных модов — копии тестовой сборки под разными именами
            string me = typeof(ExtraTests).Assembly.Location;
            File.Copy(me, Path.Combine(mods, "FakeA.dll")); File.Copy(me, Path.Combine(mods, "FakeB.dll")); File.Copy(me, Path.Combine(mods, "Disabled", "FakeC.dll"));
        }
        var store = new ModStore(game);
        var list = store.Scan();
        var on = list.Where(m => m.Enabled).ToList();
        var p = Presets.Build("Мой набор", on, "1.3.0", "0.7.3 Open-Beta");
        check(p.Mods.Count == on.Count && p.Mods.All(m => m.Sha256.Length == 64 && m.Size > 0), $"пресет из включённых модов: {string.Join(", ", p.Mods.Select(m => m.Id + " " + m.Version))}");
        string json = Path.Combine(root, "preset.json");
        string zip = Path.Combine(root, "preset.zip");
        Presets.Save(p, json);
        Presets.Save(Presets.Build("Мой набор", on), zip, on);
        var lj = Presets.Load(json);
        var lz = Presets.Load(zip);
        check(!lj.IsZip && lj.Preset.Name == "Мой набор" && lj.Preset.Mods.Count == on.Count && lj.Bundled.Count == 0, "JSON: имя и моды прочитаны");
        check(lz.IsZip && lz.Bundled.Count == on.Count && lz.Problems.Count == 0, "zip: DLL внутри, SHA-256 проверены");
        check(File.ReadAllText(json).Contains("\"format\": \"sr2mm-preset\"") && File.ReadAllText(json).Contains("Мой набор"), "JSON читаемый (кириллица без экранирования)");
        var same = Presets.Plan(lj, list);
        check(same.Enable.Count == 0 && same.Disable.Count == 0 && same.Missing.Count == 0 && same.OtherVersion.Count == 0, "тот же набор → менять нечего");

        // другой компьютер: одного мода нет, другой выключен, третий лишний включён
        var first = on[0];
        var second = on.Count > 1 ? on[1] : null;
        var extra = list.FirstOrDefault(m => !m.Enabled);
        var desired = list.ToDictionary(m => m.Id, m => m.Enabled, StringComparer.OrdinalIgnoreCase);
        if (second != null) desired[second.Id] = false;
        if (extra != null) desired[extra.Id] = true;
        store.Apply(desired);
        File.Delete(Path.Combine(mods, first.FileName));
        var now = store.Scan();
        var plan = Presets.Plan(lj, now);
        check(plan.Missing.Any(m => m.Id == first.Id) && plan.Install.Count == 0, "JSON: отсутствующий мод — в «нет у вас»");
        check(second == null || plan.Enable.Any(m => m.Id == second.Id), "выключенный мод из пресета включится");
        check(extra == null || plan.Disable.Any(m => m.Id == extra.Id), "лишний включённый мод выключится");
        var planZ = Presets.Plan(lz, now);
        check(planZ.Install.Any(m => m.Id == first.Id) && planZ.Missing.Count == 0, "zip: отсутствующий мод установится из пресета");
        var paths = Presets.ExtractBundled(zip, planZ.Install, Path.Combine(root, "preset-extract"));
        var res = store.Install(paths);
        var after = store.Scan();
        var inst = after.FirstOrDefault(m => m.Id == first.Id);
        check(res.Ok && inst != null && SaveBackups.Sha256File(inst.Path) == p.Mods.First(m => m.Id == first.Id).Sha256, "мод из пресета установлен байт в байт");
        var sel = Presets.Plan(lz, after).Selection;
        store.Apply(sel);
        var final = store.Scan();
        check(final.Where(m => m.Enabled).Select(m => m.Id).OrderBy(s => s).SequenceEqual(on.Select(m => m.Id).OrderBy(s => s)), "после импорта набор совпал с пресетом");

        // испорченная DLL внутри пресета не ставится
        string badZip = Path.Combine(root, "preset-bad.zip");
        RewriteZip(zip, badZip, z =>
        {
            var e = z.GetEntry("mods/" + first.FileName)!;
            e.Delete();
            using var w = z.CreateEntry("mods/" + first.FileName).Open();
            w.Write(new byte[] { 1, 2, 3 });
        });
        var lb = Presets.Load(badZip);
        check(!lb.Bundled.Contains(first.Id) && lb.Problems.Count > 0, "подменённая DLL в пресете → не установится: " + string.Join("; ", lb.Problems));
        bool t1 = false, t2 = false, t3 = false;
        try { Presets.Load(json.Replace("preset.json", "not-a-preset.json")); } catch (FileNotFoundException) { t1 = true; }
        File.WriteAllText(Path.Combine(root, "junk.json"), "{\"format\":\"other\"}");
        try { Presets.Load(Path.Combine(root, "junk.json")); } catch (InvalidDataException) { t2 = true; }
        try { Presets.Load(b1Zip(root)); } catch (InvalidDataException) { t3 = true; }
        check(t1 && t2 && t3, "чужой файл (другой JSON, бэкап сохранений) не принимается за пресет");
    }
}
