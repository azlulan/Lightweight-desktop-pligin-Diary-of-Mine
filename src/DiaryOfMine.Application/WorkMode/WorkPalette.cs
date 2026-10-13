using DiaryOfMine.Application.Settings;

namespace DiaryOfMine.Application.WorkMode;

public static class WorkPalette
{
    public const int SlotCount = 6;
    public const string Prefix = "work-slot-";

    public static string GetSlotKey(int slot) =>
        slot is >= 1 and <= SlotCount ? $"{Prefix}{slot}" : $"{Prefix}1";

    public static int GetSlotNumber(string? key)
    {
        if (key is not null && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[Prefix.Length..], out var slot)
            && slot is >= 1 and <= SlotCount)
            return slot;
        return 1;
    }

    public static string ResolveColor(AppSettings settings, string? key) => GetSlotNumber(key) switch
    {
        2 => settings.WorkPaletteColor2,
        3 => settings.WorkPaletteColor3,
        4 => settings.WorkPaletteColor4,
        5 => settings.WorkPaletteColor5,
        6 => settings.WorkPaletteColor6,
        _ => settings.WorkPaletteColor1
    };
}
