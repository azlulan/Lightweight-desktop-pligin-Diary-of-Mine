using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DiaryOfMine.Application.Calendar;
using DiaryOfMine.Application.Settings;
using DiaryOfMine.Desktop.ViewModels;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Views;

public partial class HomePageView : UserControl
{
    private static readonly Brush GridLineBrush = BrushFrom("#DCE3DE");
    private static readonly Brush CellBrush = BrushFrom("#FFFFFF");
    private static readonly Brush OutsideCellBrush = BrushFrom("#F3F5F2");
    private static readonly Brush TextBrush = BrushFrom("#26312D");
    private static readonly Brush MutedBrush = BrushFrom("#7B8780");
    private static readonly Brush FaintBrush = BrushFrom("#ADB7B1");
    private static readonly Brush AccentBrush = BrushFrom("#3D6B57");
    private static readonly Brush AccentSoftBrush = BrushFrom("#E0ECE6");
    private static readonly Brush SelectedCellBrush = BrushFrom("#F5F6F5");
    private static readonly Brush SelectedOutlineBrush = BrushFrom("#C7CDCA");
    private static readonly Brush RemainingTextBrush = BrushFrom("#8C9590");
    private static readonly Brush CompletedFillBrush = BrushFrom("#ECEFED");
    private static readonly Brush CompletedHoverBrush = BrushFrom("#E2E6E3");
    private static readonly Brush CompletedInkBrush = BrushFrom("#848D88");
    private static readonly Brush CompletedKeyBrush = BrushFrom("#A6AEA9");
    private readonly Dictionary<DateOnly, Border> _dayCells = new();
    private readonly Dictionary<DateOnly, Border> _dayBackgrounds = new();
    private readonly Dictionary<Guid, List<Border>> _classBars = new();
    private readonly Dictionary<Guid, List<Border>> _keyMarkers = new();
    private readonly Dictionary<DateOnly, int> _keyOffsetsByDate = new();
    private readonly Dictionary<DateOnly, int> _taskKeyOffsetsByDate = new();
    private readonly HashSet<Guid> _completionChanges = new();
    private readonly DispatcherTimer _ideaCloseTimer;
    private readonly DispatcherTimer _dragPageTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly List<(Run Text, CalendarItem Item)> _countdownRuns = new();
    private HomePageViewModel? _viewModel;
    private DragState? _dragState;
    private ItemTossState? _tossState;
    private DateTimeOffset? _dragPreviewBoundary;
    private int _dragPageDirection;
    private int _classLaneOffset;
    private DateOnly? _selectedDate;
    private bool _initialized;
    private bool _pageChanging;
    private bool _wheelChanging;
    private Border? _selectionOutline;
    private Border? _selectionFill;

    public HomePageView()
    {
        InitializeComponent();
        _ideaCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        _ideaCloseTimer.Tick += OnIdeaCloseTimerTick;
        _dragPageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _dragPageTimer.Tick += OnDragPageTimerTick;
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _countdownTimer.Tick += (_, _) => UpdateCountdownText();
        CalendarGrid.SizeChanged += (_, _) => RenderCalendar();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not HomePageViewModel viewModel) return;
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel is not null) _viewModel.CalendarChanged -= OnCalendarChanged;
            _viewModel = viewModel;
            _viewModel.CalendarChanged += OnCalendarChanged;
        }

        if (_initialized)
        {
            _countdownTimer.Start();
            RenderCalendar();
            return;
        }

        _initialized = true;
        _countdownTimer.Start();
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception exception) when (IsExpectedDataException(exception))
        {
            ShowError("无法读取日程数据。原数据文件已保留，不会被覆盖。", exception);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        IdeaPopup.IsOpen = false;
        _ideaCloseTimer.Stop();
        _countdownTimer.Stop();
        if (_dragState is not null) EndDragWithoutSaving();
        if (_tossState is not null) CancelItemToss(animate: false);
    }

    private void OnCalendarChanged(object? sender, EventArgs e) => RenderCalendar();

    private void RenderCalendar()
    {
        if (_viewModel is null || !IsLoaded || CalendarGrid.ActualWidth <= 0) return;
        if (_tossState is { IsDragging: true }) return;

        IdeaPopup.IsOpen = false;
        CalendarGrid.Children.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        CalendarGrid.RowDefinitions.Clear();
        _dayCells.Clear();
        _dayBackgrounds.Clear();
        _classBars.Clear();
        _keyMarkers.Clear();
        _countdownRuns.Clear();
        _selectionOutline = null;
        _selectionFill = null;

        for (var column = 0; column < 7; column++)
            CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition());
        CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        for (var row = 0; row < 6; row++)
            CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var weekdays = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        for (var column = 0; column < weekdays.Length; column++)
        {
            var heading = new Border
            {
                Background = BrushFrom("#F6F8F5"),
                BorderBrush = GridLineBrush,
                BorderThickness = new Thickness(column == 0 ? 1 : 0, 1, 1, 1),
                Child = new TextBlock
                {
                    Text = weekdays[column],
                    Foreground = MutedBrush,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(heading, column);
            CalendarGrid.Children.Add(heading);
        }

        var items = GetDisplayItems().Select(ApplyDragPreview).ToArray();
        var classes = items.Where(item => item.Kind == CalendarItemKind.Class).ToArray();
        var laneById = AssignClassLanes(classes);
        var totalLanes = laneById.Count == 0 ? 0 : laneById.Values.Max() + 1;
        var visibleLanes = CalculateVisibleLaneCount();
        _classLaneOffset = Math.Clamp(_classLaneOffset, 0, Math.Max(0, totalLanes - visibleLanes));
        var hasKeyOverflow = items
            .Where(item => item.Kind == CalendarItemKind.Key)
            .GroupBy(item => (
                DateOnly.FromDateTime(item.StartsAt.LocalDateTime),
                IsTask: item.TaskBatch is not null))
            .Any(group => group.Count() > 5);
        LaneScrollHint.Visibility = totalLanes > visibleLanes || hasKeyOverflow
            ? Visibility.Visible
            : Visibility.Collapsed;

        for (var index = 0; index < 42; index++)
        {
            var date = _viewModel.VisibleStart.AddDays(index);
            var row = index / 7 + 1;
            var column = index % 7;
            var dateItems = items.Where(item => HomePageViewModel.CoversDate(item, date)).ToArray();
            var background = CreateDayBackground(date);
            Grid.SetRow(background, row);
            Grid.SetColumn(background, column);
            Panel.SetZIndex(background, 0);
            CalendarGrid.Children.Add(background);
            _dayBackgrounds[date] = background;
            var cell = CreateDayCell(date, dateItems, laneById, visibleLanes);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            Panel.SetZIndex(cell, 2);
            CalendarGrid.Children.Add(cell);
            _dayCells[date] = cell;
        }

        foreach (var item in classes)
        {
            var lane = laneById[item.Id];
            if (lane < _classLaneOffset || lane >= _classLaneOffset + visibleLanes) continue;
            AddClassSegments(item, lane - _classLaneOffset);
        }
        RefreshSelectionOutline();
    }

    private Border CreateDayBackground(DateOnly date)
    {
        var inCurrentMonth = date.Month == _viewModel!.DisplayMonth.Month
            && date.Year == _viewModel.DisplayMonth.Year;
        var background = new Border
        {
            Tag = date,
            Background = inCurrentMonth ? CellBrush : OutsideCellBrush,
            BorderBrush = GridLineBrush,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Cursor = Cursors.Hand
        };
        AttachDayCellEvents(background);
        return background;
    }

    private Border CreateDayCell(
        DateOnly date,
        IReadOnlyList<CalendarItem> dateItems,
        IReadOnlyDictionary<Guid, int> laneById,
        int visibleLanes)
    {
        var inCurrentMonth = date.Month == _viewModel!.DisplayMonth.Month
            && date.Year == _viewModel.DisplayMonth.Year;
        var content = new Grid { Margin = new Thickness(7, 5, 7, 5) };
        var dateText = new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.CurrentCulture),
            Foreground = inCurrentMonth ? TextBrush : FaintBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (_viewModel.DiaryDates.Contains(date))
        {
            content.Children.Add(new Border
            {
                Width = 23,
                Height = 23,
                Margin = new Thickness(-3, -2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(12),
                BorderBrush = BrushFrom("#8FB9A3"),
                BorderThickness = new Thickness(1.4),
                Background = BrushFrom("#F4FAF6"),
                ToolTip = "这一天写过日记",
                Child = dateText
            });
        }
        else
        {
            dateText.HorizontalAlignment = HorizontalAlignment.Left;
            dateText.VerticalAlignment = VerticalAlignment.Top;
            content.Children.Add(dateText);
        }

        var orderedKeys = dateItems
            .Where(item => item.Kind == CalendarItemKind.Key)
            .OrderBy(item => item.IsCompleted)
            .ThenBy(item => Math.Abs((item.StartsAt - DateTimeOffset.Now).Ticks))
            .ThenBy(item => item.StartsAt)
            .ToArray();
        var taskKeyItems = orderedKeys.Where(item => item.TaskBatch is not null).ToArray();
        var keyItems = orderedKeys.Where(item => item.TaskBatch is null).ToArray();
        var keyOffset = Math.Clamp(
            _keyOffsetsByDate.GetValueOrDefault(date), 0, Math.Max(0, keyItems.Length - 5));
        _keyOffsetsByDate[date] = keyOffset;
        var taskKeyOffset = Math.Clamp(
            _taskKeyOffsetsByDate.GetValueOrDefault(date), 0, Math.Max(0, taskKeyItems.Length - 5));
        _taskKeyOffsetsByDate[date] = taskKeyOffset;
        if (keyItems.Length > 0 || taskKeyItems.Length > 0)
        {
            var keyColumns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0)
            };
            if (taskKeyItems.Length > 0)
            {
                var taskMarkers = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 0, keyItems.Length > 0 ? 6 : 0, 0),
                    ToolTip = "Task 批量 Key（双击编辑整个批次）"
                };
                foreach (var item in taskKeyItems.Skip(taskKeyOffset).Take(5))
                    taskMarkers.Children.Add(CreateKeyMarker(item, _selectedDate == date));
                keyColumns.Children.Add(taskMarkers);
            }
            var markers = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var item in keyItems.Skip(keyOffset).Take(5))
                markers.Children.Add(CreateKeyMarker(item, _selectedDate == date));
            keyColumns.Children.Add(markers);
            content.Children.Add(keyColumns);
        }

        var ideaItems = dateItems.Where(item => item.Kind == CalendarItemKind.Idea).ToArray();
        if (ideaItems.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Idea  {ideaItems.Length}",
                Foreground = MutedBrush,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        var hiddenClassesAbove = dateItems.Count(item =>
            item.Kind == CalendarItemKind.Class
            && laneById.TryGetValue(item.Id, out var lane)
            && lane < _classLaneOffset);
        var hiddenClassesBelow = dateItems.Count(item =>
            item.Kind == CalendarItemKind.Class
            && laneById.TryGetValue(item.Id, out var lane)
            && lane >= _classLaneOffset + visibleLanes);
        var hiddenAbove = hiddenClassesAbove + keyOffset + taskKeyOffset;
        var hiddenBelow = hiddenClassesBelow
            + Math.Max(0, keyItems.Length - keyOffset - 5)
            + Math.Max(0, taskKeyItems.Length - taskKeyOffset - 5);
        if (hiddenAbove > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"上方 {hiddenAbove} 项",
                Foreground = MutedBrush,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 16, 0, 0)
            });
        }
        if (hiddenBelow > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"还有 {hiddenBelow} 项",
                Foreground = MutedBrush,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        var cell = new Border
        {
            Tag = date,
            Background = null,
            BorderThickness = new Thickness(0),
            Child = content,
            Cursor = Cursors.Hand
        };
        AttachDayCellEvents(cell);
        return cell;
    }

    private void AttachDayCellEvents(Border cell)
    {
        cell.MouseEnter += OnDayCellMouseEnter;
        cell.MouseLeave += OnDayCellMouseLeave;
        cell.MouseLeftButtonDown += OnDayCellMouseLeftButtonDown;
    }

    private void AddClassSegments(CalendarItem originalItem, int displayLane)
    {
        var item = ApplyDragPreview(originalItem);
        var actualStart = DateOnly.FromDateTime(item.StartsAt.LocalDateTime);
        var actualEnd = item.EndsAt is null
            ? _viewModel!.VisibleEndExclusive.AddDays(-1)
            : InclusiveEndDate(item.EndsAt.Value);
        var clippedStart = actualStart < _viewModel!.VisibleStart ? _viewModel.VisibleStart : actualStart;
        var clippedEnd = actualEnd >= _viewModel.VisibleEndExclusive
            ? _viewModel.VisibleEndExclusive.AddDays(-1)
            : actualEnd;
        if (clippedEnd < clippedStart) return;
        var focusedDate = _selectedDate is { } selected
                          && HomePageViewModel.CoversDate(item, selected)
            ? selected
            : (DateOnly?)null;

        var firstWeek = (clippedStart.DayNumber - _viewModel.VisibleStart.DayNumber) / 7;
        var lastWeek = (clippedEnd.DayNumber - _viewModel.VisibleStart.DayNumber) / 7;
        for (var week = firstWeek; week <= lastWeek; week++)
        {
            var weekStart = _viewModel.VisibleStart.AddDays(week * 7);
            var weekEnd = weekStart.AddDays(6);
            var segmentStart = clippedStart > weekStart ? clippedStart : weekStart;
            var segmentEnd = clippedEnd < weekEnd ? clippedEnd : weekEnd;
            var startsHere = segmentStart == actualStart;
            var endsHere = item.EndsAt is not null && segmentEnd == actualEnd;
            var dayWidth = Math.Max(1, CalendarGrid.ActualWidth / 7);
            var leftInset = startsHere ? dayWidth * FractionOfDay(item.StartsAt) : 0;
            var focusInsideSegment = focusedDate is { } focus
                                     && focus >= segmentStart && focus <= segmentEnd;
            var showInformation = focusInsideSegment || (focusedDate is null && startsHere);
            var informationOffset = focusInsideSegment && focusedDate is { } informationDate
                ? Math.Max(0, (informationDate.DayNumber - segmentStart.DayNumber) * dayWidth - leftInset)
                : 0;
            var bar = CreateClassBar(
                item, startsHere, endsHere, showInformation, informationOffset);
            Grid.SetRow(bar, week + 1);
            Grid.SetColumn(bar, segmentStart.DayNumber - weekStart.DayNumber);
            Grid.SetColumnSpan(bar, segmentEnd.DayNumber - segmentStart.DayNumber + 1);
            Panel.SetZIndex(bar, 1);
            var rightInset = endsHere && item.EndsAt is { } end
                ? dayWidth * EndInsetFraction(end)
                : 0;
            bar.Margin = new Thickness(
                4 + leftInset,
                30 + displayLane * 19,
                4 + rightInset,
                0);
            CalendarGrid.Children.Add(bar);
            if (!_classBars.TryGetValue(item.Id, out var bars))
            {
                bars = new List<Border>();
                _classBars[item.Id] = bars;
            }
            bars.Add(bar);
        }
    }

    private Border CreateClassBar(
        CalendarItem item, bool startsHere, bool endsHere,
        bool showInformation, double informationOffset)
    {
        var colors = GetClassColors(item);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        if (showInformation)
        {
            var label = new TextBlock
            {
                Foreground = colors.Ink,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7 + informationOffset, 0, endsHere ? 7 : 4, 0)
            };
            var titleText = new Run(item.Title)
            {
                TextDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null
            };
            label.Inlines.Add(titleText);
            if (!item.IsCompleted)
            {
                var remainingText = new Run($"  {FormatRemaining(item)}")
                {
                    Foreground = RemainingTextBrush,
                    FontSize = 9,
                    FontWeight = FontWeights.Normal
                };
                label.Inlines.Add(remainingText);
                _countdownRuns.Add((remainingText, item));
            }
            content.Children.Add(label);
        }

        if (startsHere) content.Children.Add(CreateBoundaryHandle(HorizontalAlignment.Left, colors.Ink));
        if (endsHere) content.Children.Add(CreateBoundaryHandle(HorizontalAlignment.Right, colors.Ink));

        var bar = new Border
        {
            Tag = new ClassBarTag(item, startsHere, endsHere),
            Height = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Background = colors.Fill,
            CornerRadius = new CornerRadius(startsHere ? 5 : 2, endsHere ? 5 : 2,
                endsHere ? 5 : 2, startsHere ? 5 : 2),
            Child = content,
            Cursor = Cursors.Hand,
            ToolTip = BuildClassTooltip(item)
        };
        bar.MouseEnter += (_, _) => SetClassHighlight(item, true);
        bar.MouseLeave += (_, _) => SetClassHighlight(item, false);
        bar.PreviewMouseLeftButtonDown += OnClassBarMouseLeftButtonDown;
        return bar;
    }

    private Border CreateKeyMarker(CalendarItem item, bool showInformation)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = GetKeyBrush(item),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (showInformation)
        {
            row.Children.Add(new TextBlock
            {
                Text = item.Summary,
                Foreground = item.IsCompleted ? CompletedInkBrush : MutedBrush,
                FontSize = 9,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null
            });
        }

        var marker = new Border
        {
            Tag = item,
            Height = 14,
            Padding = new Thickness(2, 0, 3, 0),
            Margin = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Cursor = Cursors.Hand,
            ToolTip = BuildKeyTooltip(item),
            Child = row
        };
        marker.PreviewMouseLeftButtonDown += OnKeyMarkerMouseLeftButtonDown;
        if (!_keyMarkers.TryGetValue(item.Id, out var markers))
        {
            markers = new List<Border>();
            _keyMarkers[item.Id] = markers;
        }
        markers.Add(marker);
        return marker;
    }

    private void SetClassHighlight(CalendarItem item, bool highlighted)
    {
        if (!_classBars.TryGetValue(item.Id, out var bars)) return;
        var background = highlighted
            ? GetClassHoverBrush(item)
            : GetClassColors(item).Fill;
        foreach (var bar in bars)
            bar.Background = background;
    }

    private static Border CreateBoundaryHandle(HorizontalAlignment alignment, Brush brush) => new()
    {
        Width = 7,
        Margin = new Thickness(1),
        HorizontalAlignment = alignment,
        Background = brush,
        Opacity = 0.28,
        CornerRadius = new CornerRadius(3),
        ToolTip = alignment == HorizontalAlignment.Left ? "拖动修改开始日期" : "拖动修改结束日期"
    };

    private IReadOnlyList<CalendarItem> GetDisplayItems()
    {
        if (_viewModel is null) return Array.Empty<CalendarItem>();
        if (_dragState is null || _viewModel.Items.Any(item => item.Id == _dragState.Item.Id))
            return _viewModel.Items;
        return _viewModel.Items.Append(_dragState.Item).ToArray();
    }

    private CalendarItem ApplyDragPreview(CalendarItem item)
    {
        if (_dragState is null || _dragPreviewBoundary is null || item.Id != _dragState.Item.Id)
            return item;
        return _dragState.UpdateStart
            ? item with { StartsAt = _dragPreviewBoundary.Value }
            : item with { EndsAt = _dragPreviewBoundary.Value };
    }

    private static IReadOnlyDictionary<Guid, int> AssignClassLanes(IEnumerable<CalendarItem> source)
    {
        var result = new Dictionary<Guid, int>();
        var laneEnds = new List<DateTimeOffset>();
        foreach (var item in source
            // 未完成项优先；其后按截止时间排列，已完成 Class 始终置底。
            .OrderBy(item => item.IsCompleted)
            .ThenBy(item => item.EndsAt ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.StartsAt)
            .ThenBy(item => item.CreatedAt))
        {
            var start = item.StartsAt;
            var end = item.EndsAt ?? DateTimeOffset.MaxValue;
            var lane = laneEnds.FindIndex(previousEnd => previousEnd <= start);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(end);
            }
            else
            {
                laneEnds[lane] = end;
            }
            result[item.Id] = lane;
        }
        return result;
    }

    private int CalculateVisibleLaneCount()
    {
        var rowHeight = Math.Max(60, (CalendarGrid.ActualHeight - 30) / 6);
        return Math.Clamp((int)((rowHeight - 34) / 19), 1, 5);
    }

    private async void OnPreviousMonthClick(object sender, RoutedEventArgs e) => await ChangePeriodAsync(months: -1);
    private async void OnNextMonthClick(object sender, RoutedEventArgs e) => await ChangePeriodAsync(months: 1);
    private async void OnPreviousYearClick(object sender, RoutedEventArgs e) => await ChangePeriodAsync(years: -1);
    private async void OnNextYearClick(object sender, RoutedEventArgs e) => await ChangePeriodAsync(years: 1);

    private async void OnTodayClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _classLaneOffset = 0;
        try
        {
            await _viewModel.GoToTodayAsync();
            SetSelectedDate(DateOnly.FromDateTime(DateTime.Today));
        }
        catch (Exception exception) when (IsExpectedDataException(exception)) { ShowError("无法切换到今天。", exception); }
    }

    private async Task ChangePeriodAsync(int months = 0, int years = 0)
    {
        if (_viewModel is null) return;
        _classLaneOffset = 0;
        SetSelectedDate(null);
        try
        {
            if (months != 0) await _viewModel.ChangeMonthAsync(months);
            else if (years != 0) await _viewModel.ChangeYearAsync(years);
        }
        catch (Exception exception) when (IsExpectedDataException(exception))
        {
            ShowError("无法读取目标月份。", exception);
        }
    }

    private void OnAddClassClick(object sender, RoutedEventArgs e) => RequestCreate(CalendarItemKind.Class);
    private void OnAddKeyClick(object sender, RoutedEventArgs e) => RequestCreate(CalendarItemKind.Key);
    private void OnAddIdeaClick(object sender, RoutedEventArgs e) => RequestCreate(CalendarItemKind.Idea);
    private void OnAddTaskClick(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.RequestKeyTaskCreation(_selectedDate);

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        try { await _viewModel.UndoAsync(); }
        catch (Exception exception) when (IsExpectedDataException(exception))
        {
            ShowError("无法撤回上一步操作。", exception);
        }
    }

    private async void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        try { await _viewModel.RedoAsync(); }
        catch (Exception exception) when (IsExpectedDataException(exception))
        {
            ShowError("无法回滚上一步撤回。", exception);
        }
    }

    private void RequestCreate(CalendarItemKind kind) =>
        (Window.GetWindow(this) as MainWindow)?.RequestCalendarItemCreation(kind, _selectedDate);

    private void OnDayCellMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: DateOnly date } cell || _viewModel is null) return;
        _ideaCloseTimer.Stop();
        var ideas = _viewModel.GetItemsForDate(date)
            .Where(item => item.Kind == CalendarItemKind.Idea)
            .ToArray();
        if (ideas.Length == 0)
        {
            IdeaPopup.IsOpen = false;
            return;
        }
        ShowIdeaPopup(cell, date, ideas);
    }

    private void ShowIdeaPopup(Border cell, DateOnly date, IReadOnlyList<CalendarItem> ideas)
    {
        IdeaPanel.Children.Clear();
        IdeaPopupHeading.Text = $"{date.Month}月{date.Day}日 · Idea";
        IdeaPopupCount.Text = $"{ideas.Count} 项";
        foreach (var idea in ideas)
        {
            var checkBox = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = idea.Title,
                    Width = 128,
                    TextWrapping = TextWrapping.Wrap,
                    TextDecorations = idea.IsCompleted ? TextDecorations.Strikethrough : null
                },
                IsChecked = idea.IsCompleted,
                Tag = idea,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            checkBox.Click += OnIdeaCompletedClick;
            IdeaPanel.Children.Add(new Border
            {
                Width = 158,
                MinHeight = 48,
                Margin = new Thickness(4),
                Padding = new Thickness(9),
                Background = GetIdeaBrush(),
                CornerRadius = new CornerRadius(7),
                Child = checkBox
            });
        }
        IdeaPopup.PlacementTarget = cell;
        IdeaPopup.IsOpen = true;
    }

    private async void OnIdeaCompletedClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not CheckBox { Tag: CalendarItem item } checkBox) return;
        try
        {
            var updated = await _viewModel.ToggleIdeaAsync(item, checkBox.IsChecked == true);
            checkBox.Tag = updated;
            if (checkBox.Content is TextBlock title)
            {
                title.TextDecorations = updated.IsCompleted
                    ? TextDecorations.Strikethrough
                    : null;
            }
        }
        catch (Exception exception) when (IsExpectedDataException(exception))
        {
            checkBox.IsChecked = item.IsCompleted;
            ShowError("无法保存 Idea 完成状态。", exception);
        }
    }

    private void OnDayCellMouseLeave(object sender, MouseEventArgs e) => _ideaCloseTimer.Start();
    private void OnIdeaPopupMouseEnter(object sender, MouseEventArgs e) => _ideaCloseTimer.Stop();
    private void OnIdeaPopupMouseLeave(object sender, MouseEventArgs e) => _ideaCloseTimer.Start();

    private void OnIdeaCloseTimerTick(object? sender, EventArgs e)
    {
        if (IdeaPopupRoot.IsMouseOver
            || IdeaPopup.PlacementTarget is UIElement { IsMouseOver: true })
            return;
        _ideaCloseTimer.Stop();
        IdeaPopup.IsOpen = false;
    }

    private void OnDayCellMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragState is not null || sender is not Border { Tag: DateOnly date }) return;
        SetSelectedDate(date);
        if (e.ClickCount < 2) return;
        e.Handled = true;
        (Window.GetWindow(this) as MainWindow)?.RequestCalendarDayDetails(date);
    }

    private void SetSelectedDate(DateOnly? date)
    {
        if (_selectedDate == date) return;
        _selectedDate = date;
        RenderCalendar();
    }

    private Brush GetDayBaseBrush(DateOnly date) =>
        _viewModel is not null
        && date.Month == _viewModel.DisplayMonth.Month
        && date.Year == _viewModel.DisplayMonth.Year
            ? CellBrush
            : OutsideCellBrush;

    private void RefreshSelectionOutline()
    {
        if (_selectionOutline is not null)
            CalendarGrid.Children.Remove(_selectionOutline);
        if (_selectionFill is not null)
            CalendarGrid.Children.Remove(_selectionFill);
        _selectionOutline = null;
        _selectionFill = null;
        if (_viewModel is null || _selectedDate is not { } selected) return;

        var index = selected.DayNumber - _viewModel.VisibleStart.DayNumber;
        if (index is < 0 or >= 42) return;
        _selectionFill = new Border
        {
            Background = SelectedCellBrush,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(3),
            IsHitTestVisible = false
        };
        Grid.SetRow(_selectionFill, index / 7 + 1);
        Grid.SetColumn(_selectionFill, index % 7);
        Panel.SetZIndex(_selectionFill, 0);
        CalendarGrid.Children.Add(_selectionFill);

        _selectionOutline = new Border
        {
            BorderBrush = SelectedOutlineBrush,
            BorderThickness = new Thickness(2),
            Background = null,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(3),
            IsHitTestVisible = false
        };
        Grid.SetRow(_selectionOutline, index / 7 + 1);
        Grid.SetColumn(_selectionOutline, index % 7);
        Panel.SetZIndex(_selectionOutline, 8);
        CalendarGrid.Children.Add(_selectionOutline);
    }

    private void UpdateCountdownText()
    {
        if (!IsVisible) return;
        var now = DateTimeOffset.Now;
        foreach (var (text, item) in _countdownRuns)
            text.Text = $"  {FormatRemaining(item, now)}";
    }

    private static string FormatRemaining(CalendarItem item) =>
        FormatRemaining(item, DateTimeOffset.Now);

    private static string FormatRemaining(CalendarItem item, DateTimeOffset now)
    {
        if (item.EndsAt is null) return "长期";

        var end = item.EndsAt.Value;
        if (now >= end) return "已结束";

        // 尚未开始时显示完整历时；开始后显示距截止时刻的实时剩余时间。
        var isActive = now >= item.StartsAt;
        var remaining = end - (isActive ? now : item.StartsAt);
        var totalMinutes = Math.Max(1L, (long)Math.Ceiling(remaining.TotalMinutes));
        var days = totalMinutes / 1440;
        var hours = totalMinutes % 1440 / 60;
        var minutes = totalMinutes % 60;
        var prefix = isActive ? "剩" : "历时";
        return minutes == 0
            ? $"{prefix} {days} 天 {hours} 小时"
            : $"{prefix} {days} 天 {hours} 小时 {minutes} 分钟";
    }

    private async void OnCalendarMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel is null || _dragState is not null) return;
        e.Handled = true;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            var direction = e.Delta < 0 ? 1 : -1;
            var changed = false;
            var laneCount = AssignClassLanes(_viewModel.Items.Where(item => item.Kind == CalendarItemKind.Class));
            var total = laneCount.Count == 0 ? 0 : laneCount.Values.Max() + 1;
            var visible = CalculateVisibleLaneCount();
            if (total > visible)
            {
                var nextClassOffset = Math.Clamp(
                    _classLaneOffset + direction, 0, total - visible);
                changed = nextClassOffset != _classLaneOffset;
                _classLaneOffset = nextClassOffset;
            }

            var date = DateFromPoint(e.GetPosition(CalendarGrid));
            var keyCount = _viewModel.GetItemsForDate(date)
                .Count(item => item.Kind == CalendarItemKind.Key && item.TaskBatch is null);
            var keyOffset = _keyOffsetsByDate.GetValueOrDefault(date);
            var nextKeyOffset = Math.Clamp(keyOffset + direction, 0, Math.Max(0, keyCount - 5));
            if (nextKeyOffset != keyOffset)
            {
                _keyOffsetsByDate[date] = nextKeyOffset;
                changed = true;
            }
            var taskKeyCount = _viewModel.GetItemsForDate(date)
                .Count(item => item.Kind == CalendarItemKind.Key && item.TaskBatch is not null);
            var taskKeyOffset = _taskKeyOffsetsByDate.GetValueOrDefault(date);
            var nextTaskKeyOffset = Math.Clamp(
                taskKeyOffset + direction, 0, Math.Max(0, taskKeyCount - 5));
            if (nextTaskKeyOffset != taskKeyOffset)
            {
                _taskKeyOffsetsByDate[date] = nextTaskKeyOffset;
                changed = true;
            }
            if (changed) RenderCalendar();
            return;
        }

        if (_wheelChanging) return;
        _wheelChanging = true;
        _classLaneOffset = 0;
        SetSelectedDate(null);
        try { await _viewModel.ChangeMonthAsync(e.Delta > 0 ? -1 : 1); }
        catch (Exception exception) when (IsExpectedDataException(exception))
        {
            ShowError("滚动切换月份时无法读取日程。", exception);
        }
        finally
        {
            _wheelChanging = false;
        }
    }

    private async void OnClassBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ClassBarTag tag } bar || e.LeftButton != MouseButtonState.Pressed) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            e.Handled = true;
            if (e.ClickCount == 1) await ToggleCompletedFromShortcutAsync(tag.Item);
            return;
        }
        var point = e.GetPosition(bar);
        bool? updateStart = null;
        if (tag.CanDragStart && point.X <= 13) updateStart = true;
        else if (tag.CanDragEnd && point.X >= bar.ActualWidth - 13) updateStart = false;
        if (updateStart is null)
        {
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                (Window.GetWindow(this) as MainWindow)?.RequestCalendarItemEdit(tag.Item);
                return;
            }
            BeginItemToss(tag.Item, bar, e.GetPosition(CalendarGrid));
            return;
        }

        ConfigureBoundaryDragStatus();
        _dragState = new DragState(tag.Item, updateStart.Value);
        _dragPreviewBoundary = updateStart.Value
            ? tag.Item.StartsAt
            : tag.Item.EndsAt ?? EndOfLocalDay(tag.Item.StartsAt);
        CalendarGrid.CaptureMouse();
        _dragPageTimer.Start();
        DragStatus.Visibility = Visibility.Visible;
        UpdateDragStatus();
        e.Handled = true;
    }

    private async void OnKeyMarkerMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CalendarItem item } marker
            || item.Kind != CalendarItemKind.Key
            || e.LeftButton != MouseButtonState.Pressed)
            return;

        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            if (e.ClickCount == 1) await ToggleCompletedFromShortcutAsync(item);
            return;
        }
        if (e.ClickCount == 2)
        {
            var mainWindow = Window.GetWindow(this) as MainWindow;
            if (item.TaskBatch is not null) mainWindow?.RequestKeyTaskEdit(item);
            else mainWindow?.RequestCalendarItemEdit(item);
            return;
        }
        BeginItemToss(item, marker, e.GetPosition(CalendarGrid));
    }

    private async Task ToggleCompletedFromShortcutAsync(CalendarItem item)
    {
        if (_viewModel is null || !_completionChanges.Add(item.Id)) return;
        try
        {
            await _viewModel.ToggleCompletedAsync(item, !item.IsCompleted);
        }
        catch (Exception exception)
        {
            ShowError("无法更新完成状态。", exception);
        }
        finally
        {
            _completionChanges.Remove(item.Id);
        }
    }

    private void BeginItemToss(CalendarItem item, Border visual, Point startPoint)
    {
        var rotation = new RotateTransform();
        var translation = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(rotation);
        transform.Children.Add(translation);
        visual.RenderTransformOrigin = new Point(0.5, 0.5);
        visual.RenderTransform = transform;
        _tossState = new ItemTossState(item, visual, startPoint, translation, rotation);
        CalendarGrid.CaptureMouse();
    }

    private void OnCalendarMouseMove(object sender, MouseEventArgs e)
    {
        if (_tossState is { } tossState && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateItemToss(tossState, e);
            e.Handled = true;
            return;
        }

        if (_dragState is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(CalendarGrid);
        _dragPageDirection = point.X < 0 ? -1 : point.X > CalendarGrid.ActualWidth ? 1 : 0;
        var boundary = BoundaryFromPoint(point);
        if (!IsValidDragBoundary(_dragState, boundary) || _dragPreviewBoundary == boundary) return;
        _dragPreviewBoundary = boundary;
        UpdateDragStatus();
        RenderCalendar();
    }

    private async void OnCalendarMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_tossState is { } tossState)
        {
            _tossState = null;
            CalendarGrid.ReleaseMouseCapture();
            DragStatus.Visibility = Visibility.Collapsed;
            e.Handled = true;

            if (!tossState.IsDragging || !tossState.DeleteArmed)
            {
                ReturnItemToss(tossState);
                return;
            }

            try
            {
                await AnimateItemTossOutAsync(tossState);
                if (_viewModel is not null)
                    await _viewModel.DeleteAsync(tossState.Item);
            }
            catch (Exception exception) when (IsExpectedDataException(exception))
            {
                RestoreItemGroup(tossState.Item);
                ShowError($"无法删除 {tossState.Item.Kind}。", exception);
                RenderCalendar();
            }
            return;
        }

        if (_dragState is null || _dragPreviewBoundary is null || _viewModel is null) return;
        var state = _dragState;
        var boundary = _dragPreviewBoundary.Value;
        EndDragWithoutSaving(render: false);
        try { await _viewModel.UpdateBoundaryAsync(state.Item, state.UpdateStart, boundary); }
        catch (Exception exception) when (exception is ArgumentException || IsExpectedDataException(exception))
        {
            ShowError("无法修改 Class 日期。", exception);
            RenderCalendar();
        }
        e.Handled = true;
    }

    private void OnCalendarMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragState is null && _tossState is null) _ideaCloseTimer.Start();
    }

    private void UpdateItemToss(ItemTossState state, MouseEventArgs e)
    {
        var point = e.GetPosition(CalendarGrid);
        var deltaX = point.X - state.StartPoint.X;
        var deltaY = point.Y - state.StartPoint.Y;
        if (!state.IsDragging
            && Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
            return;

        state.IsDragging = true;
        state.Translation.X = deltaX;
        state.Translation.Y = deltaY;
        state.Rotation.Angle = Math.Clamp(deltaX / 28, -9, 9);
        Panel.SetZIndex(state.Bar, 20);

        var transformedBounds = state.Bar
            .TransformToVisual(CalendarGrid)
            .TransformBounds(new Rect(state.Bar.RenderSize));
        var widthBeyondCalendarLeft = Math.Max(0, -transformedBounds.Left);
        var deleteArmed = widthBeyondCalendarLeft >= transformedBounds.Width / 2;
        if (state.DeleteArmed != deleteArmed)
        {
            state.DeleteArmed = deleteArmed;
            state.Bar.Opacity = deleteArmed ? 0.48 : 0.88;
            SetItemGroupOpacity(state.Item, state.Bar, deleteArmed ? 0.28 : 0.62);
        }

        var kindLabel = state.Item.Kind.ToString();
        var visualLabel = state.Item.Kind == CalendarItemKind.Class ? "色块" : "Key 标记";
        DragStatusLabel.Text = deleteArmed ? $"松开以丢弃 {kindLabel}：" : "拖向日历左侧：";
        DragStatusText.Text = state.Item.Title;
        DragStatusHint.Text = deleteArmed
            ? " · 超过一半已进入左侧，松开后删除（可撤回）"
            : $" · 将{visualLabel}一半以上拖过日历左边界";
        DragStatus.Background = deleteArmed ? BrushFrom("#FFF3EF") : CellBrush;
        DragStatus.BorderBrush = deleteArmed ? BrushFrom("#E7B8AA") : BrushFrom("#CAD4CE");
        DragStatus.Visibility = Visibility.Visible;
    }

    private async Task AnimateItemTossOutAsync(ItemTossState state)
    {
        const int animationMilliseconds = 260;
        var duration = new Duration(TimeSpan.FromMilliseconds(animationMilliseconds));
        var easing = new QuarticEase { EasingMode = EasingMode.EaseIn };
        var completed = new TaskCompletionSource<bool>();
        var targetX = state.Translation.X - Math.Max(360, CalendarGrid.ActualWidth * 0.72);
        var xAnimation = new DoubleAnimation(state.Translation.X, targetX, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        xAnimation.Completed += (_, _) => completed.TrySetResult(true);
        state.Translation.BeginAnimation(TranslateTransform.XProperty, xAnimation);
        state.Translation.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(state.Translation.Y, state.Translation.Y - 24, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
        state.Rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(state.Rotation.Angle, -14, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });

        foreach (var bar in GetItemVisuals(state.Item))
        {
            bar.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(bar.Opacity, 0, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
        }

        await completed.Task;
    }

    private void ReturnItemToss(ItemTossState state)
    {
        if (!state.IsDragging)
        {
            ResetItemTossVisual(state);
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(190));
        var easing = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut };
        var xAnimation = new DoubleAnimation(state.Translation.X, 0, duration)
        {
            EasingFunction = easing
        };
        xAnimation.Completed += (_, _) => ResetItemTossVisual(state);
        state.Translation.BeginAnimation(TranslateTransform.XProperty, xAnimation);
        state.Translation.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(state.Translation.Y, 0, duration) { EasingFunction = easing });
        state.Rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(state.Rotation.Angle, 0, duration) { EasingFunction = easing });
        state.Bar.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(state.Bar.Opacity, 1, duration) { EasingFunction = easing });
        RestoreItemGroup(state.Item, state.Bar);
    }

    private void CancelItemToss(bool animate)
    {
        if (_tossState is not { } state) return;
        _tossState = null;
        CalendarGrid.ReleaseMouseCapture();
        DragStatus.Visibility = Visibility.Collapsed;
        if (animate) ReturnItemToss(state);
        else ResetItemTossVisual(state);
    }

    private void ResetItemTossVisual(ItemTossState state)
    {
        state.Translation.BeginAnimation(TranslateTransform.XProperty, null);
        state.Translation.BeginAnimation(TranslateTransform.YProperty, null);
        state.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        state.Bar.BeginAnimation(OpacityProperty, null);
        state.Bar.RenderTransform = null;
        state.Bar.Opacity = 1;
        Panel.SetZIndex(state.Bar, state.Item.Kind == CalendarItemKind.Class ? 1 : 0);
        RestoreItemGroup(state.Item);
    }

    private void SetItemGroupOpacity(CalendarItem item, Border activeBar, double opacity)
    {
        foreach (var bar in GetItemVisuals(item))
        {
            if (!ReferenceEquals(bar, activeBar)) bar.Opacity = opacity;
        }
    }

    private void RestoreItemGroup(CalendarItem item, Border? activeBar = null)
    {
        foreach (var bar in GetItemVisuals(item))
        {
            if (ReferenceEquals(bar, activeBar)) continue;
            bar.BeginAnimation(OpacityProperty, null);
            bar.Opacity = 1;
        }
    }

    private IEnumerable<Border> GetItemVisuals(CalendarItem item)
    {
        var lookup = item.Kind == CalendarItemKind.Class ? _classBars : _keyMarkers;
        return lookup.TryGetValue(item.Id, out var visuals)
            ? visuals
            : Array.Empty<Border>();
    }

    private async void OnDragPageTimerTick(object? sender, EventArgs e)
    {
        if (_dragState is null || _viewModel is null || _dragPageDirection == 0 || _pageChanging) return;
        _pageChanging = true;
        try
        {
            await _viewModel.ChangeMonthAsync(_dragPageDirection);
            var targetDate = _dragPageDirection < 0
                ? _viewModel.VisibleEndExclusive.AddDays(-1)
                : _viewModel.VisibleStart;
            _dragPreviewBoundary = WithLocalDate(
                _dragPreviewBoundary ?? _dragState.Item.StartsAt, targetDate);
            UpdateDragStatus();
            RenderCalendar();
        }
        catch (Exception exception) when (IsExpectedDataException(exception))
        {
            ShowError("拖动翻月时无法读取日程。", exception);
            EndDragWithoutSaving();
        }
        finally
        {
            _pageChanging = false;
        }
    }

    private DateOnly DateFromPoint(Point point)
    {
        var width = Math.Max(1, CalendarGrid.ActualWidth);
        var height = Math.Max(1, CalendarGrid.ActualHeight - 30);
        var column = Math.Clamp((int)(Math.Clamp(point.X, 0, width - 1) / width * 7), 0, 6);
        var row = Math.Clamp((int)(Math.Clamp(point.Y - 30, 0, height - 1) / height * 6), 0, 5);
        return _viewModel!.VisibleStart.AddDays(row * 7 + column);
    }

    private DateTimeOffset BoundaryFromPoint(Point point)
    {
        var date = DateFromPoint(point);
        var calendarWidth = Math.Max(1, CalendarGrid.ActualWidth);
        var dayWidth = calendarWidth / 7;
        var column = Math.Clamp(
            (date.DayNumber - _viewModel!.VisibleStart.DayNumber) % 7, 0, 6);
        var offsetInDay = Math.Clamp(point.X - column * dayWidth, 0, dayWidth - 0.001);
        var halfHour = Math.Clamp((int)Math.Floor(offsetInDay / dayWidth * 48), 0, 47);
        var time = TimeOnly.MinValue.AddMinutes(halfHour * 30);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static bool IsValidDragBoundary(DragState state, DateTimeOffset candidate)
    {
        var end = state.Item.EndsAt ?? DateTimeOffset.MaxValue;
        return state.UpdateStart ? candidate <= end : candidate >= state.Item.StartsAt;
    }

    private static double FractionOfDay(DateTimeOffset value)
    {
        var local = value.LocalDateTime;
        return (local.Hour * 60d + local.Minute) / (24 * 60d);
    }

    private void UpdateDragStatus()
    {
        if (_dragPreviewBoundary is null) return;
        DragStatusText.Text = _dragPreviewBoundary.Value.LocalDateTime
            .ToString("yyyy年M月d日 HH:mm", CultureInfo.CurrentCulture);
    }

    private void ConfigureBoundaryDragStatus()
    {
        DragStatusLabel.Text = "Class 日期与时间：";
        DragStatusHint.Text = " · 格内从左至右 00:00—23:30 · 松开鼠标保存";
        DragStatus.Background = CellBrush;
        DragStatus.BorderBrush = BrushFrom("#CAD4CE");
    }

    private void EndDragWithoutSaving(bool render = true)
    {
        _dragPageTimer.Stop();
        _dragPageDirection = 0;
        CalendarGrid.ReleaseMouseCapture();
        _dragState = null;
        _dragPreviewBoundary = null;
        DragStatus.Visibility = Visibility.Collapsed;
        if (render) RenderCalendar();
    }

    private static object BuildKeyTooltip(CalendarItem item)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{(item.TaskBatch is null ? "Key" : "Task Key")} · 色槽 {CalendarPalette.GetSlotNumber(item.ColorKey)}",
            Foreground = AccentBrush,
            FontSize = 10
        });
        panel.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"{item.StartsAt.LocalDateTime:HH:mm}—{FormatKeyEnd(item, EffectiveKeyEnd(item))}",
            Foreground = MutedBrush,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return panel;
    }

    private static DateTimeOffset EffectiveKeyEnd(CalendarItem item)
    {
        if (item.EndsAt is { } end) return end;
        var startDate = DateOnly.FromDateTime(item.StartsAt.LocalDateTime);
        var localMidnight = startDate.AddDays(1)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var nextMidnight = new DateTimeOffset(
            localMidnight, TimeZoneInfo.Local.GetUtcOffset(localMidnight));
        var proposed = item.StartsAt.AddHours(2);
        return proposed < nextMidnight ? proposed : nextMidnight;
    }

    private static string FormatKeyEnd(CalendarItem item, DateTimeOffset end)
    {
        var startDate = item.StartsAt.LocalDateTime.Date;
        return end.LocalDateTime.TimeOfDay == TimeSpan.Zero
               && end.LocalDateTime.Date == startDate.AddDays(1)
            ? "24:00"
            : end.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);
    }

    private static DateOnly InclusiveEndDate(DateTimeOffset end)
    {
        var local = end.LocalDateTime;
        var date = DateOnly.FromDateTime(local);
        return local.TimeOfDay == TimeSpan.Zero ? date.AddDays(-1) : date;
    }

    private static double EndInsetFraction(DateTimeOffset end) =>
        end.LocalDateTime.TimeOfDay == TimeSpan.Zero ? 0 : 1 - FractionOfDay(end);

    private static object BuildClassTooltip(CalendarItem item)
    {
        var start = item.StartsAt.LocalDateTime.ToString("yyyy/M/d HH:mm", CultureInfo.CurrentCulture);
        var end = item.EndsAt is null
            ? "长期"
            : item.EndsAt.Value.LocalDateTime.ToString("yyyy/M/d HH:mm", CultureInfo.CurrentCulture);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"Class · 色槽 {CalendarPalette.GetSlotNumber(item.ColorKey)}",
            Foreground = AccentBrush,
            FontSize = 10
        });
        panel.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(new TextBlock { Text = $"{start} → {end}", Foreground = MutedBrush, Margin = new Thickness(0, 3, 0, 0) });
        return panel;
    }

    private (Brush Fill, Brush Ink) GetClassColors(CalendarItem item)
    {
        if (item.IsCompleted) return (CompletedFillBrush, CompletedInkBrush);
        var color = GetClassPaletteColor(item.ColorKey);
        return (BrushFrom(color), BrushFrom(Blend(color, Colors.Black, 0.42)));
    }

    private Brush GetClassHoverBrush(CalendarItem item) => item.IsCompleted
        ? CompletedHoverBrush
        : BrushFrom(Blend(GetClassPaletteColor(item.ColorKey), Colors.Black, 0.08));

    private Brush GetKeyBrush(CalendarItem item) => item.IsCompleted
        ? CompletedKeyBrush
        : BrushFrom(Blend(GetKeyPaletteColor(item.ColorKey), Colors.Black, 0.12));

    private static Brush GetIdeaBrush() => BrushFrom("#FFF6E5");

    private Color GetClassPaletteColor(string key)
    {
        var settings = _viewModel?.Settings ?? new AppSettings();
        return ParseColor(CalendarPalette.ResolveColor(
            settings, CalendarItemKind.Class, key));
    }

    private Color GetKeyPaletteColor(string key)
    {
        var settings = _viewModel?.Settings ?? new AppSettings();
        return ParseColor(CalendarPalette.ResolveColor(
            settings, CalendarItemKind.Key, key));
    }

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private static Color Blend(Color source, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(source.R + (target.R - source.R) * amount),
            (byte)Math.Round(source.G + (target.G - source.G) * amount),
            (byte)Math.Round(source.B + (target.B - source.B) * amount));
    }

    private static SolidColorBrush BrushFrom(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static DateTimeOffset WithLocalDate(DateTimeOffset original, DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.FromDateTime(original.LocalDateTime), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static DateTimeOffset EndOfLocalDay(DateTimeOffset original)
    {
        var date = DateOnly.FromDateTime(original.LocalDateTime);
        var local = date.ToDateTime(new TimeOnly(23, 30), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static bool IsExpectedDataException(Exception exception) =>
        exception is System.IO.IOException
            or System.IO.InvalidDataException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception;

    private static void ShowError(string message, Exception exception) => MessageBox.Show(
        $"{message}\n\n{exception.Message}",
        "Diary of Mine",
        MessageBoxButton.OK,
        MessageBoxImage.Error);

    private sealed record ClassBarTag(CalendarItem Item, bool CanDragStart, bool CanDragEnd);
    private sealed record DragState(CalendarItem Item, bool UpdateStart);
    private sealed class ItemTossState(
        CalendarItem item,
        Border bar,
        Point startPoint,
        TranslateTransform translation,
        RotateTransform rotation)
    {
        public CalendarItem Item { get; } = item;
        public Border Bar { get; } = bar;
        public Point StartPoint { get; } = startPoint;
        public TranslateTransform Translation { get; } = translation;
        public RotateTransform Rotation { get; } = rotation;
        public bool IsDragging { get; set; }
        public bool DeleteArmed { get; set; }
    }
}
