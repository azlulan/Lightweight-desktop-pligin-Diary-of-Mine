using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Application.WorkMode;
using DiaryOfMine.Desktop.Commands;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.ViewModels;

public sealed class WorkModePageViewModel : INotifyPropertyChanged
{
    private readonly WorkModeService _service;
    private WorkModeState _state = WorkModeState.Empty;
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);
    private AppSettings _settings;
    private string _statusMessage = string.Empty;
    private bool _busy;

    public WorkModePageViewModel(WorkModeService service, AppSettings settings)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        PreviousDayCommand = new RelayCommand(() => SelectedDate = SelectedDate.AddDays(-1));
        NextDayCommand = new RelayCommand(() => SelectedDate = SelectedDate.AddDays(1));
        TodayCommand = new RelayCommand(() => SelectedDate = DateOnly.FromDateTime(DateTime.Today));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? VisualsChanged;

    public ICommand PreviousDayCommand { get; }
    public ICommand NextDayCommand { get; }
    public ICommand TodayCommand { get; }

    public DateOnly SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate == value) return;
            _selectedDate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateText));
            OnPropertyChanged(nameof(IsToday));
            VisualsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string DateText => SelectedDate.ToDateTime(TimeOnly.MinValue).ToString("yyyy年 M月 d日  dddd");
    public bool IsToday => SelectedDate == DateOnly.FromDateTime(DateTime.Today);
    public AppSettings Settings => _settings;
    public WorkModeState State => _state;
    public bool IsRecording => _state.Sessions.Any(session => session.EndsAt is null);
    public bool IsInterrupting => _state.Interruptions.Any(item => item.EndsAt is null);
    public IReadOnlyList<WorkGoal> ActiveGoals => _state.Goals
        .Where(goal => !goal.IsArchived).OrderBy(goal => goal.SortOrder).ToArray();
    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    public async Task RefreshAsync(bool silent = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _state = await _service.GetSnapshotAsync().ConfigureAwait(true);
            if (!silent) StatusMessage = IsRecording ? "正在录制工作时间" : string.Empty;
            NotifyStateChanged();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally { _busy = false; }
    }

    public IReadOnlyList<WorkSession> SessionsForDate(DateOnly date)
    {
        var start = LocalBoundary(date);
        var end = start.AddDays(1);
        var now = DateTimeOffset.Now;
        return _state.Sessions.Where(session => session.StartsAt < end
            && EffectiveEnd(session, now) > start).OrderBy(session => session.StartsAt).ToArray();
    }

    public WorkGoal? FindGoal(Guid id) => _state.Goals.FirstOrDefault(goal => goal.Id == id);

    public async Task StartAsync(Guid goalId) => await ApplyAsync(
        () => _service.StartSessionAsync(goalId, DateTimeOffset.Now), "录制已开始。");

    public async Task FinishAsync() => await ApplyAsync(
        () => _service.FinishSessionAsync(DateTimeOffset.Now), "本次录制已完成。");

    public async Task ToggleInterruptAsync() => await ApplyAsync(
        () => _service.ToggleInterruptAsync(DateTimeOffset.Now),
        IsInterrupting ? "中断区块已结束。" : "已开始标记中断。");

    public async Task AddGoalAsync(string name) => await ApplyAsync(
        () => _service.AddGoalAsync(name), "工作目标已添加。");

    public async Task RenameGoalAsync(Guid goalId, string name) => await ApplyAsync(
        () => _service.RenameGoalAsync(goalId, name), "工作目标已修改。");

    public async Task ChangeGoalColorAsync(Guid goalId, string colorKey) => await ApplyAsync(
        () => _service.ChangeGoalColorAsync(goalId, colorKey), "工作目标颜色已修改。");

    public async Task DeleteGoalAsync(Guid goalId) => await ApplyAsync(
        () => _service.DeleteGoalAsync(goalId), "工作目标已移除，历史记录仍保留。");

    public async Task ChangeSessionGoalAsync(Guid sessionId, Guid goalId) => await ApplyAsync(
        () => _service.ChangeSessionGoalAsync(sessionId, goalId), "工作记录目标已修改。");

    public async Task DeleteSessionAsync(Guid sessionId) => await ApplyAsync(
        () => _service.DeleteSessionAsync(sessionId), "工作记录已删除。");

    public async Task AddManualInterruptionAsync(Guid sessionId,
        DateTimeOffset startsAt, DateTimeOffset endsAt) => await ApplyAsync(
        () => _service.AddManualInterruptionAsync(sessionId, startsAt, endsAt), "中断区块已添加。");

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        OnPropertyChanged(nameof(Settings));
        VisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    public static DateTimeOffset EffectiveEnd(WorkSession session, DateTimeOffset now)
    {
        var end = session.EndsAt ?? now;
        var max = session.StartsAt.AddHours(24);
        return end > max ? max : end;
    }

    public static DateTimeOffset LocalBoundary(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

    private async Task ApplyAsync(Func<Task<WorkModeState>> operation, string success)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _state = await operation().ConfigureAwait(true);
            StatusMessage = success;
            NotifyStateChanged();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or InvalidDataException)
        {
            StatusMessage = exception.Message;
        }
        finally { _busy = false; }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ActiveGoals));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsInterrupting));
        VisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
