using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaryOfMine.Domain;

public sealed record DiaryEntry(
    DateOnly Date,
    string Title,
    string BodyPackageBase64,
    string PreviewText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string SearchText { get; init; } = string.Empty;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public DiaryEntry Validate()
    {
        var title = (Title ?? string.Empty).Trim();
        if (title.Length == 0)
            throw new ArgumentException("日记标题不能为空。", nameof(Title));
        if (title.Length > 120)
            throw new ArgumentException("日记标题不能超过 120 个字符。", nameof(Title));
        var body = BodyPackageBase64 ?? string.Empty;
        var preview = (PreviewText ?? string.Empty).Trim();
        if (preview.Length > 240) preview = preview[..240];
        return this with
        {
            Title = title,
            BodyPackageBase64 = body,
            PreviewText = preview,
            SearchText = (SearchText ?? string.Empty).Trim()
        };
    }
}
