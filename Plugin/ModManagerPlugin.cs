// ModManagerPlugin.cs — плагин MelonLoader (лежит в Plugins\, а не в Mods\).
// 1) До загрузки модов (OnPreModsLoaded) применяет UserData\ModManager\pending.json —
//    изменения, запланированные в игре. В этот момент DLL модов ещё не загружены и не заблокированы.
// 2) В игре по горячей клавише (по умолчанию F5) показывает панель модов.
// В этом классе нет полей Unity-типов: плагин загружается раньше, чем MelonLoader
// сгенерирует/подключит Il2Cpp-сборки, поэтому весь Unity-код живёт в Panel и создаётся позже.
using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using SR2ModManager.Core;

[assembly: MelonInfo(typeof(SR2ModManager.Plugin.ModManagerPlugin), "SR2 Mod Manager", SR2ModManager.Plugin.ModManagerPlugin.Version, "slitvin12")]
[assembly: MelonGame("MonomiPark", "SlimeRancher2")]
[assembly: MelonPriority(-1000)]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyDescription("Менеджер модов: панель в игре (F5) и применение отложенных изменений до загрузки модов.")]

namespace SR2ModManager.Plugin;

public sealed class ModManagerPlugin : MelonPlugin
{
    public const string Version = "1.1.0";
    internal static ModManagerPlugin I { get; private set; }
    internal ModStore Store { get; private set; }
    internal string StartupReport = "";
    internal string HotkeyName = "F5";
    private MelonPreferences_Entry<string> _hotkey;
    private object _panel; // Panel (Unity-типы) — создаётся только после старта Unity
    private bool _ready, _panelFailed;

    public override void OnPreInitialization()
    {
        I = this;
        try
        {
            Store = new ModStore(MelonEnvironment.GameRootDirectory);
            Store.EnsureConfig();
        }
        catch (Exception e) { LoggerInstance.Error("Не удалось инициализировать хранилище: " + e); }
        try
        {
            var cat = MelonPreferences.CreateCategory("SR2ModManager", "SR2 Mod Manager");
            _hotkey = cat.CreateEntry("Hotkey", "F5", "Горячая клавиша",
                "Клавиша панели модов: F1–F12, Insert, Home, End, PageUp, PageDown, Pause, ScrollLock. Занято: F1/Insert (Creator Toolkit), F3/F7 (MinecraftHUD), F6/F8/F9/F10 (SlimeLive).");
            HotkeyName = (_hotkey.Value ?? "F5").Trim();
        }
        catch (Exception e) { LoggerInstance.Warning("Настройки: " + e.Message); }
    }

    public override void OnPreModsLoaded()
    {
        if (Store == null) return;
        try
        {
            var r = Store.ApplyPending();
            if (r == null) { StartupReport = ""; return; }
            StartupReport = r.Ok ? (r.Moved > 0 ? "Применено при запуске: " + r.Summary() : "") : "Ошибка применения: " + string.Join("; ", r.Errors);
            if (r.Ok) LoggerInstance.Msg("Отложенные изменения применены: " + (r.Moved > 0 ? string.Join("; ", r.Log) : "нечего менять"));
            else LoggerInstance.Warning("Не все отложенные изменения применены: " + r);
        }
        catch (Exception e) { LoggerInstance.Error("pending.json: " + e); }
    }

    public override void OnLateInitializeMelon()
    {
        _ready = true;
        LoggerInstance.Msg($"Панель модов: клавиша {HotkeyName}. Папка выключенных модов: Mods\\{ModStore.DisabledFolderName}");
    }

    public override void OnUpdate()
    {
        if (!_ready || _panelFailed) return;
        try { Panel().Update(); }
        catch (Exception e) { Fail(e); }
    }

    public override void OnGUI()
    {
        if (!_ready || _panelFailed || _panel == null) return;
        try { ((Panel)_panel).Draw(); }
        catch (Exception e) { Fail(e); }
    }

    public override void OnApplicationQuit()
    {
        try { (_panel as Panel)?.Close(); } catch { }
    }

    private Panel Panel() => (Panel)(_panel ??= new Panel(this));

    private void Fail(Exception e)
    {
        _panelFailed = true;
        LoggerInstance.Error("Панель отключена из-за ошибки: " + e);
        try { (_panel as Panel)?.Close(); } catch { }
    }

    internal static string GameDir => MelonEnvironment.GameRootDirectory;
    internal static string LauncherPath => Path.Combine(MelonEnvironment.GameRootDirectory, "ModManager", "SR2ModLauncher.exe");
}

