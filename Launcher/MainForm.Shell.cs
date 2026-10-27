// MainForm.Shell.cs — Fluent и «Слаймовый» стили. Устройство окна то же, что у «Компактного» (MainForm.Compact.cs):
// боковая панель с наборами, разделами и окружением, вкладки-фильтры, «Команды» (Ctrl+K) и «Играть ▾», список модов,
// нижняя панель и строка состояния. Своя у каждого стиля — пластика:
//  • Fluent — как «Параметры» Windows 11: слайм, название и поиск над навигацией, у выбранного пункта — акцентная черта
//    слева; справа — заголовок страницы, вкладки с чертой снизу, белые карточки и панели на светло-сером фоне;
//    строка состояния — как у «Компактного», только светлая;
//  • «Слаймовый» — белая боковая панель с тенью «парит» над кремовым фоном: крупный слайм и полоска бирюза → розовый,
//    наборы-слаймики, разделы со значками в цветных кружках; поиск, вкладки, кнопки и строка состояния — капсулы.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

internal sealed partial class MainForm
{
    private static bool SlimeLook => Theme.Design == DesignKind.Slime;

    // размеры при 96 dpi
    private const int FluentSideW = 252, SlimeSideW = 264;   // боковая панель (у «Слаймового» — вместе с полями вокруг карточки)
    private const int FluentBarH = 28, SlimeBarH = 40;       // строка состояния

    private int SoftBarH => _root.Px(SlimeLook ? SlimeBarH : FluentBarH);
    private int SoftLeft(int side) => side + _root.Px(SlimeLook ? 10 : 24);
    private int SoftRight => _root.ClientSize.Width - _root.Px(SlimeLook ? 18 : 24);
    private int SoftContentW(int side) => SoftRight - SoftLeft(side);
    private int SoftBadgeW(string s) => Math.Max(_root.Px(22), Theme.Measure(s, Theme.SmallBold).Width + _root.Px(14));

    /// Ширина боковой панели: как в макете, но не уже, чем нужно подписи «Каталог и обновления» со счётчиком.
    private int SidePxSoft()
    {
        int P(float v) => _root.Px(v);
        int cat = CountIn(CatalogFilter.Catalog);
        int text = Theme.Measure("Каталог и обновления", Theme.ButtonFont).Width + P(4);
        int badge = cat > 0 ? SoftBadgeW(cat.ToString()) + P(8) : 0;
        int need = SlimeLook ? P(66) + text + badge + P(26) : P(48) + text + badge + P(20);
        return Math.Max(P(SlimeLook ? SlimeSideW : FluentSideW), need);
    }

    // ───────── раскладка ─────────

    private void LayoutSoft()
    {
        int P(float v) => _root.Px(v);
        bool slime = SlimeLook;
        int W = _root.ClientSize.Width, H = _root.ClientSize.Height;
        int side = SidePxSoft(), barH = SoftBarH, L = SoftLeft(side), R = SoftRight;
        // у «Слаймового» кнопки с тенью: элемент на пиксель шире корпуса с боков и на 3 px ниже
        void Place(Control c, int x, int y, int w, int h)
        {
            if (slime) c.SetBounds(x - P(1), y - P(1), w + P(2), h + P(3));
            else c.SetBounds(x, y, w, h);
        }

        int ch = P(slime ? 40 : 32), y1 = P(slime ? 14 : 18);
        int sw = _split.PreferredWidth();
        _lastSplitW = sw;
        int splitX = R - sw;
        bool cmdWasIcon = _cmd.IconOnly, cmdHadKey = _cmd.ShowKeycap;
        _cmd.Height = slime ? ch + P(3) : ch;
        _cmd.IconOnly = false;
        _cmd.ShowKeycap = true;
        int cmdW = _cmd.PreferredWidth();
        int fw = _filter.PreferredWidth();
        _lastFilterW = fw;
        if (slime)
        {   // первая строка: поиск на всё оставшееся место, «Команды», «Играть»; вторая — вкладки-фильтры
            int Room() => splitX - P(10) - cmdW - P(12) - L;
            if (Room() < P(260)) { _cmd.ShowKeycap = false; cmdW = _cmd.PreferredWidth(); }
            if (Room() < P(200)) { _cmd.IconOnly = true; cmdW = ch; }
            int cmdX = splitX - P(10) - cmdW;
            Place(_cmd, cmdX, y1, cmdW, ch);
            _search.SetBounds(L, y1, Math.Max(P(120), cmdX - P(12) - L), ch);
            _filter.SetBounds(L, y1 + ch + P(10), Math.Max(0, Math.Min(fw, R - L)), P(38));
        }
        else
        {   // Fluent: поиск — в боковой панели под названием; «Команды» и «Играть» — справа от заголовка страницы,
            // вкладки — под ним (подпись первой — вровень с заголовком)
            int titleW = Theme.Measure("Каталог и обновления", Theme.Title).Width + P(16);
            int Room() => splitX - P(8) - cmdW - (L + titleW);
            if (Room() < 0) { _cmd.ShowKeycap = false; cmdW = _cmd.PreferredWidth(); }
            if (Room() < 0) { _cmd.IconOnly = true; cmdW = ch; }
            _cmd.SetBounds(splitX - P(8) - cmdW, y1, cmdW, ch);
            _search.SetBounds(P(12), P(64), side - P(24), P(34));
            _filter.SetBounds(L - P(12), P(62), Math.Max(0, Math.Min(fw, R - L + P(12))), P(38));
        }
        Place(_split, splitX, y1, sw, ch);
        if (_cmd.IconOnly != cmdWasIcon || _cmd.ShowKeycap != cmdHadKey) _cmd.Invalidate();
        PlaceRenderPicker();

        _side.SetBounds(0, 0, side, Math.Max(0, H - barH));
        _bar.SetBounds(0, H - barH, W, barH);

        // справа: баннер (если есть), список, ручка и нижняя панель
        int y = P(Theme.Spec.HeaderH);
        _lastBanner = _banner.Visible;
        if (_banner.Visible)
        {
            int bh = BannerHeight(R - L);
            _banner.SetBounds(L, y, R - L, bh);
            y += bh + P(10);
        }
        int bottom = H - barH - P(slime ? 8 : 12);
        int gripH = P(10);
        int dockH = DockHeightFor(bottom - y);
        int listBottom = Math.Max(y + P(60), bottom - dockH - gripH);
        _list.SetBounds(L - P(3), y - P(3), R - L + P(6), listBottom - (y - P(3)));   // у карточек поле 3 px под рамку и тень
        _grip.SetBounds(L, listBottom, R - L, gripH);
        _dock.SetBounds(L, listBottom + gripH, R - L, Math.Max(_dock.BarH, bottom - listBottom - gripH));

        var rk = (_root.ClientSize, _root.DeviceDpi);
        if (rk != _rootLayoutKey) { _rootLayoutKey = rk; _root.Invalidate(); }
    }

    // ───────── заголовок страницы (Fluent) ─────────

    private string PageTitle() => _filterKind == CatalogFilter.Catalog ? "Каталог и обновления" : "Моды";

    private string PageSub()
    {
        if (Theme.Design != DesignKind.Fluent) return "";
        int avail = _m.Available.Count;
        if (_filterKind != CatalogFilter.Catalog)
            return Theme.Count(_m.Mods.Count, "мод", "мода", "модов") + " в папке Mods" + (avail > 0 ? " · ещё " + avail + " можно установить" : "");
        int upd = CountIn(CatalogFilter.Updates);
        var parts = new List<string>();
        if (avail > 0) parts.Add("доступно для установки: " + avail);
        if (upd > 0) parts.Add("обновлений: " + upd);
        return parts.Count > 0 ? string.Join(" · ", parts) : "новых модов и версий не найдено";
    }

    private void PaintRootSoft(Graphics g, RootView v)
    {
        if (SlimeLook) return;   // у «Слаймового» название — в боковой панели, корень — только кремовый фон
        int P(float x) => v.Px(x);
        int L = SoftLeft(_side.Width > 0 ? _side.Width : SidePxSoft());
        int limit = (_cmd.Visible && _cmd.Width > 0 ? _cmd.Left : v.Width) - P(16);
        string title = PageTitle();
        int tw = Math.Min(Theme.Measure(title, Theme.Title).Width + P(4), Math.Max(0, limit - L));
        Theme.Text(g, title, Theme.Title, new Rectangle(L - P(1), P(14), tw, P(40)), Theme.Navy);
        string sub = PageSub();
        int sx = L + tw + P(12);
        if (sub.Length > 0 && sx + P(80) < limit)
            Theme.Text(g, sub, Theme.Small, new Rectangle(sx, P(25), limit - sx, P(24)), Theme.Hint);
    }

    // ───────── боковая панель ─────────

    /// Окружение: −1 — ещё не проверено, 0 — всё в порядке, 1 — есть предупреждения, 2 — есть ошибки.
    private int EnvState()
    {
        var env = _m.Env;
        if (env == null) return -1;
        if (env.Issues.Any(i => i.Level == CompatLevel.Error)) return 2;
        return env.Issues.Any(i => i.Level == CompatLevel.Warning) ? 1 : 0;
    }

    private string EnvTip() => _m.Env != null ? "Проверено: " + _m.Env.Summary + "\nЩелчок — вкладка «Проблемы»"
        : _m.CompatBusy ? "Идёт проверка окружения…" : "Окружение ещё не проверено";

    private void PaintSideSoft(Graphics g, PaintBox v)
    {
        int P(float x) => v.Px(x);
        float s = v.S;
        bool slime = SlimeLook;
        int W = v.Width, H = v.Height;

        // поверхность: у «Слаймового» — белая карточка с тенью, у Fluent — сам фон окна
        var card = slime ? new Rectangle(P(14), P(14), W - P(22), H - P(22)) : new Rectangle(0, 0, W, H);
        if (slime)
        {
            float rad = P(20);
            Theme.Shadow(g, card, rad, P(10), 22);
            Theme.FillRound(g, card, rad, Theme.Paper);
            Theme.StrokeRound(g, card, rad, Theme.Alpha(Theme.Line, 150), Math.Max(1f, s));
        }
        Color hoverBg = slime ? Theme.Mix(Theme.Paper, Theme.Beige, 0.55f) : Theme.Mix(Theme.Cream, Theme.Navy, 0.035f);
        Color selBg = slime ? Theme.TealTint : Theme.Mix(Theme.Cream, Theme.Navy, 0.065f);

        // ── название
        int top;
        if (slime)
        {
            Theme.Slime(g, new RectangleF(card.X + P(16), card.Y + P(16), P(46), P(40)), Theme.Pink);
            int hx = card.X + P(72), hw = Math.Max(0, card.Right - P(10) - hx);
            Theme.Text(g, "Менеджер модов", Theme.H2, new Rectangle(hx - P(1), card.Y + P(13), hw, P(28)), Theme.Navy);
            Theme.Text(g, "SLIME RANCHER 2  ·  v" + AppVersion, Theme.Section, new Rectangle(hx, card.Y + P(40), hw, P(16)), Theme.Hint);
            Theme.HGradient(g, new RectangleF(hx, card.Y + P(60), P(86), P(4)), Theme.Teal, Theme.Pink);
            top = card.Y + P(80);
        }
        else
        {
            Theme.Slime(g, new RectangleF(P(16), P(17), P(30), P(26)), Theme.Pink);
            Theme.Text(g, "Менеджер модов", Theme.Strong, new Rectangle(P(56), P(12), W - P(64), P(20)), Theme.Navy);
            Theme.Text(g, "Slime Rancher 2 · v" + AppVersion, Theme.Small, new Rectangle(P(56), P(31), W - P(64), P(16)), Theme.Hint);
            top = P(106);   // под полем поиска — оно отдельный элемент поверх панели
        }

        int rowX = slime ? card.X + P(10) : P(8), rowR = slime ? card.Right - P(10) : W - P(8);
        int rowH = P(slime ? 36 : 34), pitch = P(slime ? 40 : 36), headH = P(slime ? 28 : 30);
        int tx = rowX + P(slime ? 42 : 40), countR = rowR - P(slime ? 8 : 12);
        float rowRad = slime ? rowH / 2f : P(4);
        int bottomLimit = slime ? card.Bottom - P(10) : H - P(10);

        void Head(string text, int y)
        {
            var hr = new Rectangle(rowX + P(slime ? 12 : 8), y, P(170), headH);
            if (slime) Theme.Text(g, text.ToUpperInvariant(), Theme.Section, hr, Theme.Hint);
            else Theme.Text(g, text, Theme.SmallBold, hr, Theme.NavySoft);
        }
        // выбранный пункт Fluent — короткая акцентная черта слева, как в навигации Windows 11
        void Accent(Rectangle r) => Theme.FillRound(g, new RectangleF(r.X, r.Y + (r.Height - P(16)) / 2f, P(3), P(16)), P(1.5f), Theme.Teal);
        // счётчик справа: у «Слаймового» и выделенных — в капсуле, иначе — серым числом
        int Count(string text, Rectangle r, bool sel, Color? lit = null)
        {
            if (text.Length == 0) return 0;
            if (slime || lit.HasValue)
            {
                int bw = SoftBadgeW(text), bh = P(18);
                var br = new Rectangle(countR - bw, r.Y + (r.Height - bh) / 2, bw, bh);
                Theme.FillRound(g, br, bh / 2f, lit ?? (sel ? Theme.Paper : Theme.Beige));
                Theme.Text(g, text, Theme.SmallBold, br, lit.HasValue ? Theme.OnAccent : sel ? Theme.TealText : Theme.NavySoft, TextFormatFlags.HorizontalCenter);
                return bw;
            }
            int cw = Theme.Measure(text, Theme.Small).Width;
            Theme.Text(g, text, Theme.Small, new Rectangle(countR - cw - P(2), r.Y, cw + P(2), r.Height), Theme.Hint, TextFormatFlags.Right);
            return cw;
        }

        // ── наборы
        Head("Наборы", top);
        int pd = P(24);
        var plus = new Rectangle(rowR - pd - P(4), top + (headH - pd) / 2, pd, pd);
        bool hp = v.Hot("add");
        if (slime)
        {
            Theme.FillRound(g, plus, pd / 2f, hp ? Theme.TealTint : Theme.Paper);
            Theme.StrokeRound(g, plus, pd / 2f, hp ? Theme.Teal : Theme.Line, Math.Max(1f, s));
        }
        else if (hp) Theme.FillRound(g, plus, P(4), hoverBg);
        Theme.Icon(g, VIcon.Plus, new RectangleF(plus.X + P(7), plus.Y + P(7), P(10), P(10)), hp ? (slime ? Theme.TealText : Theme.Navy) : Theme.NavySoft, Math.Max(1f, 1.4f * s));
        v.Zone(plus, "add", SaveProfileAs, "Сохранить текущий выбор модов как новый набор");

        var profiles = _m.Cfg.Profiles;
        var (shown, modified) = SideProfile();
        var secs = SideSections(v);
        int y = top + headH;
        // наборы не помещаются вместе с разделами — последние уходят в «Ещё…»
        int sectionsH = P(12) + headH + secs.Length * pitch;
        int fit = Math.Max(1, (bottomLimit - y - sectionsH) / pitch);
        int rows = profiles.Count <= fit ? profiles.Count : Math.Max(0, fit - 1);
        for (int i = 0; i < rows; i++)
        {
            var p = profiles[i];
            var r = new Rectangle(rowX, y, rowR - rowX, rowH);
            string key = "p:" + i;
            bool hl = p.Name == shown, hot = v.Hot(key);
            if (hl) Theme.FillRound(g, r, rowRad, selBg);
            else if (hot) Theme.FillRound(g, r, rowRad, hoverBg);
            if (hl && !slime) Accent(r);
            float cy = r.Y + r.Height / 2f;
            if (slime)
            {   // у набора — слаймик своего цвета: «Все моды» — бирюзовый, пустой (ваниль) — серый
                Color sc = p.All ? Theme.Teal : (p.Mods == null || p.Mods.Count == 0) ? Theme.LineDark : ProfileDot(p);
                Theme.Slime(g, new RectangleF(r.X + P(9), cy - P(9), P(22), P(19)), sc);
            }
            else Theme.Dot(g, r.X + P(20), cy, P(4), hl ? Theme.Teal : ProfileDot(p));
            int cw = Count(ProfileCount(p).ToString(), r, hl);
            int maxR = countR - cw - P(8);
            Font f = hl ? Theme.ButtonFont : Theme.Body;
            int nameW = Theme.Measure(p.Name, f).Width + P(2);
            int bw = hl && modified ? Theme.Measure("изменён", Theme.TagFont).Width + P(12) : 0;
            if (bw > 0 && maxR - tx - bw - P(9) < Math.Min(nameW, P(50))) bw = 0;
            int tw = Math.Max(0, Math.Min(nameW, maxR - tx - (bw > 0 ? bw + P(9) : 0)));
            Theme.Text(g, p.Name, f, new Rectangle(tx, r.Y, tw, r.Height), hl && slime ? Theme.TealText : Theme.Navy);
            if (bw > 0)
            {
                var br = new Rectangle(tx + tw + P(7), r.Y + (r.Height - P(16)) / 2, bw, P(16));
                Theme.FillRound(g, br, br.Height / 2f, Theme.OrangeTint);
                Theme.Text(g, "изменён", Theme.TagFont, br, Theme.OrangeText, TextFormatFlags.HorizontalCenter);
            }
            var pp = p;
            v.Zone(r, key, () => PickProfile(pp), ProfileTip(p, hl && modified), pt => ProfileMenu(pp, pt));
            y += pitch;
        }
        if (rows < profiles.Count || profiles.Count == 0)
        {
            bool more = profiles.Count > 0;
            var r = new Rectangle(rowX, y, rowR - rowX, rowH);
            string key = more ? "p:more" : "p:new";
            bool hot = v.Hot(key);
            if (hot) Theme.FillRound(g, r, rowRad, hoverBg);
            string text = more ? "Ещё " + Theme.Count(profiles.Count - rows, "набор", "набора", "наборов") + "…" : "Сохранить выбор как набор…";
            Theme.Text(g, text, Theme.Body, new Rectangle(more ? tx : rowX + P(slime ? 14 : 12), r.Y, countR - tx, r.Height), hot ? Theme.Navy : Theme.Hint);
            int from = rows;
            if (more) v.Zone(r, key, () => MoreProfilesMenu(from, new Point(r.Right + P(4), r.Y)), "Остальные наборы");
            else v.Zone(r, key, SaveProfileAs, "Набор — список включённых модов: переключайтесь между ними одним щелчком");
            y += pitch;
        }

        // ── разделы
        y += P(12);
        Head("Разделы", y);
        y += headH;
        int secBottom = y;
        var tints = new (Color Bg, Color Fg)[]
            { (Theme.TealTint, Theme.TealText), (Theme.PinkTint, Theme.Pink), (Theme.OrangeTint, Theme.OrangeText), (Theme.BlueTint, Theme.BlueText), (Theme.PurpleTint, Theme.PurpleText) };
        for (int i = 0; i < secs.Length; i++)
        {
            var sc = secs[i];
            var r = new Rectangle(rowX, y, rowR - rowX, rowH);
            bool hot = v.Hot(sc.Key);
            if (sc.Sel) Theme.FillRound(g, r, rowRad, selBg);
            else if (hot) Theme.FillRound(g, r, rowRad, hoverBg);
            if (sc.Sel && !slime) Accent(r);
            float cy = r.Y + r.Height / 2f;
            if (slime)
            {   // значок в цветном кружке
                var (tb, tf) = tints[i % tints.Length];
                int d = P(26);
                var cr = new RectangleF(r.X + P(5), cy - d / 2f, d, d);
                Theme.FillRound(g, cr, d / 2f, sc.Sel ? Theme.Paper : tb);
                Theme.Icon(g, sc.Icon, new RectangleF(cr.X + (d - P(13)) / 2f, cy - P(6.5f), P(13), P(13)), tf);
            }
            else Theme.Icon(g, sc.Icon, new RectangleF(r.X + P(13), cy - P(7), P(14), P(14)), sc.Sel || hot ? Theme.Navy : Theme.NavySoft);
            Color? lit = sc.Key == "s:cat" && sc.Count.Length > 0 ? (slime ? Theme.Pink : Theme.Teal) : null;
            int cw = Count(sc.Count, r, sc.Sel, lit);
            int textR = cw > 0 ? countR - cw - P(6) : countR;
            // выбранный раздел — полужирным, если так помещается (иначе обычным: лучше целиком, чем с «…»)
            Font sf = sc.Sel && Theme.Measure(sc.Text, Theme.ButtonFont).Width <= textR - tx ? Theme.ButtonFont : Theme.Body;
            Theme.Text(g, sc.Text, sf, new Rectangle(tx, r.Y, Math.Max(0, textR - tx), r.Height), sc.Sel && slime ? Theme.TealText : Theme.Navy);
            var rr = r;
            var click = sc.Click; var right = sc.Right;
            v.Zone(r, sc.Key, () => click(rr), sc.Tip, right == null ? null : _ => right(rr));
            secBottom = r.Bottom;
            y += pitch;
        }

        // ── окружение — карточка внизу, если не наезжает на разделы; мало места по высоте —
        //    сначала убираются нижние строки (всё есть в подсказке), потом карточка целиком
        var lines = EnvLines();
        int lh = P(18), pad = P(12);
        Rectangle EnvRect(int n)
        {
            int eh = pad + P(20) + n * lh + pad - P(4);
            return slime ? new Rectangle(card.X + P(10), card.Bottom - P(10) - eh, card.Width - P(20), eh)
                         : new Rectangle(P(12), H - P(12) - eh, W - P(24), eh);
        }
        int envN = lines.Count;
        while (envN > 0 && EnvRect(envN).Y < secBottom + P(14)) envN--;
        if (envN == 0) return;
        var er = EnvRect(envN);
        float erad = slime ? P(14) : P(6);
        if (slime) Theme.FillRound(g, er, erad, Theme.Cream);
        else
        {
            Theme.FillRound(g, er, erad, Theme.Paper);
            Theme.StrokeRound(g, er, erad, Theme.Line, Math.Max(1f, s));
        }
        int st = EnvState();
        Color dot = st switch { 2 => Theme.Coral, 1 => Theme.Orange, 0 => Theme.Teal, _ => Theme.Hint };
        int ex = er.X + pad, ew = er.Width - 2 * pad;
        Theme.Dot(g, ex + P(4), er.Y + pad + P(10), P(4), dot);
        Theme.Text(g, "Окружение", Theme.SmallBold, new Rectangle(ex + P(14), er.Y + pad, ew - P(14), P(20)), Theme.Navy);
        for (int j = 0; j < envN; j++)
            Theme.Text(g, lines[j], Theme.Small, new Rectangle(ex, er.Y + pad + P(20) + j * lh, ew, lh), Theme.NavySoft);
        v.Zone(er, "env", () => ShowDockTab(DockTab.Problems), EnvTip(), null, false);
    }

    // ───────── строка состояния «Слаймового» стиля — капсулы ─────────

    private void PaintBarSlime(Graphics g, PaintBox v)
    {
        int P(float x) => v.Px(x);
        float s = v.S;
        int W = v.Width, H = v.Height, ch = P(26), cy = (H - ch) / 2;
        var font = Theme.Small;
        int x = P(14);
        Rectangle Chip(int w, Color bg, Color? border)
        {
            var r = new Rectangle(x, cy, w, ch);
            Theme.FillRound(g, r, ch / 2f, bg);
            if (border is { } b) Theme.StrokeRound(g, r, ch / 2f, b, Math.Max(1f, s));
            return r;
        }

        // игра
        bool run = _m.GameRunning;
        string game = run ? "Игра запущена" + (_m.GameStarted.HasValue ? " · " + Theme.Ago(DateTime.Now - _m.GameStarted.Value) : "") : "Игра не запущена";
        int gw = P(26) + Theme.Measure(game, font).Width + P(12);
        var gr = Chip(gw, run ? Theme.TealTint : Theme.Paper, run ? null : Theme.Line);
        if (run) Theme.Dot(g, gr.X + P(13), gr.Y + ch / 2f, P(6.5f), Theme.Alpha(Theme.Teal, 60));
        Theme.Dot(g, gr.X + P(13), gr.Y + ch / 2f, P(3.5f), run ? Theme.Teal : Theme.Hint);
        Theme.Text(g, game, font, new Rectangle(gr.X + P(24), gr.Y, gw - P(28), ch), run ? Theme.TealText : Theme.NavySoft);
        v.Zone(gr, "game", null, run ? "Пока игра запущена, DLL модов заняты — изменения можно только запланировать" : "Можно менять моды и запускать игру", null, false);
        x = gr.Right + P(8);

        // сколько включено
        int all = _m.Mods.Count;
        string en = all == 0 ? "Модов нет — перетащите .dll или .zip в окно" : $"Включено **{_m.OnCount}** из {all}";
        int ew = Theme.Rich(null, en, font, new Rectangle(0, 0, 10000, ch), Theme.NavySoft, s) + P(26);

        // третья капсула: сообщение (тост, предупреждение, «сохраняю…») или не применённые изменения
        bool msg = _barMessage && _status.Message.Length > 0;
        var diff = msg ? new Dictionary<string, bool>() : Diff();
        bool planned = diff.Count > 0 && !Dirty();
        string third = !msg && diff.Count > 0
            ? (planned ? "Запланировано: " : run ? "Запланировать: " : "Не применено: ") + "**" + Theme.Count(diff.Count, "изменение", "изменения", "изменений") + "**" + (planned ? "" : " · Ctrl+S")
            : "";
        var acts = msg ? _status.Actions.ToList() : new List<(string Text, Action Act)>();
        int ActW(string t) => Theme.Measure(t, Theme.SmallBold).Width + P(20);
        int actsW = acts.Sum(a => ActW(a.Text) + P(6));
        int thirdW = msg ? P(32) + Theme.Measure(_status.Message, font).Width + P(12) + (acts.Count > 0 ? P(6) + actsW : 0)
            : third.Length > 0 ? P(26) + Theme.Rich(null, third, font, new Rectangle(0, 0, 10000, ch), Theme.NavySoft, s) + P(12) : 0;

        // подсказки справа — если всё помещается
        int right = W - P(16);
        int hw = Theme.Measure(BarHints, font).Width;
        int limit = right;
        if (x + ew + (thirdW > 0 ? P(8) + thirdW : 0) + P(24) + hw <= right)
        {
            Theme.Text(g, BarHints, font, new Rectangle(right - hw - P(2), 0, hw + P(2), H), Theme.Hint, TextFormatFlags.Right);
            limit = right - hw - P(24);
        }

        int ewc = Math.Min(ew, Math.Max(0, limit - x));
        if (ewc < P(60)) return;
        var er = Chip(ewc, Theme.Paper, Theme.Line);
        Theme.Rich(g, en, font, new Rectangle(er.X + P(13), er.Y, ewc - P(20), ch), Theme.NavySoft, s, Theme.Navy);
        if (all > 0) v.Zone(er, "on", null, "Отмечено модов: " + _m.OnCount + " из " + all + ". Пробел — включить или выключить выбранный", null, false);
        x = er.Right + P(8);
        if (thirdW == 0 || x >= limit - P(60)) return;

        if (msg)
        {
            var tone = _status.Tone;
            var (tb, _, _) = StatusLine.Colors(tone);
            int mw0 = Theme.Measure(_status.Message, font).Width;
            int chipW = Math.Min(P(32) + mw0 + P(12), limit - x - (acts.Count > 0 ? P(6) + ActW(acts[0].Text) : 0));
            if (chipW < P(60)) return;
            var mr = Chip(chipW, tone == Tone.Neutral ? Theme.Paper : tb, tone == Tone.Neutral ? Theme.Line : null);
            int isz = P(13);
            Theme.Icon(g, ToneIcon(tone), new RectangleF(mr.X + P(10), mr.Y + (ch - isz) / 2f, isz, isz), ToneColor(tone));
            int mw = chipW - P(30) - P(10);
            Theme.Text(g, _status.Message, font, new Rectangle(mr.X + P(30), mr.Y, mw, ch), Theme.Navy);
            if (mw < mw0) v.Zone(mr, "msg", null, _status.Message, null, false);
            x = mr.Right + P(6);
            for (int i = 0; i < acts.Count; i++)
            {
                int aw = ActW(acts[i].Text);
                if (x + aw > limit) break;
                var ar = new Rectangle(x, cy, aw, ch);
                string key = "act:" + i;
                bool hot = v.Hot(key), dn = v.Pressed(key);
                Theme.FillRound(g, ar, ch / 2f, dn ? Theme.Mix(Theme.TealDark, Theme.Navy, 0.12f) : hot ? Theme.TealDark : Theme.Teal);
                Theme.Text(g, acts[i].Text, Theme.SmallBold, ar, Theme.OnAccent, TextFormatFlags.HorizontalCenter);
                var act = acts[i].Act;
                v.Zone(ar, key, () => act());
                x += aw + P(6);
            }
            return;
        }
        int tw = Math.Min(thirdW, limit - x);
        var dr = Chip(tw, planned ? Theme.BlueTint : Theme.OrangeTint, null);
        Theme.Dot(g, dr.X + P(13), dr.Y + ch / 2f, P(3.5f), planned ? Theme.Blue : Theme.Orange);
        Theme.Rich(g, third, font, new Rectangle(dr.X + P(24), dr.Y, tw - P(30), ch), planned ? Theme.BlueText : Theme.OrangeText, s, Theme.Navy);
        v.Zone(dr, "diff", () => ShowDockTab(DockTab.Changes),
            (planned ? "Запланировано: " : "Изменения: ") + DiffText(diff) + "\nЩелчок — вкладка «Изменения»" + (planned ? "" : "; Ctrl+S — " + (run ? "запланировать" : "применить")));
    }
}
