using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Services;

internal static class CalendarFileLinkUtilities
{
    internal const int MaximumLinks = 32;

    internal sealed record AddResult(
        IReadOnlyList<CalendarFileLink> Links,
        int Added,
        int Ignored,
        bool ReachedLimit);

    internal static AddResult AddPaths(
        IEnumerable<CalendarFileLink> current,
        IEnumerable<string> paths)
    {
        var links = current.ToList();
        var knownPaths = links.Select(link => link.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var ignored = 0;
        var reachedLimit = false;

        foreach (var candidate in paths)
        {
            if (links.Count >= MaximumLinks)
            {
                reachedLimit = true;
                break;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(candidate)
                    || !Path.IsPathFullyQualified(candidate))
                {
                    ignored++;
                    continue;
                }

                var fullPath = Path.GetFullPath(candidate);
                var isDirectory = Directory.Exists(fullPath);
                if (!isDirectory && !File.Exists(fullPath))
                {
                    ignored++;
                    continue;
                }
                if (!knownPaths.Add(fullPath))
                {
                    ignored++;
                    continue;
                }

                links.Add(new CalendarFileLink(
                    Guid.NewGuid(), fullPath, GetDisplayName(fullPath), false, DateTimeOffset.Now)
                {
                    IsDirectory = isDirectory
                });
                added++;
            }
            catch (Exception) when (candidate is not null)
            {
                ignored++;
            }
        }

        return new AddResult(Order(links), added, ignored, reachedLimit);
    }

    internal static IReadOnlyList<CalendarFileLink> Pin(
        IEnumerable<CalendarFileLink> links,
        Guid linkId) =>
        Order(links.Select(link => link with { IsPinned = link.Id == linkId }));

    internal static IReadOnlyList<CalendarFileLink> Remove(
        IEnumerable<CalendarFileLink> links,
        Guid linkId) => Order(links.Where(link => link.Id != linkId));

    internal static IReadOnlyList<CalendarFileLink> Rebind(
        IEnumerable<CalendarFileLink> links,
        Guid linkId,
        string newPath)
    {
        var fullPath = Path.GetFullPath(newPath);
        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath))
            throw new FileNotFoundException("选择的文件或文件夹不存在。", fullPath);
        if (links.Any(link => link.Id != linkId
                              && string.Equals(link.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("该文件已经绑定到此日程。");

        return Order(links.Select(link => link.Id == linkId
            ? link with
            {
                FilePath = fullPath,
                DisplayName = GetDisplayName(fullPath),
                IsDirectory = isDirectory
            }
            : link));
    }

    internal static bool IsLikelyCachePath(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        return normalized.Contains("\\appdata\\local\\temp\\", StringComparison.Ordinal)
               || normalized.Contains("\\wechat files\\", StringComparison.Ordinal)
               || normalized.Contains("\\wechat\\", StringComparison.Ordinal)
               || normalized.Contains("\\tencent files\\", StringComparison.Ordinal)
               || normalized.Contains("\\qq\\filerecv\\", StringComparison.Ordinal)
               || normalized.Contains("\\cache\\", StringComparison.Ordinal)
               || normalized.Contains("\\temp\\", StringComparison.Ordinal);
    }

    internal static void Open(Window owner, CalendarFileLink link)
    {
        if (!File.Exists(link.FilePath) && !Directory.Exists(link.FilePath))
        {
            MessageBox.Show(owner, "原文件或文件夹已移动、删除，或被缓存软件清理，请使用“重新定位”。",
                "快捷链接不可用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var riskyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".scr", ".com", ".lnk", ".url" };
        if (!link.IsDirectory && riskyExtensions.Contains(Path.GetExtension(link.FilePath))
            && MessageBox.Show(owner, "该链接可能运行程序或命令。确定使用系统默认方式打开吗？",
                "打开快捷链接", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(link.FilePath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, $"无法打开文件：{exception.Message}", "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static string Describe(AddResult result)
    {
        if (result.ReachedLimit) return $"已添加 {result.Added} 个；每项最多 {MaximumLinks} 个链接。";
        if (result.Added == 0) return "没有添加项目：失效路径及重复的文件或文件夹会被忽略。";
        return result.Ignored > 0
            ? $"已添加 {result.Added} 个，另有 {result.Ignored} 个路径被忽略。"
            : $"已添加 {result.Added} 个快捷链接。";
    }

    private static IReadOnlyList<CalendarFileLink> Order(IEnumerable<CalendarFileLink> links) =>
        links.OrderByDescending(link => link.IsPinned)
            .ThenBy(link => link.AddedAt)
            .ToArray();

    private static string GetDisplayName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
