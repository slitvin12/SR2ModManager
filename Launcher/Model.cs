// Model.cs — состояние окна: моды, выбор пользователя, журнал игры, статусы и проблемы.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed class Model
{
    public ModStore Store;
    public ManagerConfig Cfg = new();
    public List<ModEntry> Mods = new();
    public Dictionary<string, bool> Sel = new(StringComparer.OrdinalIgnoreCase);
    public MelonLogInfo? Log;
    public PendingChanges? Pending;
    public bool GameRunning;
    public DateTime? GameStarted;
    public string PluginVersion = "";   // версия установленного плагина в игре ("" — нет)
    public bool PluginStaged;           // новая версия ждёт закрытия игры
    public List<string> Warnings = new();

    // ── каталог: найденное в Библиотеке, «Загрузках» и своих папках
    public List<ModEntry> Found = new();         // все найденные файлы модов
    public List<ModEntry> Available = new();     // из них не установленные (по одной лучшей копии)
    public Dictionary<string, UpdateInfo> Updates = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<BackupInfo>> Stored = new(StringComparer.OrdinalIgnoreCase);
    public bool CatalogBusy;
    public DateTime CatalogAt;

    // ── совместимость: окружение (MelonLoader, Unity, .NET, библиотеки) и проверка модов (в фоне)
    public LoaderEnv? Env;
    public CompatReport? Compat;
    public Dictionary<string, ModManifest>? Manifests;
    public bool CompatBusy;
    public string CompatKey = "";      // для каких включённых модов посчитан отчёт

    public Model(ModStore store) { Store = store; }

    public List<CompatIssue> CompatFor(ModEntry m) => Compat == null || m.Available ? CompatReport.None : Compat.For(m.Id);
    /// Предупреждения и ошибки совместимости — метки на карточке (сведения — только в подробностях).
    public List<CompatIssue> CompatBadges(ModEntry m) => CompatFor(m).Where(i => i.Level >= CompatLevel.Warning).ToList();
    public CompatLevel CompatWorst(ModEntry m) { var l = CompatFor(m); return l.Count == 0 ? CompatLevel.Ok : l.Max(i => i.Level); }
    /// Зависимость — не мод, а библиотека, которая уже есть (UserLibs, MelonLoader, сборки игры).
    public bool IsKnownLib(string name) => Env != null && Env.Known.ContainsKey(name);

    public bool On(string id) => Sel.TryGetValue(id, out var v) && v;
    public bool IsChanged(ModEntry m) => On(m.Id) != m.Enabled;
    public int ChangeCount => Mods.Count(IsChanged);
    public int OnCount => Mods.Count(m => On(m.Id));
    public ModEntry? Find(string? id) => id == null ? null : Mods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    /// Установленный или доступный (из каталога) мод.
    public ModEntry? FindAny(string? id) => id == null ? null : Find(id) ?? Available.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public UpdateInfo? UpdateFor(ModEntry m) => !m.Available && Updates.TryGetValue(m.Id, out var u) ? u : null;
    public bool HasUpdate(ModEntry m) => UpdateFor(m) != null;

    /// Пересчитать доступные и обновления по последнему скану каталога (без диска).
    public void RecomputeCatalog()
    {
        Available = SR2ModManager.Core.Catalog.AvailableOnly(Mods, Found, Cfg);
        Updates = SR2ModManager.Core.Catalog.FindUpdates(Mods, Found, Stored, Log);
    }
    public string NameOf(string id) => Find(id)?.DisplayName ?? id;
    public List<ModEntry> UsersOf(ModEntry m) => Mods.Where(x => x.DependsOn.Contains(m.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    public ModLogStats? LogFor(ModEntry m) => Log?.For(m.Id);

    public Dictionary<string, bool> CurrentOnDisk() => MapUtil.ToMap(Mods, m => m.Id, m => m.Enabled);

    /// Короткие метки проблем мода, включая совместимость (пустой список — всё хорошо).
    public List<(string Text, Tone Tone)> Problems(ModEntry m)
    {
        var p = BaseProblems(m);
        foreach (var i in CompatBadges(m)) p.Add((i.Badge, i.Level == CompatLevel.Error ? Tone.Error : Tone.Warn));
        return p;
    }

    /// Проблемы без совместимости: не читается, зависимости, конфликты, копии, ошибки в журнале.
    public List<(string Text, Tone Tone)> BaseProblems(ModEntry m)
    {
        var p = new List<(string, Tone)>();
        if (m.Available) return p;
        if (!m.Readable) p.Add(("не читается как мод", Tone.Error));
        if (On(m.Id))
            foreach (var d in m.DependsOn)
            {
                var dm = Find(d);
                if (dm == null) { if (!IsKnownLib(d)) p.Add(($"нет «{d}»", Tone.Error)); }
                else if (!On(dm.Id)) p.Add(($"нужен {dm.DisplayName}", Tone.Error));
            }
        foreach (var pair in Cfg.Conflicts)
            if (pair.Count >= 2 && pair.Contains(m.Id, StringComparer.OrdinalIgnoreCase) && pair.All(On))
                p.Add(("конфликт", Tone.Error));
        if (m.DuplicateInDisabled) p.Add(("копия в Disabled", Tone.Warn));
        var ls = LogFor(m);
        if (ls != null && ls.Errors > 0) p.Add((ls.Errors + " " + Theme.Plural(ls.Errors, "ошибка", "ошибки", "ошибок") + " в логе", Tone.Error));
        return p;
    }

    public bool HasProblem(ModEntry m) => Problems(m).Count > 0;

    /// Статус мода для капсулы справа.
    public (string Text, Color Bg, Color Fg, string Tip) Status(ModEntry m)
    {
        if (m.Available)
            return ("доступен", Theme.BlueTint, Theme.BlueText, $"Не установлен — найден в «{m.SourceLabel}»: {System.IO.Path.GetFileName(m.Path)}. Enter — установить.");
        bool want = On(m.Id), cur = m.Enabled;
        if (!m.Readable) return ("не читается", Theme.CoralTint, Theme.CoralText, "Файл не удалось прочитать как .NET-сборку — возможно, это не мод MelonLoader.");
        if (want && !cur) return ("включится", Theme.BlueTint, Theme.BlueText, GameRunning ? "Включится при следующем запуске игры" : "Включится после «Применить» или «Играть»");
        if (!want && cur) return ("выключится", Theme.OrangeTint, Theme.OrangeText, GameRunning ? "Выключится при следующем запуске игры" : "Выключится после «Применить» или «Играть»");
        if (!want) return ("выключен", Theme.Beige, Theme.NavySoft, "Лежит в Mods\\Disabled — игра его не загружает");
        if (GameRunning && Log != null)
        {
            if (Log.WasLoaded(m.Id)) return ("работает", Theme.Teal, Color.White, "Загружен в запущенную игру");
            return ("не загружен", Theme.OrangeTint, Theme.OrangeText, "Включён, но в журнале текущей игры его загрузки нет — появился после запуска или не загрузился");
        }
        var err = CompatFor(m).FirstOrDefault(i => i.Level == CompatLevel.Error);
        if (err != null) return ("не загрузится", Theme.CoralTint, Theme.CoralText, "Включён, но проверка совместимости нашла ошибку: " + err.Title + ". Подробности — справа, в разделе «Совместимость».");
        return ("включён", Theme.TealTint, Theme.TealText, "Лежит в Mods — загрузится при запуске игры");
    }

    /// Цвет полоски слева у карточки.
    public Color Accent(ModEntry m)
    {
        if (m.Available) return Theme.Blue;
        if (!m.Readable) return Theme.Coral;
        bool want = On(m.Id);
        if (want != m.Enabled) return want ? Theme.Blue : Theme.Orange;
        if (want && CompatWorst(m) == CompatLevel.Error) return Theme.Coral;
        return want ? Theme.Teal : Theme.LineDark;
    }
}


