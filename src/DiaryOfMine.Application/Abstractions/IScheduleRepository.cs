using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Abstractions;

public interface IScheduleRepository
{
    Task<IReadOnlyList<ScheduleItem>> GetRangeAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task SaveAsync(ScheduleItem item, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
