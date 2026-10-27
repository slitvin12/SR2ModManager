// Designs.cs — три стиля оформления лаунчера. Стиль = палитра + вид списка модов + размеры и скругления:
//  • «Компактный» (вариант C, по умолчанию) — тёмная плотная таблица (~30 px строка), сортировка по колонкам;
//  • «Fluent» (вариант A) — светлые карточки средней плотности (~58 px) с описанием и бейджами статуса;
//  • «Слаймовый» (вариант B) — капсулы в цветах игры: кремовый фон, тёмно-синий текст, бирюза и розовый слайм.
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
        RowAlt, RowHover, HeaderBg;
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
    public int HeaderH;       // шапка окна
    public bool Shadows;      // мягкие тени у выбранной карточки и главной кнопки
}

internal static class Designs
{
    private static Color H(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static readonly Palette DarkPalette = new()
    {
        Dark = true,
        Cream = H(0x15171C), Paper = H(0x1C1F26), Beige = H(0x272B34), BeigeDark = H(0x323743), Line = H(0x2B2F39), LineDark = H(0x3F4552),
        Navy = H(0xE6E9F0), NavySoft = H(0xA4ACBB), Hint = H(0x767F90),
        Teal = H(0x1AAE93), TealDark = H(0x24C7A8), Coral = H(0xFF6B5F), Orange = H(0xF2A541), Pink = H(0xFF6FAE), Blue = H(0x5B95F5),
        TealTint = H(0x16332F), CoralTint = H(0x3B2125), OrangeTint = H(0x382D1C), BlueTint = H(0x1B2A45), PinkTint = H(0x3A1F31), Selected = H(0x1B3431),
        TealText = H(0x62E0C7), CoralText = H(0xFF9389), OrangeText = H(0xF6BE6E), BlueText = H(0x8FB6FF),
        Mark = H(0x6E5A16), ToggleOff = H(0x3B414D), PurpleTint = H(0x33213B), PurpleText = H(0xE2A8F2),
        RowAlt = H(0x1F222A), RowHover = H(0x262A34), HeaderBg = H(0x181B21),
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
    };

    public static readonly DesignSpec Compact = new()
    {
        Kind = DesignKind.Compact, Name = "Компактный", Variant = "C", Tagline = "Тёмная таблица",
        Description = "Плотная таблица ~30 px: версия, автор, совместимость, хуки Harmony, обновления и размер. Щелчок по заголовку — сортировка.",
        Palette = DarkPalette, Mode = ListMode.Table, RowH = 30, Gap = 0, CardRadius = 0, PanelRadius = 8, CtrlRadius = 6, HeaderH = 62, Shadows = false,
    };

    public static readonly DesignSpec Fluent = new()
    {
        Kind = DesignKind.Fluent, Name = "Fluent", Variant = "A", Tagline = "Светлые карточки",
        Description = "Карточки средней плотности ~58 px в стиле Windows 11: описание, бейджи совместимости и обновлений.",
        Palette = FluentPalette, Mode = ListMode.Cards, RowH = 58, Gap = 4, CardRadius = 6, PanelRadius = 8, CtrlRadius = 4, HeaderH = 74, Shadows = false,
    };

    public static readonly DesignSpec Slime = new()
    {
        Kind = DesignKind.Slime, Name = "Слаймовый", Variant = "B", Tagline = "Капсулы в цветах игры",
        Description = "Скруглённые капсулы в цветах Slime Rancher 2: кремовый фон, бирюза и розовый слайм.",
        Palette = SlimePalette, Mode = ListMode.Capsules, RowH = 76, Gap = 8, CardRadius = 38, PanelRadius = 16, CtrlRadius = -1, HeaderH = 92, Shadows = true,
    };

    public static readonly IReadOnlyList<DesignSpec> All = new[] { Compact, Fluent, Slime };

    public static DesignSpec Get(DesignKind k) => k switch { DesignKind.Fluent => Fluent, DesignKind.Slime => Slime, _ => Compact };

    public static DesignSpec Current => Get(Theme.Design);

    /// Следующий стиль по кругу (Ctrl+Shift+D).
    public static DesignKind Next(DesignKind k) => k switch { DesignKind.Compact => DesignKind.Fluent, DesignKind.Fluent => DesignKind.Slime, _ => DesignKind.Compact };
}
