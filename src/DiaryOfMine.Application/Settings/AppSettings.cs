using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaryOfMine.Application.Settings;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    public string CacheDirectory { get; init; } = string.Empty;
    public string LogDirectory { get; init; } = string.Empty;
    public bool AutoFloatAndSleepWhenInactive { get; init; }
    public bool PinFloatingWindow { get; init; }
    public bool StartWithWindows { get; init; }
    public bool EnableNotifications { get; init; }
    public bool EnableClassNotifications { get; init; } = true;
    public bool EnableKeyNotifications { get; init; } = true;
    public bool EnableIdeaNotifications { get; init; } = true;
    public double FloatingWindowLeft { get; init; } = 32;
    public double FloatingWindowTop { get; init; } = 120;
    public string ExpandHotKey { get; init; } = "F8";
    public string ClassPaletteColor1 { get; init; } = "#DDF4E8";
    public string ClassPaletteColor2 { get; init; } = "#FBE1E8";
    public string ClassPaletteColor3 { get; init; } = "#DCEEF7";
    public string ClassPaletteColor4 { get; init; } = "#FBF3CF";
    public string ClassPaletteColor5 { get; init; } = "#EAE2F8";
    public string ClassPaletteColor6 { get; init; } = "#FFE5D9";
    public string KeyPaletteColor1 { get; init; } = "#63B89A";
    public string KeyPaletteColor2 { get; init; } = "#D98298";
    public string KeyPaletteColor3 { get; init; } = "#6CAEC8";
    public string KeyPaletteColor4 { get; init; } = "#C4A643";
    public string KeyPaletteColor5 { get; init; } = "#927AC1";
    public string KeyPaletteColor6 { get; init; } = "#D88969";
    public string WorkPaletteColor1 { get; init; } = "#95D8C4";
    public string WorkPaletteColor2 { get; init; } = "#AFCBF2";
    public string WorkPaletteColor3 { get; init; } = "#C8B7E8";
    public string WorkPaletteColor4 { get; init; } = "#F2B8C6";
    public string WorkPaletteColor5 { get; init; } = "#F4D58D";
    public string WorkPaletteColor6 { get; init; } = "#A9D9E8";
}
