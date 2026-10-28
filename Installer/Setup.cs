// SR2 Mod Manager — установщик для Windows (SR2ModManager_Setup.exe).
// Обычное приложение .NET Framework 4 (есть в любой Windows 10/11), собирается штатным csc.exe: build.ps1 -Package.
// Внутри: плагин SR2ModManager.dll, лаунчер SR2ModLauncher.exe в двух сборках (для ПК с .NET 8+ и со встроенной
// средой .NET), uninstall.ps1. Плагин — в <игра>\Plugins, лаунчер — в <игра>\ModManager, ярлык — на рабочий стол.
// Ключи: --dir "<папка игры>"  --quiet (без окна)  --no-shortcut  --log <файл>
//        --screenshot <png> [--fake-done]  (снимок окна для проверки, ничего не ставит)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SR2ModManager.Setup
{
    sealed class GameCheck
    {
        public string Dir = "";
        public bool Game, Melon, Installed;
        public string MelonVersion;   // null — MelonLoader не найден
        public bool Ok { get { return Game && Melon; } }
    }

    static class Core
    {
        public const string Repo = "https://github.com/slitvin12/SR2ModManager";
        public const string MelonUrl = "https://github.com/LavaGang/MelonLoader.Installer/releases";
        static string _version;
        public static string Version { get { return _version ?? (_version = ReadText("version.txt").Trim()); } }

        public static Stream Res(string name)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload." + name);
            if (s == null) throw new FileNotFoundException("В установщике нет файла " + name);
            return s;
        }

        static string ReadText(string name)
        {
            using (Stream s = Res(name)) using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        public static bool IsGameDir(string d)
        {
            try { return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "SlimeRancher2.exe")); }
            catch { return false; }
        }

        // ---------- поиск папки игры ----------

        public static string FindGame()
        {
            var c = new List<string>();
            try
            {
                string me = Path.GetDirectoryName(Application.ExecutablePath);
                c.Add(me); c.Add(Path.GetDirectoryName(me));                       // установщик лежит в папке игры
            }
            catch { }
            try
            {
                foreach (Process p in Process.GetProcessesByName("SlimeRancher2"))
                    try { c.Add(Path.GetDirectoryName(p.MainModule.FileName)); } catch { }
            }
            catch { }
            foreach (string lib in SteamLibraries()) c.Add(Path.Combine(lib, @"steamapps\common\Slime Rancher 2"));
            string[] subs = { @"Games\Slime Rancher 2", @"Игры\Slime Rancher 2", "Slime Rancher 2", @"SteamLibrary\steamapps\common\Slime Rancher 2",
                              @"Program Files (x86)\Steam\steamapps\common\Slime Rancher 2", @"Program Files\Slime Rancher 2", @"Program Files (x86)\Slime Rancher 2" };
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    foreach (string s in subs) c.Add(Path.Combine(d.RootDirectory.FullName, s));
                }
            }
            catch { }
            try
            {
                c.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Slime Rancher 2"));
                c.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\Slime Rancher 2"));
            }
            catch { }
            foreach (string d in c)
                if (IsGameDir(d)) { try { return Path.GetFullPath(d); } catch { return d; } }
            return "";
        }

        static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            AddReg(roots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddReg(roots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddReg(roots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            var libs = new List<string>();
            foreach (string r in roots)
            {
                libs.Add(r);
                try
                {
                    string vdf = Path.Combine(r, @"steamapps\libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { }
            }
            return libs;
        }

        static void AddReg(List<string> to, RegistryKey hive, string key, string name)
        {
            try
            {
                using (RegistryKey k = hive.OpenSubKey(key))
                {
                    string v = k == null ? null : k.GetValue(name) as string;
                    if (!string.IsNullOrEmpty(v)) to.Add(v.Replace('/', '\\'));
                }
            }
            catch { }
        }

        // ---------- проверки ----------

        public static GameCheck Inspect(string dir)
        {
            var r = new GameCheck();
            r.Dir = (dir ?? "").Trim().Trim('"').TrimEnd('\\', '/');
            r.Game = IsGameDir(r.Dir);
            if (!r.Game) return r;
            string ml = Path.Combine(r.Dir, "MelonLoader");
            string net6 = Path.Combine(ml, @"net6\MelonLoader.dll"), old = Path.Combine(ml, "MelonLoader.dll");
            if (File.Exists(net6)) { r.Melon = true; r.MelonVersion = FileVersion(net6); }
            else if (File.Exists(old)) r.MelonVersion = FileVersion(old);   // 0.5 и старше — не подходит
            r.Installed = File.Exists(Path.Combine(r.Dir, @"ModManager\SR2ModLauncher.exe")) || File.Exists(Path.Combine(r.Dir, @"Plugins\SR2ModManager.dll"));
            return r;
        }

        static string FileVersion(string path)
        {
            try
            {
                string v = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
                while (v.EndsWith(".0") && v.Split('.').Length > 3) v = v.Substring(0, v.Length - 2);
                return v.Length > 0 ? v : "?";
            }
            catch { return "?"; }
        }

        // игра или лаунчер из этой папки запущены — их файлы заняты
        public static string Busy(string game)
        {
            if (Running("SlimeRancher2", game)) return "Закройте Slime Rancher 2 и нажмите «Установить» ещё раз.";
            if (Running("SR2ModLauncher", game)) return "Закройте окно SR2 Mod Manager и нажмите «Установить» ещё раз.";
            return null;
        }

        static bool Running(string name, string game)
        {
            try
            {
                string root = Path.GetFullPath(game).TrimEnd('\\') + "\\";
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    string path;
                    try { path = p.MainModule.FileName; } catch { return true; }   // путь не узнать — считаем, что это эта копия
                    if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        // .NET 8 или новее (Desktop Runtime x64) — тогда ставим компактный лаунчер, иначе — со встроенной средой
        public static bool HasDesktopRuntime()
        {
            if (Environment.GetEnvironmentVariable("SR2MM_SETUP_FORCE_SC") == "1") return false;   // для проверки сборки со встроенной средой
            try
            {
                string pf = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string d = Path.Combine(pf, @"dotnet\shared\Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(d) || !File.Exists(Path.Combine(pf, @"dotnet\dotnet.exe"))) return false;
                foreach (string v in Directory.GetDirectories(d))
                {
                    string n = Path.GetFileName(v); int dot = n.IndexOf('.'), major;
                    if (n.IndexOf('-') < 0 && dot > 0 && int.TryParse(n.Substring(0, dot), out major) && major >= 8) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // ---------- установка ----------

        public static List<string> Install(string game, bool shortcut)
        {
            var log = new List<string>();
            string plugins = Path.Combine(game, "Plugins"), mm = Path.Combine(game, "ModManager");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(mm);

            string dll = Path.Combine(plugins, "SR2ModManager.dll");
            if (File.Exists(dll)) File.Copy(dll, dll + ".bak", true);
            Extract("SR2ModManager.dll", dll);
            log.Add("плагин: " + dll);
            foreach (string f in Directory.GetFiles(plugins, "SR2ModManager.dll.old*")) { try { File.Delete(f); } catch { } }
            string upd = Path.Combine(mm, "update");
            if (Directory.Exists(upd)) { try { Directory.Delete(upd, true); } catch { } }

            string exe = Path.Combine(mm, "SR2ModLauncher.exe");
            if (File.Exists(exe)) File.Copy(exe, exe + ".bak", true);
            bool rt = HasDesktopRuntime();
            Extract(rt ? "SR2ModLauncher.exe" : "SR2ModLauncher.sc.exe", exe);
            log.Add("лаунчер: " + exe + (rt ? " (.NET уже есть на ПК)" : " (со встроенной средой .NET)"));
            Extract("uninstall.ps1", Path.Combine(mm, "uninstall.ps1"));

            if (shortcut)
            {
                try { log.Add("ярлык: " + Shortcut(exe, mm)); }
                catch (Exception e) { log.Add("ярлык не создан: " + e.Message); }
            }
            return log;
        }

        static void Extract(string name, string dst)
        {
            string tmp = dst + ".new";
            using (Stream s = Res(name))
            using (FileStream f = File.Create(tmp))
                s.CopyTo(f);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);
        }

        public static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SR2 Mod Manager.lnk"); }
        }

        static string Shortcut(string exe, string dir)
        {
            string lnk = ShortcutPath;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic l = shell.CreateShortcut(lnk);
            l.TargetPath = exe;
            l.WorkingDirectory = dir;
            l.IconLocation = exe + ",0";
            l.Description = "Slime Rancher 2 — выбрать моды и запустить игру";
            l.Save();
            return lnk;
        }

        public static void Open(string target, string workDir)
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (workDir != null) psi.WorkingDirectory = workDir;
            Process.Start(psi);
        }
    }

    sealed class SetupForm : Form
    {
        static readonly Color Accent = Color.FromArgb(26, 158, 154), Good = Color.FromArgb(30, 142, 62), Bad = Color.FromArgb(197, 34, 31),
                              Muted = Color.FromArgb(104, 112, 122), Ink = Color.FromArgb(32, 38, 46), HeadBg = Color.FromArgb(232, 246, 245),
                              FootBg = Color.FromArgb(246, 247, 248);
        readonly TextBox _dir = new TextBox();
        readonly Label[] _mark = new Label[3], _text = new Label[3];
        readonly LinkLabel _melonLink = new LinkLabel();
        readonly CheckBox _shortcut = new CheckBox(), _open = new CheckBox();
        readonly Label _status = new Label();
        readonly Button _install = new Button(), _cancel = new Button(), _browse = new Button();
        readonly string _shot;
        GameCheck _check = new GameCheck();
        bool _done;
        string _exe, _mm;

        public SetupForm(string dir, string screenshot, bool fakeDone)
        {
            _shot = screenshot;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.75F);
            Text = "Установка SR2 Mod Manager " + Core.Version;
            ClientSize = new Size(580, 452);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.White;
            ForeColor = Ink;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // шапка
            var head = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = HeadBg };
            var pic = new PictureBox { Location = new Point(22, 14), Size = new Size(60, 60), SizeMode = PictureBoxSizeMode.Zoom, BackColor = HeadBg };
            try { pic.Image = IconImage(64); } catch { }
            var title = new Label { Text = "SR2 Mod Manager", Font = new Font("Segoe UI Semibold", 16F), AutoSize = true, Location = new Point(94, 14), ForeColor = Ink };
            var sub = new Label { Text = "Менеджер модов для Slime Rancher 2 · версия " + Core.Version, AutoSize = true, Location = new Point(97, 52), ForeColor = Muted };
            head.Controls.AddRange(new Control[] { pic, title, sub });

            // папка игры
            var lbl = new Label { Text = "Папка Slime Rancher 2", Font = new Font("Segoe UI Semibold", 9.75F), AutoSize = true, Location = new Point(22, 104) };
            _dir.Location = new Point(24, 130); _dir.Size = new Size(430, 26);
            _browse.Text = "Обзор…"; _browse.Location = new Point(462, 128); _browse.Size = new Size(96, 30);
            var hint = new Label { Text = "Та, где лежит SlimeRancher2.exe. Обычно находится сама.", AutoSize = true, Location = new Point(22, 160), ForeColor = Muted };

            // чек-лист
            for (int i = 0; i < 3; i++)
            {
                _mark[i] = new Label { Location = new Point(22, 194 + i * 30), Size = new Size(24, 24), Font = new Font("Segoe UI Symbol", 11F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
                _text[i] = new Label { Location = new Point(48, 194 + i * 30), Size = new Size(512, 24), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            }
            _melonLink.Text = "Скачать официальный установщик MelonLoader"; _melonLink.AutoSize = true;
            _melonLink.Location = new Point(48, 282); _melonLink.LinkColor = Accent; _melonLink.Visible = false;
            _melonLink.LinkClicked += delegate { TryOpen(Core.MelonUrl); };

            _shortcut.Text = "Ярлык «SR2 Mod Manager» на рабочем столе"; _shortcut.Checked = true; _shortcut.AutoSize = true; _shortcut.Location = new Point(24, 314);
            _open.Text = "Открыть менеджер модов после установки"; _open.Checked = true; _open.AutoSize = true; _open.Location = new Point(24, 340);
            _status.Location = new Point(22, 368); _status.Size = new Size(538, 24); _status.ForeColor = Muted; _status.AutoEllipsis = true;

            // низ
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = FootBg };
            var repo = new LinkLabel { Text = "github.com/slitvin12/SR2ModManager", AutoSize = true, Location = new Point(20, 19), LinkColor = Muted, ActiveLinkColor = Accent };
            repo.LinkClicked += delegate { TryOpen(Core.Repo); };
            _install.Text = "Установить"; _install.Location = new Point(338, 12); _install.Size = new Size(124, 32);
            _install.FlatStyle = FlatStyle.Flat; _install.FlatAppearance.BorderSize = 0; _install.BackColor = Accent; _install.ForeColor = Color.White;
            _install.Font = new Font("Segoe UI Semibold", 9.75F);
            _cancel.Text = "Отмена"; _cancel.Location = new Point(470, 12); _cancel.Size = new Size(92, 32); _cancel.BackColor = Color.White;
            foot.Controls.AddRange(new Control[] { repo, _install, _cancel });

            Controls.AddRange(new Control[] { head, lbl, _dir, _browse, hint, _melonLink, _shortcut, _open, _status, foot });
            for (int i = 0; i < 3; i++) Controls.AddRange(new Control[] { _mark[i], _text[i] });
            AcceptButton = _install; CancelButton = _cancel;
            ResumeLayout(false);
            PerformLayout();

            _browse.Click += delegate { Browse(); };
            _install.Click += delegate { if (_done) Close(); else DoInstall(); };
            _cancel.Click += delegate { Close(); };
            _dir.TextChanged += delegate { Revalidate(); };

            _dir.Text = !string.IsNullOrEmpty(dir) ? dir : Core.FindGame();
            Revalidate();
            if (fakeDone) ShowDone(new List<string> { "плагин", "лаунчер" }, false);
            if (_shot != null) { StartPosition = FormStartPosition.Manual; Location = new Point(-3000, -3000); ShowInTaskbar = false; }
        }

        // кадр из app.ico не меньше want (или самый крупный); PNG-кадры класс Icon читает неверно — их разбираем сами
        static Image IconImage(int want)
        {
            byte[] b;
            using (Stream s = Core.Res("app.ico")) using (var ms = new MemoryStream()) { s.CopyTo(ms); b = ms.ToArray(); }
            int n = BitConverter.ToUInt16(b, 4), best = -1, bestW = 0;
            for (int i = 0; i < n; i++)
            {
                int w = b[6 + i * 16] == 0 ? 256 : b[6 + i * 16];
                if (best < 0 || (bestW < want ? w > bestW : w >= want && w < bestW)) { best = i; bestW = w; }
            }
            int e = 6 + best * 16, size = BitConverter.ToInt32(b, e + 8), off = BitConverter.ToInt32(b, e + 12);
            if (b[off] == 0x89 && b[off + 1] == 0x50) return new Bitmap(new MemoryStream(b, off, size));
            return new Icon(new MemoryStream(b), new Size(bestW, bestW)).ToBitmap();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_shot == null) { ActiveControl = _check.Ok ? (Control)_install : _browse; return; }
            _dir.Select(0, 0);
            Application.DoEvents();
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(_shot, System.Drawing.Imaging.ImageFormat.Png);
            }
            Close();
        }

        void Row(int i, bool? ok, string text)
        {
            _mark[i].Text = ok == true ? "✓" : ok == false ? "✕" : "•";
            _mark[i].ForeColor = ok == true ? Good : ok == false ? Bad : Muted;
            _text[i].Text = text;
            _text[i].ForeColor = ok == false ? Bad : Ink;
        }

        void Revalidate()
        {
            if (_done) return;
            _check = Core.Inspect(_dir.Text);
            if (!_check.Game)
            {
                Row(0, false, _check.Dir.Length == 0 ? "Укажите папку игры: «Обзор…» → SlimeRancher2.exe" : "В этой папке нет SlimeRancher2.exe");
                Row(1, null, "MelonLoader — проверю, когда найдётся игра");
                Row(2, null, "Менеджер модов встанет в папку игры");
            }
            else
            {
                Row(0, true, "Slime Rancher 2 найдена");
                if (_check.Melon) Row(1, true, "MelonLoader " + _check.MelonVersion + " установлен");
                else if (_check.MelonVersion != null) Row(1, false, "MelonLoader " + _check.MelonVersion + " устарел: нужен 0.6 или новее (лучше 0.7.3 x64)");
                else Row(1, false, "MelonLoader не найден: сначала поставьте 0.7.3 x64 и один раз запустите игру");
                Row(2, null, _check.Installed ? "SR2 Mod Manager уже стоит — обновится до " + Core.Version + ", наборы и настройки останутся"
                                              : "Встанет в Plugins и ModManager в папке игры; моды и сейвы не тронутся");
            }
            _melonLink.Visible = _check.Game && !_check.Melon;
            _install.Text = _check.Installed ? "Обновить" : "Установить";
            _install.Enabled = _check.Ok;
            _install.BackColor = _check.Ok ? Accent : Color.FromArgb(178, 214, 212);
            _status.Text = "";
        }

        void Browse()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Где лежит SlimeRancher2.exe?";
                d.Filter = "Slime Rancher 2|SlimeRancher2.exe|Программы|*.exe";
                if (Directory.Exists(_dir.Text)) d.InitialDirectory = _dir.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _dir.Text = Path.GetDirectoryName(d.FileName);
            }
        }

        void DoInstall()
        {
            Revalidate();
            if (!_check.Ok) return;
            string busy = Core.Busy(_check.Dir);
            if (busy != null) { Status(busy, Bad); return; }
            Cursor = Cursors.WaitCursor; _install.Enabled = false; Status("Ставлю…", Muted); Update();
            try
            {
                List<string> log = Core.Install(_check.Dir, _shortcut.Checked);
                ShowDone(log, _open.Checked);
            }
            catch (UnauthorizedAccessException)
            {
                Cursor = Cursors.Default; Revalidate();
                if (Core.IsAdmin()) { Status("Нет доступа к папке игры. Проверьте, что файлы не заняты и не «только для чтения».", Bad); return; }
                if (MessageBox.Show(this, "В эту папку можно писать только с правами администратора.\nПерезапустить установщик от имени администратора?",
                                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try
                    {
                        var psi = new ProcessStartInfo(Application.ExecutablePath, "--dir \"" + _check.Dir + "\"" + (_shortcut.Checked ? "" : " --no-shortcut"))
                        { UseShellExecute = true, Verb = "runas" };
                        Process.Start(psi);
                        Close();
                    }
                    catch { Status("Установка отменена.", Muted); }
                }
            }
            catch (Exception e)
            {
                Cursor = Cursors.Default; Revalidate();
                Status("Не получилось: " + e.Message, Bad);
            }
        }

        void ShowDone(List<string> log, bool open)
        {
            _done = true; Cursor = Cursors.Default;
            _mm = Path.Combine(_check.Dir, "ModManager"); _exe = Path.Combine(_mm, "SR2ModLauncher.exe");
            Row(0, true, "Готово! SR2 Mod Manager " + Core.Version + " установлен");
            Row(1, true, _shortcut.Checked ? "Ярлык «SR2 Mod Manager» — на рабочем столе" : "Лаунчер: папка игры \\ ModManager \\ SR2ModLauncher.exe");
            Row(2, null, "Запускайте игру через менеджер: моды — одним кликом, в игре панель — F5");
            _melonLink.Visible = false; _shortcut.Enabled = false; _open.Enabled = false; _dir.ReadOnly = true; _browse.Enabled = false;
            _install.Text = "Готово"; _install.Enabled = true; _install.BackColor = Accent; _cancel.Visible = false;
            AcceptButton = _install;
            bool launched = false;
            if (open && File.Exists(_exe))
            {
                if (Core.IsAdmin()) Status("Откройте менеджер ярлыком — из окна администратора не работает перетаскивание модов.", Muted);
                else { try { Core.Open(_exe, _mm); launched = true; } catch (Exception e) { Status("Не удалось открыть менеджер: " + e.Message, Bad); } }
            }
            if (launched || !open)
            {
                string s = string.Join(" · ", log.ToArray());
                if (_check.Dir.Length > 0) s = s.Replace(_check.Dir, "…");
                Status(s, Muted);
            }
        }

        void Status(string text, Color color) { _status.Text = text; _status.ForeColor = color; }

        void TryOpen(string url) { try { Core.Open(url, null); } catch { } }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string dir = Arg(args, "--dir"), shot = Arg(args, "--screenshot"), log = Arg(args, "--log");
            if (Has(args, "--quiet")) return Quiet(dir, !Has(args, "--no-shortcut"), log);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(dir, shot, Has(args, "--fake-done")));
            return 0;
        }

        static int Quiet(string dir, bool shortcut, string logPath)
        {
            var lines = new List<string>();
            int code;
            try
            {
                GameCheck c = Core.Inspect(string.IsNullOrEmpty(dir) ? Core.FindGame() : dir);
                lines.Add("папка: " + c.Dir + " | игра: " + c.Game + " | MelonLoader: " + (c.MelonVersion ?? "нет") + " | уже стоит: " + c.Installed + " | .NET 8+: " + Core.HasDesktopRuntime());
                string busy = Core.Busy(c.Dir);
                if (!c.Ok) { lines.Add("проверка не пройдена"); code = 2; }
                else if (busy != null) { lines.Add(busy); code = 3; }
                else { lines.AddRange(Core.Install(c.Dir, shortcut)); lines.Add("готово " + Core.Version); code = 0; }
            }
            catch (Exception e) { lines.Add("ошибка: " + e); code = 1; }
            if (!string.IsNullOrEmpty(logPath)) { try { File.WriteAllLines(logPath, lines.ToArray(), new UTF8Encoding(false)); } catch { } }
            return code;
        }

        static string Arg(string[] a, string name)
        {
            for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        static bool Has(string[] a, string name) { return Array.IndexOf(a, name) >= 0; }
    }
}
