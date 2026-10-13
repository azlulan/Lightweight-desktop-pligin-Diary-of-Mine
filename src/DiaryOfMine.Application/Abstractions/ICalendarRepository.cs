using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Abstractions;

public interface ICalendarRepository
{
    Task<IReadOnlyList<CalendarItem>> GetRangeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken);

    Task SaveAsync(CalendarItem item, CancellationToken cancellationToken);
    Task SaveManyAsync(IReadOnlyCollection<CalendarItem> items, CancellationToken cancellationToken);
    Task<IReadOnlyList<CalendarItem>> GetTaskBatchAsync(Guid batchId, CancellationToken cancellationToken);
    Task ReplaceTaskBatchAsync(
        Guid batchId,
        IReadOnlyCollection<CalendarItem> items,
        CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task DeleteManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
