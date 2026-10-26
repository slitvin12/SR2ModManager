// Panel.cs — панель модов в игре (IMGUI). Клики обрабатываются вручную через Input System
// (как в Creator Toolkit / SlimeLive), а не через GUI.Button.
// Высота листа считается заранее (без пустых полей), а производное состояние — предупреждения,
// совпадающий набор, счётчики — пересчитывается только после изменений, а не в каждом OnGUI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MelonLoader;
using SR2ModManager.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SR2ModManager.Plugin;

internal sealed class Panel
{
    private readonly ModManagerPlugin _p;
    private bool _open;
    private List<ModEntry> _mods = new();
    private ManagerConfig _cfg = new();
    private Dictionary<string, bool> _desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _loaded = new(StringComparer.OrdinalIgnoreCase); // id файла -> загруженная версия
    private readonly HashSet<string> _newIds = new(StringComparer.OrdinalIgnoreCase);            // появились после запуска игры
    private MelonLogInfo _log;
    private readonly List<(Rect r, Action a)> _hits = new();
    private readonly List<(Rect r, Action a)> _hitsFrame = new();
    private Rect _sheet;
    private string _toast = ""; private float _toastUntil; private bool _toastBad;
    private float _restartArmedUntil;
    private CursorLockMode _prevLock; private bool _prevVisible; private bool _cursorTaken;
    private UnityEngine.EventSystems.EventSystem _blockedEvents;

    // прокрутка списка
    private float _scroll, _scrollTarget, _contentH, _viewH;
    private Rect _thumb, _track;
    private bool _dragThumb; private float _dragY0, _dragScroll0;

    // мышь (координаты GUI) и всплывающая подсказка
    private Vector2 _mouse = new(-9999f, -9999f);
    private string _tip, _tipShown; private float _tipSince;

    // плавные переключатели: id -> положение ручки 0..1
    private readonly Dictionary<string, float> _knob = new(StringComparer.OrdinalIgnoreCase);

    // автообновление, если что-то поменялось снаружи (лаунчер, Проводник)
    private string _sig = ""; private float _nextSigCheck;
    private DateTime _gameStart = DateTime.MinValue;

    // производное состояние
    private bool _dirty = true;
    private List<string> _warn = new();
    private List<string> _fixable = new();
    private Profile _match;
    private int _cntLoaded, _cntOn, _cntPlus, _cntMinus;
    private string _changesShort = "", _changesLong = "";

    internal Panel(ModManagerPlugin p) { _p = p; }

    // ───────── состояние ─────────

    private void Refresh()
    {
        var store = _p.Store;
        _cfg = store.LoadConfig();
        _mods = store.Scan();
        _loaded.Clear();
        foreach (var m in MelonTypeBase<MelonMod>.RegisteredMelons)
        {
            try
            {
                string loc = m.MelonAssembly?.Location ?? "";
                if (loc.Length > 0) _loaded[Path.GetFileNameWithoutExtension(loc)] = m.Info.Version;
            }
            catch { }
        }
        _desired = MapUtil.ToMap(_mods, m => m.Id, m => m.Enabled);
        var pending = store.LoadPending();
        if (pending?.Desired != null)
            foreach (var kv in pending.Desired) if (_desired.ContainsKey(kv.Key)) _desired[kv.Key] = kv.Value;
        try { _log = store.ReadMelonLog(_mods); } catch { _log = null; }
        if (_gameStart == DateTime.MinValue)
        {
            try { _gameStart = Process.GetCurrentProcess().StartTime; } catch { _gameStart = DateTime.Now; }
        }
        _newIds.Clear();
        foreach (var m in _mods)
        {
            if (m.Enabled && !IsLoaded(m))
            {
                try { if (m.Modified > _gameStart || File.GetCreationTime(m.Path) > _gameStart) _newIds.Add(m.Id); } catch { }
            }
            if (!_knob.ContainsKey(m.Id)) _knob[m.Id] = Want(m) ? 1f : 0f;
        }
        _sig = Signature();
        _dirty = true;
    }

    private bool Want(ModEntry m) => _desired.TryGetValue(m.Id, out var on) && on;
    private bool IsLoaded(ModEntry m) => _loaded.ContainsKey(m.Id);
    private bool HasChanges => _cntPlus + _cntMinus > 0;

    private void Recalc()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            _warn = ModStore.CheckSelection(_mods, _desired, _cfg);
            var probe = new Dictionary<string, bool>(_desired, StringComparer.OrdinalIgnoreCase);
            _fixable = ModStore.AddMissingDependencies(_mods, probe);
            _match = ModStore.FindMatchingProfile(_mods, _desired, _cfg, _cfg.LastProfile);
        }
        catch (Exception e)
        {
            _warn = new List<string>(); _fixable = new List<string>(); _match = null;
            _p.LoggerInstance.Warning("Проверка набора: " + e.Message);
        }
        _cntLoaded = _mods.Count(IsLoaded);
        _cntOn = _mods.Count(Want);
        var on = _mods.Where(m => Want(m) && !m.Enabled).Select(m => m.DisplayName).ToList();
        var off = _mods.Where(m => !Want(m) && m.Enabled).Select(m => m.DisplayName).ToList();
        _cntPlus = on.Count; _cntMinus = off.Count;
        _changesShort = (on.Count > 0 ? "+" + on.Count : "") + (on.Count > 0 && off.Count > 0 ? " / " : "") + (off.Count > 0 ? "−" + off.Count : "");
        var parts = new List<string>();
        if (on.Count > 0) parts.Add("включится: " + string.Join(", ", on));
        if (off.Count > 0) parts.Add("выключится: " + string.Join(", ", off));
        _changesLong = string.Join("; ", parts);
    }

    /// «Подпись» папок модов и файлов менеджера: меняется, когда кто-то снаружи двигает моды или правит наборы.
    private string Signature()
    {
        var st = _p.Store;
        return string.Concat(Stamp(st.ModsDir, true), "|", Stamp(st.DisabledDir, true), "|", Stamp(st.PendingPath, false), "|", Stamp(st.ConfigPath, false));
    }

    private static string Stamp(string path, bool dir)
    {
        try
        {
            if (dir) return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks.ToString() : "-";
            if (!File.Exists(path)) return "-";
            var fi = new FileInfo(path);
            return fi.LastWriteTimeUtc.Ticks.ToString() + ":" + fi.Length.ToString();
        }
        catch { return "?"; }
    }

    /// Сохраняет желаемое состояние в pending.json (или удаляет его, если менять нечего).
    private void SavePending()
    {
        var diff = MapUtil.ToMap(_mods.Where(m => _desired.TryGetValue(m.Id, out var on) && on != m.Enabled), m => m.Id, m => _desired[m.Id]);
        try
        {
            if (diff.Count == 0) _p.Store.ClearPending();
            else _p.Store.SavePending(new PendingChanges { Source = "in-game", Desired = diff });
        }
        catch (Exception e) { Toast("Не удалось сохранить: " + e.Message, true); }
        _sig = Signature();
        _dirty = true;
    }

    private void Toggle(ModEntry m)
    {
        bool on = !Want(m);
        _desired[m.Id] = on;
        if (on)
        {
            var added = ModStore.AddMissingDependencies(_mods, _desired);
            if (added.Count > 0) Toast("Заодно включено (нужно для работы): " + string.Join(", ", added.Select(NameOf)) + ".");
        }
        else
        {
            var users = _mods.Where(x => Want(x) && x.DependsOn.Any(d => d.Equals(m.Id, StringComparison.OrdinalIgnoreCase))).Select(x => x.DisplayName).ToList();
            if (users.Count > 0) Toast("Без «" + m.DisplayName + "» не будут работать: " + string.Join(", ", users) + ".", true);
        }
        SavePending();
    }

    private void ApplyProfile(Profile pr)
    {
        _desired = ModStore.SelectionForProfile(_mods, pr);
        var added = ModStore.AddMissingDependencies(_mods, _desired);
        SavePending();
        _cfg.LastProfile = pr.Name;
        try { _p.Store.SaveConfig(_cfg); _sig = Signature(); } catch { }
        Recalc();
        if (!HasChanges) Toast($"Набор «{pr.Name}» уже загружен — менять ничего не нужно.");
        else Toast($"Набор «{pr.Name}» выбран — применится при следующем запуске игры."
                   + (added.Count > 0 ? " Заодно: " + string.Join(", ", added.Select(NameOf)) + "." : ""));
    }

    private void ResetToCurrent()
    {
        _desired = MapUtil.ToMap(_mods, m => m.Id, m => m.Enabled);
        SavePending();
        Toast("Запланированные изменения отменены.");
    }

    private void FixDependencies()
    {
        var added = ModStore.AddMissingDependencies(_mods, _desired);
        SavePending();
        Toast(added.Count > 0 ? "Включено нужное для работы: " + string.Join(", ", added.Select(NameOf)) + "." : "Всё нужное уже включено.");
    }

    private string NameOf(string id) => _mods.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;

    private void Restart()
    {
        if (Time.unscaledTime > _restartArmedUntil)
        {
            _restartArmedUntil = Time.unscaledTime + 4f;
            Toast("Нажмите ещё раз, чтобы перезапустить. Несохранённый прогресс может пропасть!", true);
            return;
        }
        SavePending();
        int pid = Process.GetCurrentProcess().Id;
        try
        {
            string launcher = ModManagerPlugin.LauncherPath;
            if (File.Exists(launcher))
                Process.Start(new ProcessStartInfo(launcher, $"--after-exit {pid}") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(launcher) });
            else
            {
                string ps = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 4; Start-Process 'steam://rungameid/{ModStore.SteamAppId}'";
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{ps}\"") { UseShellExecute = false, CreateNoWindow = true });
            }
        }
        catch (Exception e) { Toast("Не удалось подготовить перезапуск: " + e.Message, true); return; }
        _p.LoggerInstance.Msg("Перезапуск игры по запросу из панели модов.");
        Close();
        Application.Quit();
    }

    private void OpenLauncher()
    {
        string launcher = ModManagerPlugin.LauncherPath;
        if (!File.Exists(launcher)) { Toast("Лаунчер не найден: " + launcher, true); return; }
        try
        {
            Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(launcher) });
            Toast("Лаунчер открывается — переключитесь на него (Alt+Tab).");
        }
        catch (Exception e) { Toast("Не удалось открыть лаунчер: " + e.Message, true); }
    }

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            Toast("Папка открыта в Проводнике (Alt+Tab).");
        }
        catch (Exception e) { Toast("Не удалось открыть папку: " + e.Message, true); }
    }

    private void Toast(string s, bool bad = false)
    {
        _toast = s ?? ""; _toastBad = bad;
        _toastUntil = Time.unscaledTime + Mathf.Clamp(3f + _toast.Length / 22f, 4f, 9f);
    }

    // ───────── ввод ─────────

    // Диагностика без нажатия клавиш: файл UserData\ModManager\panel-test.flag открывает панель,
    // делает скриншот panel-test.png и закрывает её (используется для автопроверки).
    private float _nextFlagCheck, _testShotAt, _testCloseAt;
    private bool _drawLogged;
    private void TestHook()
    {
        float now = Time.unscaledTime;
        if (_testShotAt > 0 && now >= _testShotAt)
        {
            _testShotAt = 0;
            try { ScreenCapture.CaptureScreenshot(Path.Combine(_p.Store.ConfigDir, "panel-test.png")); } catch (Exception e) { _p.LoggerInstance.Warning("screenshot: " + e.Message); }
        }
        if (_testCloseAt > 0 && now >= _testCloseAt) { _testCloseAt = 0; Close(); }
        if (now < _nextFlagCheck) return;
        _nextFlagCheck = now + 1f;
        string flag = Path.Combine(_p.Store.ConfigDir, "panel-test.flag");
        if (!File.Exists(flag)) return;
        try { File.Delete(flag); } catch { }
        _p.LoggerInstance.Msg("panel-test.flag: открываю панель для проверки");
        if (!_open) Open();
        _testShotAt = now + 1.5f; _testCloseAt = now + 4f;
    }

    internal void Update()
    {
        TestHook();
        var kb = Keyboard.current;
        if (kb != null && HotkeyPressed(kb))
        {
            if (_open) Close(); else Open();
        }
        if (!_open) return;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { Close(); return; }
        // Пока панель открыта, держим курсор свободным (игра может снова захватить его).
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        float now = Time.unscaledTime;
        if (now >= _nextSigCheck && !_dragThumb)
        {
            _nextSigCheck = now + 1.5f;
            try
            {
                if (Signature() != _sig)
                {
                    Refresh();
                    Toast("Список модов обновлён: что-то изменилось в папке Mods или в лаунчере.");
                }
            }
            catch (Exception e) { _sig = Signature(); _p.LoggerInstance.Warning("Автообновление списка: " + e.Message); }
        }

        float maxScroll = Mathf.Max(0f, _contentH - _viewH);
        _scrollTarget = Mathf.Clamp(_scrollTarget, 0f, maxScroll);
        _scroll = Mathf.Abs(_scrollTarget - _scroll) < 0.5f ? _scrollTarget
                : Mathf.Lerp(_scroll, _scrollTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 16f));

        var mouse = Mouse.current;
        if (mouse == null) { _mouse = new Vector2(-9999f, -9999f); return; }
        Vector2 mp = mouse.position.ReadValue();
        var gp = new Vector2(mp.x, Screen.height - mp.y);
        _mouse = gp;
        BlockGameUi(_sheet.Contains(gp));
        float wheel = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(wheel) > 0.01f && _sheet.Contains(gp))
            _scrollTarget = Mathf.Clamp(_scrollTarget - Mathf.Sign(wheel) * 68f * Ui.S, 0f, maxScroll);

        if (_dragThumb)
        {
            if (!mouse.leftButton.isPressed) _dragThumb = false;
            else
            {
                float free = Mathf.Max(1f, _track.height - _thumb.height);
                _scrollTarget = Mathf.Clamp(_dragScroll0 + (gp.y - _dragY0) * maxScroll / free, 0f, maxScroll);
                _scroll = _scrollTarget;
            }
        }
        if (mouse.leftButton.wasPressedThisFrame)
        {
            if (maxScroll > 0f)
            {
                float g = 7f * Ui.S;
                var thumbHit = new Rect(_thumb.x - g, _thumb.y, _thumb.width + 2f * g, _thumb.height);
                var trackHit = new Rect(_track.x - g, _track.y, _track.width + 2f * g, _track.height);
                if (thumbHit.Contains(gp)) { _dragThumb = true; _dragY0 = gp.y; _dragScroll0 = _scrollTarget; return; }
                if (trackHit.Contains(gp))
                {
                    _scrollTarget = Mathf.Clamp(_scrollTarget + (gp.y < _thumb.y ? -1f : 1f) * _viewH * 0.85f, 0f, maxScroll);
                    return;
                }
            }
            foreach (var (r, a) in _hits)
                if (r.Contains(gp)) { a(); break; }
        }
    }

    private bool HotkeyPressed(Keyboard kb)
    {
        var key = _p.HotkeyName.ToLowerInvariant() switch
        {
            "f1" => kb.f1Key, "f2" => kb.f2Key, "f3" => kb.f3Key, "f4" => kb.f4Key, "f5" => kb.f5Key, "f6" => kb.f6Key,
            "f7" => kb.f7Key, "f8" => kb.f8Key, "f9" => kb.f9Key, "f10" => kb.f10Key, "f11" => kb.f11Key, "f12" => kb.f12Key,
            "insert" => kb.insertKey, "home" => kb.homeKey, "end" => kb.endKey, "pageup" => kb.pageUpKey,
            "pagedown" => kb.pageDownKey, "pause" => kb.pauseKey, "scrolllock" => kb.scrollLockKey,
            _ => kb.f5Key,
        };
        if (key == null || !key.wasPressedThisFrame) return false;
        // Alt+F4 и сочетания с Ctrl/Alt не перехватываем
        return !(kb.leftAltKey.isPressed || kb.rightAltKey.isPressed || kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
    }

    private void Open()
    {
        try { Refresh(); }
        catch (Exception e) { _p.LoggerInstance.Error("Обновление списка модов: " + e); }
        _open = true;
        _scroll = _scrollTarget = 0f;
        _dragThumb = false;
        _nextSigCheck = Time.unscaledTime + 1.5f;
        _prevLock = Cursor.lockState; _prevVisible = Cursor.visible; _cursorTaken = true;
        if (!string.IsNullOrEmpty(_p.StartupReport))
        {
            Toast(_p.StartupReport, _p.StartupReport.StartsWith("Ошибка", StringComparison.Ordinal));
            _p.StartupReport = "";
        }
        _p.LoggerInstance.Msg($"Панель открыта: модов {_mods.Count}, загружено {_loaded.Count}");
    }

    internal void Close()
    {
        if (!_open) return;
        _open = false;
        _dragThumb = false;
        _tip = _tipShown = null;
        _mouse = new Vector2(-9999f, -9999f);
        BlockGameUi(false);
        _hits.Clear();
        if (_cursorTaken) { Cursor.lockState = _prevLock; Cursor.visible = _prevVisible; _cursorTaken = false; }
    }

    /// Пока мышь над панелью, выключаем EventSystem, чтобы клик не нажал кнопку меню игры под панелью.
    private void BlockGameUi(bool block)
    {
        try
        {
            if (block)
            {
                if (_blockedEvents != null) return;
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null && es.enabled) { es.enabled = false; _blockedEvents = es; }
            }
            else if (_blockedEvents != null)
            {
                if (!_blockedEvents.WasCollected) _blockedEvents.enabled = true;
                _blockedEvents = null;
            }
        }
        catch { _blockedEvents = null; }
    }

    // ───────── отрисовка ─────────

    private bool Hover(Rect r) => r.Contains(_mouse);

    internal void Draw()
    {
        if (!_open) return;
        Recalc();
        bool paint = Ui.Repaint;
        if (paint) { _hitsFrame.Clear(); _tip = null; }
        float s = Ui.S;
        float w = Mathf.Round(Mathf.Min(820f * s, Screen.width - 40f));
        float pad = 22f * s, cw = w - 2f * pad;
        int n = _mods.Count;

        // ── раскладка по высоте: те же числа используются ниже при рисовании ──
        float rowH = 62f * s, gap = 6f * s;
        _contentH = n == 0 ? 0f : n * rowH + (n - 1) * gap;
        float maxList = Mathf.Max(rowH * 2f + gap, Mathf.Floor(Screen.height * 0.5f));
        _viewH = n == 0 ? 76f * s : Mathf.Min(_contentH, maxList);
        bool scrollable = _contentH > _viewH + 0.5f;
        var pills = LayoutProfiles(cw, out float profH);
        float warnH = _warn.Count == 0 ? 24f * s : (_warn.Count > 1 ? 58f : 40f) * s;
        float btnH = 38f * s;
        float h = 16f * s + 40f * s + 10f * s + 3f * s + 12f * s   // шапка и полоска
                + 26f * s + 10f * s + 20f * s + 12f * s              // сводка и пояснение
                + (profH > 0f ? profH + 14f * s : 0f)                // наборы
                + _viewH + 14f * s                                   // список
                + warnH + 16f * s                                    // состояние
                + btnH + 18f * s;                                    // кнопки
        _sheet = new Rect(Mathf.Round((Screen.width - w) * 0.5f), Mathf.Round(Mathf.Max(20f, (Screen.height - h) * 0.5f)), w, Mathf.Round(h));
        var r = _sheet;
        Ui.Sheet(r);
        float x = r.x + pad, y = r.y + 16f * s;

        DrawHeader(x, y, cw);
        y += 40f * s + 10f * s;
        Ui.Gradient(new Rect(x, y, 240f * s, 3f * s), Ui.Teal, Ui.Pink);
        y += 3f * s + 12f * s;

        DrawChips(x, y, cw, 26f * s);
        y += 26f * s + 10f * s;
        Ui.Label(new Rect(x, y, cw, 20f * s), Ui.Ellipsize("Моды IL2CPP нельзя выгрузить на ходу — изменения применятся при следующем запуске игры.", 13, cw), 13, TextAnchor.MiddleLeft, Ui.NavySoft);
        y += 20f * s + 12f * s;

        if (profH > 0f)
        {
            DrawProfiles(x, y, pills);
            y += profH + 14f * s;
        }

        DrawList(new Rect(x, y, cw, _viewH), rowH, gap, scrollable);
        y += _viewH + 14f * s;

        DrawStatus(new Rect(x, y, cw, warnH));
        y += warnH + 16f * s;

        DrawButtons(x, y, cw, btnH);

        DrawToast(w);
        DrawTip();

        if (paint) { _hits.Clear(); _hits.AddRange(_hitsFrame); }
        if (paint && !_drawLogged) { _drawLogged = true; _p.LoggerInstance.Msg($"Панель нарисована ({_sheet.width:0}x{_sheet.height:0}, шрифт {Ui.FontName})"); }
    }

    private void DrawHeader(float x, float y, float cw)
    {
        float s = Ui.S;
        var logo = new Rect(x, y + 3f * s, 38f * s, 35f * s);
        Ui.Slime(logo);
        float tx = logo.xMax + 12f * s;
        var close = new Rect(x + cw - 34f * s, y + 3f * s, 34f * s, 34f * s);
        float textMax = close.x - tx - 10f * s;
        Ui.Label(new Rect(tx, y - 1f * s, textMax, 26f * s), "Моды Slime Rancher 2", 21, TextAnchor.MiddleLeft, Ui.Navy, true);
        string sub = $"SR2 Mod Manager v{ModManagerPlugin.Version}  ·  {_p.HotkeyName} или Esc — закрыть";
        Ui.Label(new Rect(tx, y + 24f * s, textMax, 18f * s), Ui.Ellipsize(sub, 12, textMax), 12, TextAnchor.MiddleLeft, Ui.Hint);
        bool hc = Hover(close);
        Ui.Circle(close.center, close.width * 0.5f, hc ? Ui.CoralTint : Ui.Beige);
        Ui.Label(new Rect(close.x, close.y - 1f * s, close.width, close.height), "×", 20, TextAnchor.MiddleCenter, hc ? Ui.CoralText : Ui.NavySoft, true);
        Hit(close, Close);
        if (hc) _tip = "Закрыть панель (" + _p.HotkeyName + " или Esc)";
    }

    private void DrawChips(float x, float y, float cw, float h)
    {
        float cx = x, max = x + cw;
        cx = Chip(cx, y, h, max, "Работают: " + _cntLoaded, Ui.TealTint, Ui.TealText, Ui.Teal, "Моды, загруженные в этой сессии игры");
        cx = Chip(cx, y, h, max, "Включено: " + _cntOn + " из " + _mods.Count, Ui.Beige, Ui.NavySoft, Ui.LineDark, "Столько модов будет включено при следующем запуске");
        if (HasChanges) cx = Chip(cx, y, h, max, "После перезапуска: " + _changesShort, Ui.OrangeTint, Ui.OrangeText, Ui.Orange, "После перезапуска " + _changesLong + ".");
        int errs = _log?.TotalErrors ?? 0, warns = _log?.TotalWarnings ?? 0;
        const string logTip = "Журнал MelonLoader (MelonLoader\\Latest.log). Наведите на мод, чтобы увидеть его последнее сообщение.";
        if (errs > 0) Chip(cx, y, h, max, Ui.Count(errs, "ошибка", "ошибки", "ошибок") + " в журнале", Ui.CoralTint, Ui.CoralText, Ui.Coral, logTip);
        else if (warns > 0) Chip(cx, y, h, max, Ui.Count(warns, "предупреждение", "предупреждения", "предупреждений") + " в журнале", Ui.OrangeTint, Ui.OrangeText, Ui.Orange, logTip);
        else if (_log != null) Chip(cx, y, h, max, "Журнал без ошибок", Ui.TealTint, Ui.TealText, Ui.Teal, logTip);
    }

    private float Chip(float x, float y, float h, float max, string text, Color bg, Color fg, Color dot, string tip)
    {
        float s = Ui.S;
        float w = Ui.TextW(text, 12, true) + 34f * s;
        if (x + w > max) return x;
        var r = new Rect(x, y, w, h);
        Ui.Round(r, bg, h * 0.5f);
        Ui.Circle(new Vector2(r.x + 13f * s, r.center.y), 4f * s, dot);
        Ui.Label(new Rect(r.x + 22f * s, r.y, w - 24f * s, h), text, 12, TextAnchor.MiddleLeft, fg, true);
        if (tip != null && Hover(r)) _tip = tip;
        return r.xMax + 8f * s;
    }

    // ── наборы (профили) ──

    private List<(Rect r, Profile p)> LayoutProfiles(float cw, out float height)
    {
        float s = Ui.S, ph = 28f * s, gx = 6f * s, gy = 6f * s;
        var list = new List<(Rect r, Profile p)>();
        height = 0f;
        if (_cfg?.Profiles == null || _cfg.Profiles.Count == 0) return list;
        float labelW = Ui.TextW("Наборы", 13, true) + 14f * s;
        float px = labelW, py = 0f;
        foreach (var pr in _cfg.Profiles)
        {
            float pw = Mathf.Min(Ui.TextW(pr.Name, 13, true) + 28f * s, cw - labelW);
            if (px + pw > cw && px > labelW) { px = labelW; py += ph + gy; }
            list.Add((new Rect(px, py, pw, ph), pr));
            px += pw + gx;
        }
        height = py + ph;
        return list;
    }

    private void DrawProfiles(float x, float y, List<(Rect r, Profile p)> pills)
    {
        float s = Ui.S;
        Ui.Label(new Rect(x, y, 120f * s, 28f * s), "Наборы", 13, TextAnchor.MiddleLeft, Ui.NavySoft, true);
        foreach (var (lr, pr) in pills)
        {
            var r = new Rect(x + lr.x, y + lr.y, lr.width, lr.height);
            bool cur = _match != null && ReferenceEquals(_match, pr);
            bool hv = Hover(r);
            Ui.Round(r, cur ? (hv ? Ui.TealDark : Ui.Teal) : (hv ? Ui.BeigeDark : Ui.Beige), r.height * 0.5f);
            Ui.Label(r, Ui.Ellipsize(pr.Name, 13, r.width - 20f * s, cur), 13, TextAnchor.MiddleCenter, cur ? Color.white : Ui.Navy, cur);
            var captured = pr;
            Hit(r, () => ApplyProfile(captured));
            if (hv) _tip = ProfileTip(pr, cur);
        }
    }

    private string ProfileTip(Profile pr, bool cur)
    {
        string what = pr.All ? "все моды из папки Mods"
                    : pr.Mods == null || pr.Mods.Count == 0 ? "без модов (чистая игра)"
                    : string.Join(", ", pr.Mods.Select(NameOf));
        return (cur ? "Выбран сейчас: " : "Выбрать набор: ") + what;
    }

    // ── список модов ──

    private void DrawList(Rect list, float rowH, float gap, bool scrollable)
    {
        float s = Ui.S;
        if (_mods.Count == 0)
        {
            _thumb = _track = default;
            Ui.Card(list, Ui.Paper, Ui.Line, 12f * s);
            Ui.Label(new Rect(list.x + 20f * s, list.y + 12f * s, list.width - 40f * s, 26f * s), "В папке Mods пока нет модов", 15, TextAnchor.MiddleLeft, Ui.Navy, true);
            Ui.Label(new Rect(list.x + 20f * s, list.y + 38f * s, list.width - 40f * s, 22f * s),
                Ui.Ellipsize("Положите .dll в папку Mods (кнопка «Папка Mods») или перетащите файл в окно лаунчера.", 12, list.width - 40f * s), 12, TextAnchor.MiddleLeft, Ui.Hint);
            return;
        }
        float rowW = scrollable ? list.width - 14f * s : list.width;
        GUI.BeginGroup(list);
        try
        {
            float ry = -Mathf.Round(_scroll);
            foreach (var m in _mods)
            {
                var row = new Rect(0f, ry, rowW, rowH);
                ry += rowH + gap;
                if (row.yMax < 0f || row.y > list.height) continue;
                DrawRow(m, row, list);
            }
        }
        finally { GUI.EndGroup(); }

        if (!scrollable) { _thumb = _track = default; return; }
        float max = Mathf.Max(1f, _contentH - _viewH);
        float fh = 16f * s;
        if (_scroll > 1f) Ui.VGradient(new Rect(list.x, list.y, rowW, fh), Ui.Cream, Ui.Fade(Ui.Cream, 0f));
        if (_scroll < max - 1f) Ui.VGradient(new Rect(list.x, list.yMax - fh, rowW, fh), Ui.Fade(Ui.Cream, 0f), Ui.Cream);
        _track = new Rect(list.xMax - 6f * s, list.y + 2f * s, 6f * s, list.height - 4f * s);
        float th = Mathf.Max(28f * s, _track.height * _viewH / Mathf.Max(1f, _contentH));
        float ty = _track.y + (_track.height - th) * Mathf.Clamp01(_scroll / max);
        _thumb = new Rect(_track.x, ty, _track.width, th);
        bool hv = _dragThumb || Hover(new Rect(_track.x - 7f * s, _track.y, _track.width + 14f * s, _track.height));
        Ui.Round(_track, Ui.Fade(Ui.Line, 0.75f), _track.width * 0.5f);
        Ui.Round(_thumb, hv ? Ui.NavySoft : Ui.LineDark, _track.width * 0.5f);
    }

    private void DrawRow(ModEntry m, Rect row, Rect group)
    {
        float s = Ui.S;
        bool want = Want(m), loaded = IsLoaded(m);
        var screenRow = new Rect(group.x + row.x, group.y + row.y, row.width, row.height);
        var visible = Rect.MinMaxRect(screenRow.x, Mathf.Max(screenRow.y, group.y), screenRow.xMax, Mathf.Min(screenRow.yMax, group.yMax));
        bool hover = !_dragThumb && visible.height > 0f && Hover(visible);
        var (st, stBg, stFg, accent, stTip) = StatusOf(m, want, loaded);

        Ui.Card(row, hover ? Ui.Hex(0xFFFDF8) : Ui.Paper, hover ? Ui.Mix(Ui.Line, Ui.Teal, 0.6f) : Ui.Line, 12f * s);
        Ui.Round(new Rect(row.x + 5f * s, row.y + 12f * s, 4f * s, row.height - 24f * s), accent, 2f * s);

        // переключатель с плавной ручкой
        float k = _knob.TryGetValue(m.Id, out var kv) ? kv : (want ? 1f : 0f);
        if (Ui.Repaint) { k = Mathf.MoveTowards(k, want ? 1f : 0f, Time.unscaledDeltaTime * 7f); _knob[m.Id] = k; }
        var sw = new Rect(row.x + 20f * s, row.y + (row.height - 26f * s) * 0.5f, 46f * s, 26f * s);
        Ui.Toggle(sw, k, hover);

        float tx = sw.xMax + 14f * s, right = row.xMax - 14f * s;

        // статус — справа сверху
        float pw = Ui.TextW(st, 12, true) + 22f * s;
        var pill = new Rect(right - pw, row.y + 9f * s, pw, 22f * s);
        Ui.Round(pill, stBg, 11f * s);
        Ui.Label(pill, st, 12, TextAnchor.MiddleCenter, stFg, true);

        // название, версия и метки
        string ver = string.IsNullOrEmpty(m.Version) ? "" : "v" + m.Version;
        float verW = ver.Length > 0 ? Ui.TextW(ver, 11, true) + 14f * s : 0f;
        float titleMax = pill.x - tx - 10f * s - (verW > 0f ? verW + 8f * s : 0f);
        string title = Ui.Ellipsize(m.DisplayName, 15, Mathf.Max(30f * s, titleMax), true);
        float tw = Ui.TextW(title, 15, true);
        Ui.Label(new Rect(tx, row.y + 7f * s, tw + 6f * s, 26f * s), title, 15, TextAnchor.MiddleLeft, Ui.Navy, true);
        float bx = tx + tw + 8f * s;
        if (verW > 0f && bx + verW <= pill.x - 6f * s)
        {
            var vr = new Rect(bx, row.y + 11f * s, verW, 18f * s);
            Ui.Round(vr, Ui.Beige, 9f * s);
            Ui.Label(vr, ver, 11, TextAnchor.MiddleCenter, Ui.NavySoft, true);
            bx = vr.xMax + 6f * s;
        }
        string verTip = null;
        if (_loaded.TryGetValue(m.Id, out var lv) && !string.IsNullOrEmpty(lv) && !string.IsNullOrEmpty(m.Version) && lv != m.Version)
        {
            // файл заменили во время игры: сейчас работает другая версия
            string t = "сейчас v" + lv;
            float w2 = Ui.TextW(t, 11, true) + 14f * s;
            if (bx + w2 <= pill.x - 6f * s)
            {
                var r2 = new Rect(bx, row.y + 11f * s, w2, 18f * s);
                Ui.Round(r2, Ui.BlueTint, 9f * s);
                Ui.Label(r2, t, 11, TextAnchor.MiddleCenter, Ui.BlueText, true);
            }
            verTip = $"В игре работает v{lv}, в папке — v{m.Version}. Новая версия загрузится после перезапуска.";
        }

        // нижняя строка: зависимости и описание; справа — сообщения журнала
        float subRight = right;
        var ls = _log?.For(m.Id);
        string logTip = null;
        if (ls != null && (ls.Errors > 0 || ls.Warnings > 0))
        {
            bool err = ls.Errors > 0;
            string lt = err ? Ui.Count(ls.Errors, "ошибка", "ошибки", "ошибок") : Ui.Count(ls.Warnings, "предупреждение", "предупреждения", "предупреждений");
            float lw = Ui.TextW(lt, 11, true) + 28f * s;
            var lr = new Rect(right - lw, row.y + 36f * s, lw, 20f * s);
            Ui.Round(lr, err ? Ui.CoralTint : Ui.OrangeTint, 10f * s);
            Ui.Circle(new Vector2(lr.x + 11f * s, lr.center.y), 3.5f * s, err ? Ui.Coral : Ui.Orange);
            Ui.Label(new Rect(lr.x + 18f * s, lr.y, lw - 20f * s, lr.height), lt, 11, TextAnchor.MiddleLeft, err ? Ui.CoralText : Ui.OrangeText, true);
            subRight = lr.x - 8f * s;
            logTip = err ? "Последняя ошибка в журнале: " + ls.LastError : "Последнее предупреждение в журнале: " + ls.LastWarning;
        }
        string sub = SubLine(m);
        string subShown = Ui.Ellipsize(sub, 12, subRight - tx);
        Ui.Label(new Rect(tx, row.y + 35f * s, subRight - tx, 22f * s), subShown, 12, TextAnchor.MiddleLeft, Ui.Hint);

        if (visible.height > 4f) Hit(visible, () => Toggle(m));
        if (hover) _tip = logTip ?? verTip ?? stTip ?? (subShown != sub ? sub : null);
    }

    private (string text, Color bg, Color fg, Color accent, string tip) StatusOf(ModEntry m, bool want, bool loaded)
    {
        if (loaded && want) return ("работает", Ui.Teal, Color.white, Ui.Teal, null);
        if (loaded) return ("выключится после перезапуска", Ui.Orange, Color.white, Ui.Orange, "Мод работает сейчас; при следующем запуске игры он будет выключен.");
        if (want && m.Enabled && m.Kind == "Plugin") return ("плагин", Ui.BlueTint, Ui.BlueText, Ui.Blue, "Это плагин MelonLoader: такие файлы обычно кладут в папку Plugins, а не в Mods.");
        if (want && m.Enabled && _newIds.Contains(m.Id)) return ("новый — после перезапуска", Ui.Blue, Color.white, Ui.Blue, "Файл появился после запуска игры — он загрузится при следующем запуске.");
        if (want && m.Enabled) return ("не загрузился", Ui.Coral, Color.white, Ui.Coral, "Файл лежит в Mods, но MelonLoader его не загрузил. Причина — в MelonLoader\\Latest.log (лаунчер показывает её в карточке мода).");
        if (want) return ("включится после перезапуска", Ui.Blue, Color.white, Ui.Blue, "Мод будет включён при следующем запуске игры.");
        return ("выключен", Ui.Beige, Ui.NavySoft, Ui.Line, null);
    }

    private string SubLine(ModEntry m)
    {
        string sub = !string.IsNullOrWhiteSpace(m.Description) ? m.Description.Replace('\r', ' ').Replace('\n', ' ').Trim()
                   : !string.IsNullOrWhiteSpace(m.Author) ? "Автор: " + m.Author
                   : m.FileName;
        if (m.DependsOn.Count > 0 && !sub.Contains("Нужен")) sub = "Нужен: " + string.Join(", ", m.DependsOn.Select(NameOf)) + " · " + sub;
        return sub;
    }

    // ── состояние набора ──

    private void DrawStatus(Rect r)
    {
        float s = Ui.S;
        if (_warn.Count == 0)
        {
            bool ch = HasChanges;
            Ui.Circle(new Vector2(r.x + 6f * s, r.center.y), 4.5f * s, ch ? Ui.Orange : Ui.Teal);
            string t = ch ? "Изменения сохранены: " + _changesLong + ". Применятся при следующем запуске игры."
                          : "Изменений нет — включены те же моды, что загружены сейчас.";
            var tr = new Rect(r.x + 18f * s, r.y, r.width - 18f * s, r.height);
            string shown = Ui.Ellipsize(t, 13, tr.width, ch);
            Ui.Label(tr, shown, 13, TextAnchor.MiddleLeft, ch ? Ui.OrangeText : Ui.NavySoft, ch);
            if (shown != t && Hover(tr)) _tip = t;
            return;
        }
        Ui.Round(r, Ui.CoralTint, 12f * s);
        Ui.Bang(new Vector2(r.x + 21f * s, r.y + 20f * s), 10f * s, Ui.Coral);
        float tx = r.x + 40f * s, right = r.xMax - 12f * s;
        if (_fixable.Count > 0)
        {
            const string label = "Включить нужное";
            float bw = Ui.TextW(label, 13, true) + 28f * s;
            var b = new Rect(right - bw, r.y + 5f * s, bw, 30f * s);
            bool hv = Hover(b);
            Ui.Round(b, hv ? Ui.Mix(Ui.Coral, Ui.CoralText, 0.45f) : Ui.Coral, 15f * s);
            Ui.Label(b, label, 13, TextAnchor.MiddleCenter, Color.white, true);
            Hit(b, FixDependencies);
            if (hv) _tip = "Включить: " + string.Join(", ", _fixable.Distinct(StringComparer.OrdinalIgnoreCase).Select(NameOf));
            right = b.x - 10f * s;
        }
        Ui.Label(new Rect(tx, r.y + 9f * s, right - tx, 22f * s), Ui.Ellipsize(_warn[0], 13, right - tx, true), 13, TextAnchor.MiddleLeft, Ui.CoralText, true);
        if (_warn.Count > 1)
        {
            string more = _warn.Count > 2 ? "  · и ещё " + (_warn.Count - 2) : "";
            float mw = more.Length > 0 ? Ui.TextW(more, 12, true) : 0f;
            float avail = r.xMax - 12f * s - tx - mw;
            string second = Ui.Ellipsize(_warn[1], 12, avail);
            float sw2 = Ui.TextW(second, 12);
            Ui.Label(new Rect(tx, r.y + 31f * s, sw2 + 4f * s, 20f * s), second, 12, TextAnchor.MiddleLeft, Ui.CoralText);
            if (mw > 0f) Ui.Label(new Rect(tx + sw2 + 2f * s, r.y + 31f * s, mw + 4f * s, 20f * s), more, 12, TextAnchor.MiddleLeft, Ui.CoralText, true);
        }
        if (_tip == null && Hover(r)) _tip = string.Join("\n", _warn.Take(6));
    }

    // ── кнопки ──

    private enum Look { Primary, Secondary, Outline }

    private void DrawButtons(float x, float y, float cw, float h)
    {
        float s = Ui.S;
        bool armed = Time.unscaledTime < _restartArmedUntil;
        float bx = x + cw;
        bx = Button(bx, y, h, armed ? "Точно перезапустить?" : "Перезапустить игру", HasChanges || armed ? Look.Primary : Look.Secondary, Restart,
            armed ? "Нажмите ещё раз — игра закроется и запустится снова. Несохранённый прогресс пропадёт!"
                  : "Закрыть и снова запустить игру, чтобы применить изменения (нужно нажать дважды)");
        if (HasChanges) bx = Button(bx, y, h, "Отменить изменения", Look.Secondary, ResetToCurrent, "Вернуть все переключатели к текущему состоянию");
        bx = Button(bx, y, h, "Обновить", Look.Secondary, () => { Refresh(); Toast("Список обновлён."); }, "Перечитать папку Mods, наборы и журнал MelonLoader");

        float lx = x;
        lx = ButtonLeft(lx, bx, y, h, "Лаунчер", OpenLauncher, "Открыть окно SR2 Mod Manager: установка модов, наборы, журнал");
        ButtonLeft(lx, bx, y, h, "Папка Mods", () => OpenFolder(_p.Store.ModsDir), "Открыть папку с модами в Проводнике");
    }

    private float Button(float right, float y, float h, string text, Look look, Action a, string tip)
    {
        float w = Ui.TextW(text, 14, true) + 34f * Ui.S;
        var r = new Rect(right - w, y, w, h);
        DrawButton(r, text, look, a, tip);
        return r.x - 8f * Ui.S;
    }

    private float ButtonLeft(float left, float limit, float y, float h, string text, Action a, string tip)
    {
        float w = Ui.TextW(text, 13, true) + 30f * Ui.S;
        if (left + w > limit) return left;
        var r = new Rect(left, y, w, h);
        DrawButton(r, text, Look.Outline, a, tip, 13);
        return r.xMax + 8f * Ui.S;
    }

    private void DrawButton(Rect r, string text, Look look, Action a, string tip, int size = 14)
    {
        bool hv = Hover(r);
        float rad = r.height * 0.5f;
        switch (look)
        {
            case Look.Primary:
                Ui.Shadow(r, rad, 0.1f);
                Ui.Round(r, hv ? Ui.TealDark : Ui.Teal, rad);
                Ui.Label(r, text, size, TextAnchor.MiddleCenter, Color.white, true);
                break;
            case Look.Secondary:
                Ui.Round(r, hv ? Ui.BeigeDark : Ui.Beige, rad);
                Ui.Label(r, text, size, TextAnchor.MiddleCenter, Ui.Navy, true);
                break;
            default:
                Ui.Card(r, hv ? Ui.Beige : Ui.Paper, hv ? Ui.LineDark : Ui.Line, rad);
                Ui.Label(r, text, size, TextAnchor.MiddleCenter, hv ? Ui.Navy : Ui.NavySoft, true);
                break;
        }
        Hit(r, a);
        if (hv && tip != null) _tip = tip;
    }

    // ── всплывающие элементы ──

    private void DrawToast(float sheetW)
    {
        float now = Time.unscaledTime;
        if (now >= _toastUntil || string.IsNullOrEmpty(_toast)) return;
        float s = Ui.S;
        float a = Mathf.Clamp01((_toastUntil - now) / 0.35f);
        float maxW = Mathf.Min(sheetW, Screen.width - 40f);
        var lines = Ui.Wrap(_toast, 14, maxW - 64f * s, 2, true);
        float tw = 0f;
        foreach (var l in lines) tw = Mathf.Max(tw, Ui.TextW(l, 14, true));
        float lh = 20f * s, th = lines.Count * lh + 20f * s, bw = tw + 60f * s;
        float ty = _sheet.yMax + 12f * s;
        if (ty + th > Screen.height - 10f) ty = _sheet.yMax - th - 12f * s; // снизу не помещается — поверх низа листа
        var tr = new Rect(Mathf.Round((Screen.width - bw) * 0.5f), Mathf.Round(ty), Mathf.Round(bw), Mathf.Round(th));
        Ui.Shadow(tr, 16f * s, 0.28f * a);
        Ui.Round(tr, Ui.Fade(_toastBad ? Ui.Hex(0x7A2418) : Ui.Navy, 0.97f * a), Mathf.Min(th * 0.5f, 20f * s));
        Ui.Circle(new Vector2(tr.x + 22f * s, tr.y + 10f * s + lh * 0.5f), 5f * s, Ui.Fade(_toastBad ? Ui.Coral : Ui.Teal, a));
        for (int i = 0; i < lines.Count; i++)
            Ui.Label(new Rect(tr.x + 38f * s, tr.y + 10f * s + i * lh, tw + 8f * s, lh), lines[i], 14, TextAnchor.MiddleLeft, Ui.Fade(Color.white, a), true);
    }

    private void DrawTip()
    {
        if (!Ui.Repaint) return;
        float now = Time.unscaledTime;
        if (_tip != _tipShown) { _tipShown = _tip; _tipSince = now; }
        if (string.IsNullOrEmpty(_tip) || _dragThumb || now - _tipSince < 0.45f) return;
        float s = Ui.S;
        float maxW = Mathf.Min(520f * s, Screen.width - 40f);
        var lines = new List<string>();
        foreach (var par in _tip.Split('\n'))
            foreach (var l in Ui.Wrap(par, 12, maxW - 24f * s, 3))
                if (lines.Count < 8) lines.Add(l);
        if (lines.Count == 0) return;
        float tw = 0f;
        foreach (var l in lines) tw = Mathf.Max(tw, Ui.TextW(l, 12));
        float lh = 17f * s;
        var r = new Rect(_mouse.x + 16f * s, _mouse.y + 22f * s, tw + 24f * s, lines.Count * lh + 14f * s);
        if (r.xMax > Screen.width - 8f) r.x = Mathf.Max(8f, _mouse.x - r.width - 8f * s);
        if (r.yMax > Screen.height - 8f) r.y = Mathf.Max(8f, _mouse.y - r.height - 10f * s);
        r = new Rect(Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.width), Mathf.Round(r.height));
        Ui.Shadow(r, 8f * s, 0.25f);
        Ui.Round(r, Ui.Fade(Ui.Navy, 0.96f), 8f * s);
        for (int i = 0; i < lines.Count; i++)
            Ui.Label(new Rect(r.x + 12f * s, r.y + 7f * s + i * lh, tw + 6f * s, lh), lines[i], 12, TextAnchor.MiddleLeft, Color.white);
    }

    private void Hit(Rect r, Action a) { if (Ui.Repaint) _hitsFrame.Add((r, a)); }
}
