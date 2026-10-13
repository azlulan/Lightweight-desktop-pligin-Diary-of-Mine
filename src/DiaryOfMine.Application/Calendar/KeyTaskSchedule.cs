using System.Globalization;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Calendar;

public sealed record KeyTaskSchedule(
    string Title,
    string Summary,
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlySet<DayOfWeek> Weekdays,
    int StartMinute,
    int EndMinute)
{
    private const int MaximumRangeDays = 7305;
    private const int MaximumOccurrences = 10000;

    public IReadOnlyList<CalendarItemDraft> Expand()
    {
        var title = (Title ?? string.Empty).Trim();
        var summary = (Summary ?? string.Empty).Trim();
        if (title.Length == 0) throw new ArgumentException("Task 名称不能为空。", nameof(Title));
        if (title.Length > 240) throw new ArgumentException("Task 名称不能超过 240 个字符。", nameof(Title));
        if (StringInfo.ParseCombiningCharacters(summary).Length > 3)
            throw new ArgumentException("Key 概述不能超过三个字。", nameof(Summary));
        if (EndDate < StartDate)
            throw new ArgumentException("排布结束日期不能早于开始日期。", nameof(EndDate));
        if (EndDate.DayNumber - StartDate.DayNumber > MaximumRangeDays)
            throw new ArgumentException("单次 Task 的日期范围不能超过 20 年。", nameof(EndDate));
        if (Weekdays is null || Weekdays.Count == 0)
            throw new ArgumentException("请至少选择一个星期日期。", nameof(Weekdays));
        if (StartMinute is < 0 or >= 1440)
            throw new ArgumentException("开始时间必须位于 00:00—23:59。", nameof(StartMinute));
        if (EndMinute is <= 0 or > 1440 || EndMinute <= StartMinute)
            throw new ArgumentException("结束时间必须晚于开始时间且不超过当天 24:00。", nameof(EndMinute));

        var result = new List<CalendarItemDraft>();
        for (var dayNumber = StartDate.DayNumber; dayNumber <= EndDate.DayNumber; dayNumber++)
        {
            var date = DateOnly.FromDayNumber(dayNumber);
            if (!Weekdays.Contains(date.DayOfWeek)) continue;
            result.Add(new CalendarItemDraft(
                CalendarItemKind.Key,
                title,
                ToLocalOffset(date, StartMinute),
                ToLocalOffset(date, EndMinute),
                false,
                summary));
            if (result.Count > MaximumOccurrences)
                throw new ArgumentException("单次 Task 不能生成超过 10000 个 Key。", nameof(EndDate));
        }

        if (result.Count == 0)
            throw new ArgumentException("当前日期范围内没有符合所选星期的日期。", nameof(Weekdays));
        return result;
    }

    public KeyTaskBatchInfo ToBatchInfo(Guid batchId)
    {
        _ = Expand();
        var mask = Weekdays.Aggregate(0, (value, day) => value | (1 << (int)day));
        return new KeyTaskBatchInfo(
            batchId, StartDate, EndDate, mask, StartMinute, EndMinute).Validate();
    }

    public static KeyTaskSchedule FromCalendarItem(CalendarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var batch = item.TaskBatch?.Validate()
            ?? throw new ArgumentException("该 Key 不属于 Task 批次。", nameof(item));
        var weekdays = Enumerable.Range(0, 7)
            .Where(day => (batch.WeekdaysMask & (1 << day)) != 0)
            .Select(day => (DayOfWeek)day)
            .ToHashSet();
        return new KeyTaskSchedule(
            item.Title, item.Summary, batch.StartDate, batch.EndDate,
            weekdays, batch.StartMinute, batch.EndMinute);
    }

    private static DateTimeOffset ToLocalOffset(DateOnly date, int minute)
    {
        if (minute == 1440)
        {
            date = date.AddDays(1);
            minute = 0;
        }
        var local = date.ToDateTime(
            TimeOnly.MinValue.AddMinutes(minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
