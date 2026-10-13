using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DiaryOfMine.Application.Calendar;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.ViewModels;

internal sealed class CalendarDayDetailViewModel
{
    private readonly CalendarService _calendarService;
    private readonly IAppLogger _logger;
    private readonly Stack<HistoryEntry> _undoHistory = new();
    private readonly Stack<HistoryEntry> _redoHistory = new();

    public DateOnly Date { get; private set; }
    public IReadOnlyList<CalendarItem> Items { get; private set; } = Array.Empty<CalendarItem>();
    public AppSettings Settings { get; private set; }
    public string StatusMessage { get; private set; } = string.Empty;
    public bool CanUndo => _undoHistory.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;

    public event EventHandler? ViewChanged;
    public event EventHandler? DataChanged;

    public CalendarDayDetailViewModel(
        CalendarService calendarService,
        IAppLogger logger,
        AppSettings settings,
        DateOnly date)
    {
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Date = date;
    }

    public async Task LoadDateAsync(DateOnly date)
    {
        Date = date;
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);

    public async Task ResetAfterDataClearAsync()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        StatusMessage = "日程已清空。";
        await ReloadAsync().ConfigureAwait(true);
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CalendarItem> CreateAsync(CalendarItemDraft draft)
    {
        var created = await _calendarService.CreateAsync(draft).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Create, null, created));
        StatusMessage = "已添加到详细日程表。";
        await AfterMutationAsync().ConfigureAwait(true);
        return created;
    }

    public async Task<IReadOnlyList<CalendarItem>> CreateKeyTaskAsync(KeyTaskSchedule schedule)
    {
        var items = await _calendarService.CreateKeyBatchAsync(schedule).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.BatchCreate, null, null, items));
        StatusMessage = $"Task 已批量添加 {items.Count} 个 Key。";
        await AfterMutationAsync().ConfigureAwait(true);
        return items;
    }

    public async Task<IReadOnlyList<CalendarItem>> EditKeyTaskAsync(
        CalendarItem occurrence,
        KeyTaskSchedule schedule)
    {
        var batchId = occurrence.TaskBatch?.BatchId
            ?? throw new ArgumentException("该 Key 不属于 Task 批次。", nameof(occurrence));
        var change = await _calendarService.ReplaceKeyTaskBatchAsync(batchId, schedule).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(
            HistoryAction.BatchUpdate, null, null, change.After, change.Before));
        StatusMessage = $"Task 批次已更新，共 {change.After.Count} 个 Key。";
        await AfterMutationAsync().ConfigureAwait(true);
        return change.After;
    }

    public async Task<CalendarItem> EditAsync(CalendarItem original, CalendarItemDraft draft)
    {
        ArgumentNullException.ThrowIfNull(original);
        var updated = await _calendarService.UpdateAsync(original, draft).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Update, original, updated));
        StatusMessage = "日程内容已更新。";
        await AfterMutationAsync().ConfigureAwait(true);
        return updated;
    }

    public async Task UpdateBoundaryAsync(
        CalendarItem item,
        bool updateStart,
        DateTimeOffset newBoundary)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is not (CalendarItemKind.Class or CalendarItemKind.Key))
            throw new ArgumentException("只有 Class 和 Key 可以拖动时间边界。", nameof(item));

        var dayStart = AtLocalMidnight(Date);
        var dayEnd = AtLocalMidnight(Date.AddDays(1));
        if (newBoundary < dayStart || newBoundary > dayEnd)
            throw new ArgumentException("时间边界必须位于当前日期内。", nameof(newBoundary));

        var effectiveEnd = item.EndsAt ?? (item.Kind == CalendarItemKind.Key
            ? Earlier(item.StartsAt.AddHours(2), AtLocalMidnight(
                DateOnly.FromDateTime(item.StartsAt.LocalDateTime).AddDays(1)))
            : null);

        CalendarItem updated;
        if (updateStart)
        {
            if (effectiveEnd is not null && newBoundary > effectiveEnd)
                throw new ArgumentException("开始时间不能晚于结束时间。", nameof(newBoundary));
            updated = item with
            {
                StartsAt = newBoundary,
                EndsAt = item.Kind == CalendarItemKind.Key ? effectiveEnd : item.EndsAt
            };
        }
        else
        {
            if (newBoundary < item.StartsAt)
                throw new ArgumentException("结束时间不能早于开始时间。", nameof(newBoundary));
            if (item.Kind == CalendarItemKind.Key
                && newBoundary > AtLocalMidnight(
                    DateOnly.FromDateTime(item.StartsAt.LocalDateTime).AddDays(1)))
                throw new ArgumentException("Key 不可跨越开始当天 24:00。", nameof(newBoundary));
            updated = item with { EndsAt = newBoundary };
        }

        await _calendarService.SaveAsync(updated).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Update, item, updated));
        StatusMessage = $"{item.Kind} 起止时间已更新。";
        await AfterMutationAsync().ConfigureAwait(true);
    }

    public async Task ToggleIdeaAsync(CalendarItem item, bool completed)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != CalendarItemKind.Idea)
            throw new ArgumentException("只有 Idea 支持完成状态。", nameof(item));
        await ToggleCompletedAsync(item, completed).ConfigureAwait(true);
    }

    public async Task ToggleCompletedAsync(CalendarItem item, bool completed)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is not (CalendarItemKind.Class or CalendarItemKind.Key or CalendarItemKind.Idea))
            throw new ArgumentException("该日程不支持完成状态。", nameof(item));
        var updated = item with { IsCompleted = completed };
        await _calendarService.SaveAsync(updated).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Update, item, updated));
        StatusMessage = completed ? $"{item.Kind} 已完成。" : $"{item.Kind} 已恢复。";
        await AfterMutationAsync().ConfigureAwait(true);
    }

    public async Task<CalendarItem> UpdateFileLinksAsync(
        CalendarItem item,
        IReadOnlyList<CalendarFileLink> fileLinks)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(fileLinks);
        if (item.Kind is not (CalendarItemKind.Class or CalendarItemKind.Key or CalendarItemKind.Idea))
            throw new ArgumentException("只有 Class、Key 和 Idea 支持快捷链接。", nameof(item));

        // Task 生成的 Key 只更新当前实例，不扩散到整个批次。
        var updated = (item with { FileLinks = fileLinks }).Validate();
        await _calendarService.SaveAsync(updated).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Update, item, updated));
        StatusMessage = "快捷链接已更新。";
        await AfterMutationAsync().ConfigureAwait(true);
        return updated;
    }

    public async Task DeleteAsync(CalendarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _calendarService.DeleteAsync(item.Id).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Delete, item, null));
        StatusMessage = $"{item.Kind} 已删除。";
        await AfterMutationAsync().ConfigureAwait(true);
    }

    public async Task UndoAsync()
    {
        if (_undoHistory.Count == 0) return;
        using var operation = _logger.BeginOperation("calendar.day-detail.undo");
        var entry = _undoHistory.Pop();
        try
        {
            await ApplyHistoryAsync(entry, undo: true).ConfigureAwait(true);
            _redoHistory.Push(entry);
            StatusMessage = "已撤回详细日程表中的上一步操作。";
            await AfterMutationAsync().ConfigureAwait(true);
            operation.Complete();
        }
        catch (Exception exception)
        {
            _undoHistory.Push(entry);
            ViewChanged?.Invoke(this, EventArgs.Empty);
            operation.Fail(exception);
            throw;
        }
    }

    public async Task RedoAsync()
    {
        if (_redoHistory.Count == 0) return;
        using var operation = _logger.BeginOperation("calendar.day-detail.redo");
        var entry = _redoHistory.Pop();
        try
        {
            await ApplyHistoryAsync(entry, undo: false).ConfigureAwait(true);
            _undoHistory.Push(entry);
            StatusMessage = "已重做详细日程表中的操作。";
            await AfterMutationAsync().ConfigureAwait(true);
            operation.Complete();
        }
        catch (Exception exception)
        {
            _redoHistory.Push(entry);
            ViewChanged?.Invoke(this, EventArgs.Empty);
            operation.Fail(exception);
            throw;
        }
    }

    private async Task ReloadAsync()
    {
        Items = await _calendarService.GetRangeAsync(Date, Date.AddDays(1)).ConfigureAwait(true);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task AfterMutationAsync()
    {
        await ReloadAsync().ConfigureAwait(true);
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyHistoryAsync(HistoryEntry entry, bool undo)
    {
        switch (entry.Action)
        {
            case HistoryAction.Create:
                if (undo) await _calendarService.DeleteAsync(entry.After!.Id).ConfigureAwait(true);
                else await _calendarService.SaveAsync(entry.After!).ConfigureAwait(true);
                break;
            case HistoryAction.Delete:
                if (undo) await _calendarService.SaveAsync(entry.Before!).ConfigureAwait(true);
                else await _calendarService.DeleteAsync(entry.Before!.Id).ConfigureAwait(true);
                break;
            case HistoryAction.Update:
                await _calendarService.SaveAsync(undo ? entry.Before! : entry.After!).ConfigureAwait(true);
                break;
            case HistoryAction.BatchCreate:
                if (undo) await _calendarService.DeleteBatchAsync(entry.BatchItems!).ConfigureAwait(true);
                else await _calendarService.SaveBatchAsync(entry.BatchItems!).ConfigureAwait(true);
                break;
            case HistoryAction.BatchUpdate:
                var batchItems = undo ? entry.PreviousBatchItems! : entry.BatchItems!;
                var batchId = batchItems[0].TaskBatch!.BatchId;
                await _calendarService.ReplaceKeyTaskBatchItemsAsync(batchId, batchItems).ConfigureAwait(true);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void RecordHistory(HistoryEntry entry)
    {
        _undoHistory.Push(entry);
        _redoHistory.Clear();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static DateTimeOffset AtLocalMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) =>
        first < second ? first : second;

    private enum HistoryAction
    {
        Create,
        BatchCreate,
        BatchUpdate,
        Update,
        Delete
    }

    private sealed record HistoryEntry(
        HistoryAction Action,
        CalendarItem? Before,
        CalendarItem? After,
        IReadOnlyList<CalendarItem>? BatchItems = null,
        IReadOnlyList<CalendarItem>? PreviousBatchItems = null);
}
