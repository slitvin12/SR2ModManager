// DetailsView.cs — карточка выбранного мода: описание, зависимости, журнал игры, файл, старые версии, действия.
// Для доступного (ещё не установленного) мода — откуда он, что ему нужно и кнопка «Установить»;
// для установленного с новой версией в каталоге — «Обновить до vX».
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed class DetailsView : Canvas
{
    private readonly Model _m;
    public ModEntry? Mod { get; private set; }
    /// true — своя карточка с рамкой (отдельная панель); false — содержимое вкладки нижней панели.
    public bool Framed = true;
    private List<BackupInfo> _backups = new();
    private string? _userData;
    private int _scroll, _contentH, _viewH;

    public event Action<ModEntry>? ToggleRequested;
    public event Action<string>? SelectRequested;
    public event Action<string>? EnableRequested;
    public event Action<ModEntry, BackupInfo>? RestoreRequested;
    public event Action<ModEntry>? RevealRequested;
    public event Action<ModEntry>? SettingsRequested;
    public event Action<ModEntry>? DeleteRequested;
    public event Action? OpenLogRequested;
    public event Action<ModEntry>? InstallRequested;
    public event Action<ModEntry, UpdateInfo>? UpdateRequested;
    public event Action<string>? LinkRequested;

    public DetailsView(Model m) { _m = m; }

    /// repaint = false — перерисовать, только если сменился сам мод или его копии/папка настроек.
    public void ShowMod(ModEntry? m, List<BackupInfo> backups, string? userData, bool repaint = true)
    {
        bool same = ReferenceEquals(m, Mod) && ReferenceEquals(backups, _backups) && string.Equals(userData, _userData);
        if (!string.Equals(m?.Id, Mod?.Id, StringComparison.OrdinalIgnoreCase)) _scroll = 0;
        Mod = m; _backups = backups; _userData = userData;
        if (repaint || !same) Invalidate();
    }

    private static string KindText(ModEntry m) => m.Kind switch
    {
        "Plugin" => "плагин MelonLoader",
        "Mod" => "мод MelonLoader",
        "Expansion" => "расширение Starlight",
        _ => m.Readable ? ".NET-библиотека" : "неизвестный файл",
    };

    private int Section(Graphics g, string title, int x, int y, int w, string? link = null, Action? act = null)
    {
        y += Px(6);
        var ts = Theme.Measure(title, Theme.Section);
        Theme.Text(g, title, Theme.Section, new Rectangle(x, y, ts.Width + 2, Px(18)), Theme.Hint);
        int lineEnd = x + w;
        if (link != null && act != null)
        {
            int lw = Theme.Measure(link, Theme.SmallBold).Width;
            var lr = new Rectangle(x + w - lw - Px(4), y - Px(2), lw + Px(4), Px(22));
            bool h = IsHover("sec:" + title);
            Theme.Text(g, link, Theme.SmallBold, lr, h ? Theme.TealDark : Theme.TealText, TextFormatFlags.Right);
            if (h) using (var p = new Pen(Theme.TealDark)) g.DrawLine(p, lr.X + Px(4), lr.Bottom - Px(4), lr.Right, lr.Bottom - Px(4));
            AddHit(lr, "sec:" + title, act);
            lineEnd = lr.X - Px(10);
        }
        using (var p = new Pen(Theme.Line, Math.Max(1f, S)))
            if (lineEnd > x + ts.Width + Px(20)) g.DrawLine(p, x + ts.Width + Px(10), y + Px(9), lineEnd, y + Px(9));
        return y + Px(26);
    }

    /// Капсулы с переносом строк. Возвращает новую y.
    private int Chips(Graphics g, int x, int y, int w, IEnumerable<(string Text, Color Bg, Color Fg, string? Glyph, string Key, Action? Act, string? Tip)> chips)
    {
        int cx = x, h = Px(26), any = 0;
        foreach (var c in chips)
        {
            int cw = Theme.PillWidth(c.Text, Theme.ButtonFont, h, c.Glyph != null);
            if (cx + cw > x + w && cx > x) { cx = x; y += h + Px(6); }
            bool hover = c.Act != null && IsHover(c.Key);
            var r = Theme.Pill(g, cx, y, h, c.Text, Theme.ButtonFont, hover ? Theme.Mix(c.Bg, Theme.Navy, 0.06f) : c.Bg, c.Fg, c.Glyph);
            AddHit(r, c.Key, c.Act, c.Tip, null, c.Act != null);
            cx += cw + Px(6);
            any++;
        }
        return any == 0 ? y : y + h + Px(10);
    }

    private int Box(Graphics g, int x, int y, int w, string title, string text, Color bg, Color fg, string glyph, int maxLines = 4)
    {
        int pad = Px(12), tx = x + pad + Px(24), tw = w - pad * 2 - Px(24);
        int lh = Theme.WrapHeight("Ag", Theme.Body, 1000);
        int th = Math.Min(Theme.WrapHeight(text, Theme.Body, tw), lh * maxLines);
        int h = pad + Px(20) + th + pad;
        var r = new Rectangle(x, y, w, h);
        Theme.FillRound(g, r, Math.Min(Px(10), Px(Theme.Spec.PanelRadius)), bg);
        Theme.Glyph(g, glyph, new Rectangle(x + pad - Px(2), y + pad - Px(1), Px(20), Px(20)), fg);
        Theme.Text(g, title, Theme.SmallBold, new Rectangle(tx, y + pad, tw, Px(18)), fg);
        Theme.Wrap(g, text, Theme.Body, new Rectangle(tx, y + pad + Px(20), tw, th + 1), Theme.Navy);
        if (Theme.WrapHeight(text, Theme.Body, tw) > th) AddHit(r, "box:" + title, null, text.Length > 1500 ? text.Substring(0, 1500) + "…" : text, null, false);
        return y + h + Px(10);
    }

    private (int Width, int Height) ActionW(string text, string glyph) => (Theme.Measure(text, Theme.ButtonFont).Width + Px(46) + (glyph.Length > 0 ? Px(4) : 0), Px(34));

    private Rectangle ActionButton(Graphics g, int x, int y, string text, string glyph, string key, Action act, bool danger = false, string? tip = null, bool primary = false)
    {
        var (w, h) = ActionW(text, glyph);
        var r = new Rectangle(x, y, w, h);
        bool hv = IsHover(key), dn = IsDown(key);
        if (primary)
        {
            Theme.FillRound(g, r, Theme.Radius(h, S), dn ? Theme.TealText : hv ? Theme.TealDark : Theme.Teal);
            Theme.Glyph(g, glyph, new Rectangle(x + Px(14), y, Px(16), h), Color.White);
            Theme.Text(g, text, Theme.ButtonFont, new Rectangle(x + Px(36), y, w - Px(44), h), Color.White);
            AddHit(r, key, act, tip);
            return r;
        }
        Color bg = danger ? (hv ? Theme.CoralTint : Theme.Paper) : dn ? Theme.BeigeDark : hv ? Theme.Beige : Theme.Paper;
        Theme.FillRound(g, r, Theme.Radius(h, S), bg);
        Theme.StrokeRound(g, r, Theme.Radius(h, S), danger ? (hv ? Theme.Coral : Theme.Line) : hv ? Theme.LineDark : Theme.Line, Math.Max(1f, S));
        Color fg = danger ? Theme.CoralText : Theme.Navy;
        Theme.Glyph(g, glyph, new Rectangle(x + Px(14), y, Px(16), h), danger ? Theme.Coral : Theme.NavySoft);
        Theme.Text(g, text, Theme.ButtonFont, new Rectangle(x + Px(36), y, w - Px(44), h), fg);
        AddHit(r, key, act, tip);
        return r;
    }

    /// Плашка с пояснением и кнопкой действия внутри (новая версия, «не установлен», «устарел»).
    private int Callout(Graphics g, int x, int y, int w, string title, string text, Color bg, Color fg, Color accent, string glyph,
        string? button, string key, Action? act, string? tip = null)
    {
        int pad = Px(14), tx = x + pad + Px(26), tw = w - pad * 2 - Px(26);
        int th = Theme.WrapHeight(text, Theme.Body, tw);
        int bh = button != null && act != null ? Px(32) : 0;
        int h = pad + Px(22) + th + (bh > 0 ? Px(10) + bh : 0) + pad;
        var r = new Rectangle(x, y, w, h);
        Theme.FillRound(g, r, Px(12), bg);
        Theme.FillRound(g, new RectangleF(r.X, r.Y + Px(10), Px(4), r.Height - Px(20)), Px(2), accent);
        Theme.Glyph(g, glyph, new Rectangle(x + pad, y + pad - Px(1), Px(20), Px(22)), fg);
        Theme.Text(g, title, Theme.Strong, new Rectangle(tx, y + pad - Px(1), tw, Px(22)), fg);
        Theme.Wrap(g, text, Theme.Body, new Rectangle(tx, y + pad + Px(22), tw, th + 1), Theme.Navy);
        if (bh > 0)
        {
            int bw = Theme.Measure(button, Theme.ButtonFont).Width + Px(34);
            var br = new Rectangle(tx, y + pad + Px(22) + th + Px(10), bw, bh);
            bool hv = IsHover(key), dn = IsDown(key);
            Theme.FillRound(g, br, bh / 2f, dn ? Theme.Mix(accent, Theme.Navy, 0.3f) : hv ? Theme.Mix(accent, Theme.Navy, 0.15f) : accent);
            Theme.Text(g, button, Theme.ButtonFont, br, Color.White, TextFormatFlags.HorizontalCenter);
            AddHit(br, key, act, tip);
        }
        return y + h + Px(12);
    }

    private static (Color Bg, Color Fg, string Glyph) LevelLook(CompatLevel l) => l switch
    {
        CompatLevel.Error => (Theme.CoralTint, Theme.CoralText, Theme.GError),
        CompatLevel.Warning => (Theme.OrangeTint, Theme.OrangeText, Theme.GWarning),
        _ => (Theme.BlueTint, Theme.BlueText, Theme.GInfo),
    };

    /// «СОВМЕСТИМОСТЬ»: под какой MelonLoader/.NET собран мод, что не так в текущем окружении и что делать.
    private int CompatSection(Graphics g, ModEntry m, int x, int y, int w)
    {
        var rep = _m.Compat;
        var env = _m.Env;
        y = Section(g, "СОВМЕСТИМОСТЬ", x, y, w);
        if (rep == null || env == null)
        {
            Theme.Text(g, _m.CompatBusy ? "Проверяется: MelonLoader, Unity, .NET, библиотеки и хуки Harmony…" : "Проверка совместимости недоступна",
                Theme.Body, new Rectangle(x, y - Px(2), w, Px(22)), Theme.Hint);
            return y + Px(30);
        }
        // факты о сборке
        var mf = rep.Manifest(m.Id);
        if (mf != null && mf.Readable)
        {
            int cx = x, h = Px(22), n = 0;
            foreach (var f in CompatChecker.Facts(mf))
            {
                int pw = Theme.PillWidth(f, Theme.SmallBold, h, false);
                if (cx + pw > x + w && cx > x) { cx = x; y += h + Px(6); }
                var pr = Theme.Pill(g, cx, y, h, f, Theme.SmallBold, Theme.Beige, Theme.NavySoft);
                if (f.StartsWith("Harmony") && mf.Hooks.Count > 0)
                    AddHit(pr, "fact:hooks", null, "Методы игры, которые мод меняет через Harmony:\n" + string.Join("\n",
                        mf.Hooks.Where(hk => !hk.Dynamic).GroupBy(hk => hk.Key).Take(14).Select(gr => "• " + gr.First().Short + " — " + string.Join(", ", gr.Select(hk => hk.KindText).Distinct())))
                        + (mf.Hooks.Where(hk => !hk.Dynamic).Select(hk => hk.Key).Distinct().Count() > 14 ? "\n…" : ""), null, false);
                cx = pr.Right + Px(6);
                n++;
            }
            if (n > 0) y += h + Px(10);
        }
        var issues = rep.For(m.Id);
        if (issues.Count == 0)
        {
            Theme.Glyph(g, Theme.GCheck, new Rectangle(x - Px(2), y - Px(3), Px(20), Px(22)), Theme.TealDark, Theme.IconsSmall);
            Theme.Text(g, "Подходит: " + env.Summary, Theme.Body, new Rectangle(x + Px(20), y - Px(2), w - Px(20), Px(22)), Theme.Navy);
            y += Px(28);
        }
        else
        {
            foreach (var i in issues.Take(4))
            {
                var (bg, fg, glyph) = LevelLook(i.Level);
                y = Box(g, x, y, w, i.Title, i.Text + (i.Fix.Length > 0 ? "\nЧто сделать: " + i.Fix : ""), bg, fg, glyph, 6);
            }
            if (issues.Count > 4)
            {
                Theme.Text(g, "и ещё " + (issues.Count - 4) + ": " + string.Join(", ", issues.Skip(4).Select(i => i.Badge)), Theme.Small, new Rectangle(x, y - Px(4), w, Px(20)), Theme.Hint);
                y += Px(20);
            }
            string envLine = "Проверено для: " + env.Summary;
            Theme.Text(g, envLine, Theme.Small, new Rectangle(x, y - Px(4), w, Px(20)), Theme.Hint);
            y += Px(22);
        }
        return y;
    }

    private static bool IsWebLink(string? s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    protected override void Draw(Graphics g)
    {
        // в нижней панели (Framed = false) рамку рисует сама панель — здесь только содержимое
        var card = Framed ? new Rectangle(Px(2), Px(3), Width - Px(4), Height - Px(5)) : new Rectangle(-Px(6), -Px(8), Width + Px(12), Height + Px(10));
        if (Framed)
        {
            Theme.FillRound(g, card, Px(Theme.Spec.PanelRadius), Theme.Paper);
            Theme.StrokeRound(g, card, Px(Theme.Spec.PanelRadius), Theme.Line, Math.Max(1f, S));
        }
        var m = Mod;
        if (m == null)
        {
            int sz = Px(56);
            bool slime = card.Height >= Px(220);   // в низкой нижней панели — без картинки
            int ty = slime ? card.Y + card.Height / 2 : card.Y + card.Height / 2 - Px(22);
            if (slime) Theme.Slime(g, new RectangleF(card.X + (card.Width - sz) / 2f, card.Y + card.Height / 2f - Px(70), sz, sz * 0.86f), Theme.Pink);
            Theme.Text(g, Framed ? "Выберите мод слева" : "Выберите мод в списке", Theme.Strong, new Rectangle(card.X, ty, card.Width, Px(26)), Theme.Navy, TextFormatFlags.HorizontalCenter);
            Theme.Text(g, "Здесь появятся описание, зависимости и журнал игры", Theme.Body, new Rectangle(card.X + Px(20), ty + Px(26), card.Width - Px(40), Px(22)), Theme.Hint, TextFormatFlags.HorizontalCenter);
            return;
        }

        var inner = new Rectangle(card.X + Px(22), card.Y + Px(18), card.Width - Px(44), card.Height - Px(30));
        _viewH = inner.Height;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - _viewH));
        var clip = new Rectangle(card.X + Px(1), card.Y + Px(6), card.Width - Px(2), card.Height - Px(12));
        g.SetClip(clip);
        int x = inner.X, w = inner.Width, y = inner.Y - _scroll;
        bool on = _m.On(m.Id);

        // ── заголовок
        bool avail = m.Available;
        var upd = _m.UpdateFor(m);
        var tg = new Rectangle(x, y + Px(5), Px(48), Px(26));
        if (avail)
        {
            bool ih = IsHover("inst"), dn = IsDown("inst");
            Theme.FillRound(g, tg, tg.Height / 2f, dn ? Theme.BlueText : ih ? Theme.Blue : Theme.BlueTint);
            Theme.Glyph(g, Theme.GDownload, tg, ih || dn ? Color.White : Theme.BlueText);
            AddHit(Rectangle.Inflate(tg, Px(4), Px(4)), "inst", () => InstallRequested?.Invoke(m), "Установить в Mods");
        }
        else
        {
            Theme.Toggle(g, tg, on, IsHover("tgl"), !m.Readable);
            AddHit(Rectangle.Inflate(tg, Px(4), Px(4)), "tgl", () => ToggleRequested?.Invoke(m), on ? "Выключить" : "Включить");
        }
        var st = _m.Status(m);
        var pill = Theme.Pill(g, inner.Right, y + Px(7), Px(24), st.Text, Theme.SmallBold, st.Bg, st.Fg, null, null, true);
        AddHit(pill, "st", null, st.Tip, null, false);
        int tx = tg.Right + Px(14);
        string meta = string.Join("  ·  ", new[] { string.IsNullOrEmpty(m.Version) ? null : "v" + m.Version, string.IsNullOrWhiteSpace(m.Author) ? null : m.Author, KindText(m) }.Where(s => !string.IsNullOrEmpty(s)));
        if (Framed)
        {
            Theme.Text(g, m.DisplayName, Theme.H2, new Rectangle(tx, y, pill.X - tx - Px(8), Px(36)), Theme.Navy);
            y += Px(36);
            Theme.Text(g, meta, Theme.Body, new Rectangle(tx, y - Px(4), inner.Right - tx, Px(20)), Theme.Hint);
            y += Px(28);
        }
        else
        {   // в нижней панели высота дороже: название и версия с автором — одной строкой
            int nameW = Math.Min(Theme.Measure(m.DisplayName, Theme.H2).Width + Px(4), pill.X - tx - Px(8));
            Theme.Text(g, m.DisplayName, Theme.H2, new Rectangle(tx, y, nameW, Px(36)), Theme.Navy);
            int mx = tx + nameW + Px(14);
            if (pill.X - Px(10) - mx >= Px(80)) Theme.Text(g, meta, Theme.Body, new Rectangle(mx, y + Px(3), pill.X - Px(10) - mx, Px(36)), Theme.Hint);
            y += Px(46);
        }

        // в широкой нижней панели — две колонки: слева описание, совместимость и действия, справа зависимости, журнал и файл
        bool two = !Framed && w >= Px(860);
        int gap = Px(36);
        int lw = two ? (w - gap) * 56 / 100 : w, rx = x + lw + gap, rw = w - lw - gap;
        int top = y;
        y = MainPart(g, m, x, y, lw, avail, upd);
        if (two)
        {
            y = ActionsPart(g, m, x, y, lw, avail, upd);
            int yr = SidePart(g, m, rx, top, rw, avail, on);
            using (var p = new Pen(Theme.Line, Math.Max(1f, S)))
                g.DrawLine(p, rx - gap / 2, top + Px(6), rx - gap / 2, Math.Max(y, yr) - Px(20));
            y = Math.Max(y, yr);
        }
        else
        {
            y = SidePart(g, m, x, y, w, avail, on);
            y = ActionsPart(g, m, x, y, w, avail, upd);
        }

        _contentH = y + _scroll - inner.Y;
        g.ResetClip();

        if (_contentH > _viewH)
        {
            int track = card.Height - Px(28);
            int th = Math.Max(Px(36), track * _viewH / Math.Max(1, _contentH));
            int ty = card.Y + Px(14) + (track - th) * _scroll / Math.Max(1, _contentH - _viewH);
            Theme.FillRound(g, new RectangleF(card.Right - Px(8), ty, Px(4), th), Px(2), Theme.Line);
        }
    }

    /// Описание, «новая версия / не установлен», проблемы файла и совместимость.
    private int MainPart(Graphics g, ModEntry m, int x, int y, int w, bool avail, UpdateInfo? upd)
    {
        // ── описание
        bool noDesc = string.IsNullOrWhiteSpace(m.Description);
        string desc = noDesc ? "Описания нет. Его можно дописать в UserData\\ModManager\\profiles.json → «descriptions»." : m.Description;
        int dh = Theme.WrapHeight(desc, Theme.Body, w);
        Theme.Wrap(g, desc, Theme.Body, new Rectangle(x, y, w, dh + Px(2)), noDesc ? Theme.Hint : Theme.Navy);
        y += dh + Px(14);

        // ── не установлен / есть новая версия / устарел
        if (avail)
        {
            string file = Path.GetFileName(m.Path) + (string.IsNullOrEmpty(m.EntryName) ? "" : " → " + Path.GetFileName(m.EntryName));
            y = Callout(g, x, y, w, "Не установлен",
                $"Найден в папке «{m.SourceLabel}»: {file}. Установка скопирует мод в Mods (как {m.FileName}) — исходный файл останется на месте.",
                Theme.BlueTint, Theme.BlueText, Theme.Blue, Theme.GDownload, "Установить", "co:inst", () => InstallRequested?.Invoke(m), "Скопировать в Mods и включить");
        }
        else if (upd != null && upd.CanInstall)
        {
            string cur = string.IsNullOrEmpty(m.Version) ? "текущая версия" : "v" + m.Version;
            y = Callout(g, x, y, w, "Есть новая версия v" + upd.Version,
                $"Найдена {upd.Where}. Сейчас стоит {cur} — она сохранится в Mods\\Disabled\\_replaced, и её можно будет вернуть. Перед обновлением менеджер сохранит бэкап сейвов." + (_m.GameRunning && m.Enabled ? " Игра запущена — новая версия заработает после перезапуска." : ""),
                Theme.BlueTint, Theme.BlueText, Theme.Blue, Theme.GSync, "Обновить до v" + upd.Version, "co:upd", () => UpdateRequested?.Invoke(m, upd), "Поставить новую версию вместо текущей");
        }
        else if (upd != null)
        {
            bool web = IsWebLink(m.Link);
            y = Callout(g, x, y, w, "Похоже, устарел",
                $"В журнале игры: {upd.Reason}. Так бывает, когда мод собран под другую версию игры или MelonLoader. Новой версии в ModManager\\Library и «Загрузках» нет — "
                + (web ? "загляните на страницу мода." : "поищите обновление у автора и перетащите файл в окно."),
                Theme.OrangeTint, Theme.OrangeText, Theme.Orange, Theme.GWarning, web ? "Страница мода" : null, "co:link", web ? () => LinkRequested?.Invoke(m.Link) : null, m.Link);
        }

        // ── проблемы
        if (!m.Readable)
            y = Box(g, x, y, w, "Не читается", "Файл не удалось прочитать как .NET-сборку. Скорее всего, это не мод MelonLoader — игра может выдать ошибку при загрузке.", Theme.CoralTint, Theme.CoralText, Theme.GError);
        if (m.DuplicateInDisabled)
            y = Box(g, x, y, w, "Две копии", "В Mods\\Disabled лежит ещё одна (старая) копия этого файла. Игра загружает только копию из Mods. При выключении старая копия уедет в Disabled\\_replaced.", Theme.OrangeTint, Theme.OrangeText, Theme.GWarning);

        // ── совместимость: MelonLoader 0.6/0.7, Unity, .NET 6, библиотеки, хуки Harmony
        if (!avail) y = CompatSection(g, m, x, y, w);

        return y;
    }

    /// Зависимости, «нужен для», журнал игры, файл и старые версии.
    private int SidePart(Graphics g, ModEntry m, int x, int y, int w, bool avail, bool on)
    {
        // ── зависимости
        y = Section(g, "ЗАВИСИМОСТИ", x, y, w);
        if (m.DependsOn.Count == 0)
        {
            Theme.Text(g, "Не нужны — мод работает сам по себе", Theme.Body, new Rectangle(x, y - Px(2), w, Px(22)), Theme.Hint);
            y += Px(30);
        }
        else
        {
            y = Chips(g, x, y, w, m.DependsOn.Select(d =>
            {
                var dm = _m.Find(d);
                var da = dm == null ? _m.FindAny(d) : null; // не установлен, но есть в каталоге
                bool ok = dm != null && _m.On(dm.Id);
                if (dm == null && da == null && _m.IsKnownLib(d))
                    return (d + " — есть", Theme.TealTint, Theme.TealText, (string?)Theme.GCheck, "dep:" + d, (Action?)null, (string?)("Библиотека уже установлена: " + _m.Env!.WhereOf(d)));
                if (da != null)
                    return (da.DisplayName + " — доступен", Theme.BlueTint, Theme.BlueText, (string?)Theme.GDownload, "dep:" + d,
                        (Action?)(() => SelectRequested?.Invoke(da.Id)), (string?)$"Не установлен, но найден в «{da.SourceLabel}» — откройте, чтобы установить");
                string text = (dm?.DisplayName ?? d) + (dm == null ? " — не найден" : !ok ? " — выключен" : "");
                Action? act = dm == null ? null : ok ? () => SelectRequested?.Invoke(dm.Id) : () => EnableRequested?.Invoke(dm.Id);
                string tip = dm == null ? "Такого мода нет ни в Mods, ни в ModManager\\Library, ни в «Загрузках»" : ok ? "Открыть" : "Нажмите, чтобы включить";
                return (text, ok ? Theme.TealTint : Theme.CoralTint, ok ? Theme.TealText : Theme.CoralText, (string?)(ok ? Theme.GCheck : Theme.GWarning), "dep:" + d, act, (string?)tip);
            }));
        }
        var users = _m.UsersOf(m);
        if (users.Count > 0)
        {
            y = Section(g, "НУЖЕН ДЛЯ", x, y, w);
            y = Chips(g, x, y, w, users.Select(u =>
            {
                bool uon = _m.On(u.Id);
                string? tip = uon && !on ? "Этот мод включён, но без «" + m.DisplayName + "» работать не будет" : "Открыть";
                (string Text, Color Bg, Color Fg, string? Glyph, string Key, Action? Act, string? Tip) chip =
                    (u.DisplayName, uon && !on ? Theme.CoralTint : Theme.Beige, uon && !on ? Theme.CoralText : Theme.Navy, null, "use:" + u.Id, () => SelectRequested?.Invoke(u.Id), tip);
                return chip;
            }));
        }

        // ── журнал MelonLoader (у доступного мода журнала ещё нет)
        if (!avail)
        {
            string logTitle = _m.GameRunning ? "ЖУРНАЛ ЗАПУЩЕННОЙ ИГРЫ" : "ЖУРНАЛ ПРОШЛОГО ЗАПУСКА";
            y = Section(g, logTitle, x, y, w, _m.Log != null ? (Framed ? "Открыть лог" : "Строки в журнале") : null, () => OpenLogRequested?.Invoke());
            if (_m.Log == null)
            {
                Theme.Text(g, "Журнал MelonLoader не найден — игра с модами ещё не запускалась", Theme.Body, new Rectangle(x, y - Px(2), w, Px(22)), Theme.Hint);
                y += Px(30);
            }
            else
            {
                var log = _m.Log;
                bool loaded = log.WasLoaded(m.Id);
                var ls = _m.LogFor(m);
                int cx = x, h = Px(24);
                // капсулы переносятся на новую строку, если не влезают в ширину карточки
                void Pill(string text, Color bg, Color fg, string? glyph)
                {
                    int pw = Theme.PillWidth(text, Theme.SmallBold, h, glyph != null);
                    if (cx + pw > x + w && cx > x) { cx = x; y += h + Px(6); }
                    cx = Theme.Pill(g, cx, y, h, text, Theme.SmallBold, bg, fg, glyph).Right + Px(6);
                }
                if (loaded) Pill("загружен", Theme.TealTint, Theme.TealText, Theme.GCheck);
                else Pill(m.Enabled || m.Kind == "Plugin" ? "не загружался" : "не загружался (был выключен)", Theme.Beige, Theme.NavySoft, null);
                int errs = ls?.Errors ?? 0, warns = ls?.Warnings ?? 0;
                Pill(Theme.Count(errs, "ошибка", "ошибки", "ошибок"), errs > 0 ? Theme.CoralTint : Theme.Beige, errs > 0 ? Theme.CoralText : Theme.NavySoft, errs > 0 ? Theme.GError : null);
                Pill(Theme.Count(warns, "предупреждение", "предупреждения", "предупреждений"), warns > 0 ? Theme.OrangeTint : Theme.Beige, warns > 0 ? Theme.OrangeText : Theme.NavySoft, null);
                string when = "запись " + ((DateTime.Now - log.Updated).TotalHours < 20 ? log.Updated.ToString("HH:mm") : log.Updated.ToString("dd.MM HH:mm"));
                int ww = Theme.Measure(when, Theme.Small).Width;
                if (cx + Px(4) + ww > x + w) { cx = x; y += h + Px(6); } else cx += Px(4);
                Theme.Text(g, when, Theme.Small, new Rectangle(cx, y, Math.Max(0, x + w - cx), h), Theme.Hint);
                y += h + Px(10);
                if (ls != null && ls.LastError.Length > 0)
                    y = Box(g, x, y, w, "Последняя ошибка", ls.LastError, Theme.CoralTint, Theme.CoralText, Theme.GError);
                else if (ls != null && ls.LastWarning.Length > 0)
                    y = Box(g, x, y, w, "Последнее предупреждение", ls.LastWarning, Theme.OrangeTint, Theme.OrangeText, Theme.GWarning, 3);
            }
        }

        // ── файл (у доступного — откуда он)
        y = Section(g, avail ? "ИСТОЧНИК · " + m.SourceLabel.ToUpperInvariant() : "ФАЙЛ", x, y, w, "Показать в папке", () => RevealRequested?.Invoke(m));
        string rel = m.Path.StartsWith(_m.Store.GameDir, StringComparison.OrdinalIgnoreCase) ? m.Path.Substring(_m.Store.GameDir.Length).TrimStart('\\', '/') : m.Path;
        Theme.Text(g, rel, Theme.Body, new Rectangle(x, y - Px(2), w, Px(22)), Theme.Navy, TextFormatFlags.PathEllipsis);
        AddHit(new Rectangle(x, y - Px(2), w, Px(22)), "path", null, m.Path, null, false);
        y += Px(22);
        string info = Theme.FileSize(m.Size) + "  ·  изменён " + m.Modified.ToString("dd.MM.yyyy HH:mm")
            + (avail && !string.IsNullOrEmpty(m.EntryName) ? "  ·  в архиве " + m.EntryName : string.IsNullOrEmpty(m.AssemblyName) ? "" : "  ·  сборка " + m.AssemblyName);
        Theme.Text(g, info, Theme.Small, new Rectangle(x, y - Px(2), w, Px(20)), Theme.Hint);
        y += Px(28);

        // ── старые версии
        if (_backups.Count > 0)
        {
            y = Section(g, "СТАРЫЕ ВЕРСИИ · " + _backups.Count, x, y, w);
            foreach (var b in _backups.Take(6))
            {
                int rh = Px(36);
                var row = new Rectangle(x - Px(8), y - Px(4), w + Px(16), rh);
                bool hv = IsHover("bk:" + b.Path) || IsHover("bkr:" + b.Path);
                if (hv) Theme.FillRound(g, row, Px(10), Theme.Cream);
                string ver = string.IsNullOrEmpty(b.Version) ? "копия" : "v" + b.Version;
                var vs = Theme.Measure(ver, Theme.SmallBold);
                var vp = Theme.Pill(g, x, y + (rh - Px(22)) / 2 - Px(4), Px(22), ver, Theme.SmallBold, Theme.Beige, Theme.Navy);
                string bt = b.Date.ToString("dd.MM.yyyy HH:mm") + "  ·  " + Theme.FileSize(b.Size) + "  ·  " + (b.Where == "_replaced" ? "заменённая копия" : b.FileName);
                int btnW = Theme.Measure("Вернуть", Theme.ButtonFont).Width + Px(28);
                var br = new Rectangle(x + w - btnW, y + (rh - Px(28)) / 2 - Px(4), btnW, Px(28));
                Theme.Text(g, bt, Theme.Small, new Rectangle(vp.Right + Px(10), y - Px(4), br.X - vp.Right - Px(18), rh), Theme.NavySoft);
                bool bh = IsHover("bkr:" + b.Path);
                Theme.FillRound(g, br, br.Height / 2f, bh ? Theme.Teal : Theme.TealTint);
                Theme.Text(g, "Вернуть", Theme.ButtonFont, br, bh ? Color.White : Theme.TealText, TextFormatFlags.HorizontalCenter);
                AddHit(row, "bk:" + b.Path, null, b.Path, null, false);
                var cb = b;
                AddHit(br, "bkr:" + b.Path, () => RestoreRequested?.Invoke(m, cb), "Поставить эту версию вместо текущей (текущая сохранится как копия)");
                y += rh + Px(2);
            }
            if (_backups.Count > 6)
            {
                Theme.Text(g, "и ещё " + (_backups.Count - 6) + " — в папке Mods\\Disabled\\_replaced", Theme.Small, new Rectangle(x, y - Px(4), w, Px(20)), Theme.Hint);
                y += Px(20);
            }
            y += Px(8);
        }

        return y;
    }

    /// Кнопки действий.
    private int ActionsPart(Graphics g, ModEntry m, int x, int y, int w, bool avail, UpdateInfo? upd)
    {
        // ── действия
        y += Px(4);
        int ax = x;
        var acts = new List<(string Text, string Glyph, string Key, Action Act, bool Danger, string? Tip, bool Primary)>();
        if (avail) acts.Add(("Установить", Theme.GDownload, "act:inst", () => InstallRequested?.Invoke(m), false, "Скопировать в Mods и включить (Enter)", true));
        else if (upd != null && upd.CanInstall) acts.Add(("Обновить", Theme.GSync, "act:upd", () => UpdateRequested?.Invoke(m, upd), false, "Поставить v" + upd.Version + " вместо текущей", true));
        acts.Add(("Показать файл", Theme.GFolder, "act:reveal", () => RevealRequested?.Invoke(m), false, avail ? "Открыть папку с найденным файлом" : "Открыть папку с DLL в проводнике", false));
        if (!avail && _userData != null) acts.Add(("Настройки мода", Theme.GSettings, "act:cfg", () => SettingsRequested?.Invoke(m), false, _userData, false));
        if (IsWebLink(m.Link)) acts.Add(("Страница мода", Theme.GOpen, "act:link", () => LinkRequested?.Invoke(m.Link), false, m.Link, false));
        if (!avail) acts.Add(("Удалить…", Theme.GDelete, "act:del", () => DeleteRequested?.Invoke(m), true, "Убрать DLL в корзину", false));
        foreach (var a in acts)
        {
            var (aw, ah) = ActionW(a.Text, a.Glyph);
            if (ax + aw > x + w && ax > x) { ax = x; y += ah + Px(8); }
            var r = ActionButton(g, ax, y, a.Text, a.Glyph, a.Key, a.Act, a.Danger, a.Tip, a.Primary);
            ax = r.Right + Px(8);
        }
        y += Px(34) + Px(12);

        return y;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int before = _scroll;
        _scroll = Math.Clamp(_scroll - e.Delta * Px(60) / 120, 0, Math.Max(0, _contentH - _viewH));
        if (_scroll != before) { Invalidate(); Update(); UpdateHover(PointToClient(MousePosition)); }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); }
}


