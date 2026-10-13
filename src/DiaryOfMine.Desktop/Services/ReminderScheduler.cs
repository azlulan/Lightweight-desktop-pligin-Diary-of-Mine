using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Application.Reminders;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Infrastructure.Storage;

namespace DiaryOfMine.Desktop.Services;

internal sealed class ReminderScheduler : IReminderScheduler
{
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumTimerInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan MinimumTimerInterval = TimeSpan.FromSeconds(1);

    private readonly ICalendarRepository _repository;
    private readonly ReminderAcknowledgementStore _acknowledgements;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _rescheduleGate = new(1, 1);
    private readonly HashSet<string> _deliveredKeys = new(StringComparer.Ordinal);
    private AppSettings _settings;
    private bool _disposed;

    public event EventHandler<ReminderBatchEventArgs>? RemindersDue;

    public ReminderScheduler(
        ICalendarRepository repository,
        ReminderAcknowledgementStore acknowledgements,
        IAppLogger logger,
        AppSettings settings)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _acknowledgements = acknowledgements
            ?? throw new ArgumentNullException(nameof(acknowledgements));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = MaximumTimerInterval
        };
        _timer.Tick += OnTimerTick;
    }

    public void ApplySettings(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _deliveredKeys.Clear();
        if (!_settings.EnableNotifications) _timer.Stop();
    }

    public async Task RescheduleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _rescheduleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            _timer.Stop();
            if (!_settings.EnableNotifications) return;

            using var operation = _logger.BeginOperation("reminder.reschedule");
            try
            {
                var items = await _repository.GetRangeAsync(
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue,
                    cancellationToken).ConfigureAwait(true);
                var now = DateTimeOffset.Now;
                var reminders = ReminderPlanner.Build(items, _settings);
                var due = reminders
                    .Where(reminder => reminder.TriggerAt <= now
                        && reminder.TriggerAt + CatchUpWindow >= now
                        && !_deliveredKeys.Contains(reminder.Key)
                        && !_acknowledgements.IsAcknowledged(reminder.Key))
                    .ToArray();

                foreach (var reminder in due) _deliveredKeys.Add(reminder.Key);
                if (due.Length > 0)
                    RemindersDue?.Invoke(this, new ReminderBatchEventArgs(due));

                var next = reminders
                    .Where(reminder => reminder.TriggerAt > now
                        && !_acknowledgements.IsAcknowledged(reminder.Key))
                    .Select(reminder => reminder.TriggerAt)
                    .DefaultIfEmpty(now + MaximumTimerInterval)
                    .Min();
                var delay = next - now;
                _timer.Interval = delay < MinimumTimerInterval
                    ? MinimumTimerInterval
                    : delay > MaximumTimerInterval
                        ? MaximumTimerInterval
                        : delay;
                _timer.Start();
                operation.Complete(due.Length > 0 ? $"due-{due.Length}" : "scheduled");
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException)
            {
                operation.Fail(exception);
                _timer.Interval = MaximumTimerInterval;
                _timer.Start();
            }
        }
        finally
        {
            _rescheduleGate.Release();
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            await RescheduleAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        RemindersDue = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ReminderBatchEventArgs(
    IReadOnlyList<ReminderNotification> reminders) : EventArgs
{
    public IReadOnlyList<ReminderNotification> Reminders { get; } = reminders;
}
