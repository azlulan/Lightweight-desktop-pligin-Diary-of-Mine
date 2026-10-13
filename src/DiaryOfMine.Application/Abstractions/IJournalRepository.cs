using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Abstractions;

// 实现必须先验证保险库已解锁；错误不得包含日记正文或密钥。
public interface IJournalRepository
{
    Task<IReadOnlyList<DiaryEntry>> GetPageAsync(
        DateOnly date, int offset, int limit, CancellationToken cancellationToken);

    Task SaveAsync(DiaryEntry entry, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
