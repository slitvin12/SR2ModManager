#nullable disable
// KnownMods.cs — справочник модов Slime Rancher 2 с Nexus Mods (nexusmods.com/slimerancher2) и трёх модов с официальной
// поддержкой в Ranching Together (github.com/pyeight/SlimeRancher2Multiplayer, SR2MP/Shared/ModSupport).
// Узнаём мод по имени из MelonInfo или имени сборки (без учёта регистра, пробелов и знаков): ссылка на страницу
// Nexus, если автор её не указал, и факты для подробностей. «Проверен с Creator Toolkit» — хуки сверены по исходникам
// или IL релизной DLL. Только данные и поиск, без сети.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SR2ModManager.Core;

public sealed class KnownMod
{
    public int NexusId;
    public string Name = "";
    public string Author = "";
    public string[] Aliases = Array.Empty<string>();  // MelonInfo / имя сборки / id Starlight, если отличаются от имени на Nexus
    public bool RanchingTogether;                    // «Official Mod Support» в Ranching Together
    public string CreatorToolkit = "";               // итог проверки с Creator Toolkit; "" — не проверялся (нет исходников и DLL)
    public string Note = "";                        // не мод MelonLoader, библиотека и т. п.
    public string Url => "https://www.nexusmods.com/slimerancher2/mods/" + NexusId;
}

public static class KnownMods
{
    private const string NoShared = "общих хуков нет";

    private static KnownMod M(int id, string name, string author, string ct = "", bool rt = false, string note = "", params string[] aliases) =>
        new() { NexusId = id, Name = name, Author = author, CreatorToolkit = ct, RanchingTogether = rt, Note = note, Aliases = aliases };

    public static readonly IReadOnlyList<KnownMod> All = new[]
    {
        M(5, "Growable Moondew Nectar", "Atmudia", NoShared),
        M(7, "Continuous Sprint", "Ludovix2812", NoShared),
        M(9, "TarrFireSlime", "FhpSlime", NoShared, aliases: new[] { "焦油史莱姆" }),
        M(12, "Rich Presence Mod", "Atmudia", NoShared),
        M(14, "Cube Slimes", "MegaPiggy", "общий TimeDirector.Update (постфикс) — совместимо, хуки Creator Toolkit кооперативные"),
        M(16, "Glue Slimes", "YLohkuhl", NoShared, aliases: new[] { "GlueSlime" }),
        M(18, "Enhanced Plorts", "YLohkuhl", NoShared),
        M(25, "Infinite Energy", "GoldTarrRancher", "общий PlayerDeathHandler.OnDeath (постфикс) — совместимо, хуки Creator Toolkit кооперативные"),
        M(28, "Scuttle Bugs Begone", "MysteriousAeon and Atmudia", NoShared, aliases: new[] { "ScuttleBugsBegone_.NET6.0" }),
        M(31, "More Languages", "Atmudia", NoShared, aliases: new[] { "MoreLanguagesMod" }),
        M(42, "More Vaccables", "Atmudia", "«Заполнить» и автопополнение следуют лимиту слота этого мода", aliases: new[] { "MoreVaccablesMod" }),
        M(45, "Vacuum Modifications", "BreadCh4n", "«Заполнить» и автопополнение следуют лимиту слота этого мода", true),
        M(50, "Cotton Library (CORE MOD)", "PinkTarr", NoShared, note: "библиотека для других модов", aliases: new[] { "CottonLibrary" }),
        M(52, "More Yolky Slimes", "markekraus", NoShared),
        M(53, "BepInEx for Slime Rancher 2", "BepInEx team", note: "другой загрузчик (BepInEx) — не ставьте его вместе с MelonLoader", aliases: new[] { "BepInEx" }),
        M(57, "Infinite Health", "markekraus", "оба отменяют PlayerModel.LoseHealth — дублирует «Бессмертие», совместимо (CompatibleWith)"),
        M(59, "Pop All Gordos", "markekraus", NoShared),
        M(60, "Starlight (formerly SR2E)", "ThatFinn", "общие хуки согласованы (CompatibleWith)", note: "фреймворк модов и расширений", aliases: new[] { "Starlight", "SR2E" }),
        M(61, "Unlock All Maps", "markekraus", NoShared),
        M(63, "Unlock All Ranch Expansions", "markekraus", NoShared),
        M(66, "SUNBEAR", "YLohkuhl", NoShared),
        M(68, "Walk On Water", "markekraus", NoShared),
        M(70, "Infinite AirNet", "ThatFinn", NoShared, aliases: new[] { "Infinity AirNet" }),
        M(71, "Skip Intro Sequence", "markekraus", NoShared),
        M(72, "More Slimefall", "YLohkuhl", NoShared),
        M(74, "Better Gadgets", "Alez", NoShared, aliases: new[] { "BetterFan" }),
        M(76, "Chknn Rancher 3", "Atmudia", NoShared, aliases: new[] { "ChknnRancher", "ChknnRancherMod" }),
        M(78, "Accelerator Things", "Lionmeow", NoShared),
        M(79, "Plot Hider", "Lionmeow", NoShared),
        M(82, "Custom Spawner Upgrade", "YLohkuhl", NoShared, aliases: new[] { "SpawnerUpgrade" }),
        M(96, "Resource - Treasure Pod Scanner Mod", "AlexanderR123", NoShared, aliases: new[] { "ResourceScannerMod" }),
        M(97, "Rubber Slimes (SR2 Remake)", "aureumapes", NoShared, aliases: new[] { "Rubber Slimes", "RubberSlimesSR2" }),
        M(98, "Dream Slimes - SR2 Remake", "aureumapes", NoShared, aliases: new[] { "Dream Slimes", "DreamSlimesSR2" }),
        M(106, "Italian translation", "TurinaR"),
        M(108, "Gadget Changes", "lizzerc", NoShared),
        M(109, "Keep A Single Piece Of Food In Feeders", "BreadCh4n", NoShared, aliases: new[] { "SingleFoodInFeeder" }),
        M(110, "Drone Changes", "BreadCh4n", NoShared),
        M(112, "Slime Modification", "shizophrenicgopher", NoShared, aliases: new[] { "Modify Slimes" }),
        M(113, "Shadow Rancher", "YLohkuhl", NoShared),
        M(115, "Interpolator", "PieKing1215", NoShared, aliases: new[] { "SR2Interpolator" }),
        M(117, "No Greenhouse", "ThatFinn", NoShared, aliases: new[] { "No House" }),
        M(118, "Multiplayer - Ranching Together", "Shark", "общие хуки согласованы (CompatibleWith), в коопе — правила MultiplayerPolicy", note: "мультиплеер (расширение Starlight)", aliases: new[] { "Ranching Together", "SR2MP" }),
        M(119, "Fov Changer", "huoyan1231", NoShared, aliases: new[] { "SR2FovChanger" }),
        M(121, "Cheat Table -- outdated", "Dark-SintrixTeam", note: "таблица Cheat Engine, не мод"),
        M(123, "Dynamic HUD", "DrParanoia", "общие PlayerState.Damage и SpendEnergy (постфиксы) — совместимо, «Скрыть HUD» работает через штатный флаг"),
        M(125, "PhosphorAlwaysActive", "PigPizza", NoShared),
        M(127, "Fix First Person Object Viewbob", "Brynzananas", NoShared),
        M(128, "Interpolate All Rigid Bodies", "Brynzananas", NoShared),
        M(129, "Remove Gadget Cooldown When Placed", "Brynzananas", NoShared),
        M(131, "Slime Rancher 2 Performance Suite (FPS Counter and Frame Limiter)", "Damian0O0", NoShared, aliases: new[] { "FPS Counter", "Background FPS Limiter" }),
        M(134, "Smooth Zoom", "DamianT0Zi0mal", NoShared),
        M(137, "Simple Crouch", "Damian0O0", NoShared),
        M(138, "Name Your Save", "Damiano", NoShared),
        M(141, "Radiant Chime", "Damian0O0", NoShared, aliases: new[] { "RadiantIndicator" }),
        M(145, "Ultimate Vacpack Overhaul", "Damian0O0", "общих хуков нет; «Заполнить» и автопополнение следуют лимиту слота этого мода", aliases: new[] { "UltimateVacpackOverhaul" }),
        M(147, "Pulse Vacuum", "Damian0O0", NoShared),
        M(153, "Rancher Minimap", "Contomo", NoShared),
        M(154, "SR2ESP", "pixiemays", NoShared),
        M(155, "Rancher's Titles", "Damian0O0", NoShared),
        M(156, "Classic Flashlight", "Damian0O0", NoShared),
        M(158, "Peculiar Slimes", "ThatFinn", NoShared),
        M(161, "Slimy Ranger (Google Translate Edition)", "ThatFinn", NoShared, aliases: new[] { "Slimy Ranger" }),
        M(162, "SR2 Gyro Aim", "FunkyBumbleton", NoShared),
        M(165, "Upgraded Linked Cannon", "ShadowSneak", NoShared, aliases: new[] { "Linked Cannon Upgrade" }),
        M(172, "Custom Ranch Builder", "alkaelarrogante1", NoShared),
        M(177, "Better Drones", "Shark", NoShared, true, aliases: new[] { "de.pyeight.betterdrones" }),
        M(179, "Placement Improvements", "Lunarsnail0", "размещение гаджетов Creator Toolkit идёт через штатный GadgetItem — правила этого мода действуют", true, aliases: new[] { "BuildImprovements", "nl.lunarsnail.buildimprovements" }),
        M(191, "Map Tweaks", "pmbroad", NoShared),
        M(193, "Slime rancher 2 Infinite energy mod", "cpajor", NoShared, aliases: new[] { "SR2_INF" }),
        M(197, "Slime Rancher 2 Thai language", "ooiiza123"),
    };

    /// «Vacuum Modifications», «VacuumModifications» и «vacuum-modifications» — один ключ.
    public static string Norm(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var b = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c));
        return b.ToString();
    }

    private static readonly Dictionary<string, KnownMod> ByKey = Build();

    private static Dictionary<string, KnownMod> Build()
    {
        var d = new Dictionary<string, KnownMod>(StringComparer.Ordinal);
        foreach (var k in All)
            foreach (var n in new[] { k.Name }.Concat(k.Aliases))
            {
                string key = Norm(n);
                if (key.Length > 0 && !d.ContainsKey(key)) d[key] = k;
            }
        return d;
    }

    /// По имени из MelonInfo, имени сборки или id Starlight (в таком порядке).
    public static KnownMod Find(params string[] names)
    {
        foreach (var n in names)
            if (ByKey.TryGetValue(Norm(n), out var k)) return k;
        return null;
    }

    /// Факты для подробностей: «Nexus Mods #45», «официальная поддержка в Ranching Together», «проверен с Creator Toolkit: …».
    public static List<string> Facts(KnownMod k)
    {
        var f = new List<string>();
        if (k == null) return f;
        f.Add("Nexus Mods #" + k.NexusId);
        if (k.Note.Length > 0) f.Add(k.Note);
        if (k.RanchingTogether) f.Add("официальная поддержка в Ranching Together");
        if (k.CreatorToolkit.Length > 0) f.Add("проверен с Creator Toolkit: " + k.CreatorToolkit);
        return f;
    }
}
