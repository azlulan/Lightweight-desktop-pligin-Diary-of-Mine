using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using DiaryOfMine.Application.Settings;

namespace DiaryOfMine.Desktop.Services;

internal sealed class WindowsRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallRootPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly string _registryName;
    private readonly string _displayName;

    public WindowsRegistrationService(bool development)
    {
        _registryName = development ? "DiaryOfMine.Dev" : "DiaryOfMine";
        _displayName = development ? "Diary of Mine (Development)" : "Diary of Mine";
    }

    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var executablePath = ResolveExecutablePath();
        RegisterInstalledApplication(executablePath);
        ApplyStartupSetting(executablePath, settings.StartWithWindows);
    }

    public void RemoveRegistration()
    {
        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            runKey?.DeleteValue(_registryName, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"{UninstallRootPath}\{_registryName}", throwOnMissingSubKey: false);
    }

    private void RegisterInstalledApplication(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"{UninstallRootPath}\{_registryName}", writable: true)
            ?? throw new InvalidOperationException("无法创建 Windows 应用注册项。");
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        var uninstallCommand = $"{Quote(executablePath)} --unregister";
        var installerManaged = key.GetValue("InstallerManaged") is int value && value == 1;

        key.SetValue("DisplayName", _displayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("DisplayIcon", $"{Quote(executablePath)},0", RegistryValueKind.String);
        key.SetValue("Publisher", "Diary of Mine", RegistryValueKind.String);
        key.SetValue("InstallLocation", AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), RegistryValueKind.String);
        // 正式安装器拥有卸载命令时，应用启动不得把它覆盖为仅注销注册表的命令。
        if (!installerManaged)
        {
            key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
            key.SetValue("QuietUninstallString", uninstallCommand, RegistryValueKind.String);
        }
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("WindowsInstaller", 0, RegistryValueKind.DWord);
        key.SetValue("Comments", "本地日记与日程工具；注销不会删除用户数据。",
            RegistryValueKind.String);
        if (key.GetValue("InstallDate") is null)
            key.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd"), RegistryValueKind.String);
    }

    private void ApplyStartupSetting(string executablePath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 当前用户启动项。");
        if (enabled)
            key.SetValue(_registryName, $"{Quote(executablePath)} --startup",
                RegistryValueKind.String);
        else
            key.DeleteValue(_registryName, throwOnMissingValue: false);
    }

    private static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)) return Path.GetFullPath(processPath);
        var baseDirectoryExecutable = Path.Combine(AppContext.BaseDirectory, "DiaryOfMine.exe");
        if (File.Exists(baseDirectoryExecutable)) return Path.GetFullPath(baseDirectoryExecutable);
        throw new InvalidOperationException("无法确定 Diary of Mine 的可执行文件位置。");
    }

    private static string Quote(string value) => $"\"{value}\"";
}
