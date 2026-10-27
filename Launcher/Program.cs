// SR2 Mod Launcher — мини-лаунчер перед запуском Slime Rancher 2.
// Без аргументов — окно выбора модов.
// Аргументы:
//   --game "<папка игры>"     явно указать папку игры
//   --profile "<имя>"         применить профиль (можно вместе с --launch, без окна)
//   --launch                  сразу запустить игру (после --profile / pending)
//   --after-exit <pid>        дождаться выхода игры (кнопка «Перезапустить» в игре), применить pending и запустить
//   --install <файлы…>        установить .dll/.zip и открыть окно (так же работают файлы, брошенные на exe)
//   --render <png>            (для проверки) нарисовать окно в PNG без побочных эффектов:
//                             [--assume-running|--assume-closed] [--width N --height N] [--select id] [--search text] [--toast text] [--drop]
//                             [--filter 0-6: все, установленные, требуют обновления, отключенные, проблемы, включённые, каталог]
//                             [--toggle id[,id…]: переключить отметки модов в памяти — как будто их отметили, но не применили]
//                             [--catalog <папка>: её моды показываются как найденные в «Загрузках»]
//                             [--design compact|fluent|slime] [--dock problems|hooks|logs|changes|details] [--dock-collapsed]
//                             [--sort колонка[:asc|:desc]: name, version, author, compat, hooks, update, size] [--picker]
//                             [--switch-design compact|fluent|slime: после показа сменить стиль на лету]
//   --bench <json>            (для проверки) замеры: запуск, прокрутка, колесо, переключение мода — окно за экраном,
//                             на диске ничего не меняется: [--synthetic N: ещё N копий модов в памяти] [--frames N]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal static class Program
{
    private static bool _headless;
    private static readonly string[] Flags = { "--game", "--profile", "--after-exit", "--render", "--width", "--height", "--select", "--filter", "--search", "--toast", "--catalog", "--bench", "--synthetic", "--frames", "--design", "--dock", "--sort", "--switch-design", "--toggle" };

    [STAThread]
    private static int Main(string[] args)
    {
        Bench.MainStart = Stopwatch.GetTimestamp();
        if (args.Contains("--bench"))
            try { using var self = Process.GetCurrentProcess(); Bench.ProcToMainMs = (DateTime.Now - self.StartTime).TotalMilliseconds; } catch { }
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        string? gameHint = Arg(args, "--game");
        string? profile = Arg(args, "--profile");
        string? afterExit = Arg(args, "--after-exit");
        string? render = Arg(args, "--render");
        bool launch = args.Contains("--launch");

        string? game = ModStore.FindGameDir(gameHint);
        if (game == null)
        {
            if (render != null || args.Contains("--bench")) return 2;
            using var dlg = new FolderBrowserDialog { Description = "Укажите папку Slime Rancher 2 (там, где SlimeRancher2.exe и Mods)" };
            if (dlg.ShowDialog() != DialogResult.OK || ModStore.FindGameDir(dlg.SelectedPath) == null)
            {
                MessageBox.Show("Папка игры не найдена.", "SR2 Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            game = dlg.SelectedPath;
        }
        var store = new ModStore(game);

        if (render != null) { _headless = true; return Render(store, render, args); }
        string? bench = Arg(args, "--bench");
        if (bench != null) { _headless = true; return Bench.Run(store, bench, args); }

        store.EnsureConfig();

        if (afterExit != null)
            return RestartFlow(store, afterExit);

        if (profile != null)
        {
            if (ModStore.IsGameRunning())
            {
                MessageBox.Show("Slime Rancher 2 уже запущен. Закройте игру, чтобы сменить моды.", "SR2 Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }
            store.TryApplyStagedPluginUpdate(out _);
            var cfg = store.LoadConfig();
            var p = cfg.Profiles.FirstOrDefault(x => string.Equals(x.Name, profile, StringComparison.OrdinalIgnoreCase));
            if (p == null)
            {
                MessageBox.Show($"Профиль «{profile}» не найден.", "SR2 Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 4;
            }
            var mods = store.Scan();
            var sel = ModStore.SelectionForProfile(mods, p);
            ModStore.AddMissingDependencies(mods, sel);
            store.ClearPending();
            var r = store.Apply(sel);
            if (!r.Ok) { MessageBox.Show(r.ToString(), "SR2 Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); return 5; }
            cfg.LastProfile = p.Name; store.SaveConfig(cfg);
            if (launch) store.LaunchGame(cfg.LaunchVia);
            return 0;
        }

        // файлы для установки: после --install или просто пути к .dll/.zip (брошенные на exe)
        var install = new List<string>();
        int ii = Array.FindIndex(args, a => a.Equals("--install", StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--")) { if (Flags.Contains(a.ToLowerInvariant())) i++; continue; }
            if ((ii >= 0 && i > ii) || a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                if (File.Exists(a)) install.Add(Path.GetFullPath(a));
        }

        string? updated = null;
        if (store.TryApplyStagedPluginUpdate(out var msg)) updated = msg;

        Application.Run(new MainForm(store, install.ToArray(), updated));
        return 0;
    }

    /// Отрисовка окна в PNG (для проверки внешнего вида). Ничего не меняет на диске.
    private static int Render(ModStore store, string png, string[] args)
    {
        try
        {
            var opt = new RenderOptions
            {
                AssumeRunning = args.Contains("--assume-running") ? true : args.Contains("--assume-closed") ? false : null,
                Select = Arg(args, "--select"),
                Search = Arg(args, "--search"),
                Toast = Arg(args, "--toast"),
                Drop = args.Contains("--drop"),
                CatalogDir = Arg(args, "--catalog"),
                Design = UiPrefs.ParseDesign(Arg(args, "--design")),
                Dock = UiPrefs.ParseDock(Arg(args, "--dock")),
                DockCollapsed = args.Contains("--dock-collapsed"),
                Sort = Arg(args, "--sort"),
                Picker = args.Contains("--picker"),
                SwitchTo = UiPrefs.ParseDesign(Arg(args, "--switch-design")),
                Toggle = Arg(args, "--toggle"),
            };
            if (int.TryParse(Arg(args, "--filter"), out int f)) opt.Filter = f;
            using var form = new MainForm(store, null, null, opt);
            float s = form.DeviceDpi / 96f;
            int w = int.TryParse(Arg(args, "--width"), out int ww) ? ww : (int)(1140 * s);
            int h = int.TryParse(Arg(args, "--height"), out int hh) ? hh : (int)(780 * s);
            form.StartPosition = FormStartPosition.Manual;
            form.ShowInTaskbar = false;
            form.Location = new Point(-20000, -20000);
            form.Size = new Size(w, h);
            form.Show();
            Application.DoEvents();
            if (opt.SwitchTo != null) { form.SwitchDesignForRender(); Application.DoEvents(); }
            form.RenderTo(png);
            form.Hide();
            return 0;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(png + ".error.txt", e.ToString()); } catch { }
            return 1;
        }
    }

    /// «Перезапустить игру» из плагина: ждём закрытия, применяем pending, запускаем снова.
    private static int RestartFlow(ModStore store, string pidText)
    {
        try
        {
            if (int.TryParse(pidText, out int pid))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.WaitForExit(120_000);
                }
                catch (ArgumentException) { /* уже закрыта */ }
            }
            // Подождать, пока Steam увидит, что игра закрыта, и освободятся файлы.
            for (int i = 0; i < 60 && ModStore.IsGameRunning(); i++) Thread.Sleep(1000);
            Thread.Sleep(3000);
            store.TryApplyStagedPluginUpdate(out _);
            var r = store.ApplyPending();
            if (r != null && !r.Ok)
                MessageBox.Show("Не все изменения удалось применить (их применит менеджер при следующем запуске):\n\n" + r,
                    "SR2 Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            store.LaunchGame(store.LoadConfig().LaunchVia);
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("Перезапуск не удался: " + e.Message, "SR2 Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void ReportCrash(Exception? e)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SR2ModLauncher-error.txt");
        try { System.IO.File.AppendAllText(path, DateTime.Now + " " + e + Environment.NewLine); } catch { }
        if (_headless) { Environment.Exit(9); return; }
        MessageBox.Show("Ошибка лаунчера: " + e?.Message + "\n\nПодробности: " + path, "SR2 Mod Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string? Arg(string[] a, string name)
    {
        int i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}


