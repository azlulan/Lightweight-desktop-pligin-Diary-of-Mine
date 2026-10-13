using System.Globalization;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Reminders;

public static class ReminderPlanner
{
    public static IReadOnlyList<ReminderNotification> Build(
        IEnumerable<CalendarItem> items,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EnableNotifications) return Array.Empty<ReminderNotification>();

        var reminders = new List<ReminderNotification>();
        foreach (var item in items)
        {
            // 已完成事项无论种类都不再产生新的软件提醒。
            if (item.IsCompleted) continue;

            switch (item.Kind)
            {
                case CalendarItemKind.Class when settings.EnableClassNotifications
                                                  && item.EndsAt is { } deadline:
                    reminders.Add(Create(
                        item,
                        ReminderKind.ClassDeadline,
                        deadline.AddMinutes(-30),
                        deadline));
                    break;

                case CalendarItemKind.Key when settings.EnableKeyNotifications:
                    reminders.Add(Create(
                        item,
                        ReminderKind.KeyStart,
                        item.StartsAt.AddMinutes(-30),
                        item.StartsAt));
                    break;

                case CalendarItemKind.Idea when settings.EnableIdeaNotifications:
                    var date = DateOnly.FromDateTime(item.StartsAt.LocalDateTime);
                    var trigger = AtLocalTime(date, new TimeOnly(23, 30));
                    reminders.Add(Create(
                        item,
                        ReminderKind.IdeaDaily,
                        trigger,
                        trigger));
                    break;
            }
        }

        return reminders
            .OrderBy(reminder => reminder.TriggerAt)
            .ThenBy(reminder => reminder.Kind)
            .ThenBy(reminder => reminder.Title, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static ReminderNotification Create(
        CalendarItem item,
        ReminderKind kind,
        DateTimeOffset triggerAt,
        DateTimeOffset eventTime)
    {
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{item.Id:N}:{(int)kind}:{triggerAt.UtcDateTime.Ticks}");
        return new ReminderNotification(
            key,
            kind,
            item.Id,
            item.Title,
            triggerAt,
            eventTime,
            item.Kind == CalendarItemKind.Key && item.TaskBatch is not null);
    }

    private static DateTimeOffset AtLocalTime(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
