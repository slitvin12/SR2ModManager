#nullable disable
// Catalog.cs — каталог модов лаунчера: установленные + доступные (ModManager\Library, «Загрузки», свои папки).
// Здесь же сравнение версий, поиск (раскладка, ё/е, подсветка совпадений), поиск обновлений и быстрые фильтры.
// Только логика, без WinForms — её проверяют тесты.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using IOPath = System.IO.Path;

namespace SR2ModManager.Core;

// ───────────── версии ─────────────

public static class VersionUtil
{
    private static readonly string[] PreTags = { "alpha", "beta", "rc", "pre", "preview", "dev", "snapshot", "nightly", "test" };

    /// «v1.10.0» > «1.9.2»; «0.6» == «0.6.0.0»; «1.0.0-beta» < «1.0.0»; «0.5.0a» (исправленная) > «0.5.0»; «+сборка» не влияет.
    public static int Compare(string a, string b)
    {
        Parse(a, out var na, out var sa);
        Parse(b, out var nb, out var sb);
        int n = Math.Max(na.Count, nb.Count);
        for (int i = 0; i < n; i++)
        {
            long x = i < na.Count ? na[i] : 0, y = i < nb.Count ? nb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        int ra = Rank(sa), rb = Rank(sb);
        if (ra != rb) return ra < rb ? -1 : 1;
        int c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
        return c < 0 ? -1 : c > 0 ? 1 : 0;
    }

    public static bool IsNewer(string candidate, string current)
        => !string.IsNullOrWhiteSpace(candidate) && Compare(candidate, current) > 0;

    private static void Parse(string v, out List<long> nums, out string suffix)
    {
        nums = new List<long>();
        suffix = "";
        if (string.IsNullOrWhiteSpace(v)) return;
        string s = v.Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1])) s = s.Substring(1);
        int i = 0;
        while (i < s.Length)
        {
            int st = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == st) break;
            nums.Add(long.TryParse(s.Substring(st, Math.Min(i - st, 18)), NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : 0);
            if (i + 1 < s.Length && s[i] == '.' && char.IsDigit(s[i + 1])) { i++; continue; }
            break;
        }
        string rest = s.Substring(i);
        if (rest.StartsWith("+", StringComparison.Ordinal)) rest = "";
        int plus = rest.IndexOf('+');
        if (plus >= 0) rest = rest.Substring(0, plus);
        suffix = rest.Trim().TrimStart('-', '_', '.', ' ');
    }

    private static int Rank(string suffix)
    {
        if (suffix.Length == 0) return 1;              // релиз
        string low = suffix.ToLowerInvariant();
        foreach (var t in PreTags) if (low.StartsWith(t, StringComparison.Ordinal)) return 0; // предварительная
        return 2;                                       // «0.5.0a», «1.2-fix» — исправленная после релиза
    }
}

// ───────────── поиск ─────────────

/// Разобранный запрос: каждое слово — варианты «как набрано» и «в другой раскладке» (ckfqv → слайм).
public sealed class SearchQuery
{
    public string Text = "";
    public readonly List<string[]> Tokens = new();
    public bool IsEmpty => Tokens.Count == 0;
}

public static class ModSearch
{
    private const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.";
    private const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю";

    private sealed class Key
    {
        public string Name = "", Id = "", Author = "", Desc = "", Other = "";
    }

    private static readonly ConditionalWeakTable<ModEntry, Key> Keys = new();

    /// Нормализация «символ в символ» (длина не меняется — так проще подсвечивать совпадения в исходном тексте):
    /// нижний регистр, ё → е, разделители (_ - / \ , ; : скобки, кавычки) → пробел.
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            char c = char.ToLowerInvariant(ch);
            if (c == 'ё') c = 'е';
            else if (char.IsWhiteSpace(c) || c is '_' or '-' or '/' or '\\' or ',' or ';' or ':' or '(' or ')' or '[' or ']' or '{' or '}'
                     or '|' or '"' or '«' or '»' or '—' or '–' or '·' or '•' or '!' or '?' or '*' or '#' or '\'' or '`') c = ' ';
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// Слово в другой раскладке клавиатуры (EN ⇄ RU) или null, если менять нечего.
    public static string SwapLayout(string word)
    {
        if (string.IsNullOrEmpty(word)) return null;
        bool cyr = word.Any(c => c >= 'а' && c <= 'я' || c == 'ё');
        bool lat = word.Any(c => c >= 'a' && c <= 'z');
        if (cyr == lat) return null; // нет букв или смесь — не трогаем
        string from = cyr ? Ru : En, to = cyr ? En : Ru;
        var sb = new StringBuilder(word.Length);
        foreach (char c in word)
        {
            int i = from.IndexOf(c);
            sb.Append(i >= 0 ? to[i] : c);
        }
        return sb.ToString();
    }

    public static SearchQuery Parse(string text)
    {
        var q = new SearchQuery { Text = text ?? "" };
        foreach (var raw in (text ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string plain = Normalize(raw).Trim();
            string swapped = SwapLayout(raw);
            string alt = swapped == null ? null : Normalize(swapped).Trim();
            if (plain.Length == 0 && string.IsNullOrEmpty(alt)) continue;
            if (plain.Length == 0) plain = alt;
            q.Tokens.Add(!string.IsNullOrEmpty(alt) && alt != plain ? new[] { plain, alt } : new[] { plain });
        }
        return q;
    }

    private static Key KeyOf(ModEntry m) => Keys.GetValue(m, e => new Key
    {
        Name = Normalize(e.DisplayName),
        Id = Normalize(e.Id),
        Author = Normalize(e.Author),
        Desc = Normalize(e.Description),
        Other = Normalize(string.Join(" ", new[]
        {
            e.FileName, e.AssemblyName, string.IsNullOrEmpty(e.Version) ? "" : "v" + e.Version, e.Version,
            e.Kind == "Plugin" ? "плагин plugin" : "", e.SourceLabel, IOPath.GetFileName(e.Path ?? ""),
        })),
    });

    /// Сбросить кэш ключей мода (если поменялись имя или описание).
    public static void Forget(ModEntry m) { if (m != null) Keys.Remove(m); }

    /// 0 — мод не подходит. Чем больше, тем выше в выдаче: совпадение в начале названия > в названии > id > автор > описание > файл.
    public static int Score(ModEntry m, SearchQuery q)
    {
        if (q == null || q.IsEmpty) return 1;
        if (m == null) return 0;
        var k = KeyOf(m);
        int total = 0;
        foreach (var variants in q.Tokens)
        {
            int best = 0;
            foreach (var t in variants)
            {
                if (t.Length == 0) continue;
                int s = 0, i = k.Name.IndexOf(t, StringComparison.Ordinal);
                if (i >= 0) s = i == 0 || k.Name[i - 1] == ' ' ? 40 : 26;
                else if (k.Id.IndexOf(t, StringComparison.Ordinal) >= 0) s = 16;
                else if (k.Author.IndexOf(t, StringComparison.Ordinal) >= 0) s = 12;
                else if (k.Desc.IndexOf(t, StringComparison.Ordinal) >= 0) s = 6;
                else if (k.Other.IndexOf(t, StringComparison.Ordinal) >= 0) s = 4;
                if (s > 0 && !ReferenceEquals(t, variants[0])) s -= 1; // совпадение по другой раскладке — чуть ниже
                if (s > best) best = s;
            }
            if (best == 0) return 0;
            total += best;
        }
        if (q.Tokens.Count == 1 && k.Name == q.Tokens[0][0]) total += 30; // точное название
        return total;
    }

    public static bool Matches(ModEntry m, SearchQuery q) => Score(m, q) > 0;

    /// Участки текста, совпавшие с запросом (для подсветки), — без пересечений, по возрастанию.
    public static List<(int Start, int Length)> Highlights(string text, SearchQuery q)
    {
        var res = new List<(int Start, int Length)>();
        if (string.IsNullOrEmpty(text) || q == null || q.IsEmpty) return res;
        string norm = Normalize(text);
        var spans = new List<(int, int)>();
        foreach (var variants in q.Tokens)
            foreach (var t in variants)
            {
                if (t.Length == 0) continue;
                bool any = false;
                for (int i = norm.IndexOf(t, StringComparison.Ordinal); i >= 0; i = norm.IndexOf(t, i + t.Length, StringComparison.Ordinal))
                { spans.Add((i, t.Length)); any = true; }
                if (any) break; // нашлось как набрано — другую раскладку не ищем
            }
        foreach (var (st, len) in spans.OrderBy(x => x.Item1))
        {
            if (res.Count > 0 && st <= res[^1].Start + res[^1].Length)
            {
                var last = res[^1];
                int end = Math.Max(last.Start + last.Length, st + len);
                res[^1] = (last.Start, end - last.Start);
            }
            else res.Add((st, len));
        }
        return res;
    }
}

// ───────────── каталог: где искать доступные моды ─────────────

public sealed class CatalogSource
{
    public string Dir = "";
    public string Label = "";
    public int MaxAgeDays;   // 0 — файлы любой давности
    public int Depth;        // 0 — только сама папка, 1 — и подпапки первого уровня

    public CatalogSource() { }
    public CatalogSource(string dir, string label, int maxAgeDays = 0, int depth = 0)
    { Dir = dir; Label = label; MaxAgeDays = maxAgeDays; Depth = depth; }
}

public static class CatalogScanner
{
    public const long MaxDllBytes = 64L << 20, MaxZipBytes = 256L << 20, MaxEntryBytes = 64L << 20;
    public const int MaxFilesPerSource = 300;
    public const int MaxDllsPerZip = 24;   // в архиве больше DLL — это не мод, а программа или сборник библиотек

    private static readonly Dictionary<string, (long Len, long Ticks, List<ModEntry> Mods)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static readonly Regex BrowserCopyRx = new(@"\s*\(\d{1,3}\)$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionTailRx = new(@"^[\s._-]*v?\d+(?:[._]\d+)*(?:[\s._-]*[a-z0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// Мод собран для Slime Rancher 2 (или для любой игры)?
    public static bool IsForThisGame(ModEntry e)
        => e.Games.Count == 0 || e.Games.Any(g => string.IsNullOrWhiteSpace(g) || g.Replace(" ", "").Equals("SlimeRancher2", StringComparison.OrdinalIgnoreCase));

    /// Имя, под которым мод встанет в Mods: без « (1)» от браузера и без версии в хвосте («SlimeLive-0.7.0» → «SlimeLive»).
    public static string CleanId(string fileStem, string assemblyName)
    {
        string id = BrowserCopyRx.Replace((fileStem ?? "").Trim(), "");
        if (!string.IsNullOrEmpty(assemblyName) && !id.Equals(assemblyName, StringComparison.OrdinalIgnoreCase)
            && id.StartsWith(assemblyName, StringComparison.OrdinalIgnoreCase) && VersionTailRx.IsMatch(id.Substring(assemblyName.Length)))
            id = assemblyName;
        return id.Length == 0 ? fileStem ?? "" : id;
    }

    /// Все моды из источников (копии — их можно менять). Файлы читаются один раз: дальше — из кэша по размеру и дате.
    public static List<ModEntry> Scan(IEnumerable<CatalogSource> sources, CancellationToken ct = default)
    {
        var all = new List<ModEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in sources ?? Enumerable.Empty<CatalogSource>())
        {
            if (src == null || string.IsNullOrWhiteSpace(src.Dir)) continue;
            foreach (var f in Files(src))
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(f.FullName)) continue;
                foreach (var m in ReadFile(f))
                {
                    var c = m.Clone();
                    c.SourceLabel = src.Label;
                    all.Add(c);
                }
            }
        }
        return all;
    }

    public static List<FileInfo> Files(CatalogSource src)
    {
        var list = new List<FileInfo>();
        try
        {
            var root = new DirectoryInfo(src.Dir);
            if (!root.Exists) return list;
            var dirs = new List<DirectoryInfo> { root };
            if (src.Depth > 0)
                try { dirs.AddRange(root.EnumerateDirectories().Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0).Take(50)); } catch { }
            DateTime since = src.MaxAgeDays > 0 ? DateTime.Now.AddDays(-src.MaxAgeDays) : DateTime.MinValue;
            foreach (var d in dirs)
            {
                IEnumerable<FileInfo> files;
                try { files = d.EnumerateFiles("*.dll").Concat(d.EnumerateFiles("*.zip")).ToList(); } catch { continue; }
                foreach (var f in files)
                {
                    const FileAttributes skip = FileAttributes.Offline | FileAttributes.Hidden | FileAttributes.System;
                    int raw = (int)f.Attributes;
                    if ((f.Attributes & skip) != 0 || (raw & 0x00400000) != 0 || (raw & 0x00040000) != 0) continue; // облачные «пустышки» не скачиваем
                    bool zip = f.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
                    if (!zip && !f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.Length <= 0 || f.Length > (zip ? MaxZipBytes : MaxDllBytes)) continue;
                    if (f.LastWriteTime < since) continue;
                    list.Add(f);
                }
            }
        }
        catch { }
        return list.OrderByDescending(f => f.LastWriteTime).Take(MaxFilesPerSource).ToList();
    }

    /// Моды внутри файла: .dll — сам файл, .zip — DLL из корня архива или его папки Mods/. Только моды для SR2.
    public static List<ModEntry> ReadFile(FileInfo f)
    {
        long len = f.Length, ticks = f.LastWriteTimeUtc.Ticks;
        lock (Gate)
            if (Cache.TryGetValue(f.FullName, out var hit) && hit.Len == len && hit.Ticks == ticks) return hit.Mods;
        var mods = new List<ModEntry>();
        try
        {
            if (f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var e = ModStore.Read(f.FullName, false);
                if (Keep(e)) mods.Add(Finish(e, f, ""));
            }
            else
            {
                using var zip = ZipFile.OpenRead(f.FullName);
                var dlls = ModStore.ZipDllEntries(zip);
                if (dlls.Count > MaxDllsPerZip) dlls.Clear();
                foreach (var (en, top) in dlls)
                {
                    if (top == "Plugins" || en.Length <= 0 || en.Length > MaxEntryBytes) continue;
                    using var ms = new MemoryStream((int)en.Length);
                    using (var zs = en.Open()) zs.CopyTo(ms);
                    ms.Position = 0;
                    var e = ModStore.Read(ms, en.Name, f.FullName, en.Length, f.LastWriteTime);
                    if (Keep(e)) mods.Add(Finish(e, f, en.FullName));
                }
            }
        }
        catch { /* битый архив или не DLL — просто не мод */ }
        lock (Gate)
        {
            if (Cache.Count > 3000) Cache.Clear();
            Cache[f.FullName] = (len, ticks, mods);
        }
        return mods;

        static bool Keep(ModEntry e) => e.Readable && e.Kind == "Mod" && IsForThisGame(e);
        static ModEntry Finish(ModEntry e, FileInfo f, string entry)
        {
            e.Available = true;
            e.Enabled = false;
            e.EntryName = entry;
            e.Path = f.FullName;
            e.Modified = f.LastWriteTime;
            e.Id = CleanId(e.Id, e.AssemblyName);
            e.FileName = e.Id + ".dll";
            return e;
        }
    }
}

// ───────────── обновления ─────────────

public sealed class UpdateInfo
{
    public string Id = "";            // установленный мод
    public string Current = "";       // его версия
    public string Version = "";       // найденная версия ("" — для «устарел»)
    public string Kind = "";          // catalog — новый файл в каталоге; stored — новее среди старых копий; outdated — по журналу игры
    public ModEntry Source;           // для catalog: откуда ставить
    public BackupInfo Stored;         // для stored: какую копию вернуть
    public string Reason = "";        // для outdated: что нашлось в журнале
    public bool CanInstall => Source != null || Stored != null;

    /// «в папке «Загрузки»: SlimeLive-0.7.zip», «среди сохранённых копий (…)», «в журнале игры: MissingMethodException».
    public string Where => Kind switch
    {
        "catalog" => "в папке «" + Source?.SourceLabel + "»: " + IOPath.GetFileName(Source?.Path ?? ""),
        "stored" => "среди сохранённых копий (" + (Stored?.Where == "_replaced" ? "Disabled\\_replaced" : Stored?.FileName) + ")",
        _ => "в журнале игры: " + Reason,
    };
}

public static class Catalog
{
    /// Это тот же мод? По имени файла, имени сборки или названию из MelonInfo.
    public static bool SameMod(ModEntry a, ModEntry b)
    {
        if (a == null || b == null) return false;
        if (a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(a.AssemblyName) && a.AssemblyName.Equals(b.AssemblyName, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(a.Name) && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// Лучшая копия: новее версия, при равной — свежее файл.
    private static ModEntry Best(IEnumerable<ModEntry> list)
        => list.OrderByDescending(x => x, Comparer<ModEntry>.Create((p, q) => VersionUtil.Compare(p.Version, q.Version))).ThenByDescending(x => x.Modified).FirstOrDefault();

    /// Моды каталога, которых нет среди установленных (по одной лучшей копии), с зависимостями — по алфавиту.
    public static List<ModEntry> AvailableOnly(IReadOnlyList<ModEntry> installed, IReadOnlyList<ModEntry> catalog, ManagerConfig cfg = null)
    {
        var fresh = (catalog ?? Array.Empty<ModEntry>()).Where(c => !(installed ?? Array.Empty<ModEntry>()).Any(i => SameMod(i, c))).ToList();
        var groups = new List<List<ModEntry>>();
        foreach (var c in fresh)
        {
            var g = groups.FirstOrDefault(x => SameMod(x[0], c));
            if (g == null) groups.Add(new List<ModEntry> { c }); else g.Add(c);
        }
        var list = groups.Select(g => Best(g).Clone()).OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        ResolveFor(list, (installed ?? Array.Empty<ModEntry>()).Concat(list).ToList(), cfg);
        return list;
    }

    /// Зависимости доступных модов — среди установленных и доступных (как ModStore.ResolveDependencies).
    private static void ResolveFor(List<ModEntry> targets, List<ModEntry> universe, ManagerConfig cfg)
    {
        var byAsm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in universe)
        {
            if (!string.IsNullOrEmpty(m.AssemblyName)) byAsm[m.AssemblyName] = m.Id;
            byAsm[m.Id] = m.Id;
            if (!string.IsNullOrEmpty(m.Name)) byName[m.Name] = m.Id;
        }
        foreach (var m in targets)
        {
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in m.References) if (byAsm.TryGetValue(r, out var id)) deps.Add(id);
            foreach (var r in m.MelonDependencies) deps.Add(byAsm.TryGetValue(r, out var id) || byName.TryGetValue(r, out id) ? id : r);
            if (cfg != null && cfg.ExtraDependencies.TryGetValue(m.Id, out var extra)) foreach (var r in extra) deps.Add(r);
            deps.Remove(m.Id);
            m.DependsOn = deps.OrderBy(x => x).ToList();
            if (cfg != null && string.IsNullOrWhiteSpace(m.Description) && cfg.Descriptions.TryGetValue(m.Id, out var d)) m.Description = d;
        }
    }

    /// Каким установленным модам нужно обновление: новый файл в каталоге → новее среди старых копий → «устарел» по журналу.
    public static Dictionary<string, UpdateInfo> FindUpdates(IReadOnlyList<ModEntry> installed, IReadOnlyList<ModEntry> catalog,
        IReadOnlyDictionary<string, List<BackupInfo>> stored, MelonLogInfo log)
    {
        var map = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in installed ?? Array.Empty<ModEntry>())
        {
            if (!m.Readable) continue;
            var cand = Best((catalog ?? Array.Empty<ModEntry>()).Where(c => SameMod(m, c) && VersionUtil.IsNewer(c.Version, m.Version)));
            if (cand != null)
            {
                map[m.Id] = new UpdateInfo { Id = m.Id, Current = m.Version, Version = cand.Version, Kind = "catalog", Source = cand };
                continue;
            }
            if (stored != null && stored.TryGetValue(m.Id, out var copies))
            {
                var b = copies.Where(x => VersionUtil.IsNewer(x.Version, m.Version))
                    .OrderByDescending(x => x, Comparer<BackupInfo>.Create((p, q) => VersionUtil.Compare(p.Version, q.Version))).ThenByDescending(x => x.Date).FirstOrDefault();
                if (b != null)
                {
                    map[m.Id] = new UpdateInfo { Id = m.Id, Current = m.Version, Version = b.Version, Kind = "stored", Stored = b };
                    continue;
                }
            }
            var ls = log?.For(m.Id);
            if (ls != null && ls.Outdated)
                map[m.Id] = new UpdateInfo { Id = m.Id, Current = m.Version, Kind = "outdated", Reason = ls.OutdatedReason };
        }
        return map;
    }
}

// ───────────── быстрые фильтры ─────────────

/// Быстрые фильтры списка. Enabled и Catalog — у стиля «Компактный»: вкладка «Вкл» и раздел «Каталог и обновления»
/// (доступные для установки и моды с новой версией).
public enum CatalogFilter { All = 0, Installed = 1, Updates = 2, Disabled = 3, Problems = 4, Enabled = 5, Catalog = 6 }

public static class CatalogView
{
    public static bool InFilter(CatalogFilter f, ModEntry m, Func<string, bool> isOn, Func<ModEntry, bool> hasUpdate, Func<ModEntry, bool> hasProblem) => f switch
    {
        CatalogFilter.Installed => !m.Available,
        CatalogFilter.Updates => !m.Available && hasUpdate != null && hasUpdate(m),
        CatalogFilter.Disabled => !m.Available && !(isOn?.Invoke(m.Id) ?? m.Enabled),
        CatalogFilter.Problems => !m.Available && hasProblem != null && hasProblem(m),
        CatalogFilter.Enabled => !m.Available && (isOn?.Invoke(m.Id) ?? m.Enabled),
        CatalogFilter.Catalog => m.Available || hasUpdate != null && hasUpdate(m),
        _ => true,
    };

    /// Видимый список: сначала установленные, потом доступные. При поиске внутри групп — по релевантности.
    public static List<ModEntry> Build(IReadOnlyList<ModEntry> installed, IReadOnlyList<ModEntry> available, CatalogFilter f, SearchQuery q,
        Func<string, bool> isOn, Func<ModEntry, bool> hasUpdate, Func<ModEntry, bool> hasProblem)
    {
        var rows = new List<(ModEntry M, int Score, int Group, int Order)>();
        int order = 0;
        void Add(ModEntry m, int group)
        {
            order++;
            if (!InFilter(f, m, isOn, hasUpdate, hasProblem)) return;
            int s = ModSearch.Score(m, q);
            if (s > 0) rows.Add((m, s, group, order));
        }
        foreach (var m in installed ?? Array.Empty<ModEntry>()) Add(m, 0);
        foreach (var m in available ?? Array.Empty<ModEntry>()) Add(m, 1);
        bool searching = q != null && !q.IsEmpty;
        return rows.OrderBy(r => r.Group).ThenByDescending(r => searching ? r.Score : 0).ThenBy(r => r.Order).Select(r => r.M).ToList();
    }

    public static int Count(IReadOnlyList<ModEntry> installed, IReadOnlyList<ModEntry> available, CatalogFilter f,
        Func<string, bool> isOn, Func<ModEntry, bool> hasUpdate, Func<ModEntry, bool> hasProblem)
        => (installed ?? Array.Empty<ModEntry>()).Concat(available ?? Array.Empty<ModEntry>()).Count(m => InFilter(f, m, isOn, hasUpdate, hasProblem));
}
