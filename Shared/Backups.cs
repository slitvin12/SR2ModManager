// Backups.cs — бэкапы сохранений игры и пресеты набора модов.
// Бэкап: zip (saves/… + manifest.json с размером и SHA-256 каждого файла). Пишется во временный .part,
// проверяется и только потом получает своё имя. Восстановление — через распаковку рядом с папкой сохранений,
// проверку хэшей, страховочный бэкап текущего состояния и переименования с откатом при любой ошибке.
// Пресет: список модов (id, версия, SHA-256) в JSON или zip вместе с самими DLL.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;

namespace SR2ModManager.Core;

public sealed class SaveBackupFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";       // относительный путь, разделитель '/'
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("modified")] public DateTime Modified { get; set; }   // UTC
}

public sealed class SaveBackupManifest
{
    [JsonPropertyName("format")] public string Format { get; set; } = SaveBackups.Format;
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("created")] public DateTime Created { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("mods")] public List<string> Mods { get; set; } = new();
    [JsonPropertyName("files")] public List<SaveBackupFile> Files { get; set; } = new();
    [JsonIgnore] public long Bytes => Files.Sum(f => f.Size);
}

/// Бэкап в списке (из описи внутри zip).
public sealed class SaveBackupInfo
{
    public string Path = "";
    public string FileName => IOPath.GetFileName(Path);
    public DateTime Created;
    public string Reason = "", Note = "", Game = "";
    public int Files;
    public long Bytes, ZipSize;
    public bool Readable;
    public string Error = "";
    public SaveBackupManifest? Manifest;
    public bool Auto => Reason != "manual";
    public string ReasonText => SaveBackups.ReasonText(Reason);
}

/// Результат проверки целостности бэкапа.
public sealed class BackupCheck
{
    public bool Ok => Problems.Count == 0;
    public List<string> Problems = new();
    public int Files;
    public long Bytes;
    public SaveBackupManifest? Manifest;
    public TimeSpan Took;
    public string Summary => Ok ? $"целый: файлов {Files}, SHA-256 совпали"
        : string.Join("; ", Problems.Take(3)) + (Problems.Count > 3 ? $" (и ещё {Problems.Count - 3})" : "");
}

public sealed class RestoreResult
{
    public bool Ok;
    public string Error = "";
    public string SafetyBackup = "";   // бэкап состояния до восстановления
    public int Files;
    public bool RolledBack;
    public List<string> Log = new();
}

public static class SaveBackups
{
    public const string Format = "sr2mm-saves";
    public const int KeepAuto = 20;        // автоматических бэкапов храним столько; ручные не удаляются
    private const string Prefix = "saves/";
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string DefaultDir(ModStore store) => IOPath.Combine(store.ConfigDir, "Backups", "Saves");

    public static string LocalLow
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\', '/');
            string? up = IOPath.GetDirectoryName(local);
            return IOPath.Combine(up ?? local, "LocalLow");
        }
    }

    /// Где Slime Rancher 2 хранит сохранения (Unity: %USERPROFILE%\AppData\LocalLow\<компания>\<продукт>).
    public static List<string> CandidateRoots() => new()
    {
        IOPath.Combine(LocalLow, "MonomiPark", "SlimeRancher2"),
        IOPath.Combine(LocalLow, "Monomi Park", "Slime Rancher 2"),
    };

    /// Папка сохранений: первая, где есть файлы сохранений, иначе первая существующая, иначе null.
    public static string? FindSaveRoot()
    {
        string? any = null;
        foreach (var r in CandidateRoots())
        {
            if (!Directory.Exists(r)) continue;
            any ??= r;
            try { if (Collect(r).Count > 0) return r; } catch { }
        }
        return any;
    }

    public static string ReasonText(string reason) => reason switch
    {
        "manual" => "вручную",
        "before-update" => "перед обновлением мода",
        "before-restore" => "перед восстановлением",
        "before-preset" => "перед импортом пресета",
        _ => reason,
    };

    /// Входит ли файл в бэкап: всё в папке сохранений, кроме кэша Unity\, журналов игры (Player.log) и наших временных папок.
    public static bool InScope(string rel)
    {
        rel = rel.Replace('\\', '/');
        int slash = rel.IndexOf('/');
        string first = slash < 0 ? rel : rel.Substring(0, slash);
        if (first.StartsWith(".sr2mm-", StringComparison.OrdinalIgnoreCase)) return false;
        if (slash >= 0 && first.Equals("Unity", StringComparison.OrdinalIgnoreCase)) return false;
        if (slash < 0 && (rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".log.tmp", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// Файлы сохранений: (полный путь, относительный с '/'), по алфавиту. Символьные ссылки не обходятся.
    public static List<(string Full, string Rel)> Collect(string root)
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(root)) return list;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string d = stack.Pop();
            foreach (var f in Directory.GetFiles(d))
            {
                string rel = IOPath.GetRelativePath(root, f).Replace('\\', '/');
                if (InScope(rel)) list.Add((f, rel));
            }
            foreach (var sub in Directory.GetDirectories(d))
            {
                try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                string rel = IOPath.GetRelativePath(root, sub).Replace('\\', '/');
                if (InScope(rel + "/x")) stack.Push(sub);
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return list;
    }

    public static string Hex(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Hex(SHA256.HashData(fs));
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        string r = sb.ToString().Trim('-');
        return r.Length == 0 ? "backup" : r.Length > 32 ? r.Substring(0, 32) : r;
    }

    /// Сделать бэкап папки сохранений. Возвращает проверенный бэкап; при ошибке — исключение, частичных файлов не остаётся.
    public static SaveBackupInfo Create(string saveRoot, string backupDir, string reason, string note = "", string game = "", IEnumerable<string>? mods = null, string? protect = null)
    {
        if (!Directory.Exists(saveRoot)) throw new DirectoryNotFoundException("Папка сохранений не найдена: " + saveRoot);
        var files = Collect(saveRoot);
        if (files.Count == 0) throw new InvalidOperationException("В папке сохранений нет файлов: " + saveRoot);
        Directory.CreateDirectory(backupDir);
        var now = DateTime.Now;
        string baseName = $"{now:yyyyMMdd-HHmmss}-{Slug(reason)}";
        string path = IOPath.Combine(backupDir, baseName + ".zip");
        for (int i = 2; File.Exists(path); i++) path = IOPath.Combine(backupDir, $"{baseName}-{i}.zip");
        string part = path + ".part";
        var mf = new SaveBackupManifest { Created = now, Reason = reason, Note = note, Source = saveRoot, Game = game, Mods = mods?.ToList() ?? new() };
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var buf = new byte[1 << 16];
                foreach (var (full, rel) in files)
                {
                    var fi = new FileInfo(full);
                    var e = zip.CreateEntry(Prefix + rel, CompressionLevel.Fastest);
                    try { e.LastWriteTime = fi.LastWriteTime; } catch { }   // zip хранит даты 1980–2107
                    long size = 0;
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    using (var dst = e.Open())
                    {
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) { sha.AppendData(buf, 0, n); dst.Write(buf, 0, n); size += n; }
                    }
                    mf.Files.Add(new SaveBackupFile { Path = rel, Size = size, Sha256 = Hex(sha.GetHashAndReset()), Modified = fi.LastWriteTimeUtc });
                }
                var me = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var w = me.Open()) JsonSerializer.Serialize(w, mf, Json);
            }
            var check = Verify(part);
            if (!check.Ok) throw new InvalidDataException("Бэкап не прошёл проверку: " + check.Summary);
            File.Move(part, path);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        Prune(backupDir, KeepAuto, protect);
        var info = new SaveBackupInfo { Path = path, Readable = true };
        Fill(info, mf);
        try { info.ZipSize = new FileInfo(path).Length; } catch { }
        return info;
    }

    private static void Fill(SaveBackupInfo info, SaveBackupManifest mf)
    {
        info.Manifest = mf;
        info.Created = mf.Created;
        info.Reason = mf.Reason;
        info.Note = mf.Note;
        info.Game = mf.Game;
        info.Files = mf.Files.Count;
        info.Bytes = mf.Bytes;
    }

    private static SaveBackupManifest? ReadManifest(ZipArchive zip)
    {
        var e = zip.GetEntry("manifest.json");
        if (e == null || e.Length > 16 << 20) return null;
        using var s = e.Open();
        var mf = JsonSerializer.Deserialize<SaveBackupManifest>(s, Json);
        return mf != null && mf.Format == Format ? mf : null;
    }

    private static readonly Dictionary<string, (long Size, long Ticks, SaveBackupInfo Info)> InfoCache = new(StringComparer.OrdinalIgnoreCase);

    /// Бэкапы в папке, новые сверху (опись читается один раз на версию файла).
    public static List<SaveBackupInfo> List(string backupDir)
    {
        var res = new List<SaveBackupInfo>();
        if (!Directory.Exists(backupDir)) return res;
        foreach (var p in Directory.GetFiles(backupDir, "*.zip"))
        {
            var fi = new FileInfo(p);
            lock (InfoCache)
                if (InfoCache.TryGetValue(p, out var c) && c.Size == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) { res.Add(c.Info); continue; }
            var info = new SaveBackupInfo { Path = p, Created = fi.LastWriteTime, ZipSize = fi.Length };
            try
            {
                using var zip = ZipFile.OpenRead(p);
                var mf = ReadManifest(zip);
                if (mf == null) info.Error = "нет описи manifest.json";
                else { Fill(info, mf); info.Readable = true; }
            }
            catch (Exception e) { info.Error = e.Message; }
            lock (InfoCache) InfoCache[p] = (fi.Length, fi.LastWriteTimeUtc.Ticks, info);
            res.Add(info);
        }
        return res.OrderByDescending(b => b.Created).ToList();
    }

    /// Удалить старые автоматические бэкапы (оставить keepAuto последних) и брошенные .part.
    public static int Prune(string backupDir, int keepAuto = KeepAuto, string? protect = null)
    {
        int n = 0;
        try
        {
            foreach (var p in Directory.GetFiles(backupDir, "*.zip.part"))
                if (File.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddHours(-1)) try { File.Delete(p); } catch { }
            foreach (var b in List(backupDir).Where(b => b.Readable && b.Auto).OrderByDescending(b => b.Created).Skip(keepAuto))
                if (protect == null || !IOPath.GetFullPath(b.Path).Equals(IOPath.GetFullPath(protect), StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(b.Path); n++; } catch { }
        }
        catch { }
        return n;
    }

    /// Последний бэкап, если с тех пор сохранения не менялись (тот же набор файлов, размеры и даты), иначе null.
    public static SaveBackupInfo? UpToDate(string saveRoot, string backupDir)
    {
        var last = List(backupDir).FirstOrDefault(b => b.Readable && b.Manifest != null);
        if (last?.Manifest == null) return null;
        var now = Collect(saveRoot);
        if (now.Count != last.Manifest.Files.Count) return null;
        var map = last.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var (full, rel) in now)
        {
            if (!map.TryGetValue(rel, out var f)) return null;
            var fi = new FileInfo(full);
            if (fi.Length != f.Size || fi.LastWriteTimeUtc != f.Modified) return null;
        }
        return last;
    }

    private static bool SafeRel(string p)
    {
        if (string.IsNullOrEmpty(p) || p.Length > 400) return false;
        if (p.Contains('\\') || p.Contains(':') || p.StartsWith("/", StringComparison.Ordinal)) return false;
        if (p.IndexOfAny(IOPath.GetInvalidPathChars()) >= 0) return false;
        foreach (var part in p.Split('/')) if (part == ".." || part == "." || part.Length == 0) return false;
        return true;
    }

    /// Проверка целостности: опись на месте, каждый файл есть, размер и SHA-256 совпадают, лишних и опасных путей нет.
    public static BackupCheck Verify(string zipPath)
    {
        var sw = Stopwatch.StartNew();
        var r = new BackupCheck();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var mf = ReadManifest(zip);
            if (mf == null) { r.Problems.Add("нет описи manifest.json — это не бэкап SR2 Mod Manager"); return r; }
            r.Manifest = mf;
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                if (e.FullName == "manifest.json") continue;
                if (e.FullName.EndsWith("/", StringComparison.Ordinal) && e.Length == 0) continue;   // запись папки
                if (!e.FullName.StartsWith(Prefix, StringComparison.Ordinal) || !SafeRel(e.FullName)) { r.Problems.Add("посторонний или опасный путь: " + e.FullName); continue; }
                entries[e.FullName.Substring(Prefix.Length)] = e;
            }
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in mf.Files)
            {
                if (!SafeRel(f.Path) || !InScope(f.Path)) { r.Problems.Add("опасный путь в описи: " + f.Path); continue; }
                if (!listed.Add(f.Path)) { r.Problems.Add("файл дважды в описи: " + f.Path); continue; }
                if (!entries.TryGetValue(f.Path, out var e)) { r.Problems.Add("нет файла: " + f.Path); continue; }
                if (e.Length != f.Size) { r.Problems.Add($"не совпадает размер: {f.Path} ({e.Length} вместо {f.Size})"); continue; }
                string h;
                using (var s = e.Open()) h = Hex(SHA256.HashData(s));
                if (!h.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) { r.Problems.Add("не совпадает SHA-256: " + f.Path); continue; }
                r.Files++;
                r.Bytes += f.Size;
            }
            foreach (var k in entries.Keys)
                if (!listed.Contains(k)) r.Problems.Add("файл не из описи: " + k);
            if (mf.Files.Count == 0) r.Problems.Add("бэкап пустой");
        }
        catch (InvalidDataException e) { r.Problems.Add("архив повреждён: " + e.Message); }
        catch (Exception e) { r.Problems.Add("не читается: " + e.Message); }
        r.Took = sw.Elapsed;
        return r;
    }

    /// Сравнить папку с описью: тот же набор файлов (в пределах InScope), размеры и SHA-256. Пусто — совпало.
    public static List<string> Compare(string root, SaveBackupManifest mf)
    {
        var bad = new List<string>();
        var have = Collect(root).ToDictionary(f => f.Rel, f => f.Full, StringComparer.OrdinalIgnoreCase);
        foreach (var f in mf.Files)
        {
            if (!have.Remove(f.Path, out var full)) { bad.Add("нет " + f.Path); continue; }
            var fi = new FileInfo(full);
            if (fi.Length != f.Size) { bad.Add("размер " + f.Path); continue; }
            if (!Sha256File(full).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) bad.Add("SHA-256 " + f.Path);
        }
        foreach (var k in have.Keys) bad.Add("лишний " + k);
        return bad;
    }

    private static void MoveAny(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
    }

    private static void DeleteAny(string p)
    {
        if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p);
    }

    /// Восстановить сохранения из бэкапа. Порядок: игра закрыта → бэкап цел → страховочный бэкап текущих сохранений →
    /// распаковка в соседнюю временную папку и сверка SHA-256 → текущие файлы в сторону, восстановленные на место
    /// (переименования на том же диске) → итоговая сверка. Любая ошибка — откат к прежнему состоянию.
    /// Кэш Unity\ и Player.log не трогаются.
    public static RestoreResult Restore(string zipPath, string saveRoot, string backupDir, Func<bool>? gameRunning = null,
                                        string game = "", IEnumerable<string>? mods = null)
    {
        var r = new RestoreResult();
        gameRunning ??= ModStore.IsGameRunning;
        if (gameRunning()) { r.Error = "Игра запущена — закройте её: иначе она перезапишет восстановленные сохранения."; return r; }
        var check = Verify(zipPath);
        if (!check.Ok || check.Manifest == null) { r.Error = "Бэкап повреждён, восстанавливать его нельзя: " + check.Summary; return r; }
        var mf = check.Manifest;
        r.Log.Add($"бэкап цел: файлов {check.Files}, {check.Bytes} байт, проверка {check.Took.TotalMilliseconds:0} мс");

        string full = IOPath.GetFullPath(saveRoot).TrimEnd('\\', '/');
        string? parent = IOPath.GetDirectoryName(full);
        if (parent == null) { r.Error = "Недопустимая папка сохранений: " + saveRoot; return r; }
        Directory.CreateDirectory(full);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string stage = IOPath.Combine(parent, ".sr2mm-restore-" + stamp);
        string old = IOPath.Combine(parent, ".sr2mm-old-" + stamp);

        if (Collect(full).Count > 0)
        {
            try
            {
                var sb = Create(full, backupDir, "before-restore", "перед восстановлением " + IOPath.GetFileName(zipPath), game, mods, protect: zipPath);
                r.SafetyBackup = sb.Path;
                r.Log.Add("страховочный бэкап текущих сохранений: " + sb.FileName);
            }
            catch (Exception e) { r.Error = "Не удалось сохранить текущие сохранения перед восстановлением — ничего не менял: " + e.Message; return r; }
        }

        var movedOut = new List<(string From, string To)>();
        var movedIn = new List<string>();
        bool rollbackFailed = false;
        try
        {
            Directory.CreateDirectory(stage);
            using (var zip = ZipFile.OpenRead(zipPath))
                foreach (var f in mf.Files)
                {
                    var e = zip.GetEntry(Prefix + f.Path) ?? throw new InvalidDataException("в бэкапе нет " + f.Path);
                    string dst = IOPath.GetFullPath(IOPath.Combine(stage, f.Path.Replace('/', IOPath.DirectorySeparatorChar)));
                    if (!dst.StartsWith(stage + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("опасный путь " + f.Path);
                    Directory.CreateDirectory(IOPath.GetDirectoryName(dst)!);
                    e.ExtractToFile(dst, false);
                    File.SetLastWriteTimeUtc(dst, f.Modified);
                }
            var bad = Compare(stage, mf);
            if (bad.Count > 0) throw new InvalidDataException("распакованные файлы не совпали с описью: " + string.Join(", ", bad.Take(3)));
            r.Log.Add("распаковано и сверено во временной папке");

            Directory.CreateDirectory(old);
            foreach (var item in Directory.GetFileSystemEntries(full))
            {
                string name = IOPath.GetFileName(item);
                if (!InScope(Directory.Exists(item) ? name + "/x" : name)) continue;   // Unity\, Player.log — не наши
                string to = IOPath.Combine(old, name);
                MoveAny(item, to);
                movedOut.Add((item, to));
            }
            foreach (var item in Directory.GetFileSystemEntries(stage))
            {
                string to = IOPath.Combine(full, IOPath.GetFileName(item));
                MoveAny(item, to);
                movedIn.Add(to);
            }
            bad = Compare(full, mf);
            if (bad.Count > 0) throw new InvalidDataException("после восстановления файлы не совпали с описью: " + string.Join(", ", bad.Take(3)));
            r.Ok = true;
            r.Files = mf.Files.Count;
            r.Log.Add($"восстановлено файлов: {r.Files}, итоговая сверка SHA-256 пройдена");
        }
        catch (Exception e)
        {
            r.Error = e.Message;
            try
            {
                for (int i = movedIn.Count - 1; i >= 0; i--) DeleteAny(movedIn[i]);
                for (int i = movedOut.Count - 1; i >= 0; i--) MoveAny(movedOut[i].To, movedOut[i].From);
                r.RolledBack = true;
                r.Log.Add("откат: прежние сохранения возвращены на место");
            }
            catch (Exception e2)
            {
                rollbackFailed = true;
                r.Error += $" · откат не завершён ({e2.Message}). Прежние файлы: {old}" + (r.SafetyBackup.Length > 0 ? ", страховочный бэкап: " + r.SafetyBackup : "");
            }
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            if (!rollbackFailed)
                try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { r.Log.Add("не удалось удалить временную папку " + old); }
        }
        return r;
    }
}

// ───────────── пресеты набора модов ─────────────

public sealed class PresetMod
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("bundled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Bundled { get; set; }
}

public sealed class PresetFile
{
    [JsonPropertyName("format")] public string Format { get; set; } = Presets.Format;
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("created")] public DateTime Created { get; set; }
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("loader")] public string Loader { get; set; } = "";
    [JsonPropertyName("mods")] public List<PresetMod> Mods { get; set; } = new();
}

public sealed class PresetLoad
{
    public string Path = "";
    public bool IsZip;
    public PresetFile Preset = new();
    public HashSet<string> Bundled = new(StringComparer.OrdinalIgnoreCase);   // Id модов, чьи DLL лежат в zip и прошли проверку
    public List<string> Problems = new();
}

/// Что изменится при импорте пресета.
public sealed class PresetPlan
{
    public Dictionary<string, bool> Selection = new(StringComparer.OrdinalIgnoreCase);
    public List<ModEntry> Enable = new(), Disable = new(), Keep = new();
    public List<(PresetMod Want, ModEntry Have)> OtherVersion = new();
    public List<PresetMod> Install = new();   // у нас нет, DLL есть в пресете
    public List<PresetMod> Missing = new();   // нет ни у нас, ни в пресете
}

public static class Presets
{
    public const string Format = "sr2mm-preset";

    public static PresetFile Build(string name, IEnumerable<ModEntry> on, string game = "", string loader = "")
    {
        var p = new PresetFile { Name = name, Created = DateTime.Now, Game = game, Loader = loader };
        foreach (var m in on.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var pm = new PresetMod { Id = m.Id, Name = m.DisplayName ?? m.Id, Version = m.Version ?? "", File = IOPath.GetFileName(m.Path ?? m.Id + ".dll") };
            try
            {
                if (m.Path != null && System.IO.File.Exists(m.Path)) { pm.Size = new FileInfo(m.Path).Length; pm.Sha256 = SaveBackups.Sha256File(m.Path); }
            }
            catch { }
            p.Mods.Add(pm);
        }
        return p;
    }

    private static bool SafeDll(string file) =>
        file.Length > 4 && file.Length < 200 && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        && file.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && file.IndexOfAny(IOPath.GetInvalidFileNameChars()) < 0 && !file.StartsWith(".", StringComparison.Ordinal);

    /// Сохранить пресет: .zip — вместе с DLL модов (bundle), иначе JSON.
    public static void Save(PresetFile p, string path, IReadOnlyList<ModEntry>? bundle = null)
    {
        string part = path + ".part";
        try
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pm in p.Mods)
                    {
                        pm.Bundled = false;
                        var m = bundle?.FirstOrDefault(x => x.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase));
                        if (m?.Path == null || !System.IO.File.Exists(m.Path) || !SafeDll(pm.File) || !used.Add(pm.File)) continue;
                        var e = zip.CreateEntry("mods/" + pm.File, CompressionLevel.Optimal);
                        using (var src = new FileStream(m.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dst = e.Open()) src.CopyTo(dst);
                        pm.Bundled = true;
                    }
                    var je = zip.CreateEntry("preset.json", CompressionLevel.Optimal);
                    using (var w = je.Open()) JsonSerializer.Serialize(w, p, SaveBackups.Json);
                }
            }
            else System.IO.File.WriteAllText(part, JsonSerializer.Serialize(p, SaveBackups.Json), new UTF8Encoding(false));
            System.IO.File.Move(part, path, true);
        }
        catch
        {
            try { System.IO.File.Delete(part); } catch { }
            throw;
        }
    }

    /// Прочитать пресет (JSON или zip) и проверить вложенные DLL по SHA-256.
    public static PresetLoad Load(string path)
    {
        var l = new PresetLoad { Path = path };
        PresetFile? p;
        bool zip;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var sig = new byte[2];
            zip = fs.Read(sig, 0, 2) == 2 && sig[0] == (byte)'P' && sig[1] == (byte)'K';
        }
        l.IsZip = zip;
        if (zip)
        {
            using var z = ZipFile.OpenRead(path);
            var je = z.GetEntry("preset.json") ?? throw new InvalidDataException("в архиве нет preset.json — это не пресет SR2 Mod Manager");
            using (var s = je.Open()) p = JsonSerializer.Deserialize<PresetFile>(s, SaveBackups.Json);
            if (p == null || p.Format != Format) throw new InvalidDataException("preset.json не в формате " + Format);
            foreach (var pm in p.Mods)
            {
                if (!SafeDll(pm.File)) { if (pm.Bundled) l.Problems.Add("недопустимое имя файла: " + pm.File); continue; }
                var e = z.GetEntry("mods/" + pm.File);
                if (e == null) { if (pm.Bundled) l.Problems.Add("в архиве нет " + pm.File); continue; }
                string h;
                using (var s = e.Open()) h = SaveBackups.Hex(SHA256.HashData(s));
                if (pm.Sha256.Length > 0 && !h.Equals(pm.Sha256, StringComparison.OrdinalIgnoreCase)) { l.Problems.Add($"{pm.File} повреждён (SHA-256 не совпал)"); continue; }
                l.Bundled.Add(pm.Id);
            }
        }
        else
        {
            p = JsonSerializer.Deserialize<PresetFile>(System.IO.File.ReadAllText(path), SaveBackups.Json);
            if (p == null || p.Format != Format) throw new InvalidDataException("файл не в формате " + Format);
        }
        p.Mods = p.Mods.Where(m => !string.IsNullOrWhiteSpace(m.Id)).GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        l.Preset = p;
        return l;
    }

    /// План импорта: какие моды включить/выключить, каких нет, у каких другая версия.
    public static PresetPlan Plan(PresetLoad l, IReadOnlyList<ModEntry> installed)
    {
        var plan = new PresetPlan();
        var mods = installed.Where(m => !m.Available).ToList();
        foreach (var m in mods) plan.Selection[m.Id] = false;
        foreach (var pm in l.Preset.Mods)
        {
            var have = mods.FirstOrDefault(m => m.Id.Equals(pm.Id, StringComparison.OrdinalIgnoreCase));
            if (have == null)
            {
                if (l.Bundled.Contains(pm.Id)) plan.Install.Add(pm); else plan.Missing.Add(pm);
                continue;
            }
            plan.Selection[have.Id] = true;
            (have.Enabled ? plan.Keep : plan.Enable).Add(have);
            bool differs = pm.Version.Length > 0 && (have.Version ?? "") != pm.Version;
            if (!differs && pm.Sha256.Length > 0)
                try { differs = have.Path != null && System.IO.File.Exists(have.Path) && !SaveBackups.Sha256File(have.Path).Equals(pm.Sha256, StringComparison.OrdinalIgnoreCase); } catch { }
            if (differs) plan.OtherVersion.Add((pm, have));
        }
        foreach (var m in mods)
            if (m.Enabled && !plan.Selection[m.Id]) plan.Disable.Add(m);
        return plan;
    }

    /// Распаковать DLL из пресета во временную папку (с повторной проверкой SHA-256). Возвращает пути файлов.
    public static List<string> ExtractBundled(string zipPath, IEnumerable<PresetMod> mods, string tempDir)
    {
        var res = new List<string>();
        Directory.CreateDirectory(tempDir);
        using var z = ZipFile.OpenRead(zipPath);
        foreach (var pm in mods)
        {
            if (!SafeDll(pm.File)) continue;
            var e = z.GetEntry("mods/" + pm.File);
            if (e == null) continue;
            string dst = IOPath.Combine(tempDir, pm.File);
            e.ExtractToFile(dst, true);
            if (pm.Sha256.Length > 0 && !SaveBackups.Sha256File(dst).Equals(pm.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                System.IO.File.Delete(dst);
                throw new InvalidDataException(pm.File + " в пресете повреждён (SHA-256 не совпал)");
            }
            res.Add(dst);
        }
        return res;
    }
}
