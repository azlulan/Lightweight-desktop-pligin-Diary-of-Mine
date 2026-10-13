using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiaryOfMine.Application.Calendar;
using DiaryOfMine.Desktop.Services;
using DiaryOfMine.Domain;
using Microsoft.Win32;
using CalendarDataItem = DiaryOfMine.Domain.CalendarItem;

namespace DiaryOfMine.Desktop.Views;

public partial class CalendarItemEditorWindow : Window
{
    private readonly CalendarItemKind _kind;
    private readonly List<CalendarFileLink> _fileLinks = new();
    private bool _closeStarted;
    private bool _submitOnClose;

    public CalendarItemDraft? Draft { get; private set; }

    public CalendarItemEditorWindow(CalendarItemKind kind, DateOnly? initialDate = null)
    {
        _kind = kind;
        InitializeComponent();

        var date = initialDate ?? DateOnly.FromDateTime(DateTime.Today);
        StartDatePicker.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        EndDatePicker.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        PopulateTimeChoices();
        ConfigureForKind();
        RenderFileLinks();
    }

    public CalendarItemEditorWindow(CalendarDataItem item)
        : this(
            item?.Kind ?? throw new ArgumentNullException(nameof(item)),
            DateOnly.FromDateTime(item.StartsAt.LocalDateTime))
    {
        PopulateFromItem(item);
    }

    private void PopulateTimeChoices()
    {
        StartHourComboBox.ItemsSource = BuildNumbers(24);
        EndHourComboBox.ItemsSource = BuildNumbers(_kind == CalendarItemKind.Key ? 25 : 24);
        var minuteChoices = BuildNumbers(60);
        StartMinuteComboBox.ItemsSource = minuteChoices;
        EndMinuteComboBox.ItemsSource = minuteChoices;

        var now = DateTime.Now;
        StartHourComboBox.SelectedIndex = now.Hour;
        SelectMinute(StartMinuteComboBox, now.Minute);
        var defaultEnd = now.AddHours(2);
        if (_kind == CalendarItemKind.Key && defaultEnd.Date > now.Date)
        {
            EndHourComboBox.SelectedIndex = 24;
            SelectMinute(EndMinuteComboBox, 0);
        }
        else
        {
            EndHourComboBox.SelectedIndex = Math.Min(defaultEnd.Hour, 23);
            SelectMinute(EndMinuteComboBox, defaultEnd.Minute);
        }
    }

    private void ConfigureForKind()
    {
        var label = _kind switch
        {
            CalendarItemKind.Class => "Class",
            CalendarItemKind.Key => "Key",
            CalendarItemKind.Idea => "Idea",
            CalendarItemKind.Inspiration => "灵感",
            _ => throw new ArgumentOutOfRangeException()
        };
        HeadingText.Text = $"添加 {label}";
        KindIcon.Source = (ImageSource)FindResource(_kind switch
        {
            CalendarItemKind.Class => "ClassIcon",
            CalendarItemKind.Key => "KeyIcon",
            CalendarItemKind.Idea => "IdeaIcon",
            CalendarItemKind.Inspiration => "IdeaIcon",
            _ => throw new ArgumentOutOfRangeException()
        });
        Title = $"Add {label}";

        switch (_kind)
        {
            case CalendarItemKind.Class:
                SubtitleText.Text = "录入跨日、跨月或长期持续的进程";
                HasEndCheckBox.IsChecked = false;
                OpenEndedHint.Visibility = Visibility.Visible;
                CompletedCheckBox.Visibility = Visibility.Visible;
                break;
            case CalendarItemKind.Key:
                SubtitleText.Text = "录入具有准确时间的关键事件";
                KeySummaryPanel.Visibility = Visibility.Visible;
                HasEndCheckBox.IsChecked = false;
                OpenEndedHint.Text = "不设置结束时间时默认持续 2 小时，最晚截至当天 24:00。";
                CompletedCheckBox.Visibility = Visibility.Visible;
                break;
            case CalendarItemKind.Idea:
                SubtitleText.Text = "录入当天要做、但没有准确时间的事项";
                StartTimePanel.Visibility = Visibility.Collapsed;
                EndGroup.Visibility = Visibility.Collapsed;
                CompletedCheckBox.Visibility = Visibility.Visible;
                break;
            case CalendarItemKind.Inspiration:
                SubtitleText.Text = "记下此刻出现的想法或短篇灵感";
                StartGroup.Visibility = Visibility.Collapsed;
                StartTimePanel.Visibility = Visibility.Collapsed;
                EndGroup.Visibility = Visibility.Collapsed;
                TitleTextBox.MaxLength = 1000;
                TitleTextBox.Height = 112;
                TitleTextBox.Padding = new Thickness(10, 8, 10, 8);
                TitleTextBox.AcceptsReturn = true;
                TitleTextBox.TextWrapping = TextWrapping.Wrap;
                TitleTextBox.VerticalContentAlignment = VerticalAlignment.Top;
                TitleTextBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                FileLinksPanel.Visibility = Visibility.Collapsed;
                Grid.SetColumnSpan(EditorFormScrollViewer, 2);
                Width = 620;
                Height = 620;
                MinWidth = 500;
                MinHeight = 520;
                break;
        }
        UpdateEndControls();
    }

    private void PopulateFromItem(CalendarDataItem item)
    {
        var label = item.Kind.ToString();
        Title = $"Edit {label}";
        HeadingText.Text = $"编辑 {label}";
        TitleTextBox.Text = item.Title;
        SummaryTextBox.Text = item.Summary;
        StartDatePicker.SelectedDate = item.StartsAt.LocalDateTime.Date;
        StartHourComboBox.SelectedIndex = item.StartsAt.LocalDateTime.Hour;
        SelectMinute(StartMinuteComboBox, item.StartsAt.LocalDateTime.Minute);
        HasEndCheckBox.IsChecked = item.EndsAt is not null;
        if (item.EndsAt is { } end)
        {
            var startDate = item.StartsAt.LocalDateTime.Date;
            if (item.Kind == CalendarItemKind.Key
                && end.LocalDateTime.TimeOfDay == TimeSpan.Zero
                && end.LocalDateTime.Date == startDate.AddDays(1))
            {
                EndDatePicker.SelectedDate = startDate;
                EndHourComboBox.SelectedIndex = 24;
                SelectMinute(EndMinuteComboBox, 0);
            }
            else
            {
                EndDatePicker.SelectedDate = end.LocalDateTime.Date;
                EndHourComboBox.SelectedIndex = end.LocalDateTime.Hour;
                SelectMinute(EndMinuteComboBox, end.LocalDateTime.Minute);
            }
        }
        CompletedCheckBox.IsChecked = item.IsCompleted;
        _fileLinks.Clear();
        _fileLinks.AddRange(item.FileLinks ?? Array.Empty<CalendarFileLink>());
        RenderFileLinks();
        UpdateEndControls();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MakeDatePickerTextReadOnly(StartDatePicker);
        MakeDatePickerTextReadOnly(EndDatePicker);
        TitleTextBox.Focus();
    }

    private void OnHasEndChanged(object sender, RoutedEventArgs e) => UpdateEndControls();

    private void UpdateEndControls()
    {
        var enabled = HasEndCheckBox.IsChecked == true;
        EndDatePicker.IsEnabled = enabled;
        EndHourComboBox.IsEnabled = enabled;
        EndMinuteComboBox.IsEnabled = enabled;
        OpenEndedHint.Opacity = enabled ? 0.45 : 1;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
        => ConfirmAndClose();

    private void ConfirmAndClose()
    {
        if (!TryCommitDraft()) return;
        _submitOnClose = true;
        CloseOnce();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) =>
        FormKeyboardNavigation.HandleEnter(
            e,
            ConfirmAndClose,
            TitleTextBox,
            SummaryTextBox,
            StartDatePicker,
            StartHourComboBox,
            StartMinuteComboBox,
            HasEndCheckBox,
            EndDatePicker,
            EndHourComboBox,
            EndMinuteComboBox,
            CompletedCheckBox);

    public bool TryCommitDraft()
    {
        ValidationText.Visibility = Visibility.Collapsed;
        var title = TitleTextBox.Text.Trim();
        var summary = SummaryTextBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowValidation("你这灵感也太空旷啦！");
            return false;
        }
        if (_kind == CalendarItemKind.Key && summary.Length == 0)
        {
            summary = FirstTextElements(title, 3);
            SummaryTextBox.Text = summary;
        }
        if (StartDatePicker.SelectedDate is not { } startDate)
        {
            ShowValidation("请选择开始日期。");
            return false;
        }

        var startHour = 0;
        var startMinute = 0;
        var isUntimed = _kind is CalendarItemKind.Idea or CalendarItemKind.Inspiration;
        if (!isUntimed
            && (!TryReadTimePart(StartHourComboBox, "开始小时", 23, out startHour)
                || !TryReadTimePart(StartMinuteComboBox, "开始分钟", 59, out startMinute)))
            return false;

        var start = isUntimed
            ? ToLocalOffset(startDate, 0, 0)
            : ToLocalOffset(startDate, startHour, startMinute);

        DateTimeOffset? end = null;
        if (!isUntimed && HasEndCheckBox.IsChecked == true)
        {
            if (EndDatePicker.SelectedDate is not { } endDate)
            {
                ShowValidation("请选择结束日期。");
                return false;
            }
            if (!TryReadTimePart(
                    EndHourComboBox, "结束小时", _kind == CalendarItemKind.Key ? 24 : 23,
                    out var endHour)
                || !TryReadTimePart(EndMinuteComboBox, "结束分钟", 59, out var endMinute))
                return false;
            if (endHour == 24 && endMinute != 0)
            {
                ShowValidation("24 点只能选择 00 分。");
                return false;
            }
            end = ToLocalOffset(endDate, endHour, endMinute, allow24: _kind == CalendarItemKind.Key);
            if (end < start)
            {
                ShowValidation("结束日期与时间不能早于开始时间。");
                return false;
            }
            if (_kind == CalendarItemKind.Key)
            {
                var startLocalDate = start.LocalDateTime.Date;
                var latest = startLocalDate.AddDays(1);
                if (end.Value.LocalDateTime > latest)
                {
                    ShowValidation("Key 必须在开始当天 24:00 前结束。");
                    return false;
                }
            }
        }

        if (_kind == CalendarItemKind.Key
            && StringInfo.ParseCombiningCharacters(summary).Length > 3)
        {
            ShowValidation("Key 概述不能超过三个字。");
            SummaryTextBox.Focus();
            return false;
        }

        Draft = new CalendarItemDraft(
            _kind,
            title,
            start,
            end,
            SupportsCompletion(_kind) && CompletedCheckBox.IsChecked == true,
            _kind == CalendarItemKind.Key ? summary : string.Empty,
            _fileLinks.ToArray());
        return true;
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要绑定的本地文件",
            Filter = "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) AddFilePaths(dialog.FileNames);
    }

    private void OnAddFoldersClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要绑定的本地文件夹",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) AddFilePaths(dialog.FolderNames);
    }

    private void OnFileLinksDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileLinksDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFilePaths(paths);
        else ShowFileLinksStatus("该来源未提供稳定的本地路径，请先保存到本机后再拖入。", true);
        e.Handled = true;
    }

    private void AddFilePaths(IEnumerable<string> paths)
    {
        var result = CalendarFileLinkUtilities.AddPaths(_fileLinks, paths);
        _fileLinks.Clear();
        _fileLinks.AddRange(result.Links);
        RenderFileLinks();
        ShowFileLinksStatus(CalendarFileLinkUtilities.Describe(result), result.Added == 0 || result.Ignored > 0);
    }

    private void RenderFileLinks()
    {
        if (!IsInitialized) return;
        FileLinksList.Children.Clear();
        if (_fileLinks.Count == 0)
        {
            FileLinksList.Children.Add(new TextBlock
            {
                Text = "暂无快捷链接\n可将资源管理器中的文件或文件夹拖到此处\n应用内的文件请先下载至本地再进行快捷链接绑定",
                Foreground = BrushFrom("#8A948E"),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 40, 8, 12)
            });
            return;
        }

        foreach (var link in _fileLinks.OrderByDescending(value => value.IsPinned).ThenBy(value => value.AddedAt))
            FileLinksList.Children.Add(CreateFileLinkCard(link));
    }

    private FrameworkElement CreateFileLinkCard(CalendarFileLink link)
    {
        var missing = !File.Exists(link.FilePath) && !Directory.Exists(link.FilePath);
        var cached = CalendarFileLinkUtilities.IsLikelyCachePath(link.FilePath);
        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = BrushFrom(link.IsPinned ? "#AFC9BC" : "#DCE3DE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 8, 7, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = link.IsDirectory ? "▰" : "▤",
            FontSize = 17,
            Foreground = BrushFrom(missing ? "#B98A86" : "#68877A"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var openButton = new Button
        {
            Content = link.DisplayName,
            ToolTip = link.FilePath,
            Height = 29,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsEnabled = !missing
        };
        openButton.Click += (_, _) => CalendarFileLinkUtilities.Open(this, link);
        Grid.SetColumn(openButton, 1);
        grid.Children.Add(openButton);

        var pinButton = new Button
        {
            Content = link.IsPinned ? "已置顶" : "置顶",
            ToolTip = link.IsPinned ? "此链接已置顶" : "将此链接排到最前",
            Width = 54,
            Height = 27,
            Margin = new Thickness(0, 0, 5, 0),
            IsEnabled = !link.IsPinned
        };
        pinButton.Click += (_, _) =>
        {
            ReplaceFileLinks(CalendarFileLinkUtilities.Pin(_fileLinks, link.Id));
            ShowFileLinksStatus($"已置顶 {link.DisplayName}", false);
        };
        Grid.SetColumn(pinButton, 2);
        grid.Children.Add(pinButton);

        var removeButton = new Button
        {
            Content = "×",
            ToolTip = "移除链接（不会删除原文件或文件夹）",
            Width = 28,
            Height = 27,
            Foreground = BrushFrom("#A35D5D")
        };
        removeButton.Click += (_, _) =>
        {
            ReplaceFileLinks(CalendarFileLinkUtilities.Remove(_fileLinks, link.Id));
            ShowFileLinksStatus("已移除链接，原文件或文件夹未被删除。", false);
        };
        Grid.SetColumn(removeButton, 3);
        grid.Children.Add(removeButton);

        if (missing || cached)
        {
            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 5, 0, 0) };
            statusPanel.Children.Add(new TextBlock
            {
                Text = missing
                    ? (link.IsDirectory ? "文件夹已失效或已移动" : "文件已失效或已移动")
                    : "缓存目录内容可能被微信、QQ或系统清理",
                Foreground = BrushFrom(missing ? "#B35F5F" : "#956C44"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (missing)
            {
                var rebindButton = new Button
                {
                    Content = "重新定位",
                    Height = 24,
                    Width = 66,
                    Margin = new Thickness(8, 0, 0, 0),
                    FontSize = 10
                };
                rebindButton.Click += (_, _) => RebindFileLink(link);
                statusPanel.Children.Add(rebindButton);
            }
            Grid.SetRow(statusPanel, 1);
            Grid.SetColumn(statusPanel, 1);
            Grid.SetColumnSpan(statusPanel, 3);
            grid.Children.Add(statusPanel);
        }

        card.Child = grid;
        return card;
    }

    private void RebindFileLink(CalendarFileLink link)
    {
        if (link.IsDirectory)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = $"重新定位 {link.DisplayName}",
                Multiselect = false
            };
            if (folderDialog.ShowDialog(this) == true)
                TryRebindFileLink(link, folderDialog.FolderName);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"重新定位 {link.DisplayName}",
            FileName = link.DisplayName,
            Filter = "所有文件|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) TryRebindFileLink(link, dialog.FileName);
    }

    private void TryRebindFileLink(CalendarFileLink link, string path)
    {
        try
        {
            ReplaceFileLinks(CalendarFileLinkUtilities.Rebind(_fileLinks, link.Id, path));
            ShowFileLinksStatus("快捷链接已重新定位。", false);
        }
        catch (Exception exception)
        {
            ShowFileLinksStatus(exception.Message, true);
        }
    }

    private void ReplaceFileLinks(IEnumerable<CalendarFileLink> links)
    {
        _fileLinks.Clear();
        _fileLinks.AddRange(links);
        RenderFileLinks();
    }

    private void ShowFileLinksStatus(string message, bool warning)
    {
        FileLinksStatusText.Text = message;
        FileLinksStatusText.Foreground = BrushFrom(warning ? "#956C44" : "#5E7D6F");
        FileLinksStatusText.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush BrushFrom(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelAndClose();

    private void OnCopyTitleToSummaryClick(object sender, RoutedEventArgs e)
    {
        var title = TitleTextBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowValidation("请先填写名称，再提取前三个字。");
            TitleTextBox.Focus();
            return;
        }

        SummaryTextBox.Text = FirstTextElements(title, 3);
        SummaryTextBox.Focus();
        SummaryTextBox.CaretIndex = SummaryTextBox.Text.Length;
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private static string FirstTextElements(string value, int maximumElements)
    {
        var starts = StringInfo.ParseCombiningCharacters(value);
        return starts.Length <= maximumElements ? value : value[..starts[maximumElements]];
    }

    public void CancelAndClose()
    {
        Draft = null;
        _submitOnClose = false;
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (_closeStarted) return;
        _closeStarted = true;
        Close();
    }

    public void CloseAfterExternalCommit()
    {
        _submitOnClose = true;
        CloseOnce();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_submitOnClose) Draft = null;
        _closeStarted = true;
        base.OnClosing(e);
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private static DateTimeOffset ToLocalOffset(
        DateTime date, int hour, int minute, bool allow24 = false)
    {
        if (hour == 24 && allow24 && minute == 0)
        {
            var midnight = date.Date.AddDays(1);
            return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        }
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            throw new InvalidOperationException("时间选项无效。");
        var local = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static IReadOnlyList<string> BuildNumbers(int count)
    {
        var values = new string[count];
        for (var index = 0; index < count; index++) values[index] = index.ToString("00");
        return values;
    }

    private void SelectMinute(ComboBox comboBox, int minute)
    {
        comboBox.SelectedIndex = Math.Clamp(minute, 0, 59);
    }

    private bool TryReadTimePart(
        ComboBox comboBox, string fieldName, int maximum, out int value)
    {
        var text = comboBox.Text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0
            && value <= maximum)
            return true;

        ShowValidation($"{fieldName}请输入 00—{maximum:00} 之间的整数。");
        comboBox.Focus();
        return false;
    }

    private static void MakeDatePickerTextReadOnly(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is DatePickerTextBox textBox)
            {
                textBox.IsReadOnly = true;
                return;
            }
            MakeDatePickerTextReadOnly(child);
        }
    }

    private void TitleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {

    }

    private static bool SupportsCompletion(CalendarItemKind kind) =>
        kind is CalendarItemKind.Class or CalendarItemKind.Key or CalendarItemKind.Idea;
}
