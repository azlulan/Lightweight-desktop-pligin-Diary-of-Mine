using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DiaryOfMine.Application.Calendar;
using DiaryOfMine.Application.Diary;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.ViewModels;

public sealed class HomePageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CalendarService _calendarService;
    private readonly DiaryService _diaryService;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Stack<HistoryEntry> _undoHistory = new();
    private readonly Stack<HistoryEntry> _redoHistory = new();
    private DateOnly _displayMonth;
    private IReadOnlyList<CalendarItem> _items = Array.Empty<CalendarItem>();
    private IReadOnlySet<DateOnly> _diaryDates = new HashSet<DateOnly>();
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private bool _disposed;
    private AppSettings _settings;

    public string Title => "日程表";
    public string MonthTitle => $"{_displayMonth.Year} 年 {_displayMonth.Month} 月";
    public DateOnly DisplayMonth => _displayMonth;
    public DateOnly VisibleStart => StartOfCalendar(_displayMonth);
    public DateOnly VisibleEndExclusive => VisibleStart.AddDays(42);
    public IReadOnlyList<CalendarItem> Items => _items;
    public IReadOnlySet<DateOnly> DiaryDates => _diaryDates;
    public bool CanUndo => _undoHistory.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;
    public AppSettings Settings => _settings;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CalendarChanged;

    public HomePageViewModel(
        CalendarService calendarService,
        DiaryService diaryService,
        IAppLogger logger,
        AppSettings settings)
    {
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _diaryService = diaryService ?? throw new ArgumentNullException(nameof(diaryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var today = DateOnly.FromDateTime(DateTime.Today);
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
    }

    public async Task InitializeAsync()
    {
        await RefreshDiaryDatesAsync().ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task RefreshDiaryDatesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entries = await _diaryService.GetAllAsync().ConfigureAwait(true);
        _diaryDates = entries.Select(entry => entry.Date).ToHashSet();
        OnPropertyChanged(nameof(DiaryDates));
        CalendarChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplySettings(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        OnPropertyChanged(nameof(Settings));
        CalendarChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ChangeMonthAsync(int months)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _displayMonth = _displayMonth.AddMonths(months);
        NotifyMonthChanged();
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task ChangeYearAsync(int years)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _displayMonth = _displayMonth.AddYears(years);
        NotifyMonthChanged();
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task GoToTodayAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        _displayMonth = new DateOnly(today.Year, today.Month, 1);
        NotifyMonthChanged();
        await ReloadAsync().ConfigureAwait(true);
    }

    public async Task<CalendarItem> CreateAsync(CalendarItemDraft draft)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = await _calendarService.CreateAsync(draft).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Create, null, item));
        await ReloadAsync().ConfigureAwait(true);
        StatusMessage = "已添加到日程表。";
        return item;
    }

    public async Task<IReadOnlyList<CalendarItem>> CreateKeyTaskAsync(KeyTaskSchedule schedule)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var items = await _calendarService.CreateKeyBatchAsync(schedule).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.BatchCreate, null, null, items));
        await ReloadAsync().ConfigureAwait(true);
        StatusMessage = $"Task 已批量添加 {items.Count} 个 Key。";
        return items;
    }

    public async Task<IReadOnlyList<CalendarItem>> EditKeyTaskAsync(
        CalendarItem occurrence,
        KeyTaskSchedule schedule)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var batchId = occurrence.TaskBatch?.BatchId
            ?? throw new ArgumentException("该 Key 不属于 Task 批次。", nameof(occurrence));
        var change = await _calendarService.ReplaceKeyTaskBatchAsync(batchId, schedule).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(
            HistoryAction.BatchUpdate, null, null, change.After, change.Before));
        await ReloadAsync().ConfigureAwait(true);
        StatusMessage = $"Task 批次已更新，共 {change.After.Count} 个 Key。";
        return change.After;
    }

    public async Task<CalendarItem> EditAsync(CalendarItem original, CalendarItemDraft draft)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(draft);

        var updated = await _calendarService.UpdateAsync(original, draft).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Update, original, updated));
        await ReloadAsync().ConfigureAwait(true);
        StatusMessage = "日程内容已更新。";
        return updated;
    }

    public async Task UpdateBoundaryAsync(
        CalendarItem item,
        bool updateStart,
        DateTimeOffset newBoundary)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != CalendarItemKind.Class)
            throw new ArgumentException("只有 Class 可以拖动日期边界。", nameof(item));

        CalendarItem updated;
        if (updateStart)
        {
            if (item.EndsAt is not null && newBoundary > item.EndsAt)
                throw new ArgumentException("开始时间不能晚于结束时间。", nameof(newBoundary));
            updated = item with { StartsAt = newBoundary };
        }
        else
        {
            if (newBoundary < item.StartsAt)
                throw new ArgumentException("结束时间不能早于开始时间。", nameof(newBoundary));
            updated = item with { EndsAt = newBoundary };
        }

        await _calendarService.SaveAsync(updated).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Update, item, updated));
        _items = _items.Select(candidate => candidate.Id == updated.Id ? updated : candidate).ToArray();
        OnPropertyChanged(nameof(Items));
        CalendarChanged?.Invoke(this, EventArgs.Empty);
        await ReloadAsync().ConfigureAwait(true);
        StatusMessage = "Class 起止时间已更新。";
    }

    public async Task<CalendarItem> ToggleIdeaAsync(CalendarItem item, bool completed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (item.Kind != CalendarItemKind.Idea)
            throw new ArgumentException("只有 Idea 支持完成状态。", nameof(item));
        return await ToggleCompletedAsync(item, completed).ConfigureAwait(true);
    }

    public async Task<CalendarItem> ToggleCompletedAsync(CalendarItem item, bool completed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is not (CalendarItemKind.Class or CalendarItemKind.Key or CalendarItemKind.Idea))
            throw new ArgumentException("该日程不支持完成状态。", nameof(item));
        var updated = item with { IsCompleted = completed };
        await _calendarService.SaveAsync(updated).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Update, item, updated));
        _items = _items.Select(candidate => candidate.Id == updated.Id ? updated : candidate).ToArray();
        OnPropertyChanged(nameof(Items));
        CalendarChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage = completed
            ? $"{item.Kind} 已完成。"
            : $"{item.Kind} 已恢复为未完成。";
        return updated;
    }

    public async Task DeleteAsync(CalendarItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        await _calendarService.DeleteAsync(item.Id).ConfigureAwait(true);
        RecordHistory(new HistoryEntry(HistoryAction.Delete, item, null));
        await ReloadAsync().ConfigureAwait(true);
        StatusMessage = "日程已删除。";
    }

    public async Task UndoAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_undoHistory.Count == 0) return;

        using var operation = _logger.BeginOperation("calendar.undo");
        var entry = _undoHistory.Pop();
        NotifyHistoryChanged();
        try
        {
            await ApplyHistoryAsync(entry, undo: true).ConfigureAwait(true);
            _redoHistory.Push(entry);
            NotifyHistoryChanged();
            await ReloadAsync().ConfigureAwait(true);
            StatusMessage = "已撤回上一步操作。";
            operation.Complete();
        }
        catch (Exception exception)
        {
            _undoHistory.Push(entry);
            NotifyHistoryChanged();
            operation.Fail(exception);
            throw;
        }
    }

    public async Task RedoAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_redoHistory.Count == 0) return;

        using var operation = _logger.BeginOperation("calendar.redo");
        var entry = _redoHistory.Pop();
        NotifyHistoryChanged();
        try
        {
            await ApplyHistoryAsync(entry, undo: false).ConfigureAwait(true);
            _undoHistory.Push(entry);
            NotifyHistoryChanged();
            await ReloadAsync().ConfigureAwait(true);
            StatusMessage = "已回滚上一步撤回。";
            operation.Complete();
        }
        catch (Exception exception)
        {
            _redoHistory.Push(entry);
            NotifyHistoryChanged();
            operation.Fail(exception);
            throw;
        }
    }

    public IReadOnlyList<CalendarItem> GetItemsForDate(DateOnly date) => _items
        .Where(item => CoversDate(item, date))
        .OrderBy(item => item.IsCompleted)
        .ThenBy(item => item.Kind)
        .ThenBy(item => item.StartsAt)
        .ToArray();

    public Task RefreshAsync() => ReloadAsync();

    public async Task ResetAfterDataClearAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _undoHistory.Clear();
        _redoHistory.Clear();
        NotifyHistoryChanged();
        await ReloadAsync().ConfigureAwait(true);
        StatusMessage = "日程已清空。";
    }

    private async Task ReloadAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            IsLoading = true;
            StatusMessage = string.Empty;
            _items = await _calendarService.GetRangeAsync(VisibleStart, VisibleEndExclusive)
                .ConfigureAwait(true);
            OnPropertyChanged(nameof(Items));
            CalendarChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
        }
    }

    private async Task ApplyHistoryAsync(HistoryEntry entry, bool undo)
    {
        var target = undo ? entry.Before : entry.After;
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
                await _calendarService.SaveAsync(target!).ConfigureAwait(true);
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
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void NotifyMonthChanged()
    {
        OnPropertyChanged(nameof(DisplayMonth));
        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(VisibleStart));
        OnPropertyChanged(nameof(VisibleEndExclusive));
    }

    private static DateOnly StartOfCalendar(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        return first.AddDays(-(int)first.DayOfWeek);
    }

    public static bool CoversDate(CalendarItem item, DateOnly date)
    {
        var start = DateOnly.FromDateTime(item.StartsAt.LocalDateTime);
        if (item.Kind != CalendarItemKind.Class && item.EndsAt is null) return date == start;
        var end = item.EndsAt is null
            ? DateOnly.MaxValue
            : InclusiveEndDate(item.EndsAt.Value);
        return date >= start && date <= end;
    }

    private static DateOnly InclusiveEndDate(DateTimeOffset end)
    {
        var local = end.LocalDateTime;
        var date = DateOnly.FromDateTime(local);
        return local.TimeOfDay == TimeSpan.Zero ? date.AddDays(-1) : date;
    }

    private static DateTimeOffset WithLocalDate(DateTimeOffset original, DateOnly date)
    {
        var localTime = TimeOnly.FromDateTime(original.LocalDateTime);
        var local = date.ToDateTime(localTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static DateTimeOffset AtLocalEndOfDay(DateOnly date)
    {
        var local = date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadGate.Dispose();
        _undoHistory.Clear();
        _redoHistory.Clear();
        CalendarChanged = null;
        PropertyChanged = null;
    }

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
