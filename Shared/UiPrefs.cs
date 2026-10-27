// UiPrefs.cs — настройки окна лаунчера в UserData\ModManager\ui.json (рядом с profiles.json): стиль оформления,
// сортировка таблицы модов, нижняя панель. Здесь же — логика без WinForms, которую проверяют тесты:
// порядок сортировки по колонкам и разбор строк журнала MelonLoader для вкладки «Логи».
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SR2ModManager.Core;

/// Стиль оформления: «Компактный» (тёмная таблица, по умолчанию), «Fluent» (карточки), «Слаймовый» (капсулы в цветах игры).
public enum DesignKind { Compact = 0, Fluent = 1, Slime = 2 }

/// Колонки таблицы модов (стиль «Компактный»). None — без сортировки: порядок, как его строит каталог.
public enum ModColumn { None = 0, Check, Name, Version, Author, Compat, Hooks, Update, Size }

/// Вкладки нижней панели.
public enum DockTab { Problems = 0, Hooks = 1, Logs = 2, Details = 3 }

public sealed class UiPrefs
{
    public const string FileName = "ui.json";

    [JsonPropertyName("design")] public string? Design { get; set; } = "compact";
    [JsonPropertyName("sort")] public string? Sort { get; set; } = "";
    [JsonPropertyName("sortDesc")] public bool SortDesc { get; set; }
    [JsonPropertyName("dockTab")] public string? Dock { get; set; } = "details";
    /// Высота нижней панели в пикселях при 96 dpi; 0 — по умолчанию (доля высоты окна).
    [JsonPropertyName("dockHeight")] public int DockHeight { get; set; }
    [JsonPropertyName("dockCollapsed")] public bool DockCollapsed { get; set; }
    [JsonPropertyName("logErrorsOnly")] public bool LogErrorsOnly { get; set; }
    [JsonPropertyName("logSelectedOnly")] public bool LogSelectedOnly { get; set; }
    [JsonPropertyName("hooksSelectedOnly")] public bool HooksSelectedOnly { get; set; }

    [JsonIgnore] public DesignKind DesignKind { get => ParseDesign(Design) ?? DesignKind.Compact; set => Design = Key(value); }
    [JsonIgnore] public ModColumn SortColumn { get => ParseColumn(Sort); set => Sort = value == ModColumn.None ? "" : Key(value); }
    [JsonIgnore] public DockTab DockTab { get => ParseDock(Dock) ?? DockTab.Details; set => Dock = Key(value); }

    public static string PathIn(string configDir) => Path.Combine(configDir, FileName);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Прочитать настройки. Нет файла или он битый — значения по умолчанию (исключений нет).
    public static UiPrefs Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var p = JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(path), Json);
                if (p != null) return p.Normalized();
            }
        }
        catch { }
        return new UiPrefs();
    }

    /// Сохранить атомарно: во временный файл рядом, затем подмена (при сбое старый файл цел).
    public void Save(string path)
    {
        Normalized();
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// Привести значения к каноническим ключам и допустимым пределам.
    public UiPrefs Normalized()
    {
        Design = Key(DesignKind);
        var c = SortColumn;
        Sort = c == ModColumn.None ? "" : Key(c);
        if (c == ModColumn.None) SortDesc = false;
        Dock = Key(DockTab);
        DockHeight = Math.Clamp(DockHeight, 0, 4000);
        return this;
    }

    public UiPrefs Clone() => (UiPrefs)MemberwiseClone();

    public static string Key(DesignKind d) => d switch { DesignKind.Fluent => "fluent", DesignKind.Slime => "slime", _ => "compact" };
    public static string Key(DockTab t) => t switch { DockTab.Problems => "problems", DockTab.Hooks => "hooks", DockTab.Logs => "logs", _ => "details" };
    public static string Key(ModColumn c) => c == ModColumn.None ? "" : c.ToString().ToLowerInvariant();

    /// Название стиля по-английски, по-русски, буквой варианта (C/A/B) или числом; null — не распознано.
    public static DesignKind? ParseDesign(string? s)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "compact": case "c": case "0": case "table": case "dark": case "компактный": case "компакт": case "таблица": return DesignKind.Compact;
            case "fluent": case "a": case "1": case "cards": case "флюент": return DesignKind.Fluent;
            case "slime": case "b": case "2": case "capsules": case "слаймовый": case "слайм": return DesignKind.Slime;
            default: return null;
        }
    }

    public static ModColumn ParseColumn(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return ModColumn.None;
        return Enum.TryParse<ModColumn>(s, true, out var c) && Enum.IsDefined(c) ? c : ModColumn.None;
    }

    public static DockTab? ParseDock(string? s)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "problems": case "проблемы": return DockTab.Problems;
            case "hooks": case "harmony": case "хуки": return DockTab.Hooks;
            case "logs": case "log": case "логи": return DockTab.Logs;
            case "details": case "детали": return DockTab.Details;
            default: return null;
        }
    }
}

/// Значения колонок, которые знает только окно (отметки, совместимость, хуки, обновления), — для сортировки.
public sealed class ModSortKeys
{
    public Func<ModEntry, bool> IsOn = _ => false;
    /// 0 — всё хорошо … 3 — ошибка (вместе с проблемами: зависимости, конфликты, ошибки в журнале).
    public Func<ModEntry, int> Compat = _ => 0;
    /// Сколько методов игры мод патчит через Harmony (−1 — Harmony не использует).
    public Func<ModEntry, int> Hooks = _ => 0;
    /// 2 — есть новая версия, 1 — похоже, устарел (по журналу игры), 0 — нет.
    public Func<ModEntry, int> Update = _ => 0;
}

public static class ModSort
{
    /// Направление при первом щелчке: названия, версии и авторы — по возрастанию (А→Я), «важные» колонки
    /// (отметка, проблемы, хуки, обновления, размер) — сначала большее.
    public static bool DefaultDesc(ModColumn c) => c is ModColumn.Check or ModColumn.Compat or ModColumn.Hooks or ModColumn.Update or ModColumn.Size;

    /// Щелчок по заголовку колонки: другая колонка — её направление по умолчанию; та же — обратное;
    /// третий щелчок по той же — без сортировки.
    public static (ModColumn Column, bool Desc) Next(ModColumn current, bool desc, ModColumn clicked)
    {
        if (clicked == ModColumn.None) return (ModColumn.None, false);
        if (current != clicked) return (clicked, DefaultDesc(clicked));
        if (desc == DefaultDesc(clicked)) return (clicked, !desc);
        return (ModColumn.None, false);
    }

    /// Отсортированная копия вида. Установленные и доступные (ещё не установленные) моды сортируются каждые в своей
    /// группе, группы остаются на местах. Пустые версии и авторы — в конце при любом направлении; при равенстве —
    /// по названию, затем по Id (порядок не скачет между перерисовками).
    public static List<ModEntry> Apply(IReadOnlyList<ModEntry> view, ModColumn col, bool desc, ModSortKeys? keys = null)
    {
        var res = new List<ModEntry>(view);
        if (col == ModColumn.None || res.Count < 2) return res;
        keys ??= new ModSortKeys();
        int split = res.FindIndex(m => m.Available);
        if (split < 0) split = res.Count;
        SortRange(res, 0, split, col, desc, keys);
        SortRange(res, split, res.Count - split, col, desc, keys);
        return res;
    }

    private static void SortRange(List<ModEntry> list, int start, int count, ModColumn col, bool desc, ModSortKeys k)
    {
        if (count < 2) return;
        var part = list.GetRange(start, count);
        var cmp = Comparer<ModEntry>.Create((a, b) => Compare(a, b, col, desc, k));
        var sorted = part.OrderBy(m => m, cmp).ToList();   // OrderBy — устойчивая сортировка
        for (int i = 0; i < count; i++) list[start + i] = sorted[i];
    }

    private static bool Blank(ModEntry m, ModColumn col) => col switch
    {
        ModColumn.Version => string.IsNullOrWhiteSpace(m.Version),
        ModColumn.Author => string.IsNullOrWhiteSpace(m.Author),
        _ => false,
    };

    private static int Compare(ModEntry a, ModEntry b, ModColumn col, bool desc, ModSortKeys k)
    {
        bool ea = Blank(a, col), eb = Blank(b, col);
        if (ea != eb) return ea ? 1 : -1;
        int c = ea ? 0 : col switch
        {
            ModColumn.Check => k.IsOn(a).CompareTo(k.IsOn(b)),
            ModColumn.Name => Text(a.DisplayName, b.DisplayName),
            ModColumn.Version => CompareVersions(a.Version, b.Version),
            ModColumn.Author => Text(a.Author, b.Author),
            ModColumn.Compat => k.Compat(a).CompareTo(k.Compat(b)),
            ModColumn.Hooks => k.Hooks(a).CompareTo(k.Hooks(b)),
            ModColumn.Update => k.Update(a).CompareTo(k.Update(b)),
            ModColumn.Size => a.Size.CompareTo(b.Size),
            _ => 0,
        };
        if (desc) c = -c;
        if (c == 0 && col != ModColumn.Name) c = Text(a.DisplayName, b.DisplayName);
        if (c == 0) c = string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        return c;
    }

    private static int Text(string? a, string? b) => string.Compare((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.InvariantCultureIgnoreCase);

    /// Сравнение версий по частям: числа — как числа (1.10 > 1.9), остальное — как текст; «v» в начале не считается.
    public static int CompareVersions(string? a, string? b)
    {
        var pa = VersionParts(a);
        var pb = VersionParts(b);
        for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
        {
            var (na, sa) = pa[i];
            var (nb, sb) = pb[i];
            int c;
            if (na && nb)
            {
                string x = sa.TrimStart('0'), y = sb.TrimStart('0');
                c = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            }
            else if (na != nb) c = na ? 1 : -1;   // число старше текста: 1.0.1 > 1.0.beta
            else c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return pa.Count.CompareTo(pb.Count);
    }

    private static List<(bool Num, string S)> VersionParts(string? v)
    {
        var res = new List<(bool, string)>();
        string s = (v ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        int i = 0;
        while (i < s.Length)
        {
            if (!char.IsLetterOrDigit(s[i])) { i++; continue; }
            int j = i;
            bool num = char.IsDigit(s[i]);
            while (j < s.Length && char.IsLetterOrDigit(s[j]) && char.IsDigit(s[j]) == num) j++;
            res.Add((num, s.Substring(i, j - i)));
            i = j;
        }
        return res;
    }
}

public enum LogLevel { Info = 0, Warning = 1, Error = 2 }

/// Строка журнала MelonLoader: «[12:34:56.789] [WARNING] [Creator_Toolkit] текст».
public sealed class LogLine
{
    public string Raw = "";
    public string Time = "";
    public LogLevel Level;
    public string Section = "";
    public string Text = "";
    /// Продолжение предыдущего сообщения (стек вызовов и т. п.) — без метки времени.
    public bool Continuation;
}

/// Хвост MelonLoader\Latest.log для вкладки «Логи».
public static class MelonLogTail
{
    /// Последние maxLines строк (с конца файла читается не больше maxBytes). Файл может быть открыт игрой на запись.
    public static List<string> ReadTail(string path, int maxLines = 3000, int maxBytes = 768 * 1024)
    {
        var res = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            long from = Math.Max(0, len - maxBytes);
            fs.Seek(from, SeekOrigin.Begin);
            var buf = new byte[len - from];
            int n = 0, r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
            string text = new UTF8Encoding(false).GetString(buf, 0, n);
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            if (from > 0 && lines.Count > 0) lines.RemoveAt(0);   // первая строка обрезана посередине
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            res = lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
        }
        catch { }
        return res;
    }

    public static LogLine Parse(string raw)
    {
        string line = (raw ?? "").TrimEnd('\r', '\n', ' ');
        var l = new LogLine { Raw = line };
        if (!(line.Length >= 14 && line[0] == '[' && line[13] == ']' && char.IsDigit(line[1])))
        {
            l.Continuation = true;
            l.Text = line;
            return l;
        }
        l.Time = line.Substring(1, 12);
        int p = 14;
        for (int guard = 0; guard < 4; guard++)
        {
            while (p < line.Length && line[p] == ' ') p++;
            if (p >= line.Length || line[p] != '[') break;
            int end = line.IndexOf(']', p + 1);
            if (end < 0 || end - p > 64) break;
            string tok = line.Substring(p + 1, end - p - 1).Trim();
            if (tok.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || tok.Equals("FATAL", StringComparison.OrdinalIgnoreCase)) l.Level = LogLevel.Error;
            else if (tok.Equals("WARNING", StringComparison.OrdinalIgnoreCase) || tok.Equals("WARN", StringComparison.OrdinalIgnoreCase)) { if (l.Level < LogLevel.Warning) l.Level = LogLevel.Warning; }
            else if (l.Section.Length == 0) l.Section = tok;
            p = end + 1;
        }
        l.Text = p < line.Length ? line.Substring(p).Trim() : "";
        return l;
    }

    /// Разобрать строки; продолжения (стек вызовов) получают уровень и раздел своего сообщения.
    public static List<LogLine> ParseAll(IEnumerable<string> lines)
    {
        var res = new List<LogLine>();
        LogLine? head = null;
        foreach (var raw in lines)
        {
            var l = Parse(raw);
            if (l.Continuation)
            {
                if (l.Text.Trim().Length == 0) continue;
                if (head != null) { l.Level = head.Level; l.Section = head.Section; }
            }
            else head = l;
            res.Add(l);
        }
        return res;
    }

    /// Относится ли строка к моду: раздел — его имя (как в журнале: пробелы → «_»), Id или имя сборки,
    /// либо в тексте упомянут файл мода.
    public static bool Mentions(LogLine l, ModEntry m)
    {
        if (l.Section.Length > 0)
            foreach (var k in new[] { m.Name, m.Name?.Replace(' ', '_'), m.Id, m.AssemblyName })
                if (!string.IsNullOrWhiteSpace(k) && l.Section.Equals(k, StringComparison.OrdinalIgnoreCase)) return true;
        return m.FileName.Length > 0 && l.Text.IndexOf(m.FileName, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
