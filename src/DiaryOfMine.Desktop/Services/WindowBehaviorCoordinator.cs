using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Desktop.Views;
using DiaryOfMine.Desktop.ViewModels;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Services;

internal sealed class WindowBehaviorCoordinator : IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly FloatingWindow _floatingWindow;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private readonly ApplicationActivityState _activityState;
    private readonly Func<bool> _isExiting;
    private readonly HashSet<Window> _companionWindows = new();
    private readonly HashSet<Window> _escapeClosableCompanions = new();
    private readonly HashSet<Window> _floatingPersistentCompanions = new();
    private readonly HashSet<Window> _autoFloatProtectedCompanions = new();
    private readonly HashSet<Window> _hiddenCompanions = new();
    private AppSettings _settings;
    private int _autoFloatSuspensionCount;
    private Window? _lastActiveCompanion;
    private readonly DispatcherTimer _autoFloatTimer;
    private bool _isRestoringWindowSet;
    private HwndSource? _hotKeySource;
    private bool _hotKeyRegistered;
    private bool _disposed;

    public AppSettings CurrentSettings => _settings;
    public bool IsRestoringWindowSet => _isRestoringWindowSet;
    public string? ExpandHotKeyError { get; private set; }
    public event Action<AppSettings>? SettingsChanged;
    public event EventHandler? FloatingWindowPlacementChanged;
    public event EventHandler? WorkRecordingRequested;
    public event Action<Guid>? WorkGoalSelected;
    internal Window FloatingWindow => _floatingWindow;

    public WindowBehaviorCoordinator(
        MainWindow mainWindow,
        AppSettings settings,
        IAppSettingsStore settingsStore,
        IAppLogger logger,
        ApplicationActivityState activityState,
        Func<bool> isExiting)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _activityState = activityState ?? throw new ArgumentNullException(nameof(activityState));
        _isExiting = isExiting ?? throw new ArgumentNullException(nameof(isExiting));

        _floatingWindow = new FloatingWindow();
        _autoFloatTimer = new DispatcherTimer(
            DispatcherPriority.ApplicationIdle,
            _mainWindow.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _autoFloatTimer.Tick += OnAutoFloatTimerTick;
        _floatingWindow.SetExpandHotKeyText(_settings.ExpandHotKey);
        _mainWindow.Activated += OnMainWindowActivated;
        _mainWindow.Deactivated += OnMainWindowDeactivated;
        _mainWindow.StateChanged += OnMainWindowStateChanged;
        _mainWindow.SourceInitialized += OnMainWindowSourceInitialized;
        _mainWindow.EscapeRequested += EnterFloatingMode;
        _mainWindow.CloseToBackgroundRequested += OnCloseToBackgroundRequested;
        _floatingWindow.RestoreRequested += OnRestoreRequested;
        _floatingWindow.PositionCommitted += OnFloatingPositionCommitted;
        _floatingWindow.QuickCreateRequested += OnQuickCreateRequested;
        _floatingWindow.QuickTaskCreateRequested += OnQuickTaskCreateRequested;
        _floatingWindow.WorkRecordingRequested += OnWorkRecordingRequested;
        _floatingWindow.WorkGoalSelected += OnWorkGoalSelected;
        _floatingWindow.SettingsRequested += OnSettingsRequested;
    }

    public IDisposable SuspendAutoFloat()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _autoFloatSuspensionCount);
        return new SuspensionScope(this);
    }

    public void RegisterCompanion(
        Window window,
        bool closeOnEscape = true,
        bool hideInFloatingMode = true,
        bool protectFromAutoFloat = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (!_companionWindows.Add(window)) return;
        window.Activated += OnCompanionActivated;
        window.Deactivated += OnAnyWindowDeactivated;
        if (closeOnEscape)
        {
            _escapeClosableCompanions.Add(window);
            window.PreviewKeyDown += OnCompanionPreviewKeyDown;
        }
        if (!hideInFloatingMode) _floatingPersistentCompanions.Add(window);
        if (protectFromAutoFloat) _autoFloatProtectedCompanions.Add(window);
        window.Closed += OnCompanionClosed;
    }

    public void UnregisterCompanion(Window window)
    {
        if (!_companionWindows.Remove(window)) return;
        window.Activated -= OnCompanionActivated;
        window.Deactivated -= OnAnyWindowDeactivated;
        if (_escapeClosableCompanions.Remove(window))
            window.PreviewKeyDown -= OnCompanionPreviewKeyDown;
        _floatingPersistentCompanions.Remove(window);
        _autoFloatProtectedCompanions.Remove(window);
        window.Closed -= OnCompanionClosed;
        _hiddenCompanions.Remove(window);
        if (ReferenceEquals(_lastActiveCompanion, window))
            _lastActiveCompanion = null;
    }

    public void ApplySettings(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operation = _logger.BeginOperation("window.settings.apply");
        try
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (!_settings.AutoFloatAndSleepWhenInactive)
                _autoFloatTimer.Stop();
            _floatingWindow.SetExpandHotKeyText(_settings.ExpandHotKey);
            if (_floatingWindow.IsVisible)
                ApplyFloatingWindowPlacement();
            RefreshExpandHotKeyRegistration();
            SettingsChanged?.Invoke(_settings);
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public void ShowMainWindow()
    {
        if (_disposed || _isExiting()) return;

        using var operation = _logger.BeginOperation("window.main.restore");
        try
        {
            _isRestoringWindowSet = true;
            UnregisterExpandHotKey();
            _floatingWindow.Hide();
            _activityState.Wake();
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.Focus();
            foreach (var companion in _hiddenCompanions
                         .Where(window => _companionWindows.Contains(window))
                         .ToArray())
            {
                companion.Show();
            }
            _hiddenCompanions.Clear();
            if (_lastActiveCompanion is { IsVisible: true } active)
                active.Activate();
            _mainWindow.Dispatcher.BeginInvoke(
                () => _isRestoringWindowSet = false,
                DispatcherPriority.ApplicationIdle);
            operation.Complete();
        }
        catch (Exception exception)
        {
            _isRestoringWindowSet = false;
            operation.Fail(exception);
            throw;
        }
    }

    private void OnMainWindowActivated(object? sender, EventArgs e) => _autoFloatTimer.Stop();

    private void OnMainWindowDeactivated(object? sender, EventArgs e) => QueueAutoFloatCheck();

    private void OnAnyWindowDeactivated(object? sender, EventArgs e) => QueueAutoFloatCheck();

    private void OnCompanionActivated(object? sender, EventArgs e)
    {
        _autoFloatTimer.Stop();
        if (sender is Window window) _lastActiveCompanion = window;
    }

    private void OnCompanionPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Window window) return;
        e.Handled = true;
        if (window is CalendarItemEditorWindow editor)
            editor.CancelAndClose();
        else
            window.Close();
    }

    private void OnCompanionClosed(object? sender, EventArgs e)
    {
        if (sender is Window window) UnregisterCompanion(window);
        QueueAutoFloatCheck();
    }

    private void QueueAutoFloatCheck()
    {
        if (_disposed
            || _isExiting()
            || _isRestoringWindowSet
            || !_settings.AutoFloatAndSleepWhenInactive
            || Volatile.Read(ref _autoFloatSuspensionCount) > 0
            || !_mainWindow.IsVisible
            || _mainWindow.WindowState == WindowState.Minimized
            || HasVisibleAutoFloatProtectedWindow())
            return;

        _autoFloatTimer.Stop();
        _autoFloatTimer.Start();
    }

    private void OnAutoFloatTimerTick(object? sender, EventArgs e)
    {
        _autoFloatTimer.Stop();
        if (_disposed
            || _isExiting()
            || _isRestoringWindowSet
            || !_settings.AutoFloatAndSleepWhenInactive
            || !_mainWindow.IsVisible
            || _mainWindow.WindowState == WindowState.Minimized
            || Volatile.Read(ref _autoFloatSuspensionCount) > 0
            || _mainWindow.IsActive
            || HasVisibleAutoFloatProtectedWindow()
            || IsCurrentProcessForeground())
            return;

        EnterFloatingMode();
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || _isExiting() || _mainWindow.WindowState != WindowState.Minimized)
            return;

        _autoFloatTimer.Stop();
        if (HasVisibleAutoFloatProtectedWindow()
            || Volatile.Read(ref _autoFloatSuspensionCount) > 0)
        {
            RestoreProtectedWindowSet();
            return;
        }
        if (!_settings.AutoFloatAndSleepWhenInactive) return;

        _mainWindow.Dispatcher.BeginInvoke(
            EnterFloatingMode,
            DispatcherPriority.ApplicationIdle);
    }

    private void OnCloseToBackgroundRequested()
    {
        _autoFloatTimer.Stop();
        if (HasVisibleAutoFloatProtectedWindow())
        {
            ActivateLastProtectedCompanion();
            return;
        }

        if (_settings.AutoFloatAndSleepWhenInactive)
            EnterFloatingMode();
        else
            HideToTray();
    }

    private void RestoreProtectedWindowSet()
    {
        _mainWindow.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _isExiting()) return;
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            ActivateLastProtectedCompanion();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void ActivateLastProtectedCompanion()
    {
        var companion = _lastActiveCompanion is { IsVisible: true } last
                        && _autoFloatProtectedCompanions.Contains(last)
            ? last
            : _autoFloatProtectedCompanions.FirstOrDefault(window => window.IsVisible);
        companion?.Activate();
    }

    private bool HasVisibleAutoFloatProtectedWindow() =>
        _autoFloatProtectedCompanions.Any(window => window.IsVisible);

    private static bool IsCurrentProcessForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void HideToTray()
    {
        if (_disposed || _isExiting()) return;

        using var operation = _logger.BeginOperation("window.main.hide-to-tray");
        try
        {
            _hiddenCompanions.Clear();
            foreach (var companion in _companionWindows
                         .Where(window => window.IsVisible
                             && !_floatingPersistentCompanions.Contains(window))
                         .ToArray())
            {
                _hiddenCompanions.Add(companion);
                companion.Hide();
            }
            _mainWindow.Hide();
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    public void EnterFloatingMode()
    {
        if (_disposed || _isExiting() || _floatingWindow.IsVisible) return;

        using var operation = _logger.BeginOperation("window.floating.show");
        try
        {
            _hiddenCompanions.Clear();
            foreach (var companion in _companionWindows
                         .Where(window => window.IsVisible
                             && !_floatingPersistentCompanions.Contains(window))
                         .ToArray())
            {
                _hiddenCompanions.Add(companion);
                companion.Hide();
            }
            _mainWindow.Hide();
            _activityState.Sleep();
            ApplyFloatingWindowPlacement();
            _floatingWindow.Show();
            FloatingWindowPlacementChanged?.Invoke(this, EventArgs.Empty);
            RefreshExpandHotKeyRegistration();
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    private void OnMainWindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_mainWindow).Handle;
        _hotKeySource = HwndSource.FromHwnd(handle);
        _hotKeySource?.AddHook(OnWindowMessage);
        RefreshExpandHotKeyRegistration();
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotKey
            || wParam.ToInt32() != NativeMethods.ExpandHotKeyId)
            return IntPtr.Zero;
        handled = true;
        if (_floatingWindow.IsVisible) ShowMainWindow();
        return IntPtr.Zero;
    }

    private void RefreshExpandHotKeyRegistration()
    {
        ExpandHotKeyError = null;
        UnregisterExpandHotKey();
        if (_hotKeySource is null) return;
        if (!HotKeyGesture.TryParse(_settings.ExpandHotKey, out var gesture, out var error))
        {
            ExpandHotKeyError = error;
            return;
        }

        var handle = _hotKeySource.Handle;
        var modifiers = ToNativeModifiers(gesture!.Modifiers) | NativeMethods.ModNoRepeat;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (!NativeMethods.RegisterHotKey(
                handle, NativeMethods.ExpandHotKeyId, modifiers, virtualKey))
        {
            ExpandHotKeyError = $"{gesture.DisplayText} 已被其他程序占用";
            return;
        }
        _hotKeyRegistered = true;

        // 展开状态只探测可用性，不持续拦截用户在其他程序中的按键。
        if (!_floatingWindow.IsVisible) UnregisterExpandHotKey();
    }

    private void UnregisterExpandHotKey()
    {
        if (!_hotKeyRegistered || _hotKeySource is null) return;
        NativeMethods.UnregisterHotKey(
            _hotKeySource.Handle, NativeMethods.ExpandHotKeyId);
        _hotKeyRegistered = false;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= NativeMethods.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= NativeMethods.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= NativeMethods.ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= NativeMethods.ModWin;
        return result;
    }

    private void ApplyFloatingWindowPlacement()
    {
        _floatingWindow.Topmost = _settings.PinFloatingWindow;

        double left;
        double top;
        if (_settings.PinFloatingWindow)
        {
            left = _settings.FloatingWindowLeft;
            top = _settings.FloatingWindowTop;
        }
        else
        {
            left = SystemParameters.WorkArea.Right - _floatingWindow.Width - 24;
            top = SystemParameters.WorkArea.Bottom - _floatingWindow.Height - 24;
        }

        var maximumLeft = SystemParameters.VirtualScreenLeft
            + SystemParameters.VirtualScreenWidth - _floatingWindow.Width;
        var maximumTop = SystemParameters.VirtualScreenTop
            + SystemParameters.VirtualScreenHeight - _floatingWindow.Height;
        _floatingWindow.Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, maximumLeft);
        _floatingWindow.Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, maximumTop);
        FloatingWindowPlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    internal (Rect Bounds, Rect WorkArea)? GetFloatingPlacementContext()
    {
        if (!_floatingWindow.IsVisible) return null;
        var handle = new WindowInteropHelper(_floatingWindow).Handle;
        var screen = handle == IntPtr.Zero
            ? System.Windows.Forms.Screen.PrimaryScreen
            : System.Windows.Forms.Screen.FromHandle(handle);
        if (screen is null) return null;
        var dpi = VisualTreeHelper.GetDpi(_floatingWindow);
        var area = screen.WorkingArea;
        return (
            new Rect(_floatingWindow.Left, _floatingWindow.Top,
                _floatingWindow.ActualWidth > 0 ? _floatingWindow.ActualWidth : _floatingWindow.Width,
                _floatingWindow.ActualHeight > 0 ? _floatingWindow.ActualHeight : _floatingWindow.Height),
            new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
    }

    private void OnRestoreRequested(object? sender, EventArgs e) => ShowMainWindow();

    private void OnQuickCreateRequested(CalendarItemKind kind) =>
        _mainWindow.RequestCalendarItemCreation(kind, null);

    private void OnQuickTaskCreateRequested(object? sender, EventArgs e) =>
        _mainWindow.RequestKeyTaskCreation(null);

    private void OnWorkRecordingRequested(object? sender, EventArgs e) =>
        WorkRecordingRequested?.Invoke(this, EventArgs.Empty);

    private void OnWorkGoalSelected(Guid goalId) => WorkGoalSelected?.Invoke(goalId);

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        ShowMainWindow();
        if (_mainWindow.DataContext is ShellViewModel shell)
            shell.TryNavigateTo("settings");
    }

    public void SetWorkRecordingState(bool isRecording) =>
        _floatingWindow.SetWorkRecordingState(isRecording);

    public void ShowFloatingWorkGoals(
        IReadOnlyCollection<FloatingWorkGoalOption> goals, bool isRecording) =>
        _floatingWindow.ShowWorkGoals(goals, isRecording);

    private void OnFloatingPositionCommitted(object? sender, EventArgs e)
    {
        if (_disposed || !_settings.PinFloatingWindow) return;

        using var operation = _logger.BeginOperation("window.floating.position-save");
        var updated = _settings with
        {
            FloatingWindowLeft = _floatingWindow.Left,
            FloatingWindowTop = _floatingWindow.Top
        };

        try
        {
            _settings = _settingsStore.Save(updated);
            SettingsChanged?.Invoke(_settings);
            FloatingWindowPlacementChanged?.Invoke(this, EventArgs.Empty);
            operation.Complete();
        }
        catch (IOException exception)
        {
            operation.Fail(exception);
            // 拖动位置保存失败不应关闭悬浮窗；用户可稍后在设置页重试。
        }
        catch (UnauthorizedAccessException exception)
        {
            operation.Fail(exception);
            // 不尝试写入其他目录或提升权限。
        }
    }

    private void ResumeAutoFloat()
    {
        if (_disposed) return;
        var timer = new DispatcherTimer(
            DispatcherPriority.ApplicationIdle,
            _mainWindow.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        EventHandler? resume = null;
        resume = (_, _) =>
        {
            timer.Stop();
            timer.Tick -= resume;
            if (_disposed) return;
            if (Interlocked.Decrement(ref _autoFloatSuspensionCount) < 0)
                Interlocked.Exchange(ref _autoFloatSuspensionCount, 0);
            QueueAutoFloatCheck();
        };
        timer.Tick += resume;
        timer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoFloatTimer.Stop();
        _autoFloatTimer.Tick -= OnAutoFloatTimerTick;
        _mainWindow.Activated -= OnMainWindowActivated;
        _mainWindow.Deactivated -= OnMainWindowDeactivated;
        _mainWindow.StateChanged -= OnMainWindowStateChanged;
        _mainWindow.SourceInitialized -= OnMainWindowSourceInitialized;
        _mainWindow.EscapeRequested -= EnterFloatingMode;
        _mainWindow.CloseToBackgroundRequested -= OnCloseToBackgroundRequested;
        UnregisterExpandHotKey();
        _hotKeySource?.RemoveHook(OnWindowMessage);
        _hotKeySource = null;
        foreach (var companion in _companionWindows.ToArray())
            UnregisterCompanion(companion);
        _floatingWindow.RestoreRequested -= OnRestoreRequested;
        _floatingWindow.PositionCommitted -= OnFloatingPositionCommitted;
        _floatingWindow.QuickCreateRequested -= OnQuickCreateRequested;
        _floatingWindow.QuickTaskCreateRequested -= OnQuickTaskCreateRequested;
        _floatingWindow.WorkRecordingRequested -= OnWorkRecordingRequested;
        _floatingWindow.WorkGoalSelected -= OnWorkGoalSelected;
        _floatingWindow.SettingsRequested -= OnSettingsRequested;
        WorkRecordingRequested = null;
        WorkGoalSelected = null;
        _floatingWindow.Close();
        SettingsChanged = null;
        FloatingWindowPlacementChanged = null;
    }

    private sealed class SuspensionScope : IDisposable
    {
        private WindowBehaviorCoordinator? _owner;

        public SuspensionScope(WindowBehaviorCoordinator owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ResumeAutoFloat();
    }

    private static class NativeMethods
    {
        public const int WmHotKey = 0x0312;
        public const int ExpandHotKeyId = 0x444D;
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        public const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(
            IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(
            IntPtr windowHandle, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
    }
}
