using DiaryOfMine.Application.Settings;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Calendar;

public static class CalendarPalette
{
    public const int SlotCount = 6;

    private static readonly string[] SlotKeys =
        ["palette-1", "palette-2", "palette-3", "palette-4", "palette-5", "palette-6"];

    public static string GetSlotKey(int index)
    {
        if (index is < 0 or >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return SlotKeys[index];
    }

    public static int GetSlotNumber(string colorKey) => ResolveSlot(colorKey) + 1;

    public static string ResolveColor(
        AppSettings settings, CalendarItemKind kind, string colorKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var slot = ResolveSlot(colorKey);
        return kind == CalendarItemKind.Key
            ? slot switch
            {
                1 => settings.KeyPaletteColor2,
                2 => settings.KeyPaletteColor3,
                3 => settings.KeyPaletteColor4,
                4 => settings.KeyPaletteColor5,
                5 => settings.KeyPaletteColor6,
                _ => settings.KeyPaletteColor1
            }
            : slot switch
            {
                1 => settings.ClassPaletteColor2,
                2 => settings.ClassPaletteColor3,
                3 => settings.ClassPaletteColor4,
                4 => settings.ClassPaletteColor5,
                5 => settings.ClassPaletteColor6,
                _ => settings.ClassPaletteColor1
            };
    }

    private static int ResolveSlot(string colorKey) => colorKey switch
    {
        "palette-2" or "coral" => 1,
        "palette-3" or "sky" => 2,
        "palette-4" or "sand" => 3,
        "palette-5" or "lavender" => 4,
        "palette-6" or "plum" => 5,
        _ => 0
    };
}
