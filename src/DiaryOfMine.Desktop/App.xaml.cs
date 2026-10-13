using System;
using System.Drawing;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DiaryOfMine.Application.Calendar;
using DiaryOfMine.Application.Diary;
using DiaryOfMine.Application.WorkMode;
using DiaryOfMine.Desktop.Navigation;
using DiaryOfMine.Desktop.Services;
using DiaryOfMine.Desktop.ViewModels;
using DiaryOfMine.Desktop.Views;
using DiaryOfMine.Domain;
using DiaryOfMine.Infrastructure.Configuration;
using DiaryOfMine.Infrastructure.Logging;
using DiaryOfMine.Infrastructure.Storage;

namespace DiaryOfMine.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private TrayService? _tray;
    private WindowBehaviorCoordinator? _windowBehavior;
    private WindowsRegistrationService? _windowsRegistration;
    private FileAppLogger? _logger;
    private PortableJsonUserDataRepository? _calendarRepository;
    private CalendarService? _calendarService;
    private PortableJsonDiaryRepository? _diaryRepository;
    private DiaryService? _diaryService;
    private LinksPageViewModel? _diaryPageViewModel;
    private DiaryEditorWindow? _diaryEditorWindow;
    private PortableJsonWorkModeRepository? _workModeRepository;
    private WorkModeService? _workModeService;
    private WorkModePageViewModel? _workModePageViewModel;
    private HomePageViewModel? _homePageViewModel;
    private CalendarDayDetailViewModel? _calendarDayDetailViewModel;
    private CalendarDayDetailWindow? _calendarDayDetailWindow;
    private ReminderScheduler? _reminderScheduler;
    private ReminderPresentationService? _reminderPresentation;
    private StandaloneEditorSession? _standaloneEditorSession;
    private KeyTaskEditorSession? _keyTaskEditorSession;
    private Icon? _applicationIcon;
    internal bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        const bool development = true;
#else
        const bool development = false;
#endif
        var windowsRegistration = new WindowsRegistrationService(development);
        if (e.Args.Any(argument => string.Equals(
                argument, "--unregister", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                windowsRegistration.RemoveRegistration();
            }
            catch (Exception exception) when (IsWindowsRegistrationException(exception))
            {
                System.Windows.MessageBox.Show(
                    $"无法移除 Windows 应用注册信息。\n\n{exception.Message}",
                    "Diary of Mine",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }

        var startedWithWindows = e.Args.Any(argument => string.Equals(
            argument, "--startup", StringComparison.OrdinalIgnoreCase));
        _instanceMutex = new Mutex(true,
            development ? @"Local\DiaryOfMine.Dev" : @"Local\DiaryOfMine", out _ownsMutex);
        if (!_ownsMutex)
        {
            System.Windows.MessageBox.Show("日记本已在运行，请从系统托盘打开。", "Diary of Mine");
            Shutdown();
            return;
        }

        var dataPaths = new LocalDataPaths(development);
        var settingsStore = new JsonAppSettingsStore(dataPaths);
        DiaryOfMine.Application.Settings.AppSettings settings;
        try
        {
            settings = settingsStore.LoadOrCreate();
        }
        catch (Exception exception) when (exception is System.IO.IOException
            or System.IO.InvalidDataException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            TryLogStartupFailure(dataPaths.DefaultLogDirectory, exception);
            System.Windows.MessageBox.Show(
                $"无法安全加载本地设置，应用将停止启动。\n\n{exception.Message}",
                "Diary of Mine",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var logger = new FileAppLogger(settings.LogDirectory);
        _logger = logger;
        using (var settingsLoad = logger.BeginOperation("settings.load"))
            settingsLoad.Complete();
        using (var applicationStart = logger.BeginOperation("application.start"))
            applicationStart.Complete();
        _windowsRegistration = windowsRegistration;
        using (var registration = logger.BeginOperation("windows.registration.apply"))
        {
            try
            {
                windowsRegistration.Apply(settings);
                registration.Complete();
            }
            catch (Exception exception) when (IsWindowsRegistrationException(exception))
            {
                registration.Fail(exception);
            }
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var window = new MainWindow { Logger = logger };
        var activityState = new ApplicationActivityState();
        var windowBehavior = new WindowBehaviorCoordinator(
            window, settings, settingsStore, logger, activityState, () => IsExiting);
        _windowBehavior = windowBehavior;
        windowBehavior.SettingsChanged += OnSettingsChanged;
        windowBehavior.WorkRecordingRequested += OnFloatingWorkRecordingRequested;
        windowBehavior.WorkGoalSelected += OnFloatingWorkGoalSelected;
        var folderPicker = new FolderPickerService();
        var calendarRepository = new PortableJsonUserDataRepository(
            System.IO.Path.Combine(settings.CacheDirectory, PortableJsonUserDataRepository.FileName),
            dataPaths.CalendarData);
        _calendarRepository = calendarRepository;
        var calendarService = new CalendarService(calendarRepository, logger);
        _calendarService = calendarService;
        var diaryRepository = new PortableJsonDiaryRepository(
            System.IO.Path.Combine(settings.CacheDirectory, PortableJsonDiaryRepository.FileName));
        _diaryRepository = diaryRepository;
        var diaryService = new DiaryService(diaryRepository, logger);
        _diaryService = diaryService;
        var diaryPageViewModel = new LinksPageViewModel(diaryService, logger);
        diaryPageViewModel.DiaryOpenRequested += OnDiaryOpenRequested;
        _diaryPageViewModel = diaryPageViewModel;
        var workModeRepository = new PortableJsonWorkModeRepository(
            System.IO.Path.Combine(settings.CacheDirectory, PortableJsonWorkModeRepository.FileName));
        _workModeRepository = workModeRepository;
        var workModeService = new WorkModeService(workModeRepository, logger);
        _workModeService = workModeService;
        var workModePageViewModel = new WorkModePageViewModel(workModeService, settings);
        _workModePageViewModel = workModePageViewModel;
        workModePageViewModel.VisualsChanged += OnWorkModeVisualsChanged;
        var homePageViewModel = new HomePageViewModel(
            calendarService, diaryService, logger, settings);
        _homePageViewModel = homePageViewModel;
        var acknowledgementStore = new ReminderAcknowledgementStore(
            dataPaths.ReminderAcknowledgements);
        var reminderScheduler = new ReminderScheduler(
            calendarRepository, acknowledgementStore, logger, settings);
        _reminderScheduler = reminderScheduler;
        _reminderPresentation = new ReminderPresentationService(
            window,
            windowBehavior,
            activityState,
            reminderScheduler,
            acknowledgementStore,
            logger,
            settings);
        homePageViewModel.CalendarChanged += OnCalendarChanged;

        var navigation = new NavigationRegistry()
            .Register(new NavigationPageRegistration(
                "home", "日程表", "▦", 0, NavigationPlacement.Main,
                () => homePageViewModel))
            .Register(new NavigationPageRegistration(
                "diary", "日记", "▤", 1, NavigationPlacement.Main,
                () => diaryPageViewModel))
            .Register(new NavigationPageRegistration(
                "work", "工作模式", "◷", 2, NavigationPlacement.Main,
                () => workModePageViewModel))
            .Register(new NavigationPageRegistration(
                "settings", "设置", "⚙", 1000, NavigationPlacement.Footer,
                () => new SettingsPageViewModel(
                    windowBehavior.CurrentSettings,
                    dataPaths.UserDataRoot,
                    settingsStore,
                    logger,
                    windowBehavior,
                    folderPicker,
                    windowsRegistration,
                    ClearCalendarDataAsync)));

        window.DataContext = new ShellViewModel(navigation, development, logger);
        window.CalendarItemCreationRequested += OnCalendarItemCreationRequested;
        window.KeyTaskCreationRequested += OnKeyTaskCreationRequested;
        window.KeyTaskEditRequested += OnKeyTaskEditRequested;
        window.CalendarItemEditRequested += OnCalendarItemEditRequested;
        window.CalendarDayDetailsRequested += OnCalendarDayDetailsRequested;
        MainWindow = window;
        _applicationIcon = LoadApplicationIcon();
        _tray = new TrayService(_applicationIcon, ShowWindow, ExitApplication);
        window.Show();
        Dispatcher.BeginInvoke(
            (Action)(async () => await RescheduleRemindersAsync()),
            DispatcherPriority.ApplicationIdle);
        if (startedWithWindows)
            Dispatcher.BeginInvoke(
                (Action)windowBehavior.EnterFloatingMode,
                DispatcherPriority.ApplicationIdle);
    }

    private void ShowWindow()
    {
        _windowBehavior?.ShowMainWindow();
    }

    private async void OnFloatingWorkRecordingRequested(object? sender, EventArgs e)
    {
        if (_workModePageViewModel is not { } viewModel
            || _windowBehavior is not { } windowBehavior)
            return;
        await viewModel.RefreshAsync(silent: true);
        var goals = viewModel.ActiveGoals.Select(goal => new FloatingWorkGoalOption(
            goal.Id, goal.Name,
            WorkPalette.ResolveColor(viewModel.Settings, goal.ColorKey))).ToArray();
        windowBehavior.ShowFloatingWorkGoals(goals, viewModel.IsRecording);
    }

    private async void OnFloatingWorkGoalSelected(Guid goalId)
    {
        if (_workModePageViewModel is not { } viewModel) return;
        await viewModel.StartAsync(goalId);
    }

    private void OnWorkModeVisualsChanged(object? sender, EventArgs e)
    {
        if (_workModePageViewModel is { } viewModel)
            _windowBehavior?.SetWorkRecordingState(viewModel.IsRecording);
    }

    private void OnCalendarItemCreationRequested(CalendarItemKind kind, DateOnly? initialDate)
        => ShowCalendarEditor(new CalendarItemEditorWindow(kind, initialDate), kind, null);

    private void OnKeyTaskCreationRequested(DateOnly? initialDate) =>
        ShowKeyTaskEditor(new KeyTaskEditorWindow(initialDate), null);

    private void OnKeyTaskEditRequested(CalendarItem item) =>
        ShowKeyTaskEditor(new KeyTaskEditorWindow(item), item);

    private void OnCalendarItemEditRequested(CalendarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ShowCalendarEditor(new CalendarItemEditorWindow(item), item.Kind, item);
    }

    private void ShowCalendarEditor(
        CalendarItemEditorWindow editor,
        CalendarItemKind kind,
        CalendarItem? original)
    {
        if (_homePageViewModel is null || MainWindow is not MainWindow mainWindow || _windowBehavior is null)
            return;

        if (_keyTaskEditorSession is { Editor.IsVisible: true } taskSession)
        {
            taskSession.Editor.Activate();
            return;
        }
        if (_standaloneEditorSession is { Editor.IsVisible: true } existing)
        {
            existing.Editor.Activate();
            return;
        }

        var operation = _logger?.BeginOperation(
            original is null ? "calendar.editor.open" : "calendar.editor.edit-open");
        var session = new StandaloneEditorSession(editor, kind, original);
        _standaloneEditorSession = session;
        try
        {
            if (mainWindow.IsVisible) editor.Owner = mainWindow;
            _windowBehavior.RegisterCompanion(editor);
            editor.Closed += async (_, _) =>
            {
                try
                {
                    _windowBehavior?.UnregisterCompanion(editor);
                    if (ReferenceEquals(_standaloneEditorSession, session))
                        _standaloneEditorSession = null;
                    if (editor.Draft is null)
                    {
                        operation?.Complete("cancelled");
                        return;
                    }

                    if (original is null)
                    {
                        await _homePageViewModel.CreateAsync(editor.Draft);
                        operation?.Complete($"created-{kind.ToString().ToLowerInvariant()}");
                    }
                    else
                    {
                        await _homePageViewModel.EditAsync(original, editor.Draft);
                        operation?.Complete($"edited-{kind.ToString().ToLowerInvariant()}");
                    }
                }
                catch (Exception exception)
                {
                    operation?.Fail(exception);
                    System.Windows.MessageBox.Show(
                        $"无法添加日程。\n\n{exception.Message}",
                        "Diary of Mine",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    operation?.Dispose();
                }
            };
            editor.Show();
            editor.Activate();
        }
        catch (Exception exception)
        {
            _windowBehavior.UnregisterCompanion(editor);
            _standaloneEditorSession = null;
            operation?.Fail(exception);
            operation?.Dispose();
            System.Windows.MessageBox.Show(
                $"无法添加日程。\n\n{exception.Message}",
                "Diary of Mine",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowKeyTaskEditor(KeyTaskEditorWindow editor, CalendarItem? originalOccurrence)
    {
        if (_homePageViewModel is null
            || MainWindow is not MainWindow mainWindow
            || _windowBehavior is null)
            return;
        if (_standaloneEditorSession is { Editor.IsVisible: true } calendarSession)
        {
            calendarSession.Editor.Activate();
            return;
        }
        if (_keyTaskEditorSession is { Editor.IsVisible: true } existing)
        {
            existing.Editor.Activate();
            return;
        }

        var operation = _logger?.BeginOperation("calendar.task-editor.open");
        var session = new KeyTaskEditorSession(editor, originalOccurrence);
        _keyTaskEditorSession = session;
        try
        {
            if (mainWindow.IsVisible) editor.Owner = mainWindow;
            _windowBehavior.RegisterCompanion(editor);
            editor.Closed += async (_, _) =>
            {
                try
                {
                    _windowBehavior?.UnregisterCompanion(editor);
                    if (ReferenceEquals(_keyTaskEditorSession, session))
                        _keyTaskEditorSession = null;
                    if (editor.Schedule is null)
                    {
                        operation?.Complete("cancelled");
                        return;
                    }
                    var items = session.OriginalOccurrence is { } original
                        ? await _homePageViewModel.EditKeyTaskAsync(original, editor.Schedule)
                        : await _homePageViewModel.CreateKeyTaskAsync(editor.Schedule);
                    operation?.Complete($"saved-{items.Count}");
                }
                catch (Exception exception)
                {
                    operation?.Fail(exception);
                    System.Windows.MessageBox.Show(
                        $"无法保存 Task 批次。\n\n{exception.Message}",
                        "Diary of Mine",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    operation?.Dispose();
                }
            };
            editor.Show();
            editor.Activate();
        }
        catch (Exception exception)
        {
            _windowBehavior.UnregisterCompanion(editor);
            _keyTaskEditorSession = null;
            operation?.Fail(exception);
            operation?.Dispose();
            System.Windows.MessageBox.Show(
                $"无法打开 Task 编辑器。\n\n{exception.Message}",
                "Diary of Mine",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnCalendarDayDetailsRequested(DateOnly date)
    {
        var logger = _logger;
        if (_homePageViewModel is null
            || _calendarService is null
            || logger is null
            || MainWindow is not MainWindow mainWindow
            || _windowBehavior is null)
            return;

        if (_calendarDayDetailWindow is { IsClosing: false } existing)
        {
            await existing.NavigateToAsync(date);
            if (!existing.IsVisible) existing.Show();
            existing.Activate();
            return;
        }

        var operation = _logger?.BeginOperation("calendar.day-detail.open");
        try
        {
            var viewModel = new CalendarDayDetailViewModel(
                _calendarService, logger, _homePageViewModel.Settings, date);
            var details = new CalendarDayDetailWindow(viewModel);
            _calendarDayDetailViewModel = viewModel;
            _calendarDayDetailWindow = details;
            viewModel.DataChanged += OnDetailCalendarDataChanged;
            details.DiaryRequested += OnDetailDiaryRequested;
            if (mainWindow.IsVisible) details.Owner = mainWindow;
            _windowBehavior.RegisterCompanion(details);
            details.Closed += (_, _) =>
            {
                _windowBehavior?.UnregisterCompanion(details);
                viewModel.DataChanged -= OnDetailCalendarDataChanged;
                details.DiaryRequested -= OnDetailDiaryRequested;
                if (ReferenceEquals(_calendarDayDetailWindow, details))
                {
                    _calendarDayDetailWindow = null;
                    _calendarDayDetailViewModel = null;
                }
                operation?.Complete();
                operation?.Dispose();
                if (!IsExiting)
                    _windowBehavior?.ShowMainWindow();
            };
            details.Show();
            details.Activate();
        }
        catch (Exception exception)
        {
            operation?.Fail(exception);
            operation?.Dispose();
            System.Windows.MessageBox.Show(
                $"无法打开当日日程。\n\n{exception.Message}",
                "Diary of Mine",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnDiaryOpenRequested(DateOnly date) =>
        await ShowDiaryEditorAsync(date);

    private async void OnDetailDiaryRequested(DateOnly date)
    {
        try
        {
            if (_calendarDayDetailWindow is { IsClosing: false } details
                && !await details.SavePendingAndCloseAsync())
                return;
            if (MainWindow?.DataContext is ShellViewModel shell)
                shell.TryNavigateTo("diary");
            if (_diaryPageViewModel is not null)
                await _diaryPageViewModel.InitializeAsync();
            await ShowDiaryEditorAsync(date);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"无法打开当天日记。\n\n{exception.Message}",
                "Diary of Mine", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ShowDiaryEditorAsync(DateOnly date)
    {
        if (_diaryService is null
            || MainWindow is not MainWindow mainWindow
            || _windowBehavior is null)
            return;
        if (_diaryEditorWindow is { IsVisible: true } existingEditor)
        {
            if (existingEditor.Date == date)
            {
                existingEditor.Activate();
                return;
            }
            if (!await existingEditor.SaveAndCloseAsync()) return;
        }
        if (_standaloneEditorSession is { Editor.IsVisible: true } calendarEditor)
        {
            calendarEditor.Editor.Activate();
            return;
        }
        if (_keyTaskEditorSession is { Editor.IsVisible: true } taskEditor)
        {
            taskEditor.Editor.Activate();
            return;
        }

        var entry = await _diaryService.GetAsync(date).ConfigureAwait(true);
        var editor = new DiaryEditorWindow(date, _diaryService, entry) { Owner = mainWindow };
        _diaryEditorWindow = editor;
        _windowBehavior.RegisterCompanion(editor);
        editor.Saved += OnDiaryEditorSaved;
        editor.Deleted += OnDiaryEditorSaved;
        editor.DayDetailRequested += OnDiaryDayDetailRequested;
        editor.Closed += (_, _) =>
        {
            editor.Saved -= OnDiaryEditorSaved;
            editor.Deleted -= OnDiaryEditorSaved;
            editor.DayDetailRequested -= OnDiaryDayDetailRequested;
            _windowBehavior?.UnregisterCompanion(editor);
            if (ReferenceEquals(_diaryEditorWindow, editor)) _diaryEditorWindow = null;
        };
        editor.Show();
        editor.Activate();
    }

    private async void OnDiaryEditorSaved(object? sender, EventArgs e)
    {
        if (_diaryPageViewModel is not null)
            await _diaryPageViewModel.RefreshAsync();
        if (_homePageViewModel is not null)
            await _homePageViewModel.RefreshDiaryDatesAsync();
    }

    private void OnDiaryDayDetailRequested(DateOnly date)
    {
        if (_diaryEditorWindow is { IsVisible: true } editor)
            editor.CloseAfterSavedNavigation();
        OnCalendarDayDetailsRequested(date);
    }

    internal void RegisterCompanionWindow(Window window) =>
        _windowBehavior?.RegisterCompanion(window);

    internal void UnregisterCompanionWindow(Window window) =>
        _windowBehavior?.UnregisterCompanion(window);

    internal IDisposable SuspendAutoFloat() =>
        _windowBehavior?.SuspendAutoFloat() ?? EmptyDisposable.Instance;

    private async void OnDetailCalendarDataChanged(object? sender, EventArgs e)
    {
        if (_homePageViewModel is null) return;
        try { await _homePageViewModel.RefreshAsync(); }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"详细日程已保存，但月历刷新失败。\n\n{exception.Message}",
                "Diary of Mine",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnCalendarChanged(object? sender, EventArgs e) =>
        await RescheduleRemindersAsync();

    private async Task RescheduleRemindersAsync()
    {
        if (_reminderScheduler is null || IsExiting) return;
        try
        {
            await _reminderScheduler.RescheduleAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = GetResourceStream(new Uri(
            "pack://application:,,,/DiaryOfMine;component/Assets/DiaryOfMine.ico",
            UriKind.Absolute)) ?? throw new InvalidOperationException("无法加载应用程序图标资源。");
        using (resource.Stream)
        using (var icon = new Icon(resource.Stream))
            return (Icon)icon.Clone();
    }

    private async void OnSettingsChanged(DiaryOfMine.Application.Settings.AppSettings settings)
    {
        var previousCacheDirectory = _homePageViewModel?.Settings.CacheDirectory;
        _reminderPresentation?.ApplySettings(settings);
        _reminderScheduler?.ApplySettings(settings);
        _homePageViewModel?.ApplySettings(settings);
        _workModePageViewModel?.ApplySettings(settings);
        _calendarDayDetailWindow?.ApplySettings(settings);
        if (_logger is null) return;
        using var operation = _logger.BeginOperation("logging.directory.apply");
        try
        {
            _logger.UpdateDirectory(settings.LogDirectory);
            if (!string.Equals(previousCacheDirectory, settings.CacheDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (_calendarRepository is not null)
                    await _calendarRepository.SwitchDirectoryAsync(settings.CacheDirectory);
                if (_diaryRepository is not null)
                    await _diaryRepository.SwitchDirectoryAsync(settings.CacheDirectory);
                if (_workModeRepository is not null)
                    await _workModeRepository.SwitchDirectoryAsync(settings.CacheDirectory);
                if (_homePageViewModel is not null)
                    await _homePageViewModel.InitializeAsync();
                if (_calendarDayDetailViewModel is not null)
                    await _calendarDayDetailViewModel.RefreshAsync();
                if (_diaryPageViewModel is not null)
                    await _diaryPageViewModel.RefreshAsync();
                if (_workModePageViewModel is not null)
                    await _workModePageViewModel.RefreshAsync();
            }
            await RescheduleRemindersAsync();
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            System.Windows.MessageBox.Show(
                $"无法切换用户数据缓存位置，原数据未被覆盖。\n\n{exception.Message}",
                "Diary of Mine",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ClearCalendarDataAsync()
    {
        if (_calendarService is null) return;
        await _calendarService.ClearAsync().ConfigureAwait(true);
        if (_homePageViewModel is not null)
            await _homePageViewModel.ResetAfterDataClearAsync().ConfigureAwait(true);
        if (_calendarDayDetailViewModel is not null)
            await _calendarDayDetailViewModel.ResetAfterDataClearAsync().ConfigureAwait(true);
    }

    internal async void ExitApplication()
    {
        if (IsExiting) return;
        if (_diaryEditorWindow is { IsVisible: true } diaryEditor
            && !await diaryEditor.SaveAndCloseAsync())
            return;
        using var operation = _logger?.BeginOperation("application.exit-request");
        IsExiting = true;
        operation?.Complete();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_logger is not null)
        {
            using var operation = _logger.BeginOperation("application.stop");
            operation.Complete();
        }
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (MainWindow?.DataContext is IDisposable disposableViewModel)
            disposableViewModel.Dispose();
        if (_homePageViewModel is not null)
            _homePageViewModel.CalendarChanged -= OnCalendarChanged;
        if (_diaryPageViewModel is not null)
            _diaryPageViewModel.DiaryOpenRequested -= OnDiaryOpenRequested;
        if (_workModePageViewModel is not null)
            _workModePageViewModel.VisualsChanged -= OnWorkModeVisualsChanged;
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.CalendarItemCreationRequested -= OnCalendarItemCreationRequested;
            mainWindow.KeyTaskCreationRequested -= OnKeyTaskCreationRequested;
            mainWindow.KeyTaskEditRequested -= OnKeyTaskEditRequested;
            mainWindow.CalendarItemEditRequested -= OnCalendarItemEditRequested;
            mainWindow.CalendarDayDetailsRequested -= OnCalendarDayDetailsRequested;
        }
        if (_windowBehavior is not null)
        {
            _windowBehavior.SettingsChanged -= OnSettingsChanged;
            _windowBehavior.WorkRecordingRequested -= OnFloatingWorkRecordingRequested;
            _windowBehavior.WorkGoalSelected -= OnFloatingWorkGoalSelected;
            _reminderPresentation?.Dispose();
            _reminderPresentation = null;
            _reminderScheduler?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _reminderScheduler = null;
            _windowBehavior.Dispose();
        }
        _tray?.Dispose();
        _windowsRegistration = null;
        _applicationIcon?.Dispose();
        _calendarRepository?.Dispose();
        _diaryRepository?.Dispose();
        _workModeService?.Dispose();
        _workModeRepository?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool IsWindowsRegistrationException(Exception exception) =>
        exception is ArgumentException
            or System.IO.IOException
            or InvalidOperationException
            or SecurityException
            or UnauthorizedAccessException;

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class StandaloneEditorSession(
        CalendarItemEditorWindow editor,
        CalendarItemKind kind,
        CalendarItem? original)
    {
        public CalendarItemEditorWindow Editor { get; } = editor;
        public CalendarItemKind Kind { get; } = kind;
        public CalendarItem? Original { get; } = original;
    }

    private sealed class KeyTaskEditorSession(
        KeyTaskEditorWindow editor,
        CalendarItem? originalOccurrence)
    {
        public KeyTaskEditorWindow Editor { get; } = editor;
        public CalendarItem? OriginalOccurrence { get; } = originalOccurrence;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) =>
        LogUnhandledException("error.dispatcher-unhandled", e.Exception);

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            LogUnhandledException("error.domain-unhandled", exception);
    }

    private void OnUnobservedTaskException(
        object? sender,
        System.Threading.Tasks.UnobservedTaskExceptionEventArgs e) =>
        LogUnhandledException("error.task-unobserved", e.Exception);

    private void LogUnhandledException(string eventName, Exception exception)
    {
        if (_logger is null) return;
        using var operation = _logger.BeginOperation(eventName);
        operation.Fail(exception);
    }

    private static void TryLogStartupFailure(string logDirectory, Exception exception)
    {
        try
        {
            var logger = new FileAppLogger(logDirectory);
            using var operation = logger.BeginOperation("settings.load");
            operation.Fail(exception);
        }
        catch (Exception loggingException) when (loggingException is System.IO.IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            // 设置目录本身不可用时只能向用户显示错误，不能递归记录日志。
        }
    }
}
