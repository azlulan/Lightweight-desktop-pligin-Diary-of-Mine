using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Calendar;

public sealed record KeyTaskBatchChange(
    Guid BatchId,
    IReadOnlyList<CalendarItem> Before,
    IReadOnlyList<CalendarItem> After);
