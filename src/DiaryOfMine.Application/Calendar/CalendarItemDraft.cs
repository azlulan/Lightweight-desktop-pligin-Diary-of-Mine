using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Calendar;

public sealed record CalendarItemDraft(
    CalendarItemKind Kind,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool IsCompleted = false,
    string Summary = "",
    IReadOnlyList<CalendarFileLink>? FileLinks = null);
