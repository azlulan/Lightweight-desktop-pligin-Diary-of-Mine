using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaryOfMine.Domain;

public sealed record CalendarItem(
    Guid Id,
    CalendarItemKind Kind,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool IsCompleted,
    string ColorKey,
    DateTimeOffset CreatedAt)
{
    public string Summary { get; init; } = string.Empty;
    public KeyTaskBatchInfo? TaskBatch { get; init; }
    public IReadOnlyList<CalendarFileLink> FileLinks { get; init; } = Array.Empty<CalendarFileLink>();
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public CalendarItem Validate()
    {
        if (Id == Guid.Empty)
            throw new ArgumentException("日程标识不能为空。", nameof(Id));
        if (string.IsNullOrWhiteSpace(Title))
            throw new ArgumentException("名称不能为空。", nameof(Title));
        var maximumTitleLength = Kind == CalendarItemKind.Inspiration ? 1000 : 240;
        if (Title.Trim().Length > maximumTitleLength)
            throw new ArgumentException($"名称或内容不能超过 {maximumTitleLength} 个字符。", nameof(Title));
        if (EndsAt is not null && EndsAt < StartsAt)
            throw new ArgumentException("结束时间不能早于开始时间。", nameof(EndsAt));
        if (Kind is CalendarItemKind.Idea or CalendarItemKind.Inspiration && EndsAt is not null)
            throw new ArgumentException("无时间事项不应包含结束时间。", nameof(EndsAt));
        if (TaskBatch is not null && Kind != CalendarItemKind.Key)
            throw new ArgumentException("Task 批次信息只能用于 Key。", nameof(TaskBatch));
        TaskBatch?.Validate();
        if (string.IsNullOrWhiteSpace(ColorKey) || ColorKey.Length > 32)
            throw new ArgumentException("颜色标识无效。", nameof(ColorKey));

        var summary = (Summary ?? string.Empty).Trim();
        if (Kind == CalendarItemKind.Key
            && StringInfo.ParseCombiningCharacters(summary).Length > 3)
            throw new ArgumentException("Key 概述不能超过 3 个字符。", nameof(Summary));

        var fileLinks = (FileLinks ?? Array.Empty<CalendarFileLink>())
            .Select(link => link.Validate())
            .ToArray();
        if (fileLinks.Length > 32)
            throw new ArgumentException("每项日程最多绑定 32 个快捷链接。", nameof(FileLinks));
        if (fileLinks.GroupBy(link => link.FilePath, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("同一个文件不能重复绑定。", nameof(FileLinks));
        if (fileLinks.Count(link => link.IsPinned) > 1)
            throw new ArgumentException("一项日程只能置顶一个快捷链接。", nameof(FileLinks));
        if (Kind is not (CalendarItemKind.Class or CalendarItemKind.Key or CalendarItemKind.Idea))
            fileLinks = Array.Empty<CalendarFileLink>();

        return this with
        {
            Title = Title.Trim(),
            ColorKey = ColorKey.Trim(),
            Summary = Kind == CalendarItemKind.Key ? summary : string.Empty,
            FileLinks = fileLinks
                .OrderByDescending(link => link.IsPinned)
                .ThenBy(link => link.AddedAt)
                .ToArray(),
            IsCompleted = (Kind is CalendarItemKind.Class
                or CalendarItemKind.Key
                or CalendarItemKind.Idea) && IsCompleted
        };
    }
}
