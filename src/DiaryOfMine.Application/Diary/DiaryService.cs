using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Diary;

public sealed class DiaryService
{
    private readonly IDiaryRepository _repository;
    private readonly IAppLogger _logger;

    public DiaryService(IDiaryRepository repository, IAppLogger logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<DiaryEntry>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _repository.GetAllAsync(cancellationToken);

    public Task<DiaryEntry?> GetAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        _repository.GetAsync(date, cancellationToken);

    public async Task<DiaryEntry> SaveAsync(
        DateOnly date,
        string title,
        string bodyPackageBase64,
        string previewText,
        string searchText,
        CancellationToken cancellationToken = default)
    {
        using var operation = _logger.BeginOperation("diary.save");
        try
        {
            var existing = await _repository.GetAsync(date, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            var entry = new DiaryEntry(
                date, title, bodyPackageBase64, previewText,
                existing?.CreatedAt ?? now, now)
            {
                SearchText = searchText,
                ExtensionData = existing?.ExtensionData
            }.Validate();
            await _repository.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
            operation.Complete(existing is null ? "created" : "updated");
            return entry;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task DeleteAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        using var operation = _logger.BeginOperation("diary.delete");
        try
        {
            await _repository.DeleteAsync(date, cancellationToken).ConfigureAwait(false);
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }
}
