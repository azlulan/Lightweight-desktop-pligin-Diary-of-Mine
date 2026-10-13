using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Abstractions;

public interface IDiaryRepository
{
    Task<IReadOnlyList<DiaryEntry>> GetAllAsync(CancellationToken cancellationToken);
    Task<DiaryEntry?> GetAsync(DateOnly date, CancellationToken cancellationToken);
    Task SaveAsync(DiaryEntry entry, CancellationToken cancellationToken);
    Task DeleteAsync(DateOnly date, CancellationToken cancellationToken);
}
