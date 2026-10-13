using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiaryOfMine.Application.Calendar;
using DiaryOfMine.Desktop.Services;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Views;

public partial class KeyTaskEditorWindow : Window
{
    private bool _closeStarted;
    private bool _submitOnClose;
    private bool _editingBatch;

    public KeyTaskSchedule? Schedule { get; private set; }

    public KeyTaskEditorWindow(DateOnly? initialDate = null)
    {
        InitializeComponent();
        var start = initialDate ?? DateOnly.FromDateTime(DateTime.Today);
        RangeStartDatePicker.SelectedDate = start.ToDateTime(TimeOnly.MinValue);
        RangeEndDatePicker.SelectedDate = start.AddMonths(1).AddDays(-1)
            .ToDateTime(TimeOnly.MinValue);

        StartHourComboBox.ItemsSource = BuildNumbers(24);
        StartMinuteComboBox.ItemsSource = BuildNumbers(60);
        EndHourComboBox.ItemsSource = BuildNumbers(25);
        EndMinuteComboBox.ItemsSource = BuildNumbers(60);
        StartHourComboBox.SelectedIndex = 8;
        StartMinuteComboBox.SelectedIndex = 0;
        EndHourComboBox.SelectedIndex = 10;
        EndMinuteComboBox.SelectedIndex = 0;
        SetDayChecked(start.DayOfWeek, true);
        UpdatePreview();
    }

    public KeyTaskEditorWindow(CalendarItem occurrence)
        : this(DateOnly.FromDateTime(occurrence.StartsAt.LocalDateTime))
    {
        var schedule = KeyTaskSchedule.FromCalendarItem(occurrence);
        _editingBatch = true;
        Title = "Edit Task";
        HeadingText.Text = "编辑 Task 批次";
        HeadingDescriptionText.Text = "保存后将按新规则重新生成整个批次；单独拖动或删除仍只作用于当天实例";
        TitleTextBox.Text = schedule.Title;
        SummaryTextBox.Text = schedule.Summary;
        RangeStartDatePicker.SelectedDate = schedule.StartDate.ToDateTime(TimeOnly.MinValue);
        RangeEndDatePicker.SelectedDate = schedule.EndDate.ToDateTime(TimeOnly.MinValue);
        SetAllDays(false);
        foreach (var day in schedule.Weekdays) SetDayChecked(day, true);
        SetTime(StartHourComboBox, StartMinuteComboBox, schedule.StartMinute);
        SetTime(EndHourComboBox, EndMinuteComboBox, schedule.EndMinute);
        UpdatePreview();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => TitleTextBox.Focus();

    public bool TryCommitSchedule()
    {
        ValidationText.Visibility = Visibility.Collapsed;
        var title = TitleTextBox.Text.Trim();
        var summary = SummaryTextBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowValidation("名称不能为空。");
            TitleTextBox.Focus();
            return false;
        }
        if (summary.Length == 0)
        {
            summary = FirstTextElements(title, 3);
            SummaryTextBox.Text = summary;
        }
        if (StringInfo.ParseCombiningCharacters(summary).Length > 3)
        {
            ShowValidation("Key 概述不能超过三个字。");
            SummaryTextBox.Focus();
            return false;
        }
        if (RangeStartDatePicker.SelectedDate is not { } startDate
            || RangeEndDatePicker.SelectedDate is not { } endDate)
        {
            ShowValidation("请选择完整的排布起止日期。");
            return false;
        }
        if (!TryReadTimePart(StartHourComboBox, "开始小时", 23, out var startHour)
            || !TryReadTimePart(StartMinuteComboBox, "开始分钟", 59, out var startMinute)
            || !TryReadTimePart(EndHourComboBox, "结束小时", 24, out var endHour)
            || !TryReadTimePart(EndMinuteComboBox, "结束分钟", 59, out var endMinute))
            return false;
        if (endHour == 24 && endMinute != 0)
        {
            ShowValidation("24 点只能输入 00 分。");
            EndMinuteComboBox.Focus();
            return false;
        }

        try
        {
            var schedule = new KeyTaskSchedule(
                title,
                summary,
                DateOnly.FromDateTime(startDate),
                DateOnly.FromDateTime(endDate),
                GetSelectedDays(),
                startHour * 60 + startMinute,
                endHour * 60 + endMinute);
            _ = schedule.Expand();
            Schedule = schedule;
            return true;
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
            return false;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
        => ConfirmAndClose();

    private void ConfirmAndClose()
    {
        if (!TryCommitSchedule()) return;
        _submitOnClose = true;
        CloseOnce();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) =>
        FormKeyboardNavigation.HandleEnter(
            e,
            ConfirmAndClose,
            TitleTextBox,
            SummaryTextBox,
            RangeStartDatePicker,
            RangeEndDatePicker,
            MondayCheckBox,
            TuesdayCheckBox,
            WednesdayCheckBox,
            ThursdayCheckBox,
            FridayCheckBox,
            SaturdayCheckBox,
            SundayCheckBox,
            StartHourComboBox,
            StartMinuteComboBox,
            EndHourComboBox,
            EndMinuteComboBox);

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Schedule = null;
        _submitOnClose = false;
        CloseOnce();
    }

    public void CloseAfterExternalCommit()
    {
        _submitOnClose = true;
        CloseOnce();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_submitOnClose) Schedule = null;
        _closeStarted = true;
        base.OnClosing(e);
    }

    private void CloseOnce()
    {
        if (_closeStarted) return;
        _closeStarted = true;
        Close();
    }

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
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private static string FirstTextElements(string value, int maximumElements)
    {
        var starts = StringInfo.ParseCombiningCharacters(value);
        return starts.Length <= maximumElements ? value : value[..starts[maximumElements]];
    }

    private void OnOneMonthClick(object sender, RoutedEventArgs e) => SetRange(months: 1);
    private void OnThreeMonthsClick(object sender, RoutedEventArgs e) => SetRange(months: 3);
    private void OnOneYearClick(object sender, RoutedEventArgs e) => SetRange(years: 1);

    private void SetRange(int months = 0, int years = 0)
    {
        if (RangeStartDatePicker.SelectedDate is not { } value) return;
        var start = DateOnly.FromDateTime(value);
        var exclusiveEnd = years == 0 ? start.AddMonths(months) : start.AddYears(years);
        RangeEndDatePicker.SelectedDate = exclusiveEnd.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        UpdatePreview();
    }

    private void OnWeekdaysClick(object sender, RoutedEventArgs e)
    {
        SetAllDays(false);
        MondayCheckBox.IsChecked = TuesdayCheckBox.IsChecked = WednesdayCheckBox.IsChecked =
            ThursdayCheckBox.IsChecked = FridayCheckBox.IsChecked = true;
        UpdatePreview();
    }

    private void OnEveryDayClick(object sender, RoutedEventArgs e)
    {
        SetAllDays(true);
        UpdatePreview();
    }

    private void OnClearDaysClick(object sender, RoutedEventArgs e)
    {
        SetAllDays(false);
        UpdatePreview();
    }

    private void SetAllDays(bool value)
    {
        MondayCheckBox.IsChecked = value;
        TuesdayCheckBox.IsChecked = value;
        WednesdayCheckBox.IsChecked = value;
        ThursdayCheckBox.IsChecked = value;
        FridayCheckBox.IsChecked = value;
        SaturdayCheckBox.IsChecked = value;
        SundayCheckBox.IsChecked = value;
    }

    private void SetDayChecked(DayOfWeek day, bool value)
    {
        GetDayCheckBox(day).IsChecked = value;
    }

    private HashSet<DayOfWeek> GetSelectedDays()
    {
        var result = new HashSet<DayOfWeek>();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
            if (GetDayCheckBox(day).IsChecked == true) result.Add(day);
        return result;
    }

    private CheckBox GetDayCheckBox(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => MondayCheckBox,
        DayOfWeek.Tuesday => TuesdayCheckBox,
        DayOfWeek.Wednesday => WednesdayCheckBox,
        DayOfWeek.Thursday => ThursdayCheckBox,
        DayOfWeek.Friday => FridayCheckBox,
        DayOfWeek.Saturday => SaturdayCheckBox,
        _ => SundayCheckBox
    };

    private void OnScheduleInputChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null || ConfirmButton is null) return;
        if (RangeStartDatePicker.SelectedDate is not { } startValue
            || RangeEndDatePicker.SelectedDate is not { } endValue)
        {
            PreviewText.Text = "请选择排布日期范围。";
            ConfirmButton.Content = _editingBatch ? "保存整个批次" : "批量添加 Key";
            return;
        }
        var start = DateOnly.FromDateTime(startValue);
        var end = DateOnly.FromDateTime(endValue);
        var days = GetSelectedDays();
        if (end < start || days.Count == 0)
        {
            PreviewText.Text = days.Count == 0 ? "请至少选择一个星期日期。" : "结束日期不能早于开始日期。";
            ConfirmButton.Content = _editingBatch ? "保存整个批次" : "批量添加 Key";
            return;
        }
        var count = 0;
        for (var number = start.DayNumber; number <= end.DayNumber; number++)
            if (days.Contains(DateOnly.FromDayNumber(number).DayOfWeek)) count++;
        PreviewText.Text = $"将在 {start:yyyy/M/d}—{end:yyyy/M/d} 之间生成 {count} 个独立 Key。";
        ConfirmButton.Content = _editingBatch
            ? (count == 0 ? "保存整个批次" : $"保存 {count} 个 Key")
            : (count == 0 ? "批量添加 Key" : $"添加 {count} 个 Key");
    }

    private bool TryReadTimePart(ComboBox comboBox, string fieldName, int maximum, out int value)
    {
        var text = comboBox.Text.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0 && value <= maximum)
            return true;
        ShowValidation($"{fieldName}请输入 00—{maximum:00} 之间的整数。");
        comboBox.Focus();
        return false;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private static IReadOnlyList<string> BuildNumbers(int count)
    {
        var result = new string[count];
        for (var index = 0; index < count; index++) result[index] = index.ToString("00");
        return result;
    }

    private static void SetTime(ComboBox hourBox, ComboBox minuteBox, int totalMinutes)
    {
        hourBox.Text = (totalMinutes / 60).ToString("00", CultureInfo.InvariantCulture);
        minuteBox.Text = (totalMinutes % 60).ToString("00", CultureInfo.InvariantCulture);
    }
}
