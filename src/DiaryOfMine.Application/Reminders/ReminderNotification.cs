namespace DiaryOfMine.Application.Reminders;

public sealed record ReminderNotification(
    string Key,
    ReminderKind Kind,
    Guid CalendarItemId,
    string Title,
    DateTimeOffset TriggerAt,
    DateTimeOffset EventTime,
    bool IsTaskKey);
