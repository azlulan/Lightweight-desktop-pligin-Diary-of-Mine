using System.Security.Cryptography;
using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.Calendar;

public sealed class CalendarService
{
    private readonly ICalendarRepository _repository;
    private readonly IAppLogger _logger;

    public CalendarService(ICalendarRepository repository, IAppLogger logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<CalendarItem>> GetRangeAsync(
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default)
    {
        if (toExclusive <= fromInclusive)
            throw new ArgumentException("查询结束日期必须晚于开始日期。", nameof(toExclusive));

        using var operation = _logger.BeginOperation("calendar.load");
        try
        {
            var items = await _repository.GetRangeAsync(
                AtLocalMidnight(fromInclusive),
                AtLocalMidnight(toExclusive),
                cancellationToken).ConfigureAwait(false);
            operation.Complete(items.Count == 0 ? "empty" : "success");
            return items;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task<CalendarItem> CreateAsync(
        CalendarItemDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        using var operation = _logger.BeginOperation("calendar.create");
        try
        {
            draft = NormalizeDraft(draft);
            var item = new CalendarItem(
                Guid.NewGuid(),
                draft.Kind,
                draft.Title,
                draft.StartsAt,
                draft.EndsAt,
                SupportsCompletion(draft.Kind) && draft.IsCompleted,
                CalendarPalette.GetSlotKey(
                    RandomNumberGenerator.GetInt32(CalendarPalette.SlotCount)),
                DateTimeOffset.Now)
            {
                Summary = draft.Kind == CalendarItemKind.Key ? draft.Summary : string.Empty,
                FileLinks = draft.FileLinks ?? Array.Empty<CalendarFileLink>()
            }.Validate();

            await _repository.SaveAsync(item, cancellationToken).ConfigureAwait(false);
            operation.Complete();
            return item;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task<IReadOnlyList<CalendarItem>> CreateKeyBatchAsync(
        KeyTaskSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        using var operation = _logger.BeginOperation("calendar.key-batch.create");
        try
        {
            var drafts = schedule.Expand();
            var batchId = Guid.NewGuid();
            var batchInfo = schedule.ToBatchInfo(batchId);
            var colorKey = CalendarPalette.GetSlotKey(
                RandomNumberGenerator.GetInt32(CalendarPalette.SlotCount));
            var createdAt = DateTimeOffset.Now;
            var items = drafts.Select(draft =>
                {
                    var normalized = NormalizeDraft(draft);
                    return new CalendarItem(
                    Guid.NewGuid(),
                    CalendarItemKind.Key,
                    normalized.Title,
                    normalized.StartsAt,
                    normalized.EndsAt,
                    false,
                    colorKey,
                    createdAt)
                {
                    Summary = normalized.Summary,
                    TaskBatch = batchInfo
                }.Validate();
                })
                .ToArray();
            await _repository.SaveManyAsync(items, cancellationToken).ConfigureAwait(false);
            operation.Complete($"created-{items.Length}");
            return items;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task<KeyTaskBatchChange> ReplaceKeyTaskBatchAsync(
        Guid batchId,
        KeyTaskSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (batchId == Guid.Empty) throw new ArgumentException("Task 批次标识不能为空。", nameof(batchId));
        using var operation = _logger.BeginOperation("calendar.key-batch.replace");
        try
        {
            var before = await _repository.GetTaskBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (before.Count == 0) throw new InvalidOperationException("该 Task 批次已不存在。");
            var drafts = schedule.Expand();
            var batchInfo = schedule.ToBatchInfo(batchId) with
            {
                ExtensionData = before.Select(item => item.TaskBatch?.ExtensionData)
                    .FirstOrDefault(data => data is not null)
            };
            var colorKey = before[0].ColorKey;
            var createdAt = before.Min(item => item.CreatedAt);
            var existingByDate = before
                .GroupBy(item => DateOnly.FromDateTime(item.StartsAt.LocalDateTime))
                .ToDictionary(group => group.Key, group => group.OrderBy(item => item.CreatedAt).First());
            var after = drafts.Select(draft =>
            {
                var normalized = NormalizeDraft(draft);
                existingByDate.TryGetValue(
                    DateOnly.FromDateTime(normalized.StartsAt.LocalDateTime), out var existing);
                return new CalendarItem(
                    Guid.NewGuid(), CalendarItemKind.Key, normalized.Title,
                    normalized.StartsAt, normalized.EndsAt, false, colorKey, createdAt)
                {
                    Summary = normalized.Summary,
                    TaskBatch = batchInfo,
                    FileLinks = existing?.FileLinks ?? Array.Empty<CalendarFileLink>(),
                    ExtensionData = existing?.ExtensionData
                }.Validate();
            }).ToArray();
            await _repository.ReplaceTaskBatchAsync(batchId, after, cancellationToken).ConfigureAwait(false);
            operation.Complete($"replaced-{before.Count}-with-{after.Length}");
            return new KeyTaskBatchChange(batchId, before, after);
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public Task ReplaceKeyTaskBatchItemsAsync(
        Guid batchId,
        IReadOnlyCollection<CalendarItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return _repository.ReplaceTaskBatchAsync(batchId, items, cancellationToken);
    }

    public async Task SaveBatchAsync(
        IReadOnlyCollection<CalendarItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var operation = _logger.BeginOperation("calendar.batch.save");
        try
        {
            await _repository.SaveManyAsync(items, cancellationToken).ConfigureAwait(false);
            operation.Complete($"saved-{items.Count}");
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task DeleteBatchAsync(
        IReadOnlyCollection<CalendarItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var operation = _logger.BeginOperation("calendar.batch.delete");
        try
        {
            await _repository.DeleteManyAsync(
                items.Select(item => item.Id).ToArray(), cancellationToken).ConfigureAwait(false);
            operation.Complete($"deleted-{items.Count}");
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task<CalendarItem> UpdateAsync(
        CalendarItem original,
        CalendarItemDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(draft);
        draft = NormalizeDraft(draft);

        var updated = original with
        {
            Kind = draft.Kind,
            Title = draft.Title,
            StartsAt = draft.StartsAt,
            EndsAt = draft.EndsAt,
            IsCompleted = SupportsCompletion(draft.Kind) && draft.IsCompleted,
            Summary = draft.Kind == CalendarItemKind.Key ? draft.Summary : string.Empty,
            FileLinks = draft.FileLinks ?? original.FileLinks
        };
        await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated.Validate();
    }

    public async Task SaveAsync(CalendarItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        using var operation = _logger.BeginOperation("calendar.update");
        try
        {
            await _repository.SaveAsync(item.Validate(), cancellationToken).ConfigureAwait(false);
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("日程标识不能为空。", nameof(id));
        using var operation = _logger.BeginOperation("calendar.delete");
        try
        {
            await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _logger.BeginOperation("calendar.clear");
        try
        {
            await _repository.ClearAsync(cancellationToken).ConfigureAwait(false);
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    private static DateTimeOffset AtLocalMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static bool SupportsCompletion(CalendarItemKind kind) =>
        kind is CalendarItemKind.Class or CalendarItemKind.Key or CalendarItemKind.Idea;

    private static CalendarItemDraft NormalizeDraft(CalendarItemDraft draft)
    {
        if (draft.Kind != CalendarItemKind.Key) return draft;

        var startDate = DateOnly.FromDateTime(draft.StartsAt.LocalDateTime);
        var nextMidnight = AtLocalMidnight(startDate.AddDays(1));
        var end = draft.EndsAt;
        if (end is null)
        {
            var defaultEnd = draft.StartsAt.AddHours(2);
            end = defaultEnd < nextMidnight ? defaultEnd : nextMidnight;
        }
        else if (end > nextMidnight)
        {
            throw new ArgumentException("Key 必须在开始当天 24:00 前结束。", nameof(draft));
        }

        return draft with { EndsAt = end };
    }
}
