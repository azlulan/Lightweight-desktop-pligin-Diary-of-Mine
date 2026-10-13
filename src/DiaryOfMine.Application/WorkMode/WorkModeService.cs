using System.Globalization;
using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Application.WorkMode;

public sealed class WorkModeService : IDisposable
{
    private readonly IWorkModeRepository _repository;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WorkModeService(IWorkModeRepository repository, IAppLogger logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkModeState> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
            var state = EnsureDefaults(loaded);
            var capped = CapExpiredRecording(state, DateTimeOffset.Now);
            var normalized = ValidateState(capped);
            if (loaded.Goals.Count == 0 || !ReferenceEquals(state, capped))
                await _repository.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
            return normalized;
        }
        finally { _gate.Release(); }
    }

    public Task<WorkModeState> AddGoalAsync(string name, string? colorKey = null,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        name = ValidateGoalName(name);
        EnsureUniqueName(state, name, null);
        var order = state.Goals.Count == 0 ? 0 : state.Goals.Max(goal => goal.SortOrder) + 1;
        var goal = new WorkGoal(Guid.NewGuid(), name,
            colorKey ?? WorkPalette.GetSlotKey((order % WorkPalette.SlotCount) + 1),
            false, false, order, DateTimeOffset.Now).Validate();
        return state with { Goals = state.Goals.Append(goal).ToArray() };
    }, "work.goal.add", cancellationToken);

    public Task<WorkModeState> RenameGoalAsync(Guid goalId, string name,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        name = ValidateGoalName(name);
        EnsureUniqueName(state, name, goalId);
        if (!state.Goals.Any(goal => goal.Id == goalId))
            throw new InvalidOperationException("工作目标不存在。");
        return state with
        {
            Goals = state.Goals.Select(goal => goal.Id == goalId
                ? goal with { Name = name }
                : goal).ToArray()
        };
    }, "work.goal.rename", cancellationToken);

    public Task<WorkModeState> ChangeGoalColorAsync(Guid goalId, string colorKey,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        if (!state.Goals.Any(goal => goal.Id == goalId))
            throw new InvalidOperationException("工作目标不存在。");
        return state with
        {
            Goals = state.Goals.Select(goal => goal.Id == goalId
                ? (goal with { ColorKey = WorkPalette.GetSlotKey(WorkPalette.GetSlotNumber(colorKey)) }).Validate()
                : goal).ToArray()
        };
    }, "work.goal.color", cancellationToken);

    public Task<WorkModeState> DeleteGoalAsync(Guid goalId,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        var goal = state.Goals.FirstOrDefault(candidate => candidate.Id == goalId)
            ?? throw new InvalidOperationException("工作目标不存在。");
        var used = state.Sessions.Any(session => session.GoalId == goalId);
        return state with
        {
            Goals = used
                ? state.Goals.Select(candidate => candidate.Id == goalId
                    ? candidate with { IsArchived = true }
                    : candidate).ToArray()
                : state.Goals.Where(candidate => candidate.Id != goalId).ToArray()
        };
    }, "work.goal.delete", cancellationToken);

    public Task<WorkModeState> StartSessionAsync(Guid goalId, DateTimeOffset when,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        when = FloorToMinute(when);
        if (!state.Goals.Any(goal => goal.Id == goalId && !goal.IsArchived))
            throw new InvalidOperationException("请选择有效的工作目标。");
        if (state.Sessions.Any(session => session.EndsAt is null))
            throw new InvalidOperationException("当前已有正在录制的工作，请先结束它。");
        var session = new WorkSession(Guid.NewGuid(), goalId, when, null, DateTimeOffset.Now).Validate();
        return state with { Sessions = state.Sessions.Append(session).ToArray() };
    }, "work.session.start", cancellationToken);

    public Task<WorkModeState> FinishSessionAsync(DateTimeOffset when,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        var active = state.Sessions.SingleOrDefault(session => session.EndsAt is null)
            ?? throw new InvalidOperationException("当前没有正在录制的工作。");
        var end = FloorToMinute(when);
        if (end <= active.StartsAt) end = active.StartsAt.AddMinutes(1);
        if (end - active.StartsAt > TimeSpan.FromHours(24)) end = active.StartsAt.AddHours(24);
        var sessions = state.Sessions.Select(session => session.Id == active.Id
            ? (session with { EndsAt = end }).Validate()
            : session).ToArray();
        var interruptions = CloseOpenInterruptions(state.Interruptions, active.Id, end);
        return state with { Sessions = sessions, Interruptions = interruptions };
    }, "work.session.finish", cancellationToken);

    public Task<WorkModeState> ToggleInterruptAsync(DateTimeOffset when,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        var active = state.Sessions.SingleOrDefault(session => session.EndsAt is null)
            ?? throw new InvalidOperationException("当前没有正在录制的工作。");
        when = FloorToMinute(when);
        var open = state.Interruptions.SingleOrDefault(item => item.EndsAt is null);
        if (open is not null)
        {
            var end = when <= open.StartsAt ? open.StartsAt.AddMinutes(1) : when;
            return state with
            {
                Interruptions = state.Interruptions.Select(item => item.Id == open.Id
                    ? (item with { EndsAt = end }).Validate()
                    : item).ToArray()
            };
        }
        var interruption = new WorkInterruption(
            Guid.NewGuid(), active.Id, when < active.StartsAt ? active.StartsAt : when,
            null, DateTimeOffset.Now).Validate();
        return state with { Interruptions = state.Interruptions.Append(interruption).ToArray() };
    }, "work.interrupt.toggle", cancellationToken);

    public Task<WorkModeState> AddManualInterruptionAsync(Guid sessionId,
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        var session = state.Sessions.FirstOrDefault(candidate => candidate.Id == sessionId)
            ?? throw new InvalidOperationException("工作记录不存在。");
        if (session.EndsAt is null)
            throw new InvalidOperationException("正在录制时请使用中断按钮。");
        startsAt = FloorToMinute(startsAt);
        endsAt = FloorToMinute(endsAt);
        if (startsAt < session.StartsAt || endsAt > session.EndsAt || endsAt <= startsAt)
            throw new ArgumentException("中断时间必须完整位于所选工作记录内。");
        if (state.Interruptions.Any(item => item.SessionId == sessionId
            && startsAt < (item.EndsAt ?? DateTimeOffset.MaxValue) && endsAt > item.StartsAt))
            throw new ArgumentException("中断时间不能与已有中断重叠。");
        var interruption = new WorkInterruption(
            Guid.NewGuid(), sessionId, startsAt, endsAt, DateTimeOffset.Now).Validate();
        return state with { Interruptions = state.Interruptions.Append(interruption).ToArray() };
    }, "work.interrupt.add", cancellationToken);

    public Task<WorkModeState> ChangeSessionGoalAsync(Guid sessionId, Guid goalId,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        if (!state.Goals.Any(goal => goal.Id == goalId && !goal.IsArchived))
            throw new InvalidOperationException("工作目标不存在。");
        if (!state.Sessions.Any(session => session.Id == sessionId))
            throw new InvalidOperationException("工作记录不存在。");
        return state with
        {
            Sessions = state.Sessions.Select(session => session.Id == sessionId
                ? session with { GoalId = goalId }
                : session).ToArray()
        };
    }, "work.session.goal", cancellationToken);

    public Task<WorkModeState> DeleteSessionAsync(Guid sessionId,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
        state with
        {
            Sessions = state.Sessions.Where(session => session.Id != sessionId).ToArray(),
            Interruptions = state.Interruptions.Where(item => item.SessionId != sessionId).ToArray()
        }, "work.session.delete", cancellationToken);

    private async Task<WorkModeState> MutateAsync(
        Func<WorkModeState, WorkModeState> mutate, string operationName,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operation = _logger.BeginOperation(operationName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = EnsureDefaults(await _repository.LoadAsync(cancellationToken).ConfigureAwait(false));
            var changed = ValidateState(mutate(CapExpiredRecording(state, DateTimeOffset.Now)));
            await _repository.SaveAsync(changed, cancellationToken).ConfigureAwait(false);
            operation.Complete();
            return changed;
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
        finally { _gate.Release(); }
    }

    private static WorkModeState EnsureDefaults(WorkModeState state)
    {
        if (state.Goals.Count > 0) return state;
        var now = DateTimeOffset.Now;
        var names = new[] { "备考", "冥想", "休息" };
        return state with
        {
            Goals = names.Select((name, index) => new WorkGoal(
                Guid.NewGuid(), name, WorkPalette.GetSlotKey(index + 1), true,
                false, index, now)).ToArray()
        };
    }

    private static WorkModeState ValidateState(WorkModeState state)
    {
        var goals = state.Goals.Select(goal => goal.Validate()).ToArray();
        if (goals.Select(goal => goal.Id).Distinct().Count() != goals.Length)
            throw new InvalidDataException("工作目标数据包含重复标识。");
        var sessions = state.Sessions.Select(session => session.Validate()).ToArray();
        if (sessions.Any(session => goals.All(goal => goal.Id != session.GoalId)))
            throw new InvalidDataException("工作记录引用了不存在的目标。");
        if (sessions.Count(session => session.EndsAt is null) > 1)
            throw new InvalidDataException("同时存在多条未结束的工作记录。");
        var interruptions = state.Interruptions.Select(item => item.Validate()).ToArray();
        if (interruptions.Any(item => sessions.All(session => session.Id != item.SessionId)))
            throw new InvalidDataException("中断记录引用了不存在的工作记录。");
        return state with
        {
            Goals = goals,
            Sessions = sessions,
            Interruptions = interruptions
        };
    }

    private static WorkModeState CapExpiredRecording(WorkModeState state, DateTimeOffset now)
    {
        var active = state.Sessions.SingleOrDefault(session => session.EndsAt is null);
        if (active is null || now - active.StartsAt < TimeSpan.FromHours(24)) return state;
        var end = active.StartsAt.AddHours(24);
        return state with
        {
            Sessions = state.Sessions.Select(session => session.Id == active.Id
                ? session with { EndsAt = end }
                : session).ToArray(),
            Interruptions = CloseOpenInterruptions(state.Interruptions, active.Id, end)
        };
    }

    private static IReadOnlyCollection<WorkInterruption> CloseOpenInterruptions(
        IReadOnlyCollection<WorkInterruption> interruptions, Guid sessionId, DateTimeOffset end) =>
        interruptions.Select(item => item.SessionId == sessionId && item.EndsAt is null
            ? item with { EndsAt = end > item.StartsAt ? end : item.StartsAt.AddMinutes(1) }
            : item).ToArray();

    private static DateTimeOffset FloorToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);

    private static string ValidateGoalName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("工作目标不能为空。", nameof(name));
        if (StringInfo.ParseCombiningCharacters(name).Length > 4)
            throw new ArgumentException("工作目标不能超过 4 个字符。", nameof(name));
        return name;
    }

    private static void EnsureUniqueName(WorkModeState state, string name, Guid? exceptId)
    {
        if (state.Goals.Any(goal => !goal.IsArchived && goal.Id != exceptId
            && string.Equals(goal.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException("已经存在同名工作目标。", nameof(name));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
