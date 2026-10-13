using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaryOfMine.Domain;

public sealed record CalendarFileLink(
    Guid Id,
    string FilePath,
    string DisplayName,
    bool IsPinned,
    DateTimeOffset AddedAt)
{
    public bool IsDirectory { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public CalendarFileLink Validate()
    {
        if (Id == Guid.Empty)
            throw new ArgumentException("快捷链接标识不能为空。", nameof(Id));
        if (string.IsNullOrWhiteSpace(FilePath) || !Path.IsPathFullyQualified(FilePath))
            throw new ArgumentException("快捷链接必须使用完整的本地文件路径。", nameof(FilePath));

        var fullPath = Path.GetFullPath(FilePath.Trim());
        var displayName = string.IsNullOrWhiteSpace(DisplayName)
            ? GetDisplayName(fullPath)
            : DisplayName.Trim();
        if (displayName.Length > 260)
            throw new ArgumentException("快捷链接文件名过长。", nameof(DisplayName));

        return this with { FilePath = fullPath, DisplayName = displayName };
    }

    private static string GetDisplayName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
