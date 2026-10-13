using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class UninstallerHost
{
    private const string ProductName = "Diary of Mine";

    [STAThread]
    private static void Main(string[] args)
    {
        bool quiet = args != null && Array.Exists(args, value => String.Equals(value, "/quiet", StringComparison.OrdinalIgnoreCase));
        if (!quiet && MessageBox.Show("确定卸载 Diary of Mine？\n\n个人日程、日记和设置数据将被保留。",
                ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        string installDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string applicationPath = Path.Combine(installDirectory, "DiaryOfMine.exe");
        string uninstallerPath = Application.ExecutablePath;
        foreach (Process process in Process.GetProcessesByName("DiaryOfMine"))
        {
            try { process.Kill(); process.WaitForExit(3000); } catch { }
            finally { process.Dispose(); }
        }

        try
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (run != null) run.DeleteValue("DiaryOfMine", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DiaryOfMine", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\App Paths\DiaryOfMine.exe", false);
        }
        catch { }

        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar", ProductName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", ProductName + ".lnk"));
        TryDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", ProductName));

        if (!quiet)
            MessageBox.Show("Diary of Mine 已卸载。个人数据仍保留在本机。", ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);

        ScheduleFinalCleanup(Process.GetCurrentProcess().Id, installDirectory,
            applicationPath, uninstallerPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void ScheduleFinalCleanup(int processId, string installDirectory,
        string applicationPath, string uninstallerPath)
    {
        string cleanupScript = Path.Combine(Path.GetTempPath(),
            "DiaryOfMine-uninstall-cleanup-" + Guid.NewGuid().ToString("N") + ".cmd");
        string commands = "@echo off\r\n"
            + ":wait\r\n"
            + "tasklist /FI \"PID eq " + processId + "\" /NH | findstr /C:\"" + processId + "\" >nul\r\n"
            + "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)\r\n"
            + "del /f /q \"" + applicationPath + "\" 2>nul\r\n"
            + "del /f /q \"" + uninstallerPath + "\" 2>nul\r\n"
            + "rmdir \"" + installDirectory + "\" 2>nul\r\n"
            + "del /f /q \"%~f0\"\r\n";
        File.WriteAllText(cleanupScript, commands, System.Text.Encoding.Default);
        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + cleanupScript + "\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
