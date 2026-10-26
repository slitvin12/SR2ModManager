#nullable disable
// ModCore.cs — общая логика менеджера модов SR2 (используется и лаунчером, и плагином в игре).
// Включение/выключение = перенос DLL между Mods\ и Mods\Disabled\.
// MelonLoader 0.7.3 сам пропускает подпапки с именем "Disabled" (MelonFolderHandler._nameExclusions),
// а в корне Mods\ грузит только *.dll — поэтому выключенные моды гарантированно не загружаются.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IOPath = System.IO.Path;

namespace SR2ModManager.Core;

public sealed class ModEntry
{
    public string Id = "";            // имя файла без .dll — ключ во всех конфигах
    public string FileName = "";      // SlimeLive.dll
    public string Path = "";          // текущий полный путь
    public bool Enabled;              // лежит в Mods\ (true) или Mods\Disabled\ (false)
    public bool Readable;             // удалось ли прочитать метаданные
    public string AssemblyName = "";
    public string Name = "";          // из MelonInfo
    public string Version = "";
    public string Author = "";
    public string Description = "";
    public string Kind = "";          // Mod / Plugin / "" (не мелон)
    public long Size;
    public DateTime Modified;
    public List<string> References = new();      // имена сборок, на которые ссылается DLL
    public List<string> MelonDependencies = new(); // MelonAdditionalDependencies
    public List<string> DependsOn = new();       // Id других модов из папки (вычисляется в Scan)
    public bool DuplicateInDisabled;             // есть и в Mods\, и в Mods\Disabled\
    public string Link = "";                     // MelonInfo downloadLink (если автор указал)
    public List<string> Games = new();           // MelonGame: для каких игр собран (пусто — для любой)

    // ── только для «доступных» модов каталога (Библиотека, Загрузки): ещё не установлены
    public bool Available;
    public string SourceLabel = "";              // «Библиотека», «Загрузки», имя папки
    public string EntryName = "";                // путь DLL внутри .zip ("" — сам файл .dll)

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public ModEntry Clone()
    {
        var c = (ModEntry)MemberwiseClone();
        c.References = new List<string>(References);
        c.MelonDependencies = new List<string>(MelonDependencies);
        c.DependsOn = new List<string>(DependsOn);
        c.Games = new List<string>(Games);
        return c;
    }
}

public sealed class Profile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("all")] public bool All { get; set; }
    [JsonPropertyName("mods")] public List<string> Mods { get; set; } = new();
}

public sealed class ManagerConfig
{
    [JsonPropertyName("profiles")] public List<Profile> Profiles { get; set; } = new();
    [JsonPropertyName("lastProfile")] public string LastProfile { get; set; } = "";
    [JsonPropertyName("launchVia")] public string LaunchVia { get; set; } = "steam"; // steam | exe
    [JsonPropertyName("descriptions")] public Dictionary<string, string> Descriptions { get; set; } = new();
    [JsonPropertyName("extraDependencies")] public Dictionary<string, List<string>> ExtraDependencies { get; set; } = new();
    [JsonPropertyName("conflicts")] public List<List<string>> Conflicts { get; set; } = new();
    /// Дополнительные папки, где лаунчер ищет новые версии и доступные моды (кроме ModManager\Library и «Загрузок»).
    [JsonPropertyName("catalogFolders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> CatalogFolders { get; set; }
    /// Искать моды в папке «Загрузки» (по умолчанию да; false — выключить).
    [JsonPropertyName("scanDownloads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ScanDownloads { get; set; }
    /// Положение окна лаунчера (необязательно; старые версии просто игнорируют поле).
    [JsonPropertyName("window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowPlacement Window { get; set; }

    public static ManagerConfig CreateDefault() => new()
    {
        Profiles = new()
        {
            new Profile { Name = "Все моды", All = true },
            new Profile { Name = "Ваниль (без модов)" },
            new Profile { Name = "Стрим (SlimeLive)", Mods = new() { "CreatorToolkit", "SlimeLive" } },
            new Profile { Name = "Только Creator Toolkit", Mods = new() { "CreatorToolkit" } },
            new Profile { Name = "Minecraft HUD", Mods = new() { "MinecraftHUD" } },
        },
        Descriptions = new()
        {
            ["CreatorToolkit"] = "Меню создателя (F1 / Insert): игрок, спавн, ресурсы, погода, открытия, съёмка, карта, удобства.",
            ["CreatorToolkitReviewDirector"] = "Служебный контроллер записи обзоров (Rec60). Спит, пока его не вызовут. Нужен Creator Toolkit.",
            ["MinecraftHUD"] = "Живой Minecraft внутри SR2 (F7 — HUD, F3). Нужен запущенный профиль Minecraft sr2-passthrough.",
            ["SlimeLive"] = "Интерактив для TikTok LIVE: гонки слаймов (F6), эффекты от зрителей (F8–F10). Нужен Creator Toolkit и мост slimelive-bridge.",
        },
        ExtraDependencies = new()
        {
            ["SlimeLive"] = new() { "CreatorToolkit" },
        },
    };
}

public sealed class WindowPlacement
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
    [JsonPropertyName("max")] public bool Maximized { get; set; }
    [JsonPropertyName("split")] public int Split { get; set; }
}

/// Отложенные изменения: пишет плагин в игре (DLL заняты, пока игра запущена),
/// применяет лаунчер или сам плагин при следующем старте ДО загрузки модов.
public sealed class PendingChanges
{
    [JsonPropertyName("created")] public DateTime Created { get; set; } = DateTime.Now;
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("desired")] public Dictionary<string, bool> Desired { get; set; } = new();
}

public sealed class ApplyResult
{
    public List<string> Log = new();
    public List<string> Errors = new();
    public int Moved;
    public List<string> TurnedOn = new();      // отображаемые имена включённых
    public List<string> TurnedOff = new();     // и выключенных модов
    public List<string> Installed = new();     // «Имя → Mods» для установки
    public List<string> InstalledIds = new();  // Id установленных модов (для выделения в списке)
    public bool Ok => Errors.Count == 0;
    public override string ToString() => string.Join(Environment.NewLine, Log.Concat(Errors.Select(e => "ОШИБКА: " + e)));

    /// Короткая сводка для строки состояния.
    public string Summary()
    {
        var parts = new List<string>();
        if (TurnedOn.Count > 0) parts.Add("включено: " + string.Join(", ", TurnedOn));
        if (TurnedOff.Count > 0) parts.Add("выключено: " + string.Join(", ", TurnedOff));
        if (Installed.Count > 0) parts.Add("установлено: " + string.Join(", ", Installed));
        if (parts.Count == 0 && Moved > 0) parts.Add(string.Join("; ", Log));
        if (Errors.Count > 0) parts.Add("ошибки: " + string.Join("; ", Errors));
        return parts.Count == 0 ? "ничего менять не нужно" : string.Join(" · ", parts);
    }
}

public sealed class ModLogStats
{
    public int Errors, Warnings;
    public string LastError = "", LastWarning = "";
    /// В журнале ошибки несовместимости (MissingMethodException и т. п.) — мод, скорее всего, устарел для этой версии игры.
    public bool Outdated;
    public string OutdatedReason = "";
}

/// Сводка MelonLoader\Latest.log: какие DLL загрузились и сколько ошибок/предупреждений у каждого мода.
public sealed class MelonLogInfo
{
    public string Path = "";
    public DateTime Updated;
    public string LoaderVersion = "", GameVersion = "";
    public HashSet<string> LoadedFiles = new(StringComparer.OrdinalIgnoreCase); // Id (имя файла без .dll)
    public Dictionary<string, ModLogStats> ByMod = new(StringComparer.OrdinalIgnoreCase);
    public ModLogStats Other = new();
    public int Lines;
    public int TotalErrors => ByMod.Values.Sum(s => s.Errors) + Other.Errors;
    public int TotalWarnings => ByMod.Values.Sum(s => s.Warnings) + Other.Warnings;
    public ModLogStats For(string id) => id != null && ByMod.TryGetValue(id, out var s) ? s : null;
    public bool WasLoaded(string id) => id != null && LoadedFiles.Contains(id);
}

/// Старая копия DLL мода (бэкап рядом с модом или в Disabled\_replaced).
public sealed class BackupInfo
{
    public string Path = "";
    public string FileName = "";
    public string Version = "";
    public string Where = "";   // Mods / Disabled / _replaced
    public DateTime Date;
    public long Size;
}


public static class MapUtil
{
    public static Dictionary<string, V> ToMap<T, V>(IEnumerable<T> src, Func<T, string> key, Func<T, V> val)
    {
        var d = new Dictionary<string, V>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in src) d[key(x)] = val(x);
        return d;
    }
}

public sealed class ModStore
{
    public const string SteamAppId = "1657630";
    public const string DisabledFolderName = "Disabled"; // имя, которое MelonLoader 0.7 игнорирует
    public const string GameProcessName = "SlimeRancher2";

    public string GameDir { get; }
    public string ModsDir => System.IO.Path.Combine(GameDir, "Mods");
    public string DisabledDir => System.IO.Path.Combine(ModsDir, DisabledFolderName);
    public string ReplacedDir => System.IO.Path.Combine(DisabledDir, "_replaced");
    public string ConfigDir => System.IO.Path.Combine(GameDir, "UserData", "ModManager");
    public string ConfigPath => System.IO.Path.Combine(ConfigDir, "profiles.json");
    public string PendingPath => System.IO.Path.Combine(ConfigDir, "pending.json");
    public string LogPath => System.IO.Path.Combine(ConfigDir, "modmanager.log");
    public string GameExe => System.IO.Path.Combine(GameDir, "SlimeRancher2.exe");
    public string UserDataDir => IOPath.Combine(GameDir, "UserData");
    public string PluginsDir => IOPath.Combine(GameDir, "Plugins");
    public string UserLibsDir => IOPath.Combine(GameDir, "UserLibs");
    public string ManagerDir => IOPath.Combine(GameDir, "ModManager");
    /// Библиотека: сюда можно класть .dll/.zip модов «про запас» — лаунчер покажет их как доступные или как обновления.
    public string LibraryDir => IOPath.Combine(ManagerDir, "Library");
    public string PluginPath => IOPath.Combine(PluginsDir, "SR2ModManager.dll");
    /// Новая версия плагина, отложенная build.ps1, пока игра была запущена (DLL плагина занята).
    public string StagedPluginPath => IOPath.Combine(ManagerDir, "update", "SR2ModManager.dll");
    public string MelonLogPath => IOPath.Combine(GameDir, "MelonLoader", "Latest.log");
    public string PreferencesPath => IOPath.Combine(UserDataDir, "MelonPreferences.cfg");

    public ModStore(string gameDir) { GameDir = System.IO.Path.GetFullPath(gameDir); }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ───────────── сканирование ─────────────

    public List<ModEntry> Scan()
    {
        var map = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(DisabledDir))
            foreach (var f in Directory.GetFiles(DisabledDir, "*.dll", SearchOption.TopDirectoryOnly))
                map[System.IO.Path.GetFileNameWithoutExtension(f)] = Read(f, false);
        if (Directory.Exists(ModsDir))
            foreach (var f in Directory.GetFiles(ModsDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string id = System.IO.Path.GetFileNameWithoutExtension(f);
                bool dup = map.ContainsKey(id);
                var e = Read(f, true);
                e.DuplicateInDisabled = dup;
                map[id] = e; // копия в Mods\ — «главная» (её загрузит игра)
            }
        var list = map.Values.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        ResolveDependencies(list, LoadConfig());
        return list;
    }

    public static void ResolveDependencies(List<ModEntry> mods, ManagerConfig cfg)
    {
        var byAsm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (!string.IsNullOrEmpty(m.AssemblyName)) byAsm[m.AssemblyName] = m.Id;
            byAsm[m.Id] = m.Id;
            if (!string.IsNullOrEmpty(m.Name)) byName[m.Name] = m.Id;
        }
        foreach (var m in mods)
        {
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in m.References) if (byAsm.TryGetValue(r, out var id) && !id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)) deps.Add(id);
            foreach (var r in m.MelonDependencies) if (byAsm.TryGetValue(r, out var id) || byName.TryGetValue(r, out id)) deps.Add(id); else deps.Add(r);
            if (cfg.ExtraDependencies.TryGetValue(m.Id, out var extra)) foreach (var r in extra) deps.Add(r);
            deps.Remove(m.Id);
            m.DependsOn = deps.OrderBy(x => x).ToList();
            if (string.IsNullOrWhiteSpace(m.Description) && cfg.Descriptions.TryGetValue(m.Id, out var d)) m.Description = d;
        }
    }

    /// Проблемы выбранного набора: id -> включён?
    public static List<string> CheckSelection(IReadOnlyList<ModEntry> mods, IDictionary<string, bool> selection, ManagerConfig cfg)
    {
        var warn = new List<string>();
        var ids = new HashSet<string>(mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        bool On(string id) => selection.TryGetValue(id, out var v) && v;
        foreach (var m in mods)
        {
            if (!On(m.Id)) continue;
            foreach (var d in m.DependsOn)
            {
                if (!ids.Contains(d)) warn.Add($"{m.DisplayName}: нужен «{d}», но такого мода нет в папке Mods.");
                else if (!On(d)) warn.Add($"{m.DisplayName}: нужен «{Display(mods, d)}» — он выключен.");
            }
        }
        foreach (var pair in cfg.Conflicts)
            if (pair.Count >= 2 && pair.All(On))
                warn.Add("Конфликт: " + string.Join(" + ", pair.Select(p => Display(mods, p))) + " не стоит включать вместе.");
        return warn;
    }

    /// Включает недостающие зависимости (рекурсивно). Возвращает список включённых.
    public static List<string> AddMissingDependencies(IReadOnlyList<ModEntry> mods, IDictionary<string, bool> selection)
    {
        var added = new List<string>();
        var byId = MapUtil.ToMap(mods, m => m.Id, m => m);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var m in mods)
            {
                if (!(selection.TryGetValue(m.Id, out var on) && on)) continue;
                foreach (var d in m.DependsOn)
                    if (byId.ContainsKey(d) && !(selection.TryGetValue(d, out var dOn) && dOn))
                    { selection[byId[d].Id] = true; added.Add(byId[d].Id); changed = true; }
            }
        }
        return added;
    }

    private static string Display(IReadOnlyList<ModEntry> mods, string id)
        => mods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;

    public static Dictionary<string, bool> SelectionForProfile(IReadOnlyList<ModEntry> mods, Profile p)
    {
        var set = new HashSet<string>(p.Mods ?? new(), StringComparer.OrdinalIgnoreCase);
        return MapUtil.ToMap(mods, m => m.Id, m => p.All || set.Contains(m.Id));
    }

    // ───────────── применение ─────────────

    public static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName(GameProcessName).Length > 0; } catch { return false; }
    }

    /// Переносит DLL так, чтобы состояние совпало с desired (id -> включён).
    /// Моды, которых нет в desired, не трогаются. Ничего не удаляет: при конфликте имён
    /// старая копия уезжает в Mods\Disabled\_replaced\.
    public ApplyResult Apply(IDictionary<string, bool> desired)
    {
        var res = new ApplyResult();
        var current = MapUtil.ToMap(Scan(), m => m.Id, m => m);
        foreach (var kv in desired)
        {
            if (!current.TryGetValue(kv.Key, out var mod)) { res.Log.Add($"пропуск: {kv.Key} — файла нет"); continue; }
            if (mod.Enabled == kv.Value) continue;
            string src = mod.Path;
            string dst = System.IO.Path.Combine(kv.Value ? ModsDir : DisabledDir, mod.FileName);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                if (File.Exists(dst)) StashReplaced(dst, res);
                File.Move(src, dst);
                res.Moved++;
                res.Log.Add((kv.Value ? "включён: " : "выключен: ") + mod.FileName);
                (kv.Value ? res.TurnedOn : res.TurnedOff).Add(mod.DisplayName);
            }
            catch (Exception e) { res.Errors.Add($"{mod.FileName}: {e.Message}"); }
        }
        AppendLog("apply", res);
        return res;
    }

    /// Убирает файл в Mods\Disabled\_replaced\<файл>.<дата>[-N].bak (ничего не удаляется насовсем).
    private string StashReplaced(string path, ApplyResult res)
    {
        Directory.CreateDirectory(ReplacedDir);
        string stem = IOPath.GetFileName(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string to = IOPath.Combine(ReplacedDir, stem + ".bak");
        for (int i = 2; File.Exists(to); i++) to = IOPath.Combine(ReplacedDir, stem + "-" + i + ".bak");
        File.Move(path, to);
        res?.Log.Add($"старая копия {IOPath.GetFileName(path)} сохранена в Disabled\\_replaced\\{IOPath.GetFileName(to)}");
        return to;
    }

    // ───────────── отложенные изменения ─────────────

    public PendingChanges LoadPending()
    {
        try { return File.Exists(PendingPath) ? JsonSerializer.Deserialize<PendingChanges>(File.ReadAllText(PendingPath), Json) : null; }
        catch { return null; }
    }

    public void SavePending(PendingChanges p)
    {
        Directory.CreateDirectory(ConfigDir);
        WriteAtomic(PendingPath, JsonSerializer.Serialize(p, Json));
    }

    public void ClearPending() { try { if (File.Exists(PendingPath)) File.Delete(PendingPath); } catch { } }

    /// Применяет pending.json, если он есть. Файл удаляется только при полном успехе.
    public ApplyResult ApplyPending()
    {
        var p = LoadPending();
        if (p == null) return null;
        var res = Apply(p.Desired);
        if (res.Ok) ClearPending();
        return res;
    }

    // ───────────── конфиг ─────────────

    public ManagerConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var c = JsonSerializer.Deserialize<ManagerConfig>(File.ReadAllText(ConfigPath), Json);
                if (c != null) { c.Profiles ??= new(); c.Descriptions ??= new(); c.ExtraDependencies ??= new(); c.Conflicts ??= new(); return c; }
            }
        }
        catch { }
        return ManagerConfig.CreateDefault();
    }

    public void SaveConfig(ManagerConfig c)
    {
        Directory.CreateDirectory(ConfigDir);
        WriteAtomic(ConfigPath, JsonSerializer.Serialize(c, Json));
    }

    public void EnsureConfig() { if (!File.Exists(ConfigPath)) SaveConfig(ManagerConfig.CreateDefault()); }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    public void AppendLog(string what, ApplyResult r)
    {
        try
        {
            if (r.Moved == 0 && r.Errors.Count == 0) return;
            Directory.CreateDirectory(ConfigDir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}{Environment.NewLine}{r}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { }
    }

    // ───────────── запуск ─────────────

    public void LaunchGame(string via)
    {
        if (via == "exe" && File.Exists(GameExe))
            Process.Start(new ProcessStartInfo(GameExe) { WorkingDirectory = GameDir, UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId) { UseShellExecute = true });
    }

    public static string FindGameDir(string hint = null)
    {
        var c = new List<string>();
        c.Add(hint);
        try
        {
            // лаунчер лежит в <игра>\ModManager\ или в <игра>\
            string baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            c.Add(baseDir);
            c.Add(System.IO.Path.GetDirectoryName(baseDir));
        }
        catch { }
        c.Add(@"D:\SteamLibrary\steamapps\common\Slime Rancher 2");
        c.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Slime Rancher 2");
        foreach (var d in c)
            if (!string.IsNullOrEmpty(d) && File.Exists(System.IO.Path.Combine(d, "SlimeRancher2.exe")) && Directory.Exists(System.IO.Path.Combine(d, "Mods")))
                return d;
        return null;
    }

    // ───────────── профили: какой набор сейчас выбран ─────────────

    /// Профиль, который даёт ровно такой выбор (с учётом авто-зависимостей). Предпочитает prefer.
    public static Profile FindMatchingProfile(IReadOnlyList<ModEntry> mods, IDictionary<string, bool> selection, ManagerConfig cfg, string prefer = null)
    {
        if (cfg?.Profiles == null || mods.Count == 0) return null;
        bool Same(Profile p)
        {
            var want = SelectionForProfile(mods, p);
            AddMissingDependencies(mods, want);
            foreach (var m in mods)
            {
                bool a = want.TryGetValue(m.Id, out var x) && x;
                bool b = selection.TryGetValue(m.Id, out var y) && y;
                if (a != b) return false;
            }
            return true;
        }
        var first = prefer == null ? null : cfg.Profiles.FirstOrDefault(p => string.Equals(p.Name, prefer, StringComparison.OrdinalIgnoreCase));
        if (first != null && Same(first)) return first;
        return cfg.Profiles.FirstOrDefault(Same);
    }

    /// Папка настроек мода в UserData (если есть): UserData\<Id> или UserData\<Name>.
    public string UserDataDirFor(ModEntry m)
    {
        if (m == null) return null;
        var names = new List<string> { m.Id, m.Name, (m.Name ?? "").Replace(" ", ""), m.AssemblyName };
        if (m.Id.Equals("SR2ModManager", StringComparison.OrdinalIgnoreCase)) names.Insert(0, "ModManager");
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n) || n.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0) continue;
            string d = IOPath.Combine(UserDataDir, n);
            if (Directory.Exists(d)) return d;
        }
        return null;
    }

    // ───────────── обновление самого плагина ─────────────

    /// Ставит плагин из ModManager\update, если игра закрыта. true — что-то сделано (message — итог).
    public bool TryApplyStagedPluginUpdate(out string message)
    {
        message = null;
        try
        {
            if (IsGameRunning()) return false;
            CleanupOldPlugin();
            if (!File.Exists(StagedPluginPath)) return false;
            Directory.CreateDirectory(PluginsDir);
            if (File.Exists(PluginPath)) File.Copy(PluginPath, PluginPath + ".bak", true);
            File.Copy(StagedPluginPath, PluginPath, true);
            File.Delete(StagedPluginPath);
            try { Directory.Delete(IOPath.GetDirectoryName(StagedPluginPath)); } catch { }
            string ver = Read(PluginPath, true).Version;
            var r = new ApplyResult { Moved = 1 };
            r.Log.Add("плагин менеджера обновлён до v" + ver);
            AppendLog("plugin-update", r);
            message = "Плагин менеджера в игре обновлён до v" + ver + ".";
            return true;
        }
        catch (Exception e)
        {
            message = "Не удалось обновить плагин менеджера: " + e.Message;
            return true;
        }
    }

    /// Удаляет Plugins\SR2ModManager.dll.old* — старую DLL, переименованную при установке во время игры.
    private void CleanupOldPlugin()
    {
        try
        {
            if (!Directory.Exists(PluginsDir)) return;
            foreach (var f in Directory.GetFiles(PluginsDir, "SR2ModManager.dll.old*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // ───────────── журнал MelonLoader ─────────────

    public MelonLogInfo ReadMelonLog(IReadOnlyList<ModEntry> mods)
    {
        try
        {
            var fi = new FileInfo(MelonLogPath);
            if (!fi.Exists) return null;
            string text;
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                const long Max = 6L << 20, Head = 1L << 20;
                if (fs.Length <= Max)
                {
                    using var sr = new StreamReader(fs, new UTF8Encoding(false), true);
                    text = sr.ReadToEnd();
                }
                else
                {
                    var head = new byte[Head];
                    int hn = ReadFully(fs, head);
                    fs.Seek(-(Max - Head), SeekOrigin.End);
                    var tail = new byte[Max - Head];
                    int tn = ReadFully(fs, tail);
                    text = Encoding.UTF8.GetString(head, 0, hn) + "\n" + Encoding.UTF8.GetString(tail, 0, tn);
                }
            }
            var info = ParseMelonLog(text.Split('\n'), mods);
            info.Path = fi.FullName;
            info.Updated = fi.LastWriteTime;
            return info;
        }
        catch { return null; }
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        int n = 0, r;
        while (n < buf.Length && (r = s.Read(buf, n, buf.Length - n)) > 0) n += r;
        return n;
    }

    private static readonly Regex LoadedRx = new(@"Melon Assembly loaded:\s*'([^']+)'", RegexOptions.CultureInvariant);
    /// Ошибки, по которым видно, что мод собран под другую версию игры или MelonLoader.
    private static readonly Regex OutdatedRx = new(@"MissingMethodException|MissingFieldException|TypeLoadException|EntryPointNotFoundException|Could not load type|Method not found|Field not found|is incompatible|Incompatible (?:Game|MelonLoader|Version)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// Разбор строк вида «[12:34:56.789] [WARNING] [Creator_Toolkit] текст».
    public static MelonLogInfo ParseMelonLog(IEnumerable<string> lines, IReadOnlyList<ModEntry> mods)
    {
        var info = new MelonLogInfo();
        var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byFile = new List<(string key, string id)>();
        foreach (var m in mods ?? Array.Empty<ModEntry>())
        {
            foreach (var k in new[] { m.Name, m.Name?.Replace(' ', '_'), m.Id, m.AssemblyName })
                if (!string.IsNullOrWhiteSpace(k) && !section.ContainsKey(k)) section[k] = m.Id;
            byFile.Add((m.FileName, m.Id));
            if (!string.IsNullOrWhiteSpace(m.Name) && m.Name.Length >= 4) byFile.Add(("\"" + m.Name + "\"", m.Id));
        }
        byFile.Sort((a, b) => b.key.Length.CompareTo(a.key.Length));

        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r', '\n', ' ');
            if (line.Length == 0) continue;
            info.Lines++;
            int p = 0;
            // [HH:mm:ss.fff]
            if (line.Length >= 14 && line[0] == '[' && line[13] == ']' && char.IsDigit(line[1])) p = 14;
            else continue; // продолжение многострочного сообщения (стек вызовов) — не считаем отдельно
            string level = null, sec = null;
            for (int guard = 0; guard < 4; guard++)
            {
                while (p < line.Length && line[p] == ' ') p++;
                if (p >= line.Length || line[p] != '[') break;
                int end = line.IndexOf(']', p + 1);
                if (end < 0 || end - p > 64) break;
                string tok = line.Substring(p + 1, end - p - 1).Trim();
                if (tok.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || tok.Equals("FATAL", StringComparison.OrdinalIgnoreCase)) level = "E";
                else if (tok.Equals("WARNING", StringComparison.OrdinalIgnoreCase) || tok.Equals("WARN", StringComparison.OrdinalIgnoreCase)) level = level ?? "W";
                else if (sec == null) sec = tok;
                p = end + 1;
            }
            string msg = p < line.Length ? line.Substring(p).Trim() : "";

            if (level == null && sec == null)
            {
                var lm = LoadedRx.Match(msg);
                if (lm.Success)
                {
                    string f = lm.Groups[1].Value.Replace('/', '\\');
                    info.LoadedFiles.Add(IOPath.GetFileNameWithoutExtension(f.Substring(f.LastIndexOf('\\') + 1)));
                }
                else if (info.LoaderVersion.Length == 0 && msg.StartsWith("MelonLoader v", StringComparison.Ordinal))
                    info.LoaderVersion = msg.Substring("MelonLoader v".Length).Trim();
                else if (info.GameVersion.Length == 0 && msg.StartsWith("Game Version:", StringComparison.Ordinal))
                {
                    string v = msg.Substring("Game Version:".Length).Trim();
                    int b = v.IndexOf(' ');
                    info.GameVersion = b > 0 ? v.Substring(0, b) : v;
                }
                continue;
            }
            if (level == null) continue;

            string id = null;
            if (sec != null && section.TryGetValue(sec, out var sid)) id = sid;
            if (id == null)
                foreach (var (key, fid) in byFile)
                    if (msg.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { id = fid; break; }

            ModLogStats st;
            if (id == null) st = info.Other;
            else if (!info.ByMod.TryGetValue(id, out st)) info.ByMod[id] = st = new ModLogStats();
            if (msg.Length > 400) msg = msg.Substring(0, 400) + "…";
            if (level == "E")
            {
                st.Errors++; st.LastError = msg;
                if (id != null && !st.Outdated)
                {
                    var om = OutdatedRx.Match(msg);
                    if (om.Success) { st.Outdated = true; st.OutdatedReason = om.Value; }
                }
            }
            else { st.Warnings++; st.LastWarning = msg; }
        }
        return info;
    }

    // ───────────── установка модов из файлов ─────────────

    private static readonly string[] KnownRoots = { "Mods", "Plugins", "UserLibs", "UserData" };

    /// Ставит .dll / .zip: мод MelonLoader → Mods, плагин → Plugins, обычная .NET-библиотека → UserLibs.
    /// Существующие копии не удаляются — уезжают в Mods\Disabled\_replaced.
    public ApplyResult Install(IEnumerable<string> files)
    {
        var res = new ApplyResult();
        foreach (var f in files ?? Array.Empty<string>())
        {
            string name = IOPath.GetFileName(f);
            try
            {
                string ext = IOPath.GetExtension(f).ToLowerInvariant();
                if (ext == ".dll") InstallDll(f, null, res);
                else if (ext == ".zip") InstallZip(f, res);
                else res.Errors.Add($"{name}: можно ставить только .dll или .zip");
            }
            catch (Exception e) { res.Errors.Add($"{name}: {e.Message}"); }
        }
        AppendLog("install", res);
        return res;
    }

    private void InstallDll(string src, string forcedRoot, ApplyResult res)
    {
        var e = Read(src, true);
        string file = IOPath.GetFileName(src);
        if (!e.Readable && forcedRoot == null)
        {
            res.Errors.Add($"{file}: это не .NET-сборка — не похоже на мод MelonLoader");
            return;
        }
        string root = forcedRoot ?? (e.Kind == "Plugin" ? "Plugins" : e.Kind == "Mod" ? "Mods" : "UserLibs");
        string label = string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name;
        if (root == "Mods")
        {
            string inMods = IOPath.Combine(ModsDir, file), inDisabled = IOPath.Combine(DisabledDir, file);
            bool keepDisabled = !File.Exists(inMods) && File.Exists(inDisabled); // обновление выключенного мода
            string dst = keepDisabled ? inDisabled : inMods;
            Directory.CreateDirectory(IOPath.GetDirectoryName(dst)!);
            string tmp = dst + ".installing";
            File.Copy(src, tmp, true);
            try
            {
                if (File.Exists(dst)) StashReplaced(dst, res);
                if (!keepDisabled && File.Exists(inDisabled)) StashReplaced(inDisabled, res); // чтобы не было дубликата
                File.Move(tmp, dst);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            res.Moved++;
            res.Log.Add($"установлен: {file} → {(keepDisabled ? "Mods\\Disabled" : "Mods")}");
            res.Installed.Add(label + (keepDisabled ? " (выключен)" : ""));
            res.InstalledIds.Add(e.Id);
            return;
        }
        string dir = root == "Plugins" ? PluginsDir : UserLibsDir;
        string target = IOPath.Combine(dir, file);
        Directory.CreateDirectory(dir);
        string t2 = target + ".installing";
        File.Copy(src, t2, true);
        try
        {
            if (File.Exists(target)) StashReplaced(target, res);
            File.Move(t2, target);
        }
        finally { try { if (File.Exists(t2)) File.Delete(t2); } catch { } }
        res.Moved++;
        res.Log.Add($"установлен: {file} → {root}");
        res.Installed.Add(label + " → " + root);
    }

    private void InstallZip(string zipPath, ApplyResult res)
    {
        string tmpRoot = IOPath.Combine(IOPath.GetTempPath(), "sr2mm-zip-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        int before = res.Installed.Count, skipped = 0;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
            string strip = ZipStrip(entries);
            foreach (var en in entries)
            {
                string rel = Norm(en.FullName).Substring(strip.Length);
                if (rel.Length == 0) continue;
                string[] seg = rel.Split('/');
                string top = KnownRoots.FirstOrDefault(r => r.Equals(seg[0], StringComparison.OrdinalIgnoreCase));
                bool isDll = rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if ((top == "Mods" || top == "Plugins") && seg.Length == 2 && isDll || top == null && seg.Length == 1 && isDll)
                {
                    string dir = IOPath.Combine(tmpRoot, Guid.NewGuid().ToString("N").Substring(0, 6));
                    Directory.CreateDirectory(dir);
                    string tmp = IOPath.Combine(dir, seg[seg.Length - 1]);
                    en.ExtractToFile(tmp, true);
                    InstallDll(tmp, top, res);
                }
                else if (top == "UserLibs" || top == "UserData")
                {
                    string baseDir = top == "UserLibs" ? UserLibsDir : UserDataDir;
                    string relPath = IOPath.Combine(seg.Skip(1).ToArray());
                    string dst = IOPath.GetFullPath(IOPath.Combine(baseDir, relPath));
                    string baseFull = IOPath.GetFullPath(baseDir).TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar) + IOPath.DirectorySeparatorChar;
                    if (relPath.Length == 0 || !dst.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                    { res.Errors.Add($"{IOPath.GetFileName(zipPath)}: подозрительный путь в архиве — {rel}"); continue; }
                    if (top == "UserData" && File.Exists(dst)) { skipped++; continue; } // настройки пользователя не трогаем
                    Directory.CreateDirectory(IOPath.GetDirectoryName(dst)!);
                    if (File.Exists(dst)) StashReplaced(dst, res);
                    en.ExtractToFile(dst, false);
                    res.Moved++;
                    res.Log.Add($"распакован: {top}\\{relPath}");
                }
                else skipped++;
            }
        }
        finally { try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { } }
        if (res.Installed.Count == before && !res.Log.Any(l => l.StartsWith("распакован")))
            res.Errors.Add($"{IOPath.GetFileName(zipPath)}: в архиве не найдено DLL модов");
        else if (skipped > 0) res.Log.Add($"{IOPath.GetFileName(zipPath)}: пропущено файлов: {skipped} (readme, существующие настройки и т. п.)");
    }

    /// Общий корневой каталог архива («MyMod-1.0/Mods/X.dll» → «MyMod-1.0/»), который при установке отбрасывается.
    private static string ZipStrip(List<ZipArchiveEntry> entries)
    {
        string strip = "";
        for (int depth = 0; depth < 2; depth++)
        {
            var segs = entries.Select(x => Norm(x.FullName).Substring(strip.Length).Split('/')).ToList();
            if (segs.Count == 0 || segs.Any(a => a.Length < 2)) break;
            string first = segs[0][0];
            if (KnownRoots.Contains(first, StringComparer.OrdinalIgnoreCase) || segs.Any(a => !a[0].Equals(first, StringComparison.OrdinalIgnoreCase))) break;
            strip += first + "/";
        }
        return strip;
    }

    /// DLL архива, которые поставит Install: из корня (за вычетом общей папки) и из Mods/ или Plugins/ архива.
    /// Top — "Mods", "Plugins" или null (корень архива).
    public static List<(ZipArchiveEntry Entry, string Top)> ZipDllEntries(ZipArchive zip)
    {
        var list = new List<(ZipArchiveEntry, string)>();
        var entries = zip.Entries.Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
        string strip = ZipStrip(entries);
        foreach (var en in entries)
        {
            string rel = Norm(en.FullName).Substring(strip.Length);
            if (!rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string[] seg = rel.Split('/');
            string top = KnownRoots.FirstOrDefault(r => r.Equals(seg[0], StringComparison.OrdinalIgnoreCase));
            if ((top == "Mods" || top == "Plugins") && seg.Length == 2 || top == null && seg.Length == 1) list.Add((en, top));
        }
        return list;
    }

    /// Ставит мод из каталога (Библиотека, Загрузки). Если имя DLL совпадает с нужным — как обычная установка
    /// (.zip целиком, вместе с UserLibs). Иначе берётся одна DLL и кладётся под именем установленного мода,
    /// чтобы не появилось второй копии («SlimeLive (1).dll» рядом с «SlimeLive.dll»).
    public ApplyResult InstallFromCatalog(ModEntry item, ModEntry replaces = null)
    {
        var res = new ApplyResult();
        if (item == null || !File.Exists(item.Path)) { res.Errors.Add("файл мода не найден: " + item?.Path); return res; }
        string want = replaces?.FileName ?? item.Id + ".dll";
        bool zip = item.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        string srcName = zip ? IOPath.GetFileName(Norm(item.EntryName ?? "")) : IOPath.GetFileName(item.Path);
        if (srcName.Equals(want, StringComparison.OrdinalIgnoreCase)) return Install(new[] { item.Path });
        string tmpDir = IOPath.Combine(IOPath.GetTempPath(), "sr2mm-cat-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(tmpDir);
            string tmp = IOPath.Combine(tmpDir, want);
            if (zip)
            {
                using var z = ZipFile.OpenRead(item.Path);
                var en = z.Entries.FirstOrDefault(x => Norm(x.FullName).Equals(Norm(item.EntryName ?? ""), StringComparison.OrdinalIgnoreCase));
                if (en == null) { res.Errors.Add($"{IOPath.GetFileName(item.Path)}: в архиве нет {item.EntryName}"); return res; }
                en.ExtractToFile(tmp, true);
            }
            else File.Copy(item.Path, tmp, true);
            return Install(new[] { tmp });
        }
        catch (Exception e) { res.Errors.Add($"{IOPath.GetFileName(item.Path)}: {e.Message}"); return res; }
        finally { try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { } }
    }

    private static string Norm(string p)
    {
        p = p.Replace('\\', '/');
        while (p.StartsWith("/") || p.StartsWith("./")) p = p.StartsWith("/") ? p.Substring(1) : p.Substring(2);
        return p;
    }

    // ───────────── старые копии и откат ─────────────

    private static readonly Regex StampRx = new(@"\.(\d{8}-\d{6})(?:-\d+)?\.bak$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// Старые копии DLL мода: Mods\<файл>.bak*, Mods\Disabled\<файл>.bak*, Mods\Disabled\_replaced\<файл>.*.bak.
    public List<BackupInfo> ListBackups(ModEntry m)
    {
        var list = new List<BackupInfo>();
        if (m == null) return list;
        void Add(string dir, string pattern, string where)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                {
                    string fn = IOPath.GetFileName(f);
                    if (!fn.StartsWith(m.FileName + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    if (where == "_replaced" && !fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                    var fi = new FileInfo(f);
                    var b = new BackupInfo { Path = fi.FullName, FileName = fn, Where = where, Size = fi.Length, Date = fi.LastWriteTime };
                    var sm = StampRx.Match(fn);
                    if (sm.Success && DateTime.TryParseExact(sm.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) b.Date = d;
                    var e = ReadCached(f, false);
                    b.Version = e.Readable ? e.Version : "";
                    list.Add(b);
                }
            }
            catch { }
        }
        Add(ModsDir, m.FileName + ".bak*", "Mods");
        Add(DisabledDir, m.FileName + ".bak*", "Disabled");
        Add(ReplacedDir, m.FileName + ".*", "_replaced");
        return list.OrderByDescending(b => b.Date).ToList();
    }

    /// Старые копии всех модов (для поиска обновлений: вдруг среди них есть версия новее установленной).
    public Dictionary<string, List<BackupInfo>> ListAllBackups(IReadOnlyList<ModEntry> mods)
    {
        var map = new Dictionary<string, List<BackupInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods ?? Array.Empty<ModEntry>())
        {
            var l = ListBackups(m);
            if (l.Count > 0) map[m.Id] = l;
        }
        return map;
    }

    /// Возвращает старую копию на место текущей DLL. Текущая уезжает в _replaced (её тоже можно вернуть).
    public ApplyResult RestoreBackup(ModEntry m, string backupPath)
    {
        var res = new ApplyResult();
        try
        {
            if (m == null || !File.Exists(backupPath)) throw new FileNotFoundException("копия не найдена", backupPath);
            string dst = m.Path;
            string tmp = dst + ".restoring";
            File.Copy(backupPath, tmp, true);
            try
            {
                if (File.Exists(dst)) StashReplaced(dst, res);
                File.Move(tmp, dst);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            // копию из _replaced забираем (она стала текущей), пользовательские .bak оставляем
            if (IOPath.GetDirectoryName(IOPath.GetFullPath(backupPath)).Equals(IOPath.GetFullPath(ReplacedDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                try { File.Delete(backupPath); } catch { }
            res.Moved++;
            res.Log.Add($"{m.FileName}: восстановлена копия {IOPath.GetFileName(backupPath)}");
        }
        catch (Exception e) { res.Errors.Add($"{m?.FileName}: {e.Message}"); }
        AppendLog("restore", res);
        return res;
    }

    // ───────────── чтение метаданных DLL без загрузки ─────────────

    public static ModEntry Read(string path, bool enabled)
    {
        var fi = new FileInfo(path);
        var e = new ModEntry
        {
            Id = System.IO.Path.GetFileNameWithoutExtension(path),
            FileName = fi.Name,
            Path = fi.FullName,
            Enabled = enabled,
            Size = fi.Length,
            Modified = fi.LastWriteTime,
        };
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ReadMeta(fs, e);
        }
        catch { e.Readable = false; }
        return e;
    }

    /// То же для DLL из архива: поток должен уметь Seek (например, MemoryStream).
    public static ModEntry Read(Stream s, string fileName, string path, long size, DateTime modified)
    {
        var e = new ModEntry
        {
            Id = IOPath.GetFileNameWithoutExtension(fileName),
            FileName = fileName,
            Path = path,
            Size = size,
            Modified = modified,
        };
        try { ReadMeta(s, e); }
        catch { e.Readable = false; }
        return e;
    }

    private static readonly Dictionary<string, (long Len, long Ticks, ModEntry E)> MetaCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MetaGate = new();

    /// Read с кэшем по (путь, размер, время изменения): старые копии и файлы каталога не перечитываются
    /// при каждом обновлении экрана — поиск и переключение модов не ждут диск.
    public static ModEntry ReadCached(string path, bool enabled)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return Read(path, enabled); }
        catch { return Read(path, enabled); }
        long len = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
        lock (MetaGate)
            if (MetaCache.TryGetValue(fi.FullName, out var hit) && hit.Len == len && hit.Ticks == ticks)
            {
                var c = hit.E.Clone();
                c.Enabled = enabled;
                return c;
            }
        var e = Read(path, enabled);
        lock (MetaGate)
        {
            if (MetaCache.Count > 2000) MetaCache.Clear();
            MetaCache[fi.FullName] = (len, ticks, e.Clone());
        }
        return e;
    }

    private static void ReadMeta(Stream fs, ModEntry e)
    {
        {
            using var pe = new PEReader(fs, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) return;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return;
            var asm = md.GetAssemblyDefinition();
            e.AssemblyName = md.GetString(asm.Name);
            e.Version = asm.Version.ToString();
            string fileVer = null, melonType = null;
            foreach (var h in asm.GetCustomAttributes())
            {
                try
                {
                    var ca = md.GetCustomAttribute(h);
                    string an = AttributeName(md, ca);
                    if (an is "MelonInfoAttribute" or "MelonModInfoAttribute" or "MelonPluginInfoAttribute")
                    {
                        var v = ca.DecodeValue(new TypeProvider());
                        var a = v.FixedArguments;
                        if (a.Length >= 4)
                        {
                            melonType = a[0].Value as string;
                            e.Name = a[1].Value as string ?? "";
                            e.Version = a[2].Value as string ?? e.Version;
                            e.Author = a[3].Value as string ?? "";
                        }
                        if (a.Length >= 5 && a[a.Length - 1].Value is string link) e.Link = link;
                        if (an == "MelonPluginInfoAttribute") e.Kind = "Plugin";
                        else if (an == "MelonModInfoAttribute") e.Kind = "Mod";
                    }
                    else if (an == "MelonGameAttribute")
                    {
                        var ga = ca.DecodeValue(new TypeProvider()).FixedArguments;
                        e.Games.Add(ga.Length >= 2 ? ga[1].Value as string ?? "" : ""); // "" — любая игра
                    }
                    else if (an == "AssemblyDescriptionAttribute")
                        e.Description = ca.DecodeValue(new TypeProvider()).FixedArguments[0].Value as string ?? "";
                    else if (an == "AssemblyFileVersionAttribute")
                        fileVer = ca.DecodeValue(new TypeProvider()).FixedArguments[0].Value as string;
                    else if (an == "MelonAdditionalDependenciesAttribute")
                    {
                        var arr = ca.DecodeValue(new TypeProvider()).FixedArguments[0].Value as ImmutableArray<CustomAttributeTypedArgument<string>>?;
                        if (arr.HasValue) foreach (var x in arr.Value) if (x.Value is string s) e.MelonDependencies.Add(s);
                    }
                }
                catch { /* одно кривое поле не должно ломать чтение */ }
            }
            if (string.IsNullOrEmpty(e.Name) && fileVer != null) e.Version = fileVer;
            foreach (var rh in md.AssemblyReferences)
                e.References.Add(md.GetString(md.GetAssemblyReference(rh).Name));
            if (string.IsNullOrEmpty(e.Kind) && melonType != null) e.Kind = GuessKind(md, melonType);
            e.Readable = true;
        }
    }

    private static string GuessKind(MetadataReader md, string typeName)
    {
        string shortName = typeName.Split(',')[0].Trim();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            string full = md.GetString(td.Namespace);
            full = (full.Length > 0 ? full + "." : "") + md.GetString(td.Name);
            if (full != shortName) continue;
            if (td.BaseType.Kind == HandleKind.TypeReference)
            {
                string b = md.GetString(md.GetTypeReference((TypeReferenceHandle)td.BaseType).Name);
                if (b == "MelonPlugin") return "Plugin";
                if (b == "MelonMod") return "Mod";
            }
        }
        return "Mod";
    }

    private static string AttributeName(MetadataReader md, CustomAttribute ca)
    {
        if (ca.Constructor.Kind == HandleKind.MemberReference)
        {
            var mr = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
            if (mr.Parent.Kind == HandleKind.TypeReference) return md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
            if (mr.Parent.Kind == HandleKind.TypeDefinition) return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent).Name);
        }
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var mdef = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
            return md.GetString(md.GetTypeDefinition(mdef.GetDeclaringType()).Name);
        }
        return "";
    }

    private sealed class TypeProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawTypeKind) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawTypeKind) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type" || type == "Type";
    }
}

