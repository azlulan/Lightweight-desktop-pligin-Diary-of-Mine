using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiaryOfMine.Domain;

public sealed record WorkGoal(
    Guid Id,
    string Name,
    string ColorKey,
    bool IsBuiltIn,
    bool IsArchived,
    int SortOrder,
    DateTimeOffset CreatedAt)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public WorkGoal Validate()
    {
        var name = (Name ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new ArgumentException("工作目标不能为空。", nameof(Name));
        if (StringInfo.ParseCombiningCharacters(name).Length > 4)
            throw new ArgumentException("工作目标不能超过 4 个字符。", nameof(Name));
        var colorKey = (ColorKey ?? string.Empty).Trim();
        if (colorKey.Length == 0)
            throw new ArgumentException("工作目标必须指定颜色。", nameof(ColorKey));
        return this with { Name = name, ColorKey = colorKey };
    }
}

public sealed record WorkSession(
    Guid Id,
    Guid GoalId,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public WorkSession Validate()
    {
        if (EndsAt is { } end && end <= StartsAt)
            throw new ArgumentException("工作记录的结束时间必须晚于开始时间。", nameof(EndsAt));
        if (EndsAt is { } cappedEnd && cappedEnd - StartsAt > TimeSpan.FromHours(24))
            throw new ArgumentException("单次工作记录不能超过 24 小时。", nameof(EndsAt));
        return this;
    }
}

public sealed record WorkInterruption(
    Guid Id,
    Guid SessionId,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public WorkInterruption Validate()
    {
        if (EndsAt is { } end && end <= StartsAt)
            throw new ArgumentException("中断结束时间必须晚于开始时间。", nameof(EndsAt));
        return this;
    }
}

public sealed record WorkModeState(
    IReadOnlyCollection<WorkGoal> Goals,
    IReadOnlyCollection<WorkSession> Sessions,
    IReadOnlyCollection<WorkInterruption> Interruptions)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public static WorkModeState Empty { get; } = new(
        Array.Empty<WorkGoal>(), Array.Empty<WorkSession>(), Array.Empty<WorkInterruption>());
}
