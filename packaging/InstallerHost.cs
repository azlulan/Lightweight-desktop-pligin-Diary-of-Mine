using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class InstallerHost
{
    private const string ProductName = "Diary of Mine";
    private const string Version = "1.1.1";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiaryOfMine";

    [STAThread]
    private static void Main()
    {
        string extractionDirectory = AppDomain.CurrentDomain.BaseDirectory;
        int installerProcessId = Process.GetCurrentProcess().Id;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
        ScheduleTemporaryPayloadCleanup(extractionDirectory, installerProcessId);
    }

    private sealed class InstallerForm : Form
    {
        private readonly TextBox _path = new TextBox();
        private readonly CheckBox _shortcuts = new CheckBox();
        private readonly CheckBox _taskbar = new CheckBox();
        private readonly CheckBox _launch = new CheckBox();
        private readonly Button _browse;
        private readonly Button _install = new Button();
        private readonly Label _status = new Label();
        private readonly ProgressBar _progress = new ProgressBar();

        public InstallerForm()
        {
            string existingInstallDirectory = FindExistingInstallDirectory();
            bool isUpgrade = !String.IsNullOrEmpty(existingInstallDirectory);
            Text = ProductName + (isUpgrade ? " 更新程序" : " 安装程序");
            ClientSize = new Size(620, 386);
            MinimumSize = new Size(620, 425);
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(250, 250, 248);
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label { Text = isUpgrade ? "更新 Diary of Mine 至 v" + Version : "安装 Diary of Mine v" + Version, AutoSize = true, Font = new Font(Font.FontFamily, 19F, FontStyle.Bold), Location = new Point(34, 28) };
            var subtitle = new Label { Text = "Windows 本地日记、日程与工作记录工具", AutoSize = true, ForeColor = Color.FromArgb(102, 116, 108), Location = new Point(37, 70) };
            var pathLabel = new Label { Text = "安装位置", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Location = new Point(37, 112) };
            _path.Location = new Point(40, 140);
            _path.Size = new Size(446, 30);
            _path.Text = isUpgrade
                ? existingInstallDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DiaryOfMine");
            _path.ReadOnly = isUpgrade;
            _browse = MakeButton("浏览…", Color.FromArgb(239, 244, 240));
            _browse.Location = new Point(497, 138);
            _browse.Size = new Size(82, 32);
            _browse.Enabled = !isUpgrade;
            _browse.Click += BrowseClick;

            _shortcuts.Text = "创建桌面快捷方式";
            _shortcuts.Checked = !isUpgrade || File.Exists(GetDesktopShortcutPath());
            _shortcuts.AutoSize = true;
            _shortcuts.Location = new Point(40, 194);
            _taskbar.Text = "添加到任务栏";
            _taskbar.Checked = !isUpgrade || File.Exists(GetTaskbarShortcutPath());
            _taskbar.AutoSize = true;
            _taskbar.Location = new Point(40, 225);
            _launch.Text = "安装完成后启动 Diary of Mine";
            _launch.Checked = false;
            _launch.AutoSize = true;
            _launch.Location = new Point(40, 256);

            _status.Location = new Point(40, 292);
            _status.Size = new Size(420, 34);
            _status.ForeColor = Color.FromArgb(103, 116, 108);
            _status.Text = isUpgrade
                ? "已找到上一版本，将在原安装位置更新并保留个人数据与设置。"
                : "安装包不包含开发电脑上的任何日程或个人数据。";
            _progress.Location = new Point(40, 330);
            _progress.Size = new Size(420, 8);
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Visible = false;
            _install = MakeButton(isUpgrade ? "更新" : "安装", Color.FromArgb(61, 107, 87));
            _install.ForeColor = Color.White;
            _install.Location = new Point(477, 301);
            _install.Size = new Size(102, 42);
            _install.Font = new Font(Font, FontStyle.Bold);
            _install.Click += InstallClick;

            Controls.AddRange(new Control[] { title, subtitle, pathLabel, _path, _browse,
                _shortcuts, _taskbar, _launch, _status, _progress, _install });
        }

        private static Button MakeButton(string text, Color background)
        {
            return new Button
            {
                Text = text,
                BackColor = background,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderColor = Color.FromArgb(207, 218, 210), BorderSize = 1 },
                Cursor = Cursors.Hand
            };
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 Diary of Mine 安装位置";
                dialog.SelectedPath = Directory.Exists(_path.Text) ? _path.Text : Path.GetDirectoryName(_path.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _path.Text = Path.Combine(dialog.SelectedPath, "DiaryOfMine");
            }
        }

        private void InstallClick(object sender, EventArgs e)
        {
            string installDirectory;
            try
            {
                if (String.IsNullOrWhiteSpace(_path.Text) || !Path.IsPathRooted(_path.Text))
                    throw new InvalidOperationException("请选择完整的安装路径。");
                installDirectory = Path.GetFullPath(_path.Text.Trim());
                if (String.Equals(installDirectory, Path.GetPathRoot(installDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("不能直接安装到磁盘根目录。");
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, "正在安装，请稍候…");
            try
            {
                bool launchAfterInstall = _launch.Checked;
                Install(installDirectory, _shortcuts.Checked, _taskbar.Checked);
                Environment.CurrentDirectory = Path.GetTempPath();
                SetBusy(false, "安装完成。现有个人数据与设置均已保留。");
                MessageBox.Show(this, "Diary of Mine v" + Version + " 已安装完成。", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (launchAfterInstall)
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(installDirectory, "DiaryOfMine.exe"))
                    {
                        WorkingDirectory = installDirectory,
                        UseShellExecute = true
                    });
                }
                Close();
            }
            catch (Exception exception)
            {
                SetBusy(false, "安装失败：" + exception.Message);
                MessageBox.Show(this, "安装失败。\n\n" + exception.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetBusy(bool busy, string message)
        {
            _install.Enabled = !busy;
            _path.Enabled = !busy;
            _browse.Enabled = !busy && !_path.ReadOnly;
            _shortcuts.Enabled = !busy;
            _taskbar.Enabled = !busy;
            _launch.Enabled = !busy;
            _progress.Visible = busy;
            _status.Text = message;
            Refresh();
            Application.DoEvents();
        }
    }

    private static void Install(string installDirectory, bool createShortcuts, bool pinToTaskbar)
    {
        string sourceDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string sourceApplication = Path.Combine(sourceDirectory, "DiaryOfMine.exe");
        string sourceUninstaller = Path.Combine(sourceDirectory, "Uninstall-DiaryOfMine.exe");
        if (!File.Exists(sourceApplication) || !File.Exists(sourceUninstaller))
            throw new FileNotFoundException("安装包载荷不完整。请重新下载安装包。");

        foreach (Process process in Process.GetProcessesByName("DiaryOfMine"))
        {
            try { process.Kill(); process.WaitForExit(3000); } catch { }
            finally { process.Dispose(); }
        }

        Directory.CreateDirectory(installDirectory);
        string targetApplication = Path.Combine(installDirectory, "DiaryOfMine.exe");
        string targetUninstaller = Path.Combine(installDirectory, "Uninstall-DiaryOfMine.exe");
        ReplaceInstalledBinaries(sourceApplication, targetApplication,
            sourceUninstaller, targetUninstaller);

        string startMenuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs");
        string startMenuShortcut = Path.Combine(startMenuDirectory, ProductName + ".lnk");
        string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");
        Directory.CreateDirectory(startMenuDirectory);
        CreateShortcut(startMenuShortcut, targetApplication, installDirectory, "Windows 本地日记与日程工具");
        if (createShortcuts)
            CreateShortcut(desktopShortcut, targetApplication, installDirectory, "Windows 本地日记与日程工具");
        else
            TryDelete(desktopShortcut);

        if (pinToTaskbar)
            PinShortcutToTaskbar(startMenuShortcut, targetApplication, installDirectory);
        else
            TryDelete(GetTaskbarShortcutPath());

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            if (key == null) throw new InvalidOperationException("无法注册卸载程序。");
            string uninstallCommand = "\"" + targetUninstaller + "\"";
            key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
            key.SetValue("Publisher", ProductName, RegistryValueKind.String);
            key.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
            key.SetValue("DisplayIcon", "\"" + targetApplication + "\",0", RegistryValueKind.String);
            key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
            key.SetValue("QuietUninstallString", uninstallCommand + " /quiet", RegistryValueKind.String);
            key.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd"), RegistryValueKind.String);
            key.SetValue("EstimatedSize", (int)(new FileInfo(targetApplication).Length / 1024L), RegistryValueKind.DWord);
            key.SetValue("Comments", "本地日记与日程工具；卸载不会删除用户数据。", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("WindowsInstaller", 0, RegistryValueKind.DWord);
            key.SetValue("InstallerManaged", 1, RegistryValueKind.DWord);
        }
        using (RegistryKey appPath = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\App Paths\DiaryOfMine.exe"))
        {
            if (appPath != null)
            {
                appPath.SetValue(String.Empty, targetApplication, RegistryValueKind.String);
                appPath.SetValue("Path", installDirectory, RegistryValueKind.String);
            }
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private static void ScheduleTemporaryPayloadCleanup(string extractionDirectory, int processId)
    {
        try
        {
            string fullDirectory = Path.GetFullPath(extractionDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string directoryName = Path.GetFileName(fullDirectory);
            if (!fullDirectory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !directoryName.StartsWith("IXP", StringComparison.OrdinalIgnoreCase))
                return;

            string cleanupScript = Path.Combine(Path.GetTempPath(), "DiaryOfMine-setup-cleanup-" + Guid.NewGuid().ToString("N") + ".cmd");
            string commands = "@echo off\r\n"
                + ":wait\r\n"
                + "tasklist /FI \"PID eq " + processId + "\" /NH | findstr /C:\"" + processId + "\" >nul\r\n"
                + "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)\r\n"
                + "for /L %%i in (1,1,30) do (\r\n"
                + "  rmdir /s /q \"" + fullDirectory + "\" 2>nul\r\n"
                + "  if not exist \"" + fullDirectory + "\" goto done\r\n"
                + "  timeout /t 1 /nobreak >nul\r\n"
                + ")\r\n"
                + ":done\r\n"
                + "del /f /q \"%~f0\"\r\n";
            File.WriteAllText(cleanupScript, commands, System.Text.Encoding.Default);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + cleanupScript + "\"\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static string GetTaskbarShortcutPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar", ProductName + ".lnk");
    }

    private static string GetDesktopShortcutPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ProductName + ".lnk");
    }

    private static string FindExistingInstallDirectory()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey, false))
            {
                string value = key == null ? null : Convert.ToString(key.GetValue("InstallLocation"));
                if (IsExistingInstallDirectory(value)) return Path.GetFullPath(value);
            }
            using (RegistryKey appPath = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\DiaryOfMine.exe", false))
            {
                string executable = appPath == null ? null : Convert.ToString(appPath.GetValue(String.Empty));
                if (!String.IsNullOrWhiteSpace(executable))
                {
                    executable = executable.Trim().Trim('"');
                    string directory = Path.GetDirectoryName(executable);
                    if (IsExistingInstallDirectory(directory)) return Path.GetFullPath(directory);
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsExistingInstallDirectory(string directory)
    {
        return !String.IsNullOrWhiteSpace(directory)
            && Path.IsPathRooted(directory)
            && File.Exists(Path.Combine(directory, "DiaryOfMine.exe"));
    }

    private static void ReplaceInstalledBinaries(string sourceApplication, string targetApplication,
        string sourceUninstaller, string targetUninstaller)
    {
        string token = Guid.NewGuid().ToString("N");
        string stagedApplication = targetApplication + "." + token + ".new";
        string stagedUninstaller = targetUninstaller + "." + token + ".new";
        string applicationBackup = targetApplication + "." + token + ".bak";
        string uninstallerBackup = targetUninstaller + "." + token + ".bak";
        bool applicationExisted = File.Exists(targetApplication);
        bool uninstallerExisted = File.Exists(targetUninstaller);
        bool applicationCommitted = false;
        bool uninstallerCommitted = false;
        try
        {
            File.Copy(sourceApplication, stagedApplication, false);
            File.Copy(sourceUninstaller, stagedUninstaller, false);
            CommitStagedFile(stagedApplication, targetApplication, applicationBackup, applicationExisted);
            applicationCommitted = true;
            CommitStagedFile(stagedUninstaller, targetUninstaller, uninstallerBackup, uninstallerExisted);
            uninstallerCommitted = true;
        }
        catch
        {
            RollBackFile(targetUninstaller, uninstallerBackup, uninstallerExisted, uninstallerCommitted);
            RollBackFile(targetApplication, applicationBackup, applicationExisted, applicationCommitted);
            throw;
        }
        finally
        {
            TryDelete(stagedApplication);
            TryDelete(stagedUninstaller);
            TryDelete(applicationBackup);
            TryDelete(uninstallerBackup);
        }
    }

    private static void CommitStagedFile(string stagedFile, string targetFile,
        string backupFile, bool targetExisted)
    {
        if (targetExisted)
            File.Replace(stagedFile, targetFile, backupFile, true);
        else
            File.Move(stagedFile, targetFile);
    }

    private static void RollBackFile(string targetFile, string backupFile,
        bool targetExisted, bool committed)
    {
        if (!committed) return;
        try
        {
            if (targetExisted && File.Exists(backupFile))
                File.Copy(backupFile, targetFile, true);
            else if (!targetExisted)
                TryDelete(targetFile);
        }
        catch { }
    }

    private static void PinShortcutToTaskbar(string sourceShortcut, string target, string workingDirectory)
    {
        string taskbarShortcut = GetTaskbarShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(taskbarShortcut));
        CreateShortcut(taskbarShortcut, target, workingDirectory, "Windows 本地日记与日程工具");
        TryInvokeTaskbarPinVerb(sourceShortcut);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private static void TryInvokeTaskbarPinVerb(string shortcutPath)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell,
                new object[] { Path.GetDirectoryName(shortcutPath) });
            if (folder == null) return;
            object item = folder.GetType().InvokeMember("ParseName", BindingFlags.InvokeMethod, null, folder,
                new object[] { Path.GetFileName(shortcutPath) });
            if (item == null) return;
            object verbs = item.GetType().InvokeMember("Verbs", BindingFlags.InvokeMethod, null, item, null);
            int count = Convert.ToInt32(verbs.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, verbs, null));
            for (int index = 0; index < count; index++)
            {
                object verb = verbs.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, verbs, new object[] { index });
                string name = Convert.ToString(verb.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, verb, null));
                string normalized = (name ?? String.Empty).Replace("&", String.Empty).ToLowerInvariant();
                if (normalized.Contains("pin to taskbar") || normalized.Contains("固定到任务栏"))
                {
                    verb.GetType().InvokeMember("DoIt", BindingFlags.InvokeMethod, null, verb, null);
                    break;
                }
            }
        }
        catch { }
    }

    private static void CreateShortcut(string shortcutPath, string target, string workingDirectory, string description)
    {
        IShellLinkW link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(workingDirectory);
        link.SetDescription(description);
        link.SetIconLocation(target, 0);
        ((IPersistFile)link).Save(shortcutPath, false);
        Marshal.FinalReleaseComObject(link);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
