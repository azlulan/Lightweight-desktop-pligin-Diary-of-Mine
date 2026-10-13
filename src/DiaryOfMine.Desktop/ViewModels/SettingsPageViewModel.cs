using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Desktop.Commands;
using DiaryOfMine.Desktop.Services;

namespace DiaryOfMine.Desktop.ViewModels;

public sealed class SettingsPageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private readonly WindowBehaviorCoordinator _windowBehavior;
    private readonly FolderPickerService _folderPicker;
    private readonly WindowsRegistrationService _windowsRegistration;
    private readonly Func<Task> _clearCalendarDataAsync;
    private AppSettings _settingsSnapshot;
    private string _cacheDirectory;
    private string _logDirectory;
    private bool _autoFloatAndSleepWhenInactive;
    private bool _pinFloatingWindow;
    private bool _startWithWindows;
    private bool _enableNotifications;
    private bool _enableClassNotifications;
    private bool _enableKeyNotifications;
    private bool _enableIdeaNotifications;
    private string _floatingWindowLeft;
    private string _floatingWindowTop;
    private string _expandHotKey;
    private string _classPaletteColor1;
    private string _classPaletteColor2;
    private string _classPaletteColor3;
    private string _classPaletteColor4;
    private string _classPaletteColor5;
    private string _classPaletteColor6;
    private string _keyPaletteColor1;
    private string _keyPaletteColor2;
    private string _keyPaletteColor3;
    private string _keyPaletteColor4;
    private string _keyPaletteColor5;
    private string _keyPaletteColor6;
    private string _workPaletteColor1;
    private string _workPaletteColor2;
    private string _workPaletteColor3;
    private string _workPaletteColor4;
    private string _workPaletteColor5;
    private string _workPaletteColor6;
    private string _statusMessage = string.Empty;
    private bool _disposed;
    private bool _suppressSettingsSync;
    private bool _isClearingCalendar;

    public string UserDataRoot { get; }

    public string CacheDirectory
    {
        get => _cacheDirectory;
        set => SetProperty(ref _cacheDirectory, value);
    }

    public string LogDirectory
    {
        get => _logDirectory;
        set => SetProperty(ref _logDirectory, value);
    }

    public bool AutoFloatAndSleepWhenInactive
    {
        get => _autoFloatAndSleepWhenInactive;
        set => SetProperty(ref _autoFloatAndSleepWhenInactive, value);
    }

    public bool PinFloatingWindow
    {
        get => _pinFloatingWindow;
        set => SetProperty(ref _pinFloatingWindow, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool EnableNotifications
    {
        get => _enableNotifications;
        set => SetProperty(ref _enableNotifications, value);
    }

    public bool EnableClassNotifications
    {
        get => _enableClassNotifications;
        set => SetProperty(ref _enableClassNotifications, value);
    }

    public bool EnableKeyNotifications
    {
        get => _enableKeyNotifications;
        set => SetProperty(ref _enableKeyNotifications, value);
    }

    public bool EnableIdeaNotifications
    {
        get => _enableIdeaNotifications;
        set => SetProperty(ref _enableIdeaNotifications, value);
    }

    public string FloatingWindowLeft
    {
        get => _floatingWindowLeft;
        set => SetProperty(ref _floatingWindowLeft, value);
    }

    public string FloatingWindowTop
    {
        get => _floatingWindowTop;
        set => SetProperty(ref _floatingWindowTop, value);
    }

    public string ExpandHotKey
    {
        get => _expandHotKey;
        set => SetProperty(ref _expandHotKey, value);
    }

    public string ClassPaletteColor1 { get => _classPaletteColor1; set => SetProperty(ref _classPaletteColor1, value); }
    public string ClassPaletteColor2 { get => _classPaletteColor2; set => SetProperty(ref _classPaletteColor2, value); }
    public string ClassPaletteColor3 { get => _classPaletteColor3; set => SetProperty(ref _classPaletteColor3, value); }
    public string ClassPaletteColor4 { get => _classPaletteColor4; set => SetProperty(ref _classPaletteColor4, value); }
    public string ClassPaletteColor5 { get => _classPaletteColor5; set => SetProperty(ref _classPaletteColor5, value); }
    public string ClassPaletteColor6 { get => _classPaletteColor6; set => SetProperty(ref _classPaletteColor6, value); }
    public string KeyPaletteColor1 { get => _keyPaletteColor1; set => SetProperty(ref _keyPaletteColor1, value); }
    public string KeyPaletteColor2 { get => _keyPaletteColor2; set => SetProperty(ref _keyPaletteColor2, value); }
    public string KeyPaletteColor3 { get => _keyPaletteColor3; set => SetProperty(ref _keyPaletteColor3, value); }
    public string KeyPaletteColor4 { get => _keyPaletteColor4; set => SetProperty(ref _keyPaletteColor4, value); }
    public string KeyPaletteColor5 { get => _keyPaletteColor5; set => SetProperty(ref _keyPaletteColor5, value); }
    public string KeyPaletteColor6 { get => _keyPaletteColor6; set => SetProperty(ref _keyPaletteColor6, value); }
    public string WorkPaletteColor1 { get => _workPaletteColor1; set => SetProperty(ref _workPaletteColor1, value); }
    public string WorkPaletteColor2 { get => _workPaletteColor2; set => SetProperty(ref _workPaletteColor2, value); }
    public string WorkPaletteColor3 { get => _workPaletteColor3; set => SetProperty(ref _workPaletteColor3, value); }
    public string WorkPaletteColor4 { get => _workPaletteColor4; set => SetProperty(ref _workPaletteColor4, value); }
    public string WorkPaletteColor5 { get => _workPaletteColor5; set => SetProperty(ref _workPaletteColor5, value); }
    public string WorkPaletteColor6 { get => _workPaletteColor6; set => SetProperty(ref _workPaletteColor6, value); }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ICommand BrowseCacheDirectoryCommand { get; }
    public ICommand BrowseLogDirectoryCommand { get; }
    public ICommand OpenLogDirectoryCommand { get; }
    public ICommand ClearCalendarCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand PickClassPaletteColor1Command { get; }
    public ICommand PickClassPaletteColor2Command { get; }
    public ICommand PickClassPaletteColor3Command { get; }
    public ICommand PickClassPaletteColor4Command { get; }
    public ICommand PickClassPaletteColor5Command { get; }
    public ICommand PickClassPaletteColor6Command { get; }
    public ICommand PickKeyPaletteColor1Command { get; }
    public ICommand PickKeyPaletteColor2Command { get; }
    public ICommand PickKeyPaletteColor3Command { get; }
    public ICommand PickKeyPaletteColor4Command { get; }
    public ICommand PickKeyPaletteColor5Command { get; }
    public ICommand PickKeyPaletteColor6Command { get; }
    public ICommand PickWorkPaletteColor1Command { get; }
    public ICommand PickWorkPaletteColor2Command { get; }
    public ICommand PickWorkPaletteColor3Command { get; }
    public ICommand PickWorkPaletteColor4Command { get; }
    public ICommand PickWorkPaletteColor5Command { get; }
    public ICommand PickWorkPaletteColor6Command { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal SettingsPageViewModel(
        AppSettings settings,
        string userDataRoot,
        IAppSettingsStore settingsStore,
        IAppLogger logger,
        WindowBehaviorCoordinator windowBehavior,
        FolderPickerService folderPicker,
        WindowsRegistrationService windowsRegistration,
        Func<Task> clearCalendarDataAsync)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settingsSnapshot = settings;
        UserDataRoot = userDataRoot ?? throw new ArgumentNullException(nameof(userDataRoot));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _windowBehavior = windowBehavior ?? throw new ArgumentNullException(nameof(windowBehavior));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _windowsRegistration = windowsRegistration
            ?? throw new ArgumentNullException(nameof(windowsRegistration));
        _clearCalendarDataAsync = clearCalendarDataAsync
            ?? throw new ArgumentNullException(nameof(clearCalendarDataAsync));

        _cacheDirectory = settings.CacheDirectory;
        _logDirectory = settings.LogDirectory;
        _floatingWindowLeft = FormatCoordinate(settings.FloatingWindowLeft);
        _floatingWindowTop = FormatCoordinate(settings.FloatingWindowTop);
        _expandHotKey = settings.ExpandHotKey;
        _autoFloatAndSleepWhenInactive = settings.AutoFloatAndSleepWhenInactive;
        _pinFloatingWindow = settings.PinFloatingWindow;
        _startWithWindows = settings.StartWithWindows;
        _enableNotifications = settings.EnableNotifications;
        _enableClassNotifications = settings.EnableClassNotifications;
        _enableKeyNotifications = settings.EnableKeyNotifications;
        _enableIdeaNotifications = settings.EnableIdeaNotifications;
        _classPaletteColor1 = settings.ClassPaletteColor1;
        _classPaletteColor2 = settings.ClassPaletteColor2;
        _classPaletteColor3 = settings.ClassPaletteColor3;
        _classPaletteColor4 = settings.ClassPaletteColor4;
        _classPaletteColor5 = settings.ClassPaletteColor5;
        _classPaletteColor6 = settings.ClassPaletteColor6;
        _keyPaletteColor1 = settings.KeyPaletteColor1;
        _keyPaletteColor2 = settings.KeyPaletteColor2;
        _keyPaletteColor3 = settings.KeyPaletteColor3;
        _keyPaletteColor4 = settings.KeyPaletteColor4;
        _keyPaletteColor5 = settings.KeyPaletteColor5;
        _keyPaletteColor6 = settings.KeyPaletteColor6;
        _workPaletteColor1 = settings.WorkPaletteColor1;
        _workPaletteColor2 = settings.WorkPaletteColor2;
        _workPaletteColor3 = settings.WorkPaletteColor3;
        _workPaletteColor4 = settings.WorkPaletteColor4;
        _workPaletteColor5 = settings.WorkPaletteColor5;
        _workPaletteColor6 = settings.WorkPaletteColor6;

        BrowseCacheDirectoryCommand = new RelayCommand(BrowseCacheDirectory, () => !_disposed);
        BrowseLogDirectoryCommand = new RelayCommand(BrowseLogDirectory, () => !_disposed);
        OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory, () => !_disposed);
        ClearCalendarCommand = new RelayCommand(
            ClearCalendarWithConfirmation,
            () => !_disposed && !_isClearingCalendar);
        SaveCommand = new RelayCommand(Save, () => !_disposed);
        PickClassPaletteColor1Command = CreateColorCommand(() => ClassPaletteColor1, value => ClassPaletteColor1 = value);
        PickClassPaletteColor2Command = CreateColorCommand(() => ClassPaletteColor2, value => ClassPaletteColor2 = value);
        PickClassPaletteColor3Command = CreateColorCommand(() => ClassPaletteColor3, value => ClassPaletteColor3 = value);
        PickClassPaletteColor4Command = CreateColorCommand(() => ClassPaletteColor4, value => ClassPaletteColor4 = value);
        PickClassPaletteColor5Command = CreateColorCommand(() => ClassPaletteColor5, value => ClassPaletteColor5 = value);
        PickClassPaletteColor6Command = CreateColorCommand(() => ClassPaletteColor6, value => ClassPaletteColor6 = value);
        PickKeyPaletteColor1Command = CreateColorCommand(() => KeyPaletteColor1, value => KeyPaletteColor1 = value);
        PickKeyPaletteColor2Command = CreateColorCommand(() => KeyPaletteColor2, value => KeyPaletteColor2 = value);
        PickKeyPaletteColor3Command = CreateColorCommand(() => KeyPaletteColor3, value => KeyPaletteColor3 = value);
        PickKeyPaletteColor4Command = CreateColorCommand(() => KeyPaletteColor4, value => KeyPaletteColor4 = value);
        PickKeyPaletteColor5Command = CreateColorCommand(() => KeyPaletteColor5, value => KeyPaletteColor5 = value);
        PickKeyPaletteColor6Command = CreateColorCommand(() => KeyPaletteColor6, value => KeyPaletteColor6 = value);
        PickWorkPaletteColor1Command = CreateColorCommand(() => WorkPaletteColor1, value => WorkPaletteColor1 = value);
        PickWorkPaletteColor2Command = CreateColorCommand(() => WorkPaletteColor2, value => WorkPaletteColor2 = value);
        PickWorkPaletteColor3Command = CreateColorCommand(() => WorkPaletteColor3, value => WorkPaletteColor3 = value);
        PickWorkPaletteColor4Command = CreateColorCommand(() => WorkPaletteColor4, value => WorkPaletteColor4 = value);
        PickWorkPaletteColor5Command = CreateColorCommand(() => WorkPaletteColor5, value => WorkPaletteColor5 = value);
        PickWorkPaletteColor6Command = CreateColorCommand(() => WorkPaletteColor6, value => WorkPaletteColor6 = value);
        _windowBehavior.SettingsChanged += OnSettingsChanged;
    }

    private async void ClearCalendarWithConfirmation()
    {
        if (_disposed || _isClearingCalendar) return;
        using var operation = _logger.BeginOperation("settings.calendar.clear");
        using var suspension = _windowBehavior.SuspendAutoFloat();
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null
            ? System.Windows.MessageBox.Show(
                "将永久清空当前缓存目录中的全部 Class、Key、Task、Idea 和灵感。\n\n此操作无法通过撤回恢复，是否继续？",
                "确认清空全部日程",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            : System.Windows.MessageBox.Show(
                owner,
                "将永久清空当前缓存目录中的全部 Class、Key、Task、Idea 和灵感。\n\n此操作无法通过撤回恢复，是否继续？",
                "确认清空全部日程",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            operation.Complete("cancelled");
            return;
        }

        _isClearingCalendar = true;
        CommandManager.InvalidateRequerySuggested();
        StatusMessage = "正在清空日程…";
        try
        {
            await _clearCalendarDataAsync().ConfigureAwait(true);
            StatusMessage = "日程已全部清空，缓存目录和其他设置保持不变。";
            operation.Complete();
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            operation.Fail(exception);
            StatusMessage = $"清空日程失败：{exception.Message}";
        }
        finally
        {
            _isClearingCalendar = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void BrowseCacheDirectory()
    {
        using var operation = _logger.BeginOperation("settings.cache-directory.browse");
        using var suspension = _windowBehavior.SuspendAutoFloat();
        try
        {
            var selected = _folderPicker.PickFolder("选择用户数据缓存位置", CacheDirectory);
            if (selected is not null) CacheDirectory = selected;
            operation.Complete(selected is null ? "cancelled" : "selected");
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            StatusMessage = "无法打开文件夹选择器。";
        }
    }

    private void BrowseLogDirectory()
    {
        using var operation = _logger.BeginOperation("settings.log-directory.browse");
        using var suspension = _windowBehavior.SuspendAutoFloat();
        try
        {
            var selected = _folderPicker.PickFolder("选择日志输出目录", LogDirectory);
            if (selected is not null) LogDirectory = selected;
            operation.Complete(selected is null ? "cancelled" : "selected");
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            StatusMessage = "无法打开文件夹选择器。";
        }
    }

    private void OpenLogDirectory()
    {
        using var operation = _logger.BeginOperation("settings.log-directory.open");
        using var suspension = _windowBehavior.SuspendAutoFloat();
        StatusMessage = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(LogDirectory)
                || !Path.IsPathFullyQualified(LogDirectory))
                throw new ArgumentException("日志目录必须是完整的绝对路径。");

            var directory = Path.GetFullPath(LogDirectory);
            Directory.CreateDirectory(directory);

            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
            operation.Complete();
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            operation.Fail(exception);
            StatusMessage = $"无法打开日志文件夹：{exception.Message}";
        }
    }

    private void Save()
    {
        using var operation = _logger.BeginOperation("settings.save");
        StatusMessage = string.Empty;
        if (!TryParseCoordinate(FloatingWindowLeft, out var left)
            || !TryParseCoordinate(FloatingWindowTop, out var top))
        {
            StatusMessage = "悬浮窗坐标必须是有效数字。";
            operation.Complete("validation-failed");
            return;
        }
        if (!HotKeyGesture.TryParse(ExpandHotKey, out var hotKey, out var hotKeyError))
        {
            StatusMessage = hotKeyError;
            operation.Complete("hotkey-validation-failed");
            return;
        }
        ExpandHotKey = hotKey!.DisplayText;

        var settings = _settingsSnapshot with
        {
            CacheDirectory = CacheDirectory,
            LogDirectory = LogDirectory,
            AutoFloatAndSleepWhenInactive = AutoFloatAndSleepWhenInactive,
            PinFloatingWindow = PinFloatingWindow,
            StartWithWindows = StartWithWindows,
            EnableNotifications = EnableNotifications,
            EnableClassNotifications = EnableClassNotifications,
            EnableKeyNotifications = EnableKeyNotifications,
            EnableIdeaNotifications = EnableIdeaNotifications,
            FloatingWindowLeft = left,
            FloatingWindowTop = top,
            ExpandHotKey = ExpandHotKey,
            ClassPaletteColor1 = ClassPaletteColor1,
            ClassPaletteColor2 = ClassPaletteColor2,
            ClassPaletteColor3 = ClassPaletteColor3,
            ClassPaletteColor4 = ClassPaletteColor4,
            ClassPaletteColor5 = ClassPaletteColor5,
            ClassPaletteColor6 = ClassPaletteColor6,
            KeyPaletteColor1 = KeyPaletteColor1,
            KeyPaletteColor2 = KeyPaletteColor2,
            KeyPaletteColor3 = KeyPaletteColor3,
            KeyPaletteColor4 = KeyPaletteColor4,
            KeyPaletteColor5 = KeyPaletteColor5,
            KeyPaletteColor6 = KeyPaletteColor6,
            WorkPaletteColor1 = WorkPaletteColor1,
            WorkPaletteColor2 = WorkPaletteColor2,
            WorkPaletteColor3 = WorkPaletteColor3,
            WorkPaletteColor4 = WorkPaletteColor4,
            WorkPaletteColor5 = WorkPaletteColor5,
            WorkPaletteColor6 = WorkPaletteColor6
        };

        try
        {
            var saved = _settingsStore.Save(settings);
            _settingsSnapshot = saved;
            _windowBehavior.ApplySettings(saved);
            try
            {
                _windowsRegistration.Apply(saved);
                operation.Complete();
                StatusMessage = _windowBehavior.ExpandHotKeyError is null
                    ? "设置已保存，Windows 系统注册已同步。"
                    : $"设置已保存，但展开快捷键未启用：{_windowBehavior.ExpandHotKeyError}";
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or InvalidOperationException
                or SecurityException
                or UnauthorizedAccessException)
            {
                operation.Fail(exception);
                StatusMessage = $"设置已保存，但 Windows 系统注册更新失败：{exception.Message}";
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            operation.Fail(exception);
            StatusMessage = $"保存失败：{exception.Message}";
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_suppressSettingsSync) return;
        _settingsSnapshot = settings;
        CacheDirectory = settings.CacheDirectory;
        LogDirectory = settings.LogDirectory;
        AutoFloatAndSleepWhenInactive = settings.AutoFloatAndSleepWhenInactive;
        PinFloatingWindow = settings.PinFloatingWindow;
        StartWithWindows = settings.StartWithWindows;
        EnableNotifications = settings.EnableNotifications;
        EnableClassNotifications = settings.EnableClassNotifications;
        EnableKeyNotifications = settings.EnableKeyNotifications;
        EnableIdeaNotifications = settings.EnableIdeaNotifications;
        FloatingWindowLeft = FormatCoordinate(settings.FloatingWindowLeft);
        FloatingWindowTop = FormatCoordinate(settings.FloatingWindowTop);
        ExpandHotKey = settings.ExpandHotKey;
        ClassPaletteColor1 = settings.ClassPaletteColor1;
        ClassPaletteColor2 = settings.ClassPaletteColor2;
        ClassPaletteColor3 = settings.ClassPaletteColor3;
        ClassPaletteColor4 = settings.ClassPaletteColor4;
        ClassPaletteColor5 = settings.ClassPaletteColor5;
        ClassPaletteColor6 = settings.ClassPaletteColor6;
        KeyPaletteColor1 = settings.KeyPaletteColor1;
        KeyPaletteColor2 = settings.KeyPaletteColor2;
        KeyPaletteColor3 = settings.KeyPaletteColor3;
        KeyPaletteColor4 = settings.KeyPaletteColor4;
        KeyPaletteColor5 = settings.KeyPaletteColor5;
        KeyPaletteColor6 = settings.KeyPaletteColor6;
        WorkPaletteColor1 = settings.WorkPaletteColor1;
        WorkPaletteColor2 = settings.WorkPaletteColor2;
        WorkPaletteColor3 = settings.WorkPaletteColor3;
        WorkPaletteColor4 = settings.WorkPaletteColor4;
        WorkPaletteColor5 = settings.WorkPaletteColor5;
        WorkPaletteColor6 = settings.WorkPaletteColor6;
    }

    private ICommand CreateColorCommand(Func<string> get, Action<string> set) =>
        new RelayCommand(() => PickColor(get(), set), () => !_disposed);

    private void PickColor(string current, Action<string> apply)
    {
        using var operation = _logger.BeginOperation("settings.calendar-color.pick");
        using var suspension = _windowBehavior.SuspendAutoFloat();
        try
        {
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.ColorTranslator.FromHtml(current)
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var color = dialog.Color;
                var previous = current;
                apply($"#{color.R:X2}{color.G:X2}{color.B:X2}");
                if (!TrySavePalette())
                {
                    apply(previous);
                    operation.Complete("save-failed");
                    return;
                }
                operation.Complete("selected-and-saved");
            }
            else
            {
                operation.Complete("cancelled");
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or System.ComponentModel.Win32Exception)
        {
            operation.Fail(exception);
            StatusMessage = $"无法打开颜色选择器：{exception.Message}";
        }
    }

    private bool TrySavePalette()
    {
        try
        {
            var saved = _settingsStore.Save(_windowBehavior.CurrentSettings with
            {
                ClassPaletteColor1 = ClassPaletteColor1,
                ClassPaletteColor2 = ClassPaletteColor2,
                ClassPaletteColor3 = ClassPaletteColor3,
                ClassPaletteColor4 = ClassPaletteColor4,
                ClassPaletteColor5 = ClassPaletteColor5,
                ClassPaletteColor6 = ClassPaletteColor6,
                KeyPaletteColor1 = KeyPaletteColor1,
                KeyPaletteColor2 = KeyPaletteColor2,
                KeyPaletteColor3 = KeyPaletteColor3,
                KeyPaletteColor4 = KeyPaletteColor4,
                KeyPaletteColor5 = KeyPaletteColor5,
                KeyPaletteColor6 = KeyPaletteColor6,
                WorkPaletteColor1 = WorkPaletteColor1,
                WorkPaletteColor2 = WorkPaletteColor2,
                WorkPaletteColor3 = WorkPaletteColor3,
                WorkPaletteColor4 = WorkPaletteColor4,
                WorkPaletteColor5 = WorkPaletteColor5,
                WorkPaletteColor6 = WorkPaletteColor6
            });
            _suppressSettingsSync = true;
            try { _windowBehavior.ApplySettings(saved); }
            finally { _suppressSettingsSync = false; }
            StatusMessage = "色板已保存，现有日程颜色已同步更新。";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            StatusMessage = $"色板保存失败：{exception.Message}";
            return false;
        }
    }

    private static bool TryParseCoordinate(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        && double.IsFinite(value);

    private static string FormatCoordinate(double value) =>
        value.ToString("0.##", CultureInfo.CurrentCulture);

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windowBehavior.SettingsChanged -= OnSettingsChanged;
        PropertyChanged = null;
    }
}
