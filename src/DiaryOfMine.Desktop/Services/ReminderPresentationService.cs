using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DiaryOfMine.Application.Lifecycle;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Application.Reminders;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Desktop.Views;
using DiaryOfMine.Infrastructure.Storage;

namespace DiaryOfMine.Desktop.Services;

internal sealed class ReminderPresentationService : IDisposable
{
    private const double ScreenMargin = 22;
    private const double FloatingGap = 10;

    private readonly MainWindow _mainWindow;
    private readonly WindowBehaviorCoordinator _windowBehavior;
    private readonly IApplicationActivityState _activityState;
    private readonly ReminderScheduler _scheduler;
    private readonly ReminderAcknowledgementStore _acknowledgements;
    private readonly IAppLogger _logger;
    private readonly ReminderWindow _window;
    private AppSettings _settings;
    private bool _disposed;

    public ReminderPresentationService(
        MainWindow mainWindow,
        WindowBehaviorCoordinator windowBehavior,
        IApplicationActivityState activityState,
        ReminderScheduler scheduler,
        ReminderAcknowledgementStore acknowledgements,
        IAppLogger logger,
        AppSettings settings)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _windowBehavior = windowBehavior ?? throw new ArgumentNullException(nameof(windowBehavior));
        _activityState = activityState ?? throw new ArgumentNullException(nameof(activityState));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _acknowledgements = acknowledgements
            ?? throw new ArgumentNullException(nameof(acknowledgements));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _window = new ReminderWindow();
        _windowBehavior.RegisterCompanion(
            _window,
            closeOnEscape: false,
            hideInFloatingMode: false,
            protectFromAutoFloat: false);
        _window.ConfirmationRequested += OnConfirmationRequested;
        _window.AllConfirmed += OnAllConfirmed;
        _scheduler.RemindersDue += OnRemindersDue;
        _activityState.StateChanged += OnActivityStateChanged;
        _windowBehavior.FloatingWindowPlacementChanged += OnFloatingWindowPlacementChanged;
        _mainWindow.LocationChanged += OnMainWindowPlacementChanged;
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_disposed) return;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (!_settings.EnableNotifications)
        {
            _window.ClearAndHide();
            return;
        }

        _window.RemoveWhere(notification => notification.Kind switch
        {
            ReminderKind.ClassDeadline => !_settings.EnableClassNotifications,
            ReminderKind.KeyStart => !_settings.EnableKeyNotifications,
            ReminderKind.IdeaDaily => !_settings.EnableIdeaNotifications,
            _ => true
        });
        if (_window.HasPendingItems) PositionAndShow();
    }

    private void OnRemindersDue(object? sender, ReminderBatchEventArgs e)
    {
        if (_disposed || !_settings.EnableNotifications || e.Reminders.Count == 0) return;
        _window.AddReminders(e.Reminders);
        PositionAndShow();
        SystemSounds.Exclamation.Play();
    }

    private void OnConfirmationRequested(
        object? sender,
        ReminderConfirmationRequestedEventArgs e)
    {
        using var operation = _logger.BeginOperation("reminder.acknowledge");
        try
        {
            _acknowledgements.Acknowledge(e.Notification.Key, DateTimeOffset.Now);
            e.Accepted = true;
            operation.Complete();
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            operation.Fail(exception);
            e.ErrorMessage = $"无法保存确认记录：{exception.Message}";
        }
    }

    private void OnAllConfirmed(object? sender, EventArgs e) => _window.ClearAndHide();

    private void OnActivityStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_window.HasPendingItems) return;
        // 展开主界面时先关闭悬浮位置的提醒，再按唤醒模式重新呈现。
        _window.Hide();
        _mainWindow.Dispatcher.BeginInvoke(
            PositionAndShow,
            DispatcherPriority.ApplicationIdle);
    }

    private void OnFloatingWindowPlacementChanged(object? sender, EventArgs e)
    {
        if (!_disposed && _activityState.IsSleeping && _window.HasPendingItems)
            PositionAndShow();
    }

    private void OnMainWindowPlacementChanged(object? sender, EventArgs e)
    {
        if (!_disposed && !_activityState.IsSleeping && _window.HasPendingItems)
            PositionAndShow();
    }

    private void PositionAndShow()
    {
        if (_disposed || !_window.HasPendingItems || !_settings.EnableNotifications) return;

        _window.SetPresentationMode(_activityState.IsSleeping);
        if (!_window.IsVisible) _window.Show();
        _window.UpdateLayout();

        var width = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
        var height = _window.ActualHeight > 0 ? _window.ActualHeight : 260;
        if (_activityState.IsSleeping
            && _windowBehavior.GetFloatingPlacementContext() is { } floating)
        {
            var left = floating.Bounds.Left + ((floating.Bounds.Width - width) / 2);
            var below = floating.Bounds.Bottom + FloatingGap;
            var top = below + height <= floating.WorkArea.Bottom
                ? below
                : floating.Bounds.Top - FloatingGap - height;
            _window.Left = Math.Clamp(
                left,
                floating.WorkArea.Left + ScreenMargin,
                floating.WorkArea.Right - width - ScreenMargin);
            _window.Top = Math.Clamp(
                top,
                floating.WorkArea.Top + ScreenMargin,
                floating.WorkArea.Bottom - height - ScreenMargin);
            return;
        }

        var workArea = GetMainWindowWorkArea();
        _window.Left = workArea.Right - width - ScreenMargin;
        _window.Top = workArea.Bottom - height - ScreenMargin;
    }

    private Rect GetMainWindowWorkArea()
    {
        var handle = new WindowInteropHelper(_mainWindow).Handle;
        var screen = handle == IntPtr.Zero
            ? System.Windows.Forms.Screen.PrimaryScreen
            : System.Windows.Forms.Screen.FromHandle(handle);
        if (screen is null) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(_mainWindow);
        var area = screen.WorkingArea;
        return new Rect(
            area.Left / dpi.DpiScaleX,
            area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX,
            area.Height / dpi.DpiScaleY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mainWindow.LocationChanged -= OnMainWindowPlacementChanged;
        _windowBehavior.FloatingWindowPlacementChanged -= OnFloatingWindowPlacementChanged;
        _activityState.StateChanged -= OnActivityStateChanged;
        _scheduler.RemindersDue -= OnRemindersDue;
        _window.ConfirmationRequested -= OnConfirmationRequested;
        _window.AllConfirmed -= OnAllConfirmed;
        _windowBehavior.UnregisterCompanion(_window);
        _window.CloseForApplicationExit();
    }
}
