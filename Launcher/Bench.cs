// Bench.cs — замеры (--bench <out.json>): запуск, прокрутка, колесо мыши, переключение мода, кэш карточек.
// Внутренние поля читаются через отражение — файл собирается и со старой версией менеджера (для сравнения).
// Окно — за пределами экрана, без активации и без значка на панели задач; на диске ничего не меняется:
// моды «--synthetic N» добавляются только в память, переключения — только отметки (без «Применить»).
// За пределами экрана Windows присылает WM_PAINT с пустой областью, и WinForms ничего не рисует, поэтому
// каждая перерисовка повторяется в память (PaintNow): OnPaintBackground + OnPaint с той же областью отсечения.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal static class Bench
{
    public static long MainStart;          // Stopwatch.GetTimestamp() в начале Main
    public static double ProcToMainMs;     // от старта процесса до Main: загрузка .NET и сборок

    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rc, IntPtr rgn, uint flags);
    [DllImport("user32.dll")] private static extern int GetUpdateRgn(IntPtr hWnd, IntPtr hRgn, bool erase);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hWnd, IntPtr rc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern uint GetRegionData(IntPtr hRgn, uint count, IntPtr data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    private const uint RDW_INVALIDATE = 0x1, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;

    private static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    private static double R3(double v) => Math.Round(v, 3);

    private static FieldInfo? Fld(Type t, string n) { for (var x = t; x != null; x = x.BaseType) { var f = x.GetField(n, F); if (f != null) return f; } return null; }
    private static PropertyInfo? Prop(Type t, string n) { for (var x = t; x != null; x = x.BaseType) { var p = x.GetProperty(n, F); if (p != null) return p; } return null; }
    private static object? Get(object o, string n) { var f = Fld(o.GetType(), n); if (f != null) return f.GetValue(o); return Prop(o.GetType(), n)?.GetValue(o); }
    private static object? Call(object o, string n, params object?[] a)
    {
        for (var x = o.GetType(); x != null; x = x.BaseType)
            foreach (var m in x.GetMethods(F))
                if (m.Name == n && m.GetParameters().Length == a.Length) return m.Invoke(o, a);
        throw new MissingMethodException(o.GetType().Name, n);
    }
    private static object? StaticGet(Type t, string n) => Fld(t, n)?.GetValue(null);

    /// Перехват WM_PAINT окна: сколько длилась отрисовка (повторённая в память), какую площадь и при какой прокрутке.
    private sealed class PaintSpy : NativeWindow
    {
        public readonly List<(long Start, double Ms, long Px, int Pos)> Frames = new();
        public bool On;
        private readonly Func<int> _pos;
        private readonly Control _c;
        public PaintSpy(Control c, Func<int> pos) { _c = c; _pos = pos; AssignHandle(c.Handle); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg != 0x000F || !On) { base.WndProc(ref m); return; }
            long t0 = Stopwatch.GetTimestamp();
            long px = UpdateArea(_c, out var rg);
            base.WndProc(ref m);
            if (rg != null) { PaintNow(_c, rg); rg.Dispose(); }
            Frames.Add((t0, Ms(t0), px, _pos()));
        }
    }

    private static readonly MethodInfo OnPaintM = typeof(Control).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo OnPaintBgM = typeof(Control).GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Dictionary<Control, Bitmap> Canvases = new();

    /// Отрисовка элемента, как по WM_PAINT, но в память. clip — область перерисовки (null — весь элемент). Возвращает мс.
    private static double PaintNow(Control c, Region? clip = null)
    {
        if (c.Width <= 0 || c.Height <= 0) return 0;
        if (!Canvases.TryGetValue(c, out var bmp) || bmp.Width != c.Width || bmp.Height != c.Height)
        {
            bmp?.Dispose();
            Canvases[c] = bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppPArgb);
        }
        using var g = Graphics.FromImage(bmp);
        var r = c.ClientRectangle;
        if (clip != null)
        {
            g.Clip = clip;
            r = Rectangle.Intersect(r, Rectangle.Ceiling(clip.GetBounds(g)));
            if (r.Width <= 0 || r.Height <= 0) return 0;
        }
        long t = Stopwatch.GetTimestamp();
        var pe = new PaintEventArgs(g, r);
        OnPaintBgM.Invoke(c, new object[] { pe });
        OnPaintM.Invoke(c, new object[] { pe });
        return Ms(t);
    }

    /// Весь видимый интерфейс целиком (первый кадр, разворачивание окна).
    private static double PaintAll(Form f)
    {
        double ms = 0;
        foreach (var c in All(f))
            if (c.IsHandleCreated && c.Visible) { ms += PaintNow(c); ValidateRect(c.Handle, IntPtr.Zero); }
        return ms;
    }

    /// Обработать сообщения и отрисовать (в память) всё, что ждёт перерисовки. Возвращает мс отрисовки.
    private static double FlushPaint(Form f)
    {
        Application.DoEvents();
        double ms = 0;
        foreach (var c in All(f))
        {
            if (!c.IsHandleCreated || !c.Visible) continue;
            if (UpdateArea(c, out var rg) > 0 && rg != null) { ms += PaintNow(c, rg); rg.Dispose(); }
            ValidateRect(c.Handle, IntPtr.Zero);
        }
        RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, RDW_UPDATENOW | RDW_ALLCHILDREN);
        return ms;
    }

    /// Что окну c предстоит перерисовать — без площади дочерних окон (их рисуют они сами): пиксели и область.
    private static long UpdateArea(Control c, out Region? region)
    {
        region = null;
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetUpdateRgn(c.Handle, rgn, false) <= 1) return 0;   // ERROR / NULLREGION
            foreach (Control ch in c.Controls)
            {
                if (!ch.IsHandleCreated || !ch.Visible) continue;
                var b = ch.Bounds;
                IntPtr cr = CreateRectRgn(b.Left, b.Top, b.Right, b.Bottom);
                CombineRgn(rgn, rgn, cr, 4);   // RGN_DIFF
                DeleteObject(cr);
            }
            long a = RegionArea(rgn);
            if (a > 0) region = Region.FromHrgn(rgn);
            return a;
        }
        finally { DeleteObject(rgn); }
    }

    private static long RegionArea(IntPtr rgn)
    {
        uint size = GetRegionData(rgn, 0, IntPtr.Zero);
        if (size < 32) return 0;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRegionData(rgn, size, buf) == 0) return 0;
            int n = Marshal.ReadInt32(buf, 8);   // RGNDATAHEADER: dwSize, iType, nCount, nRgnSize, rcBound → 32 байта, дальше RECT[n]
            long a = 0;
            for (int i = 0, o = 32; i < n; i++, o += 16)
                a += (long)(Marshal.ReadInt32(buf, o + 8) - Marshal.ReadInt32(buf, o)) * (Marshal.ReadInt32(buf, o + 12) - Marshal.ReadInt32(buf, o + 4));
            return a;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static Dictionary<string, object> Stats(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        if (a.Length == 0) return new Dictionary<string, object> { ["n"] = 0 };
        double P(double q) => a[Math.Clamp((int)Math.Ceiling(q * a.Length) - 1, 0, a.Length - 1)];
        return new Dictionary<string, object>
        {
            ["n"] = a.Length, ["avg"] = R3(a.Average()), ["p50"] = R3(P(0.5)), ["p95"] = R3(P(0.95)), ["max"] = R3(a[^1]),
            ["over8ms"] = a.Count(x => x > 8), ["over16ms"] = a.Count(x => x > 16.7),
        };
    }

    private static IEnumerable<Control> All(Control c)
    {
        foreach (Control x in c.Controls) { yield return x; foreach (var y in All(x)) yield return y; }
    }

    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint ms, uint wakeMask, uint flags);

    /// Цикл сообщений на ms миллисекунд — как в обычном окне: ждём следующего сообщения (таймер, WM_PAINT), а не Sleep.
    private static void Pump(double ms)
    {
        long t = Stopwatch.GetTimestamp();
        while (true)
        {
            Application.DoEvents();
            double left = ms - Ms(t);
            if (left <= 0) break;
            MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)Math.Ceiling(left), 0x04FF /* QS_ALLINPUT */, 0x0004 /* MWMO_INPUTAVAILABLE */);
        }
    }

    private static string Arg(string[] a, string name)
    {
        int i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : "";
    }

    private static void Memory(Dictionary<string, object?> res, string prefix)
    {
        using var p = Process.GetCurrentProcess();
        res[prefix + "WorkingSetMB"] = R3(p.WorkingSet64 / 1048576.0);
        res[prefix + "PrivateMB"] = R3(p.PrivateMemorySize64 / 1048576.0);
        res[prefix + "ManagedMB"] = R3(GC.GetTotalMemory(false) / 1048576.0);
        res[prefix + "GdiObjects"] = GetGuiResources(p.Handle, 0);
        var gi = GC.GetGCMemoryInfo();
        res[prefix + "GcCommittedMB"] = R3(gi.TotalCommittedBytes / 1048576.0);
        res[prefix + "GcHeapMB"] = R3(gi.HeapSizeBytes / 1048576.0);
        res[prefix + "Threads"] = p.Threads.Count;
        res[prefix + "AllocatedMB"] = R3(GC.GetTotalAllocatedBytes(false) / 1048576.0);
        res[prefix + "Assemblies"] = AppDomain.CurrentDomain.GetAssemblies().Length;
    }
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr hProcess, int flags);

    public static int Run(ModStore store, string outPath, string[] args)
    {
        var res = new Dictionary<string, object?>();
        PaintSpy? spy = null;
        try
        {
            int synthetic = int.TryParse(Arg(args, "--synthetic"), out var sn) ? sn : 0;
            int frames = int.TryParse(Arg(args, "--frames"), out var fr) ? fr : 120;
            var benchField = typeof(RenderOptions).GetProperty("Bench");
            res["version"] = benchField != null ? "new" : "old";
            res["exe"] = Environment.ProcessPath;
            res["procToMainMs"] = R3(ProcToMainMs);

            // ── запуск: конструктор окна → первый полный кадр → фоновая проверка совместимости и значки
            var opt = new RenderOptions();
            benchField?.SetValue(opt, true);
            long t0 = Stopwatch.GetTimestamp();
            var form = new MainForm(store, null, null, opt);
            res["ctorMs"] = R3(Ms(t0));
            float s = form.DeviceDpi / 96f;
            res["dpi"] = form.DeviceDpi;
            form.StartPosition = FormStartPosition.Manual;
            form.ShowInTaskbar = false;
            form.Location = new Point(-20000, -20000);
            form.Size = new Size((int)(1140 * s), (int)(780 * s));
            var list = (Control)Get(form, "_list")!;
            var model = Get(form, "_m")!;
            long t1 = Stopwatch.GetTimestamp();
            form.Show();
            res["showMs"] = R3(Ms(t1));
            res["firstPaintMs"] = R3(PaintAll(form));
            res["showToFirstFrameMs"] = R3(Ms(t1));
            res["mainToFirstFrameMs"] = R3(Ms(MainStart));
            res["processToFirstFrameMs"] = R3(ProcToMainMs + Ms(MainStart));
            Memory(res, "firstFrame");
            bool hasCompat = Prop(model.GetType(), "Env") != null || Fld(model.GetType(), "Env") != null;
            if (hasCompat)
            {
                long tw = Stopwatch.GetTimestamp();
                while (Get(model, "Env") == null && Ms(tw) < 20000) { Application.DoEvents(); Thread.Sleep(1); }
                res["mainToCompatReadyMs"] = R3(Ms(MainStart));
                res["compatBackgroundMs"] = R3(Convert.ToDouble(Get(form, "CompatMs") ?? 0.0));
                res["compatAllocMB"] = R3(Convert.ToDouble(Get(form, "CompatAllocBytes") ?? 0L) / 1048576.0);
                var env = Get(model, "Env");
                res["env"] = env == null ? null : Get(env, "Summary");
                if (Get(model, "Compat") is object rep && Get(rep, "ByMod") is System.Collections.IDictionary by)
                {
                    var d = new Dictionary<string, object>();
                    foreach (System.Collections.DictionaryEntry kv in by)
                        d[(string)kv.Key] = ((System.Collections.IEnumerable)kv.Value!).Cast<object>().Select(i => i.ToString()).ToList();
                    res["compatIssues"] = d;
                }
                if (env != null && Get(env, "Issues") is System.Collections.IEnumerable ei)
                    res["envIssues"] = ei.Cast<object>().Select(i => i.ToString()).ToList();
            }
            Pump(400);   // значки и отложенные перерисовки
            res["afterStartPaintMs"] = R3(FlushPaint(form));
            res["fullRepaintMs"] = R3(PaintAll(form));
            var mods = (List<ModEntry>)Get(model, "Mods")!;
            var sel = (Dictionary<string, bool>)Get(model, "Sel")!;
            res["realMods"] = mods.Count;
            Memory(res, "startup");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Memory(res, "startupAfterGc");

            // ── синтетические моды (только в памяти): копии настоящих с новыми Id
            if (synthetic > 0 && mods.Count > 0)
            {
                var src = mods.Where(m => !m.Available).ToList();
                for (int i = 0; i < synthetic; i++)
                {
                    var b = src[i % src.Count];
                    var c = b.Clone();
                    c.Id = $"Bench{i:000}_{b.Id}";
                    c.Name = $"{b.DisplayName} · копия {i + 1}";
                    c.FileName = c.Id + ".dll";
                    c.DependsOn.Clear();
                    c.Enabled = i % 3 != 0;
                    mods.Add(c);
                    sel[c.Id] = c.Enabled;
                }
                long ts = Stopwatch.GetTimestamp();
                Call(form, "RefreshUi");
                FlushPaint(form);
                res["syntheticRefreshAndPaintMs"] = R3(Ms(ts));
                Pump(1500);
                FlushPaint(form);
            }
            res["mods"] = mods.Count;

            var scrollF = Fld(list.GetType(), "_scroll")!;
            int Pos() => (int)scrollF.GetValue(list)!;
            spy = new PaintSpy(list, Pos);
            int max = (int)Get(list, "MaxScroll")!;
            res["listSize"] = list.Width + "x" + list.Height;
            res["maxScroll"] = max;
            var listStats = new[] { "Paints", "CardsDrawn", "CacheHits", "CacheMisses" };
            void ResetListStats() { if (Fld(list.GetType(), "Paints") != null) Call(list, "ResetStats"); }
            Dictionary<string, object?> ListStats()
            {
                var d = new Dictionary<string, object?>();
                foreach (var k in listStats) if (Fld(list.GetType(), k) != null) d[k] = Get(list, k);
                if (Prop(list.GetType(), "CacheCount") != null) d["CacheCount"] = Get(list, "CacheCount");
                return d;
            }

            // ── прокрутка: кадр = сдвиг на 40 px (быстрое колесо), Invalidate + Update; дважды — по свежим и по уже виденным карточкам
            if (max > 0)
            {
                int step = Math.Max(1, (int)(40 * s));
                for (int pass = 1; pass <= 2; pass++)
                {
                    scrollF.SetValue(list, 0); list.Invalidate(); FlushPaint(form);
                    ResetListStats();
                    var frameMs = new List<double>();
                    int pos = 0;
                    long painted = 0;
                    for (int f = 0; f < frames && pos < max; f++)
                    {
                        pos = Math.Min(max, pos + step);
                        scrollF.SetValue(list, pos);
                        long tf = Stopwatch.GetTimestamp();
                        list.Invalidate();
                        long a = UpdateArea(list, out var rg);
                        if (rg != null) { PaintNow(list, rg); rg.Dispose(); }
                        ValidateRect(list.Handle, IntPtr.Zero);
                        frameMs.Add(Ms(tf));
                        painted += a;
                    }
                    res["scroll" + pass] = Stats(frameMs);
                    res["scroll" + pass + "PaintedMPx"] = R3(painted / 1e6);
                    res["scroll" + pass + "List"] = ListStats();
                }
            }

            // ── колесо мыши: 12 щелчков по 90 мс (быстрая прокрутка), анимация как в обычном окне
            if (max > 0)
            {
                var animF = typeof(Anim).GetField("Enabled", BindingFlags.Static | BindingFlags.Public);
                animF?.SetValue(null, true);
                var onWheel = typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!;
                scrollF.SetValue(list, 0); list.Invalidate(); FlushPaint(form);
                ResetListStats();
                spy.Frames.Clear(); spy.On = true;
                long tw0 = Stopwatch.GetTimestamp();
                int notches = 12;
                for (int k = 0; k < notches; k++)
                {
                    onWheel.Invoke(list, new object[] { new MouseEventArgs(MouseButtons.None, 0, list.Width / 2, list.Height / 2, -120) });
                    Pump(90);
                }
                Pump(400);
                spy.On = false;
                animF?.SetValue(null, false);
                var fs = spy.Frames;
                var jumps = new List<double>();
                var gaps = new List<double>();
                int prev = 0;
                for (int i = 0; i < fs.Count; i++)
                {
                    jumps.Add(Math.Abs(fs[i].Pos - prev)); prev = fs[i].Pos;
                    if (i > 0) gaps.Add((fs[i].Start - fs[i - 1].Start) * 1000.0 / Stopwatch.Frequency);
                }
                res["wheel"] = new Dictionary<string, object?>
                {
                    ["notches"] = notches, ["scrolledPx"] = Pos(), ["frames"] = fs.Count,
                    ["framesPerNotch"] = R3(fs.Count / (double)notches),
                    ["pxPerFrame"] = Stats(jumps), ["paintMs"] = Stats(fs.Select(x => x.Ms)), ["frameGapMs"] = Stats(gaps),
                    ["list"] = ListStats(),
                };
            }

            // ── переключение мода: сколько длится Toggle и какую площадь окна приходится перерисовывать
            scrollF.SetValue(list, 0); list.Invalidate(); FlushPaint(form);
            var target = mods.Where(m => !m.Available)
                .FirstOrDefault(m => !mods.Any(o => o.DependsOn.Contains(m.Id, StringComparer.OrdinalIgnoreCase)) && m.DependsOn.All(d => sel.TryGetValue(d, out var v) && v));
            if (target != null)
            {
                res["toggleMod"] = target.Id;
                // prefix "toggle" — переключается выбранный мод (панель справа меняется);
                // "toggleOther" — выбран другой, не связанный с ним мод (панели справа перерисовываться незачем)
                void MeasureToggles(string prefix, ModEntry selected)
                {
                    Call(list, "Select", selected.Id, true);
                    Pump(50); FlushPaint(form);
                    var callMs = new List<double>(); var paintMs = new List<double>(); var areas = new List<double>(); var totalMs = new List<double>();
                    var byCtl = new Dictionary<string, long>();
                    var msByCtl = new Dictionary<string, double>();
                    ResetListStats();
                    const int toggles = 10;
                    for (int k = 0; k < toggles; k++)
                    {
                        FlushPaint(form);
                        long tt = Stopwatch.GetTimestamp();
                        Call(form, "Toggle", target);
                        callMs.Add(Ms(tt));
                        long px = 0;
                        var dirty = new List<(Control C, Region R, string Key)>();
                        foreach (var c in All(form))
                        {
                            if (!c.IsHandleCreated || !c.Visible) continue;
                            long a = UpdateArea(c, out var rg);
                            if (a <= 0 || rg == null) continue;
                            px += a;
                            string key = c.GetType().Name + (c.GetType().Name == "PillButton" ? ":" + c.Text : "");
                            dirty.Add((c, rg, key));
                            byCtl[key] = (byCtl.TryGetValue(key, out var was) ? was : 0) + a;
                        }
                        areas.Add(px);
                        double pm = 0;
                        foreach (var (c, rg, key) in dirty)
                        {
                            double cm = PaintNow(c, rg);
                            pm += cm;
                            msByCtl[key] = (msByCtl.TryGetValue(key, out var wm) ? wm : 0) + cm;
                            ValidateRect(c.Handle, IntPtr.Zero);
                            rg.Dispose();
                        }
                        paintMs.Add(pm);
                        totalMs.Add(callMs[^1] + pm);
                        RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero, RDW_UPDATENOW | RDW_ALLCHILDREN);
                    }
                    res[prefix + "Selected"] = selected.Id;
                    res[prefix + "CallMs"] = Stats(callMs);
                    res[prefix + "PaintMs"] = Stats(paintMs);
                    res[prefix + "TotalMs"] = Stats(totalMs);
                    res[prefix + "InvalidatedKPx"] = Stats(areas.Select(a => a / 1000.0));
                    res[prefix + "InvalidatedByControlKPx"] = byCtl.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => R3(kv.Value / 1000.0 / toggles));
                    res[prefix + "PaintByControlMs"] = msByCtl.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => R3(kv.Value / toggles));
                    res[prefix + "List"] = ListStats();
                }
                MeasureToggles("toggle", target);
                var other = mods.FirstOrDefault(x => !ReferenceEquals(x, target) && !x.Available
                    && !x.DependsOn.Contains(target.Id, StringComparer.OrdinalIgnoreCase) && !target.DependsOn.Contains(x.Id, StringComparer.OrdinalIgnoreCase));
                if (other != null) MeasureToggles("toggleOther", other);
                res["windowKPx"] = R3(form.ClientSize.Width * (long)form.ClientSize.Height / 1000.0);
                res["listKPx"] = R3(list.Width * (long)list.Height / 1000.0);
            }

            // ── кэш карточек (только новая версия): картинка с кэшем и без него должна совпадать
            var cacheF = list.GetType().GetField("CacheEnabled", BindingFlags.Static | BindingFlags.Public);
            if (cacheF != null)
            {
                scrollF.SetValue(list, Math.Min(max, (int)(250 * s)));
                Bitmap Snap()
                {
                    var b = new Bitmap(list.Width, list.Height, PixelFormat.Format32bppArgb);
                    list.DrawToBitmap(b, new Rectangle(0, 0, list.Width, list.Height));
                    return b;
                }
                cacheF.SetValue(null, false);
                using var live = Snap();
                cacheF.SetValue(null, true);
                using (Snap()) { }
                using var cached = Snap();
                res["cacheVsLive"] = Compare(live, cached);
            }

            // ── значки, измерение текста
            if (Get(form, "_icons") is object icons)
                res["icons"] = new[] { "Requests", "Found", "NotFound", "Hits", "LoadMs" }.ToDictionary(k => k, k => Get(icons, k));
            var mc = StaticGet(typeof(Theme), "MeasureCalls");
            if (mc != null) res["measure"] = new Dictionary<string, object?> { ["calls"] = mc, ["misses"] = StaticGet(typeof(Theme), "MeasureMisses") };
            Memory(res, "end");
            res["totalMs"] = R3(Ms(MainStart));

            spy.ReleaseHandle(); spy = null;
            form.Hide();
            form.Dispose();
            Write(outPath, res);
            return 0;
        }
        catch (Exception e)
        {
            try { spy?.ReleaseHandle(); } catch { }
            res["error"] = e.ToString();
            Write(outPath, res);
            return 1;
        }
    }

    private static Dictionary<string, object> Compare(Bitmap a, Bitmap b)
    {
        int diff = 0, maxd = 0;
        var r = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pa = new int[a.Width]; var pb = new int[a.Width];
            for (int y = 0; y < a.Height; y++)
            {
                Marshal.Copy(da.Scan0 + y * da.Stride, pa, 0, a.Width);
                Marshal.Copy(db.Scan0 + y * db.Stride, pb, 0, a.Width);
                for (int x = 0; x < a.Width; x++)
                {
                    if (pa[x] == pb[x]) continue;
                    int d = 0;
                    for (int sh = 0; sh < 24; sh += 8) d = Math.Max(d, Math.Abs(((pa[x] >> sh) & 255) - ((pb[x] >> sh) & 255)));
                    if (d > 0) diff++;
                    maxd = Math.Max(maxd, d);
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
        return new Dictionary<string, object> { ["pixels"] = a.Width * a.Height, ["different"] = diff, ["maxChannelDiff"] = maxd };
    }

    private static void Write(string path, Dictionary<string, object?> res)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { }
    }
}
