using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DiaryOfMine.Application.WorkMode;
using DiaryOfMine.Desktop.ViewModels;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Views;

public partial class WorkModePageView : UserControl
{
    private readonly DispatcherTimer _timer;
    private double _zoom = 1;
    private bool _middleDown;
    private bool _middleMoved;
    private Point _middleOrigin;
    private double _scrollOrigin;
    private Guid? _editingSessionId;
    private Guid? _pendingSessionEditId;
    private Border? _pendingSessionEditTarget;
    private bool _allStats;
    private bool _sortByName;
    private bool _loaded;

    public WorkModePageView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background,
            OnTimerTick, Dispatcher);
    }

    private WorkModePageViewModel? ViewModel => DataContext as WorkModePageViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (ViewModel is { } viewModel)
        {
            viewModel.VisualsChanged += OnVisualsChanged;
            await viewModel.RefreshAsync();
        }
        _timer.Start();
        RenderAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _loaded = false;
        _timer.Stop();
        if (ViewModel is { } viewModel) viewModel.VisualsChanged -= OnVisualsChanged;
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.RefreshAsync(silent: true);
    }

    private void OnVisualsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RenderAll);

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => RenderAll();

    private void RenderAll()
    {
        if (!_loaded || ViewModel is not { } viewModel || TimelineHost.ActualWidth < 80) return;
        var viewportWidth = Math.Max(560, TimelineHost.ActualWidth - 28);
        TimelineSurface.Width = viewportWidth * _zoom;
        TimelineSurface.Height = Math.Max(352, TimelineHost.ActualHeight - 28);
        DrawRow(TopCanvas, 0);
        DrawRow(BottomCanvas, 12);
        UpdateToolbarState();
        RenderGoalPopup();
        RenderStats();
    }

    private void DrawRow(Canvas canvas, int startHour)
    {
        if (ViewModel is not { } viewModel) return;
        canvas.Children.Clear();
        var width = TimelineSurface.Width;
        var height = Math.Max(160, TimelineSurface.Height / 2 - 10);
        canvas.Height = height;
        var labelTop = 12d;
        var axisTop = 43d;
        var barHeight = Math.Clamp(height * 0.28, 44, 62);
        var barTop = Math.Max(axisTop + 25, (height - barHeight) / 2 + 8);
        var pixelsPerHour = width / 12d;
        var labelEveryHours = pixelsPerHour switch
        {
            >= 105 => 0.5,
            >= 56 => 1,
            >= 31 => 2,
            _ => 3
        };

        for (var half = 0; half <= 24; half++)
        {
            var hourOffset = half / 2d;
            var x = width * hourOffset / 12d;
            var isHour = half % 2 == 0;
            canvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = axisTop, Y2 = height - 14,
                Stroke = BrushOf(isHour ? "#D9E0DA" : "#EBEFEB"),
                StrokeThickness = isHour ? 1 : 0.8
            });
            var quotient = hourOffset / labelEveryHours;
            var shouldLabel = Math.Abs(quotient - Math.Round(quotient)) < 0.001;
            if (!shouldLabel) continue;
            var absoluteMinutes = (int)Math.Round((startHour + hourOffset) * 60);
            var label = absoluteMinutes == 1440
                ? "24:00"
                : $"{absoluteMinutes / 60:00}:{absoluteMinutes % 60:00}";
            var text = new TextBlock
            {
                Text = label,
                FontSize = pixelsPerHour < 40 ? 10 : 10.5,
                Foreground = BrushOf("#6F7B74"),
                IsHitTestVisible = false
            };
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(text, Math.Clamp(x - text.DesiredSize.Width / 2, 2,
                Math.Max(2, width - text.DesiredSize.Width - 2)));
            Canvas.SetTop(text, labelTop);
            canvas.Children.Add(text);
        }

        var dateStart = WorkModePageViewModel.LocalBoundary(viewModel.SelectedDate);
        var rowStart = dateStart.AddHours(startHour);
        var rowEnd = rowStart.AddHours(12);
        var now = DateTimeOffset.Now;
        foreach (var session in viewModel.SessionsForDate(viewModel.SelectedDate))
        {
            var effectiveEnd = WorkModePageViewModel.EffectiveEnd(session, now);
            var visibleStart = session.StartsAt > rowStart ? session.StartsAt : rowStart;
            var visibleEnd = effectiveEnd < rowEnd ? effectiveEnd : rowEnd;
            if (visibleEnd <= visibleStart) continue;
            var x = (visibleStart - rowStart).TotalHours / 12d * width;
            var segmentWidth = Math.Max(3, (visibleEnd - visibleStart).TotalHours / 12d * width);
            var goal = viewModel.FindGoal(session.GoalId);
            var color = WorkPalette.ResolveColor(viewModel.Settings, goal?.ColorKey);
            var shell = new Border
            {
                Width = segmentWidth,
                Height = barHeight,
                Background = BrushOf(color),
                BorderBrush = BrushOf(Darken(color, 0.2)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Cursor = Cursors.Hand,
                ToolTip = $"{goal?.Name ?? "已删除目标"}\n{session.StartsAt:yyyy-MM-dd HH:mm} — {(session.EndsAt is null ? "录制中" : $"{session.EndsAt:yyyy-MM-dd HH:mm}")}\n双击修改工作目标"
            };
            var text = new TextBlock
            {
                Text = segmentWidth > 85
                    ? $"{goal?.Name ?? "目标"}  {session.StartsAt:HH:mm}–{(session.EndsAt ?? now):HH:mm}"
                    : goal?.Name ?? "目标",
                Margin = new Thickness(10, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushOf("#30453B"),
                IsHitTestVisible = false
            };
            shell.Child = text;
            shell.PreviewMouseLeftButtonDown += OnSessionMouseDown;
            shell.PreviewMouseLeftButtonUp += OnSessionMouseUp;
            shell.Tag = session.Id;
            Canvas.SetLeft(shell, x);
            Canvas.SetTop(shell, barTop);
            canvas.Children.Add(shell);

            foreach (var interruption in viewModel.State.Interruptions.Where(item => item.SessionId == session.Id))
            {
                var interruptionEnd = interruption.EndsAt ?? now;
                var partStart = interruption.StartsAt > visibleStart ? interruption.StartsAt : visibleStart;
                var partEnd = interruptionEnd < visibleEnd ? interruptionEnd : visibleEnd;
                if (partEnd <= partStart) continue;
                var overlayX = (partStart - rowStart).TotalHours / 12d * width;
                var overlayWidth = Math.Max(3, (partEnd - partStart).TotalHours / 12d * width);
                var overlay = new Border
                {
                    Width = overlayWidth,
                    Height = Math.Max(18, barHeight * 0.48),
                    Background = BrushOf("#F5C3C3"),
                    BorderBrush = BrushOf("#DEA5A5"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Opacity = 0.92,
                    IsHitTestVisible = false,
                    ToolTip = "中断"
                };
                if (overlayWidth > 46)
                    overlay.Child = new TextBlock { Text = "中断", FontSize = 10, Foreground = BrushOf("#8D5555"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Canvas.SetLeft(overlay, overlayX);
                Canvas.SetTop(overlay, barTop + (barHeight - overlay.Height) / 2);
                Panel.SetZIndex(overlay, 5);
                canvas.Children.Add(overlay);
            }
        }

        if (viewModel.IsToday && now >= rowStart && now <= rowEnd)
        {
            var x = (now - rowStart).TotalHours / 12d * width;
            var line = new Line { X1 = x, X2 = x, Y1 = axisTop - 5, Y2 = height - 10,
                Stroke = BrushOf("#A98AC8"), StrokeThickness = 2, IsHitTestVisible = false };
            Panel.SetZIndex(line, 10);
            canvas.Children.Add(line);
        }
    }

    private void UpdateToolbarState()
    {
        if (ViewModel is not { } vm) return;
        FinishButton.IsEnabled = vm.IsRecording;
        TranscribeButton.IsEnabled = !vm.IsRecording;
        InterruptButton.Background = BrushOf(vm.IsInterrupting ? "#F7DCDC" : "#FAFCFA");
        RecordingBadge.Visibility = vm.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        RecordingBadge.Background = BrushOf(vm.IsInterrupting ? "#F7E2E2" : "#E4F1E9");
        RecordingBadge.BorderBrush = BrushOf(vm.IsInterrupting ? "#E5BDBD" : "#BFD8C8");
        RecordingBadgeText.Text = vm.IsInterrupting ? "录制中 · 中断标记中" : "录制中";
    }

    private void RenderGoalPopup()
    {
        if (ViewModel is not { } vm) return;
        GoalsPanel.Children.Clear();
        foreach (var goal in vm.ActiveGoals)
        {
            var button = MakeRoundedButton(goal.Name, WorkPalette.ResolveColor(vm.Settings, goal.ColorKey));
            button.Margin = new Thickness(0, 3, 0, 3);
            button.Tag = goal.Id;
            button.Click += OnGoalPicked;
            GoalsPanel.Children.Add(button);
        }
        if (vm.ActiveGoals.Count == 0)
            GoalsPanel.Children.Add(new TextBlock { Text = "请先使用 Add 创建目标", Margin = new Thickness(4, 8, 4, 8), Foreground = BrushOf("#7A847E") });
    }

    private void RenderStats()
    {
        if (ViewModel is not { } vm) return;
        TodayStatsButton.Background = BrushOf(_allStats ? "#F3F6F3" : "#DDEAE1");
        AllStatsButton.Background = BrushOf(_allStats ? "#DDEAE1" : "#F3F6F3");
        DurationSortButton.Background = BrushOf(_sortByName ? "#F3F6F3" : "#E8EFE9");
        NameSortButton.Background = BrushOf(_sortByName ? "#E8EFE9" : "#F3F6F3");
        StatsPanel.Children.Clear();
        var now = DateTimeOffset.Now;
        var dayStart = WorkModePageViewModel.LocalBoundary(DateOnly.FromDateTime(DateTime.Today));
        var dayEnd = dayStart.AddDays(1);
        var values = vm.State.Sessions.GroupBy(session => session.GoalId).Select(group =>
        {
            var total = group.Sum(session =>
            {
                var start = session.StartsAt;
                var end = WorkModePageViewModel.EffectiveEnd(session, now);
                if (!_allStats)
                {
                    start = start > dayStart ? start : dayStart;
                    end = end < dayEnd ? end : dayEnd;
                }
                return Math.Max(0, (end - start).TotalMinutes);
            });
            return (Goal: vm.FindGoal(group.Key), Minutes: total);
        }).Where(item => item.Minutes > 0).ToList();
        values = _sortByName
            ? values.OrderBy(item => item.Goal?.Name).ToList()
            : values.OrderByDescending(item => item.Minutes).ToList();
        if (values.Count == 0)
        {
            StatsPanel.Children.Add(new TextBlock { Text = _allStats ? "暂无累计记录" : "今日暂无记录", Foreground = BrushOf("#7A847E"), Margin = new Thickness(0, 12, 0, 0) });
            return;
        }
        var max = values.Max(item => item.Minutes);
        foreach (var item in values)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };
            var caption = new DockPanel();
            caption.Children.Add(new TextBlock { Text = item.Goal?.Name ?? "已删除目标", FontSize = 11.5, FontWeight = FontWeights.SemiBold });
            var duration = new TextBlock { Text = FormatDuration(item.Minutes), FontSize = 10.5, Foreground = BrushOf("#6F7B74") };
            DockPanel.SetDock(duration, Dock.Right);
            caption.Children.Add(duration);
            panel.Children.Add(caption);
            var track = new Grid { Height = 9, Margin = new Thickness(0, 5, 0, 0), Background = BrushOf("#EEF1EE") };
            track.Children.Add(new Border { Width = Math.Max(5, 192 * item.Minutes / max), HorizontalAlignment = HorizontalAlignment.Left,
                Background = BrushOf(WorkPalette.ResolveColor(vm.Settings, item.Goal?.ColorKey)), CornerRadius = new CornerRadius(5) });
            panel.Children.Add(track);
            StatsPanel.Children.Add(panel);
        }
    }

    private async void OnGoalPicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Button { Tag: Guid goalId }) return;
        GoalPopup.IsOpen = false;
        if (_editingSessionId is { } sessionId)
            await vm.ChangeSessionGoalAsync(sessionId, goalId);
        else
            await vm.StartAsync(goalId);
        _editingSessionId = null;
    }

    private void OnTranscribeClick(object sender, RoutedEventArgs e)
    {
        _editingSessionId = null;
        GoalPopup.PlacementTarget = TranscribeButton;
        GoalPopup.Placement = PlacementMode.Left;
        GoalPopup.PlacementRectangle = Rect.Empty;
        GoalPopup.HorizontalOffset = 0;
        GoalPopup.VerticalOffset = 0;
        GoalPopupTitle.Text = "选择工作目标";
        RenderGoalPopup();
        GoalPopup.IsOpen = true;
        FocusFirstGoalButton();
    }

    private async void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.FinishAsync();
    }

    private async void OnInterruptClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (vm.IsRecording) await vm.ToggleInterruptAsync();
        else ShowManualInterruptEditor(vm);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var window = new WorkGoalManagerWindow(vm)
        {
            Owner = Window.GetWindow(this)
        };
        RegisterCompanion(window);
        window.Show();
    }

    private void ShowManualInterruptEditor(WorkModePageViewModel vm)
    {
        var sessions = vm.SessionsForDate(vm.SelectedDate).Where(session => session.EndsAt is not null).ToArray();
        var window = new WorkInterruptEditorWindow(vm, sessions)
        {
            Owner = Window.GetWindow(this)
        };
        RegisterCompanion(window);
        window.Show();
    }

    private static void RegisterCompanion(Window window)
    {
        if (System.Windows.Application.Current is not App app) return;
        app.RegisterCompanionWindow(window);
        window.Closed += (_, _) => app.UnregisterCompanionWindow(window);
    }

    private void OnSessionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Guid id } shell || e.ClickCount != 2) return;
        _pendingSessionEditId = id;
        _pendingSessionEditTarget = shell;
        shell.CaptureMouse();
        e.Handled = true;
    }

    private void OnSessionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border shell
            || !ReferenceEquals(shell, _pendingSessionEditTarget)
            || _pendingSessionEditId is not { } sessionId)
            return;

        var anchorPoint = e.GetPosition(shell);
        _pendingSessionEditId = null;
        _pendingSessionEditTarget = null;
        shell.ReleaseMouseCapture();
        e.Handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (!shell.IsLoaded) return;
            _editingSessionId = sessionId;
            GoalPopup.PlacementTarget = shell;
            GoalPopup.Placement = PlacementMode.RelativePoint;
            GoalPopup.PlacementRectangle = new Rect(anchorPoint.X, anchorPoint.Y, 1, 1);
            GoalPopup.HorizontalOffset = 10;
            GoalPopup.VerticalOffset = 8;
            GoalPopupTitle.Text = "修改这条记录的目标";
            RenderGoalPopup();
            GoalPopup.IsOpen = true;
            FocusFirstGoalButton();
        }, DispatcherPriority.ContextIdle);
    }

    private void FocusFirstGoalButton() => Dispatcher.BeginInvoke(() =>
    {
        GoalsPanel.Children.OfType<Button>().FirstOrDefault()?.Focus();
    }, DispatcherPriority.Input);

    private void OnTimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var old = _zoom;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.14 : 1 / 1.14), 0.72, 4.5);
        if (Math.Abs(old - _zoom) < 0.001) return;
        var ratio = TimelineScroller.ScrollableWidth <= 0 ? 0.5
            : (TimelineScroller.HorizontalOffset + e.GetPosition(TimelineScroller).X) / Math.Max(1, TimelineSurface.Width);
        RenderAll();
        Dispatcher.BeginInvoke(() => TimelineScroller.ScrollToHorizontalOffset(
            ratio * TimelineSurface.Width - e.GetPosition(TimelineScroller).X));
        e.Handled = true;
    }

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _middleDown = true;
        _middleMoved = false;
        _middleOrigin = e.GetPosition(TimelineScroller);
        _scrollOrigin = TimelineScroller.HorizontalOffset;
        TimelineScroller.CaptureMouse();
        e.Handled = true;
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (!_middleDown || e.MiddleButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(TimelineScroller);
        var delta = current.X - _middleOrigin.X;
        if (Math.Abs(delta) > 3) _middleMoved = true;
        TimelineScroller.ScrollToHorizontalOffset(_scrollOrigin - delta);
        e.Handled = true;
    }

    private void OnTimelineMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_middleDown || e.ChangedButton != MouseButton.Middle) return;
        TimelineScroller.ReleaseMouseCapture();
        _middleDown = false;
        if (!_middleMoved)
        {
            _zoom = 1;
            RenderAll();
            TimelineScroller.ScrollToHorizontalOffset(0);
        }
        e.Handled = true;
    }

    private void OnJumpDateClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            var date = vm.SelectedDate.ToDateTime(TimeOnly.MinValue);
            JumpCalendar.DisplayDate = date;
            JumpCalendar.SelectedDate = date;
        }
        DatePopup.IsOpen = true;
    }

    private void OnJumpCalendarSelected(object sender, SelectionChangedEventArgs e)
    {
        if (JumpCalendar.SelectedDate is { } date && ViewModel is { } vm)
            vm.SelectedDate = DateOnly.FromDateTime(date);
        DatePopup.IsOpen = false;
    }

    private void OnTodayStatsClick(object sender, RoutedEventArgs e) { _allStats = false; RenderStats(); }
    private void OnAllStatsClick(object sender, RoutedEventArgs e) { _allStats = true; RenderStats(); }
    private void OnDurationSortClick(object sender, RoutedEventArgs e) { _sortByName = false; RenderStats(); }
    private void OnNameSortClick(object sender, RoutedEventArgs e) { _sortByName = true; RenderStats(); }

    private static Button MakeRoundedButton(string text, string background)
    {
        var button = new Button { Content = text, Height = 34, Background = BrushOf(background),
            BorderBrush = BrushOf(Darken(background, 0.16)), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        button.Template = new ControlTemplate(typeof(Button))
        {
            VisualTree = MakeButtonTemplate()
        };
        return button;
    }

    private static FrameworkElementFactory MakeButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return border;
    }

    private static SolidColorBrush BrushOf(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private static string Darken(string hex, double amount)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        byte channel(byte value) => (byte)Math.Clamp(value * (1 - amount), 0, 255);
        return $"#{channel(color.R):X2}{channel(color.G):X2}{channel(color.B):X2}";
    }

    private static string FormatDuration(double minutes)
    {
        var rounded = (int)Math.Round(minutes);
        return rounded >= 60 ? $"{rounded / 60}h {rounded % 60:00}m" : $"{rounded}m";
    }
}
