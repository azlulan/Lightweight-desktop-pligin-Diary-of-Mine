namespace DiaryOfMine.Domain;

// 首版以一次性提醒为范围；重复规则在后续独立建模。
public sealed record ScheduleItem(
    Guid Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? RemindAt,
    bool IsCompleted);
