// Designs.cs — три стиля оформления лаунчера. У всех одно устройство окна: слева наборы, разделы и окружение, сверху
// вкладки-фильтры, «Команды» (Ctrl+K) и «Играть ▾», в середине список модов, под ним нижняя панель (проблемы, хуки,
// журнал, изменения, подробности) и строка состояния. Стиль = палитра + вид списка + размеры, скругления и пластика:
//  • «Компактный» (вариант C, по умолчанию) — тёмное плоское окно как у редактора кода, таблица (~29 px строка,
//    сортировка по колонкам);
//  • «Fluent» (вариант A) — светлое окно в духе «Параметров» Windows 11: поиск и навигация слева, заголовок страницы,
//    вкладки с акцентной чертой, белые карточки (~58 px) с описанием и бейджами (MainForm.Shell.cs);
//  • «Слаймовый» (вариант B) — кремовое окно в цветах игры: «парящая» боковая панель со слаймом, капсулы (~76 px),
//    бирюза и розовый, строка состояния из капсул (MainForm.Shell.cs).
// Выбор хранится в UserData\ModManager\ui.json и применяется сразу, без перезапуска (Theme.Use + Theme.Retint).
using System.Collections.Generic;
using System.Drawing;
using SR2ModManager.Core;

namespace SR2ModManager.Launcher;

/// Цвета по ролям. Имена ролей исторические (из светлой «слаймовой» палитры): Cream — фон окна, Paper — поверхность
/// карточек и панелей, Beige — заливка второстепенных элементов, Line — рамки, Navy/NavySoft/Hint — текст трёх уровней,
/// Teal — акцент (включено, главная кнопка, выбор), Coral/Orange/Blue — ошибка/предупреждение/сведения, *Tint — их
/// мягкий фон, *Text — текст поверх мягкого фона.
internal sealed class Palette
{
    public bool Dark;
    public Color Cream, Paper, Beige, BeigeDark, Line, LineDark, Navy, NavySoft, Hint,
        Teal, TealDark, Coral, Orange, Pink, Blue,
        TealTint, CoralTint, OrangeTint, BlueTint, PinkTint, Selected,
        TealText, CoralText, OrangeText, BlueText,
        Mark, ToggleOff, PurpleTint, PurpleText,
        RowAlt, RowHover, HeaderBg,
        Green,      // «совместим» в таблице
        OnAccent;   // текст и значки поверх акцентной заливки (главная кнопка, отметка)
}

internal enum ListMode { Table, Cards, Capsules }

internal sealed class DesignSpec
{
    public DesignKind Kind;
    public string Name = "", Variant = "", Tagline = "", Description = "";
    public Palette Palette = null!;
    public ListMode Mode;
    public int RowH;          // строка таблицы / карточка, px при 96 dpi
    public int Gap;           // между карточками
    public int CardRadius;    // скругление карточек (у капсул — половина высоты)
    public int PanelRadius;   // панели: подробности, вкладки, пустой список
    public int CtrlRadius;    // кнопки, поле поиска, сегменты; −1 — капсула
    public int HeaderH;       // верхняя полоса над списком: фильтр, вкладки, «Команды», «Играть»
    public bool Shadows;      // мягкие тени у выбранной карточки и главной кнопки
    /// Плоское окно как у редактора кода: верхняя панель, боковая панель с наборами и разделами, таблица и нижняя
    /// панель без рамок и скруглений, строка состояния внизу («Компактный»).
    public bool Flat;
}

internal static class Designs
{
    private static Color H(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    // «Компактный»: строки таблицы — Cream, панели вокруг (верх, бок, шапка таблицы, нижняя панель, строка состояния) —
    // HeaderBg. Цвета ролей не совпадают между собой — иначе Theme.Retint при смене стиля спутал бы роли.
    public static readonly Palette DarkPalette = new()
    {
        Dark = true,
        Cream = H(0x15171C), Paper = H(0x1C1F26), Beige = H(0x20242B), BeigeDark = H(0x2E333C), Line = H(0x2A2F38), LineDark = H(0x363C47),
        Navy = H(0xE7E9ED), NavySoft = H(0xAEB5C0), Hint = H(0x7D8592),
        Teal = H(0x3DD6B0), TealDark = H(0x5FE0C0), Coral = H(0xFF7A7A), Orange = H(0xF2C14E), Pink = H(0xFF9BB5), Blue = H(0x7FB5FF),
        TealTint = H(0x1F3A34), CoralTint = H(0x3D2328), OrangeTint = H(0x3A3220), BlueTint = H(0x1D2C42), PinkTint = H(0x3A2430), Selected = H(0x1F3A35),
        TealText = H(0x3DD6B0), CoralText = H(0xFF7A7A), OrangeText = H(0xF2C14E), BlueText = H(0x7FB5FF),
        Mark = H(0x6E5A16), ToggleOff = H(0x3B414D), PurpleTint = H(0x33213B), PurpleText = H(0xE2A8F2),
        RowAlt = H(0x15171C), RowHover = H(0x1A1D23), HeaderBg = H(0x1B1E24),
        Green = H(0x5BD08A), OnAccent = H(0x06261D),
    };

    public static readonly Palette FluentPalette = new()
    {
        Cream = H(0xF3F3F3), Paper = H(0xFFFFFF), Beige = H(0xEDEDED), BeigeDark = H(0xE0E0E0), Line = H(0xE5E5E5), LineDark = H(0xCDCDCD),
        Navy = H(0x1B1B1B), NavySoft = H(0x5C5C5C), Hint = H(0x8A8A8A),
        Teal = H(0x005FB8), TealDark = H(0x004C93), Coral = H(0xC42B1C), Orange = H(0xDB8A00), Pink = H(0xE3008C), Blue = H(0x6B5FD3),
        TealTint = H(0xE5F0FA), CoralTint = H(0xFDE7E9), OrangeTint = H(0xFFF4CE), BlueTint = H(0xEEECFC), PinkTint = H(0xFCE8F3), Selected = H(0xEBF3FB),
        TealText = H(0x00539F), CoralText = H(0xB3261A), OrangeText = H(0x8A5300), BlueText = H(0x4B3FB0),
        Mark = H(0xFFF1A1), ToggleOff = H(0xC6C6C6), PurpleTint = H(0xF3E8FD), PurpleText = H(0x8A3FB8),
        RowAlt = H(0xFAFAFA), RowHover = H(0xF5F5F5), HeaderBg = H(0xF7F7F7),
        Green = H(0x0F7B0F), OnAccent = H(0xFFFFFF),
    };

    public static readonly Palette SlimePalette = new()
    {
        Cream = H(0xF7F3EA), Paper = H(0xFFFFFF), Beige = H(0xEFE6D6), BeigeDark = H(0xE5D9C3), Line = H(0xE2D8C4), LineDark = H(0xCBBDA3),
        Navy = H(0x1F2A44), NavySoft = H(0x56607A), Hint = H(0x8A8F9C),
        Teal = H(0x22C3A6), TealDark = H(0x17A98F), Coral = H(0xFF7A6B), Orange = H(0xFFA445), Pink = H(0xFF6FAE), Blue = H(0x4C8DF6),
        TealTint = H(0xE2F6F1), CoralTint = H(0xFFEBE7), OrangeTint = H(0xFFF0DB), BlueTint = H(0xE7EFFE), PinkTint = H(0xFFE8F2), Selected = H(0xF2FBF8),
        TealText = H(0x0C7A66), CoralText = H(0xC23B2A), OrangeText = H(0xA45A05), BlueText = H(0x2B5FC4),
        Mark = H(0xFFE38A), ToggleOff = H(0xD9CDB6), PurpleTint = H(0xF6E6F4), PurpleText = H(0x9C3D8E),
        RowAlt = H(0xFBF8F1), RowHover = H(0xF3EEE3), HeaderBg = H(0xEFE6D6),
        Green = H(0x0C7A66), OnAccent = H(0xFFFFFF),
    };

    public static readonly DesignSpec Compact = new()
    {
        Kind = DesignKind.Compact, Name = "Компактный", Variant = "C", Tagline = "Тёмная таблица",
        Description = "Тёмное окно как у редактора кода: наборы и разделы слева, плотная таблица (версия, автор, совместимость, хуки Harmony, обновления, размер), проблемы и изменения внизу.",
        Palette = DarkPalette, Mode = ListMode.Table, RowH = 29, Gap = 0, CardRadius = 0, PanelRadius = 6, CtrlRadius = 6, HeaderH = 46, Shadows = false, Flat = true,
    };

    public static readonly DesignSpec Fluent = new()
    {
        Kind = DesignKind.Fluent, Name = "Fluent", Variant = "A", Tagline = "Светлый, как Windows 11",
        Description = "Светлое окно в духе «Параметров» Windows 11: поиск и разделы слева, крупный заголовок страницы, вкладки с акцентной чертой, белые карточки с бейджами.",
        Palette = FluentPalette, Mode = ListMode.Cards, RowH = 58, Gap = 4, CardRadius = 6, PanelRadius = 8, CtrlRadius = 4, HeaderH = 104, Shadows = false,
    };

    public static readonly DesignSpec Slime = new()
    {
        Kind = DesignKind.Slime, Name = "Слаймовый", Variant = "B", Tagline = "Капсулы в цветах игры",
        Description = "Кремовое окно в цветах Slime Rancher 2: «парящая» боковая панель со слаймом и наборами-слаймиками, капсулы модов, бирюза и розовый, строка состояния из капсул.",
        Palette = SlimePalette, Mode = ListMode.Capsules, RowH = 76, Gap = 8, CardRadius = 38, PanelRadius = 16, CtrlRadius = -1, HeaderH = 110, Shadows = true,
    };

    public static readonly IReadOnlyList<DesignSpec> All = new[] { Compact, Fluent, Slime };

    public static DesignSpec Get(DesignKind k) => k switch { DesignKind.Fluent => Fluent, DesignKind.Slime => Slime, _ => Compact };

    public static DesignSpec Current => Get(Theme.Design);

    /// Следующий стиль по кругу (Ctrl+Shift+D).
    public static DesignKind Next(DesignKind k) => k switch { DesignKind.Compact => DesignKind.Fluent, DesignKind.Fluent => DesignKind.Slime, _ => DesignKind.Compact };
}
