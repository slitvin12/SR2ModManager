// IconCache.cs — значки модов для карточек. Ищутся рядом с DLL (<Id>.png/.jpg), в UserData\<Id>\icon.png,
// в ModManager\Icons, у доступных из .zip — icon.png в архиве (формат Thunderstore), у остальных —
// встроенный ресурс DLL с именем icon/logo. Грузятся в фоне (не больше двух сразу), уменьшаются до
// размера карточки и хранятся в памяти (LRU). Пока значка нет (или его нет вовсе) — аватар с буквами.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed class IconCache : IDisposable
{
    private sealed class Entry
    {
        public Bitmap? Bmp;
        public bool Done;
        public long Used, Serial, Started;
        public string Source = "";
    }

    private readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly Control _owner;
    private readonly ModStore _store;
    private long _clock, _serial;
    private bool _disposed;
    public int Capacity = 300;
    /// Синхронная загрузка (снимок окна --render: фоновых потоков там нет).
    public bool Sync;
    public static bool Enabled = true;
    /// Значок мода с этим Id загружен (или выяснилось, что его нет) — перерисовать карточку.
    public event Action<string>? Ready;
    // статистика для замеров
    public int Requests, Found, NotFound, Hits;
    public double LoadMs;

    public IconCache(Control owner, ModStore store) { _owner = owner; _store = store; }

    private static string Key(ModEntry m, int px) => m.Id + "|" + px + "|" + (m.Available ? "a" : "i") + "|" + m.Path + "|" + m.EntryName + "|" + m.Modified.Ticks;

    /// Готовый значок (null — ещё грузится или его нет; pending — грузится). serial меняется, когда значок меняется.
    public Bitmap? Get(ModEntry m, int px, out bool pending, out long serial)
    {
        pending = false; serial = 0;
        if (!Enabled || _disposed || m == null || string.IsNullOrEmpty(m.Path)) return null;
        string key = Key(m, px);
        if (_map.TryGetValue(key, out var e))
        {
            // загрузка, ответ которой потерялся (окно пересоздавалось), через 15 с начинается заново
            if (e.Done || Stopwatch.GetElapsedTime(e.Started).TotalSeconds < 15)
            {
                e.Used = ++_clock;
                pending = !e.Done;
                serial = e.Serial;
                if (e.Done) Hits++;
                return e.Bmp;
            }
            _map.Remove(key);
        }
        // окна ещё нет (карточки раскладываются в конструкторе формы): готовый значок некуда вернуть —
        // он будет запрошен при первой отрисовке
        if (!Sync && !_owner.IsHandleCreated) { pending = true; return null; }
        e = new Entry { Used = ++_clock, Started = Stopwatch.GetTimestamp() };
        _map[key] = e;
        Requests++;
        string id = m.Id, path = m.Path, entry = m.EntryName, asm = m.AssemblyName;
        bool avail = m.Available;
        if (Sync)
        {
            var sw = Stopwatch.StartNew();
            Bitmap? b = null; string src = "";
            try { b = Load(id, asm, path, avail, entry, px, out src); } catch { }
            LoadMs += sw.Elapsed.TotalMilliseconds;
            Finish(e, b, src);
            serial = e.Serial;
            Trim();
            return e.Bmp;
        }
        pending = true;
        Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            Bitmap? b = null; string src = ""; double ms = 0;
            try
            {
                var sw = Stopwatch.StartNew();
                b = Load(id, asm, path, avail, entry, px, out src);
                ms = sw.Elapsed.TotalMilliseconds;
            }
            catch { b = null; }
            finally { _gate.Release(); }
            try
            {
                for (int i = 0; i < 100 && !_disposed && !_owner.IsHandleCreated; i++) await Task.Delay(50).ConfigureAwait(false);
                if (_disposed || !_owner.IsHandleCreated) { b?.Dispose(); return; }
                _owner.BeginInvoke(new Action(() =>
                {
                    LoadMs += ms;
                    if (_disposed || !_map.TryGetValue(key, out var cur) || !ReferenceEquals(cur, e)) { b?.Dispose(); return; }
                    Finish(e, b, src);
                    Ready?.Invoke(id);
                }));
            }
            catch { b?.Dispose(); }
        });
        Trim();
        return null;
    }

    private void Finish(Entry e, Bitmap? b, string src)
    {
        e.Bmp = b; e.Done = true; e.Source = src; e.Serial = ++_serial;
        if (b != null) Found++; else NotFound++;
    }

    /// Откуда взят значок мода (для подсказки), "" — значка нет.
    public string SourceOf(ModEntry m, int px) => _map.TryGetValue(Key(m, px), out var e) ? e.Source : "";

    private void Trim()
    {
        if (_map.Count <= Capacity) return;
        foreach (var kv in _map.Where(kv => kv.Value.Done).OrderBy(kv => kv.Value.Used).Take(_map.Count - Capacity * 3 / 4).ToList())
        {
            kv.Value.Bmp?.Dispose();
            _map.Remove(kv.Key);
        }
    }

    public void Clear()
    {
        foreach (var e in _map.Values) e.Bmp?.Dispose();
        _map.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }

    // ───────── поиск и загрузка (фоновый поток) ─────────

    private const long MaxBytes = 4 << 20;

    private Bitmap? Load(string id, string asm, string path, bool avail, string entry, int px, out string source)
    {
        source = "";
        var files = new List<string>();
        string dir = Path.GetDirectoryName(path) ?? "";
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            files.Add(Path.ChangeExtension(path, ".png"));
            files.Add(Path.ChangeExtension(path, ".jpg"));
        }
        foreach (var d in new[] { _store.ModsDir, _store.DisabledDir })
            if (!string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) { files.Add(Path.Combine(d, id + ".png")); files.Add(Path.Combine(d, id + ".jpg")); }
        files.Add(Path.Combine(_store.UserDataDir, id, "icon.png"));
        if (!string.IsNullOrEmpty(asm) && !asm.Equals(id, StringComparison.OrdinalIgnoreCase)) files.Add(Path.Combine(_store.UserDataDir, asm, "icon.png"));
        files.Add(Path.Combine(_store.ManagerDir, "Icons", id + ".png"));
        files.Add(Path.Combine(_store.ManagerDir, "Icons", id + ".jpg"));
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                if (!fi.Exists || fi.Length == 0 || fi.Length > MaxBytes) continue;
                var b = Decode(File.ReadAllBytes(f), px, false);
                if (b != null) { source = f; return b; }
            }
            catch { }
        }
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = ZipIcon(path, entry);
            if (bytes != null) { var b = Decode(bytes, px, false); if (b != null) { source = path + " → icon.png"; return b; } }
            return null;
        }
        var (name, data) = EmbeddedIcon(path);
        if (data != null)
        {
            var b = Decode(data, px, true);
            if (b != null) { source = Path.GetFileName(path) + " → " + name; return b; }
        }
        return null;
    }

    /// icon.png рядом с DLL внутри архива или в корне архива (как в пакетах Thunderstore).
    private static byte[]? ZipIcon(string zip, string entry)
    {
        using var z = ZipFile.OpenRead(zip);
        string folder = "";
        int slash = Math.Max(entry.LastIndexOf('/'), entry.LastIndexOf('\\'));
        if (slash > 0) folder = entry.Substring(0, slash + 1).Replace('\\', '/');
        foreach (var name in new[] { folder + "icon.png", "icon.png" }.Distinct())
        {
            var e = z.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e == null || e.Length == 0 || e.Length > MaxBytes) continue;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        return null;
    }

    /// Встроенный в DLL ресурс-картинка с именем icon/logo/thumbnail (например, MyMod.Resources.icon.png).
    public static (string Name, byte[]? Data) EmbeddedIcon(string dll)
    {
        try
        {
            using var fs = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 64L << 20) return ("", null);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return ("", null);
            var md = pe.GetMetadataReader();
            var cor = pe.PEHeaders.CorHeader;
            if (cor == null || cor.ResourcesDirectory.Size <= 0) return ("", null);
            string? best = null; long offset = 0; int bestScore = 0;
            foreach (var h in md.ManifestResources)
            {
                var r = md.GetManifestResource(h);
                if (!r.Implementation.IsNil) continue; // ресурс в другом файле
                string name = md.GetString(r.Name);
                string n = name.ToLowerInvariant();
                string ext = Path.GetExtension(n);
                if (ext is not (".png" or ".jpg" or ".jpeg")) continue;
                string stem = Path.GetFileNameWithoutExtension(n);
                int dot = stem.LastIndexOf('.');
                string last = dot >= 0 ? stem.Substring(dot + 1) : stem;
                int score = last is "icon" or "logo" or "modicon" or "mod_icon" or "thumbnail" or "appicon" ? 3
                          : last.StartsWith("icon") || last.EndsWith("icon") || last.StartsWith("logo") || last.EndsWith("logo") ? 2 : 0;
                if (score > bestScore) { bestScore = score; best = name; offset = r.Offset; }
            }
            if (best == null) return ("", null);
            var block = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress);
            var br = block.GetReader();
            if (offset < 0 || offset + 4 > br.Length) return ("", null);
            br.Offset = (int)offset;
            int len = br.ReadInt32();
            if (len <= 0 || len > MaxBytes || br.RemainingBytes < len) return ("", null);
            return (best, br.ReadBytes(len));
        }
        catch { return ("", null); }
    }

    /// Картинка → квадрат px×px со скруглёнными краями (сглаженными). requireSquare — у ресурсов из DLL
    /// берём только почти квадратные (иначе это, скорее всего, не значок, а текстура/атлас).
    private static Bitmap? Decode(byte[] data, int px, bool requireSquare)
    {
        using var ms = new MemoryStream(data);
        using var img = Image.FromStream(ms, false, true);
        if (img.Width < 8 || img.Height < 8 || img.Width > 4096 || img.Height > 4096) return null;
        float aspect = img.Width / (float)img.Height;
        if (requireSquare && (aspect < 0.8f || aspect > 1.25f)) return null;
        // по центру, с обрезкой до квадрата
        int side = Math.Min(img.Width, img.Height);
        var src = new Rectangle((img.Width - side) / 2, (img.Height - side) / 2, side, side);
        using var scaled = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = side >= px * 2 ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY); // без тёмной каймы по краям
            g.DrawImage(img, new Rectangle(0, 0, px, px), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        var res = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(res))
        using (var tb = new TextureBrush(scaled))
        using (var path = Theme.RoundRect(new RectangleF(0, 0, px, px), px * 0.25f))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.FillPath(tb, path);
        }
        return res;
    }

    // ───────── аватар-заглушка ─────────

    /// Две буквы из названия: «CreatorToolkit» → CT, «Minecraft HUD» → MH, «SlimeLive» → SL.
    public static string Initials(string? name)
    {
        var words = new List<string>();
        var cur = new System.Text.StringBuilder();
        string s = name ?? "";
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (!char.IsLetterOrDigit(ch)) { if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); } continue; }
            if (cur.Length > 0 && char.IsUpper(ch) && i > 0 && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]))) { words.Add(cur.ToString()); cur.Clear(); }
            cur.Append(ch);
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        if (words.Count == 0) return "?";
        if (words.Count == 1) { string w = words[0]; return w.Length >= 2 ? char.ToUpperInvariant(w[0]) + w.Substring(1, 1).ToLowerInvariant() : w.ToUpperInvariant(); }
        return (char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[^1][0])).ToString();
    }

    // Цвета аватаров — из активной палитры (в тёмном стиле — тёмные подложки со светлыми буквами).
    private static (Color Bg, Color Fg)[] _palette = Array.Empty<(Color, Color)>();
    private static int _paletteGen = -1;

    private static (Color Bg, Color Fg)[] Palette
    {
        get
        {
            if (_paletteGen == Theme.Generation && _palette.Length > 0) return _palette;
            _paletteGen = Theme.Generation;
            _palette = new[]
            {
                (Theme.TealTint, Theme.TealText), (Theme.BlueTint, Theme.BlueText), (Theme.OrangeTint, Theme.OrangeText),
                (Theme.CoralTint, Theme.CoralText), (Theme.Beige, Theme.NavySoft), (Theme.PurpleTint, Theme.PurpleText),
            };
            return _palette;
        }
    }

    /// Цвета аватара — по Id, чтобы у мода они не менялись.
    public static (Color Bg, Color Fg) AvatarColors(string id)
    {
        uint h = 2166136261;
        foreach (char c in (id ?? "").ToLowerInvariant()) h = (h ^ c) * 16777619;
        return Palette[h % (uint)Palette.Length];
    }
}
