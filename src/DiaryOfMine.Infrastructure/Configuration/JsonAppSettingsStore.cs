using System.Text.Json;
using System.Globalization;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Infrastructure.Storage;

namespace DiaryOfMine.Infrastructure.Configuration;

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    private readonly LocalDataPaths _paths;
    private readonly object _writeLock = new();

    public JsonAppSettingsStore(LocalDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public AppSettings LoadOrCreate()
    {
        EnsureBaseDirectories();
        if (!File.Exists(_paths.SettingsFile))
            return Save(CreateDefaults());

        AppSettings? settings;
        try
        {
            using var stream = new FileStream(
                _paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            settings = JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"设置文件格式损坏，原文件已保留：{_paths.SettingsFile}", exception);
        }

        if (settings is null)
            throw new InvalidDataException($"设置文件为空，原文件已保留：{_paths.SettingsFile}");

        return ValidateAndPrepare(settings);
    }

    public AppSettings Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = ValidateAndPrepare(settings);
        EnsureBaseDirectories();

        lock (_writeLock)
        {
            var temporaryFile = Path.Combine(
                _paths.UserDataRoot, $"appsettings.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                    temporaryFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, normalized, SerializerOptions);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_paths.SettingsFile))
                    File.Replace(temporaryFile, _paths.SettingsFile, null);
                else
                    File.Move(temporaryFile, _paths.SettingsFile);
            }
            finally
            {
                if (File.Exists(temporaryFile))
                {
                    try { File.Delete(temporaryFile); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        return normalized;
    }

    private AppSettings CreateDefaults() => new()
    {
        CacheDirectory = _paths.DefaultCacheDirectory,
        LogDirectory = _paths.DefaultLogDirectory
    };

    private AppSettings ValidateAndPrepare(AppSettings settings)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"不支持的设置版本：{settings.SchemaVersion}。");

        var cacheDirectory = NormalizeDirectory(settings.CacheDirectory, nameof(settings.CacheDirectory));
        var logDirectory = NormalizeDirectory(settings.LogDirectory, nameof(settings.LogDirectory));
        var expandHotKey = NormalizeHotKey(settings.ExpandHotKey);
        var classPaletteColor1 = NormalizeColor(settings.ClassPaletteColor1, nameof(settings.ClassPaletteColor1));
        var classPaletteColor2 = NormalizeColor(settings.ClassPaletteColor2, nameof(settings.ClassPaletteColor2));
        var classPaletteColor3 = NormalizeColor(settings.ClassPaletteColor3, nameof(settings.ClassPaletteColor3));
        var classPaletteColor4 = NormalizeColor(settings.ClassPaletteColor4, nameof(settings.ClassPaletteColor4));
        var classPaletteColor5 = NormalizeColor(settings.ClassPaletteColor5, nameof(settings.ClassPaletteColor5));
        var classPaletteColor6 = NormalizeColor(settings.ClassPaletteColor6, nameof(settings.ClassPaletteColor6));
        var keyPaletteColor1 = NormalizeColor(settings.KeyPaletteColor1, nameof(settings.KeyPaletteColor1));
        var keyPaletteColor2 = NormalizeColor(settings.KeyPaletteColor2, nameof(settings.KeyPaletteColor2));
        var keyPaletteColor3 = NormalizeColor(settings.KeyPaletteColor3, nameof(settings.KeyPaletteColor3));
        var keyPaletteColor4 = NormalizeColor(settings.KeyPaletteColor4, nameof(settings.KeyPaletteColor4));
        var keyPaletteColor5 = NormalizeColor(settings.KeyPaletteColor5, nameof(settings.KeyPaletteColor5));
        var keyPaletteColor6 = NormalizeColor(settings.KeyPaletteColor6, nameof(settings.KeyPaletteColor6));
        var workPaletteColor1 = NormalizeColor(settings.WorkPaletteColor1, nameof(settings.WorkPaletteColor1));
        var workPaletteColor2 = NormalizeColor(settings.WorkPaletteColor2, nameof(settings.WorkPaletteColor2));
        var workPaletteColor3 = NormalizeColor(settings.WorkPaletteColor3, nameof(settings.WorkPaletteColor3));
        var workPaletteColor4 = NormalizeColor(settings.WorkPaletteColor4, nameof(settings.WorkPaletteColor4));
        var workPaletteColor5 = NormalizeColor(settings.WorkPaletteColor5, nameof(settings.WorkPaletteColor5));
        var workPaletteColor6 = NormalizeColor(settings.WorkPaletteColor6, nameof(settings.WorkPaletteColor6));
        if (classPaletteColor1 == "#83C5A5"
            && classPaletteColor2 == "#F0B6C1"
            && classPaletteColor3 == "#91C9D8"
            && classPaletteColor4 == "#E8CF87"
            && classPaletteColor5 == "#BEAED8"
            && classPaletteColor6 == "#EDB18D")
        {
            classPaletteColor1 = "#BDEBD3";
            classPaletteColor2 = "#F7C9D5";
            classPaletteColor3 = "#BFE3F2";
            classPaletteColor4 = "#F7EDB5";
            classPaletteColor5 = "#D8CDF2";
            classPaletteColor6 = "#FFD5C2";
        }
        var usesPreviousKeyDefaults =
            keyPaletteColor1 == "#79B99A"
            && keyPaletteColor2 == "#E9A8B5"
            && keyPaletteColor3 == "#83BCD0"
            && keyPaletteColor4 == "#DBC274"
            && keyPaletteColor5 == "#AD9ACA"
            && keyPaletteColor6 == "#E4A681";
        var usesLegacyKeyDefaults =
            keyPaletteColor1 == "#57A982"
            && keyPaletteColor2 == "#D98296"
            && keyPaletteColor3 == "#5EA6BC"
            && keyPaletteColor4 == "#C4A248"
            && keyPaletteColor5 == "#927BB7"
            && keyPaletteColor6 == "#CC7F5D";
        if (usesPreviousKeyDefaults || usesLegacyKeyDefaults)
        {
            keyPaletteColor1 = "#AEE5DA";
            keyPaletteColor2 = "#F4BCCA";
            keyPaletteColor3 = "#BCD2F4";
            keyPaletteColor4 = "#F4E7A8";
            keyPaletteColor5 = "#CDBEEB";
            keyPaletteColor6 = "#F7C5B8";
        }
        var usesPreviousClassAndKeyDefaults =
            classPaletteColor1 == "#BDEBD3"
            && classPaletteColor2 == "#F7C9D5"
            && classPaletteColor3 == "#BFE3F2"
            && classPaletteColor4 == "#F7EDB5"
            && classPaletteColor5 == "#D8CDF2"
            && classPaletteColor6 == "#FFD5C2"
            && keyPaletteColor1 == "#AEE5DA"
            && keyPaletteColor2 == "#F4BCCA"
            && keyPaletteColor3 == "#BCD2F4"
            && keyPaletteColor4 == "#F4E7A8"
            && keyPaletteColor5 == "#CDBEEB"
            && keyPaletteColor6 == "#F7C5B8";
        if (usesPreviousClassAndKeyDefaults)
        {
            classPaletteColor1 = "#DDF4E8";
            classPaletteColor2 = "#FBE1E8";
            classPaletteColor3 = "#DCEEF7";
            classPaletteColor4 = "#FBF3CF";
            classPaletteColor5 = "#EAE2F8";
            classPaletteColor6 = "#FFE5D9";
            keyPaletteColor1 = "#63B89A";
            keyPaletteColor2 = "#D98298";
            keyPaletteColor3 = "#6CAEC8";
            keyPaletteColor4 = "#C4A643";
            keyPaletteColor5 = "#927AC1";
            keyPaletteColor6 = "#D88969";
        }
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(logDirectory);

        if (!double.IsFinite(settings.FloatingWindowLeft)
            || !double.IsFinite(settings.FloatingWindowTop))
            throw new InvalidDataException("悬浮窗位置必须是有效数字。");

        return settings with
        {
            CacheDirectory = cacheDirectory,
            LogDirectory = logDirectory,
            ExpandHotKey = expandHotKey,
            ClassPaletteColor1 = classPaletteColor1,
            ClassPaletteColor2 = classPaletteColor2,
            ClassPaletteColor3 = classPaletteColor3,
            ClassPaletteColor4 = classPaletteColor4,
            ClassPaletteColor5 = classPaletteColor5,
            ClassPaletteColor6 = classPaletteColor6,
            KeyPaletteColor1 = keyPaletteColor1,
            KeyPaletteColor2 = keyPaletteColor2,
            KeyPaletteColor3 = keyPaletteColor3,
            KeyPaletteColor4 = keyPaletteColor4,
            KeyPaletteColor5 = keyPaletteColor5,
            KeyPaletteColor6 = keyPaletteColor6,
            WorkPaletteColor1 = workPaletteColor1,
            WorkPaletteColor2 = workPaletteColor2,
            WorkPaletteColor3 = workPaletteColor3,
            WorkPaletteColor4 = workPaletteColor4,
            WorkPaletteColor5 = workPaletteColor5,
            WorkPaletteColor6 = workPaletteColor6
        };
    }

    private static string NormalizeColor(string color, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(color)
            || color.Length != 7
            || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("颜色必须是 #RRGGBB 格式。", propertyName);
        return color.ToUpperInvariant();
    }

    private static string NormalizeHotKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 32)
            throw new ArgumentException("展开快捷键不能为空且不能超过 32 个字符。", nameof(value));
        return value.Trim();
    }

    private static string NormalizeDirectory(string path, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("目录必须是完整的绝对路径。", propertyName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(_paths.UserDataRoot);
        Directory.CreateDirectory(_paths.VaultDirectory);
        Directory.CreateDirectory(_paths.Backups);
    }
}
