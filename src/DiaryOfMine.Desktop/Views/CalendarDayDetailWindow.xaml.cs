using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
using DiaryOfMine.Desktop.Services;
using DiaryOfMine.Desktop.ViewModels;
using DiaryOfMine.Domain;
using Microsoft.Win32;

namespace DiaryOfMine.Desktop.Views;

public partial class CalendarDayDetailWindow : Window
{
    private const double DefaultPixelsPerHour = 62;
    private const double MaximumPixelsPerHour = 124;
    private const double MinimumFullDayPixelsPerHour = 46;

    private static readonly Brush GridLineBrush = BrushFrom("#DCE3DE");
    private static readonly Brush HalfHourBrush = BrushFrom("#EDF0ED");
    private static readonly Brush TextBrush = BrushFrom("#26312D");
    private static readonly Brush MutedBrush = BrushFrom("#7B8780");
    private static readonly Brush CurrentTimeBrush = BrushFrom("#9B7AD8");
    private static readonly Brush CompletedFillBrush = BrushFrom("#ECEFED");
    private static readonly Brush CompletedHoverBrush = BrushFrom("#E2E6E3");
    private static readonly Brush CompletedInkBrush = BrushFrom("#848D88");

    private readonly CalendarDayDetailViewModel _viewModel;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _fileLinksCloseTimer;
    private readonly Dictionary<Guid, List<EventVisual>> _itemVisuals = new();
    private readonly List<RowVisual> _rows = new();
    private readonly HashSet<Guid> _completionChanges = new();

    private CalendarItemEditorWindow? _editor;
    private bool _editorExternallyHandled;
    private KeyTaskEditorWindow? _taskEditor;
    private bool _taskEditorExternallyHandled;
    private CalendarItem? _taskEditorOriginal;
    private BoundaryDragState? _boundaryDrag;
    private TossState? _tossState;
    private bool _loaded;
    private bool _renderQueued;
    private bool _closeStarted;
    private bool _middleDown;
    private bool _middleMoved;
    private Point _middleStart;
    private double _middleStartViewMinute;
    private double _pixelsPerHour = DefaultPixelsPerHour;
    private double _viewStartMinute;
    private int _visibleItemCapacity = 1;
    private OverlapSelection? _overlapSelection;
    private CalendarItem? _fileLinksPopupItem;
    private FrameworkElement? _fileLinksHoverTarget;

    internal bool IsClosing => _closeStarted;
    internal event Action<DateOnly>? DiaryRequested;

    internal CalendarDayDetailWindow(CalendarDayDetailViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _viewModel.ViewChanged += OnViewModelChanged;
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnWindowPreviewMouseLeftButtonUp;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clockTimer.Tick += OnClockTick;
        _fileLinksCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _fileLinksCloseTimer.Tick += (_, _) =>
        {
            _fileLinksCloseTimer.Stop();
            if (FileLinksPopupRoot.IsMouseOver || _fileLinksHoverTarget?.IsMouseOver == true) return;
            CloseFileLinksPopup();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            await _viewModel.LoadDateAsync(_viewModel.Date);
            RenderAll();
            ScrollToRelevantTime();
            _clockTimer.Start();
        }
        catch (Exception exception)
        {
            ShowError("无法读取当日日程。", exception);
        }
    }

    internal async Task NavigateToAsync(DateOnly date)
    {
        if (!await CommitPendingEditorAsync()) return;
        CancelActiveInteraction();
        _overlapSelection = null;
        await _viewModel.LoadDateAsync(date);
        RenderAll();
        ScrollToRelevantTime();
        Activate();
    }

    internal void ApplySettings(DiaryOfMine.Application.Settings.AppSettings settings) =>
        _viewModel.ApplySettings(settings);

    internal async Task<bool> SavePendingAndCloseAsync()
    {
        if (_closeStarted) return true;
        if (_boundaryDrag is not null)
            await CompleteBoundaryDragAsync();
        if (_tossState is not null)
            CancelToss(animate: false);
        if (!await CommitPendingEditorAsync()) return false;
        Close();
        return true;
    }

    private async Task<bool> CommitPendingEditorAsync()
    {
        if (_editor is { IsVisible: true } editor)
        {
            if (!editor.TryCommitDraft())
            {
                editor.Activate();
                return false;
            }

            try
            {
                _editorExternallyHandled = true;
                await SaveEditorDraftAsync(editor, editor.Draft!);
                editor.CloseAfterExternalCommit();
            }
            catch (Exception exception)
            {
                _editorExternallyHandled = false;
                ShowError("无法保存编辑内容。", exception);
                editor.Activate();
                return false;
            }
        }

        if (_taskEditor is not { IsVisible: true } taskEditor) return true;
        if (!taskEditor.TryCommitSchedule())
        {
            taskEditor.Activate();
            return false;
        }
        try
        {
            _taskEditorExternallyHandled = true;
            if (_taskEditorOriginal is { } original)
                await _viewModel.EditKeyTaskAsync(original, taskEditor.Schedule!);
            else
                await _viewModel.CreateKeyTaskAsync(taskEditor.Schedule!);
            taskEditor.CloseAfterExternalCommit();
            return true;
        }
        catch (Exception exception)
        {
            _taskEditorExternallyHandled = false;
            ShowError("无法保存 Task 批次。", exception);
            taskEditor.Activate();
            return false;
        }
    }

    private void OnViewModelChanged(object? sender, EventArgs e)
    {
        if (!_loaded) return;
        Dispatcher.BeginInvoke(RenderAll, DispatcherPriority.Background);
    }

    private void RenderAll()
    {
        if (!_loaded || _closeStarted) return;
        CloseFileLinksPopup();
        UpdateHeading();
        _itemVisuals.Clear();
        RenderIdeas();
        RenderInspirations();
        RenderTimeline();
        UndoButton.IsEnabled = _viewModel.CanUndo;
        RedoButton.IsEnabled = _viewModel.CanRedo;
    }

    private void UpdateHeading()
    {
        DateHeading.Text = _viewModel.Date.ToString("yyyy年M月d日  dddd", CultureInfo.CurrentCulture);
        var classCount = _viewModel.Items.Count(item => item.Kind == CalendarItemKind.Class);
        var keyCount = _viewModel.Items.Count(item => item.Kind == CalendarItemKind.Key);
        DateSubtitle.Text = $"Class {classCount} · Key {keyCount} · 分钟级日程";
        Title = $"{_viewModel.Date:yyyy/M/d} · 详细日程表";
    }

    private void RenderIdeas()
    {
        IdeaPanel.Children.Clear();
        var ideas = _viewModel.Items
            .Where(item => item.Kind == CalendarItemKind.Idea)
            .OrderBy(item => item.IsCompleted)
            .ThenBy(item => item.CreatedAt)
            .ToArray();
        IdeaCountText.Text = $"{ideas.Length} 项";

        if (ideas.Length == 0)
        {
            IdeaPanel.Children.Add(new TextBlock
            {
                Text = "今天没有记录任何Idea",
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 46, 0, 0),
                FontSize = 11
            });
            return;
        }

        foreach (var item in ideas)
        {
            var checkBox = new CheckBox
            {
                IsChecked = item.IsCompleted,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 9, 0),
                Tag = item,
                ToolTip = item.IsCompleted ? "标记为未完成" : "标记为已完成"
            };
            checkBox.Click += OnIdeaCompletedClick;

            var text = new TextBlock
            {
                Text = item.Title,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = item.IsCompleted ? MutedBrush : TextBrush,
                TextDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null
            };

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(checkBox);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);

            var normal = BrushFrom(item.IsCompleted ? "#F0F3EE" : "#FFF6E5");
            var hover = BrushFrom(item.IsCompleted ? "#E7ECE7" : "#FCEBCB");
            var card = new Border
            {
                Tag = item,
                Background = normal,
                BorderBrush = BrushFrom("#E7DDC8"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 10, 11, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                ToolTip = "双击编辑 · 拖出便签区删除",
                Child = content
            };
            card.MouseEnter += (_, _) =>
            {
                SetItemHighlight(item.Id, true);
                ShowFileLinksPopup(item, card);
            };
            card.MouseLeave += (_, _) =>
            {
                SetItemHighlight(item.Id, false);
                ScheduleFileLinksPopupClose();
            };
            card.PreviewMouseLeftButtonDown += OnIdeaCardMouseLeftButtonDown;
            AddItemVisual(item.Id, card, normal, hover);
            IdeaPanel.Children.Add(card);
        }
    }

    private async void OnIdeaCompletedClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: CalendarItem item } checkBox) return;
        e.Handled = true;
        try
        {
            await _viewModel.ToggleIdeaAsync(item, checkBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            ShowError("无法保存 Idea 完成状态。", exception);
            RenderAll();
        }
    }

    private void ShowFileLinksPopup(CalendarItem item, FrameworkElement target)
    {
        if (item.Kind is not (CalendarItemKind.Class or CalendarItemKind.Key or CalendarItemKind.Idea)) return;
        _fileLinksCloseTimer.Stop();
        _fileLinksPopupItem = item;
        _fileLinksHoverTarget = target;
        FileLinksPopup.PlacementTarget = target;
        RenderFileLinksPopup(item);
        FileLinksPopup.IsOpen = true;
    }

    private void ScheduleFileLinksPopupClose()
    {
        _fileLinksCloseTimer.Stop();
        _fileLinksCloseTimer.Start();
    }

    private void CloseFileLinksPopup()
    {
        _fileLinksCloseTimer.Stop();
        FileLinksPopup.IsOpen = false;
        _fileLinksPopupItem = null;
        _fileLinksHoverTarget = null;
    }

    private void OnFileLinksPopupMouseEnter(object sender, MouseEventArgs e) =>
        _fileLinksCloseTimer.Stop();

    private void OnFileLinksPopupMouseLeave(object sender, MouseEventArgs e) =>
        ScheduleFileLinksPopupClose();

    private void RenderFileLinksPopup(CalendarItem item)
    {
        FileLinksPopupItems.Children.Clear();
        FileLinksPopupStatusText.Visibility = Visibility.Collapsed;
        var links = (item.FileLinks ?? Array.Empty<CalendarFileLink>())
            .OrderByDescending(link => link.IsPinned)
            .ThenBy(link => link.AddedAt)
            .ToArray();
        FileLinksPopupCountText.Text = $"{links.Length}/{CalendarFileLinkUtilities.MaximumLinks}";

        if (links.Length == 0)
        {
            FileLinksPopupItems.Children.Add(new TextBlock
            {
                Text = "尚未绑定快捷链接\n可直接拖入一个或多个本地文件或文件夹",
                Foreground = MutedBrush,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 24, 8, 22)
            });
            return;
        }

        foreach (var link in links) FileLinksPopupItems.Children.Add(CreatePopupFileLinkRow(item, link));
    }

    private FrameworkElement CreatePopupFileLinkRow(CalendarItem item, CalendarFileLink link)
    {
        var missing = !File.Exists(link.FilePath) && !Directory.Exists(link.FilePath);
        var cached = CalendarFileLinkUtilities.IsLikelyCachePath(link.FilePath);
        var row = new Border
        {
            Background = Brushes.White,
            BorderBrush = BrushFrom(link.IsPinned ? "#AFC9BC" : "#DCE3DE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 7, 7, 7),
            Margin = new Thickness(0, 0, 0, 7)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = link.IsDirectory ? "▰" : "▤",
            FontSize = 17,
            Foreground = missing ? BrushFrom("#B98A86") : BrushFrom("#68877A"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var fileNameButton = PopupButton(link.DisplayName, 29);
        fileNameButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        fileNameButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        fileNameButton.Background = Brushes.Transparent;
        fileNameButton.BorderThickness = new Thickness(0);
        fileNameButton.Padding = new Thickness(5, 0, 5, 0);
        fileNameButton.Margin = new Thickness(0, 0, 5, 0);
        fileNameButton.ToolTip = link.FilePath;
        fileNameButton.IsEnabled = !missing;
        fileNameButton.Click += (_, _) => CalendarFileLinkUtilities.Open(this, link);
        Grid.SetColumn(fileNameButton, 1);
        grid.Children.Add(fileNameButton);

        var pinButton = PopupButton(link.IsPinned ? "已置顶" : "置顶", 27, 54);
        pinButton.ToolTip = link.IsPinned ? "此链接已置顶" : "将此链接排到最前";
        pinButton.IsEnabled = !link.IsPinned;
        pinButton.Margin = new Thickness(0, 0, 5, 0);
        pinButton.Click += async (_, _) =>
            await SavePopupFileLinksAsync(item, CalendarFileLinkUtilities.Pin(item.FileLinks, link.Id));
        Grid.SetColumn(pinButton, 2);
        grid.Children.Add(pinButton);

        var removeButton = PopupButton("×", 27, 28);
        removeButton.ToolTip = "移除链接（不会删除原文件或文件夹）";
        removeButton.Foreground = BrushFrom("#A35D5D");
        removeButton.Click += async (_, _) =>
            await SavePopupFileLinksAsync(item, CalendarFileLinkUtilities.Remove(item.FileLinks, link.Id));
        Grid.SetColumn(removeButton, 3);
        grid.Children.Add(removeButton);

        if (missing || cached)
        {
            var statusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 4, 0, 0)
            };
            statusPanel.Children.Add(new TextBlock
            {
                Text = missing
                    ? (link.IsDirectory ? "文件夹失效或已移动" : "文件失效或已移动")
                    : "缓存目录内容可能被自动清理",
                Foreground = missing ? BrushFrom("#B35F5F") : BrushFrom("#956C44"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (missing)
            {
                var rebindButton = PopupButton("重新定位", 24, 66);
                rebindButton.FontSize = 10;
                rebindButton.Margin = new Thickness(8, 0, 0, 0);
                rebindButton.Click += (_, _) => RebindPopupFileLink(item, link);
                statusPanel.Children.Add(rebindButton);
            }
            Grid.SetRow(statusPanel, 1);
            Grid.SetColumn(statusPanel, 1);
            Grid.SetColumnSpan(statusPanel, 3);
            grid.Children.Add(statusPanel);
        }

        row.Child = grid;
        return row;
    }

    private Button PopupButton(string content, double height, double? width = null)
    {
        var button = new Button
        {
            Content = content,
            Height = height,
            Style = (Style)FindResource("DetailButtonStyle")
        };
        if (width is { } value) button.Width = value;
        return button;
    }

    private void OnPopupAddFilesClick(object sender, RoutedEventArgs e)
    {
        if (_fileLinksPopupItem is not { } item) return;
        _fileLinksCloseTimer.Stop();
        var dialog = new OpenFileDialog
        {
            Title = "选择要绑定的本地文件",
            Filter = "所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) AddPopupFilePaths(item, dialog.FileNames);
    }

    private void OnPopupAddFoldersClick(object sender, RoutedEventArgs e)
    {
        if (_fileLinksPopupItem is not { } item) return;
        _fileLinksCloseTimer.Stop();
        var dialog = new OpenFolderDialog
        {
            Title = "选择要绑定的本地文件夹",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) AddPopupFilePaths(item, dialog.FolderNames);
    }

    private void OnPopupFileLinksDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPopupFileLinksDrop(object sender, DragEventArgs e)
    {
        if (_fileLinksPopupItem is { } item
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPopupFilePaths(item, paths);
        else ShowPopupFileLinksStatus("该来源未提供稳定路径，请先另存为本机文件。", true);
        e.Handled = true;
    }

    private async void AddPopupFilePaths(CalendarItem item, IEnumerable<string> paths)
    {
        var result = CalendarFileLinkUtilities.AddPaths(item.FileLinks, paths);
        ShowPopupFileLinksStatus(CalendarFileLinkUtilities.Describe(result), result.Added == 0 || result.Ignored > 0);
        if (result.Added == 0) return;
        await SavePopupFileLinksAsync(item, result.Links);
    }

    private async void RebindPopupFileLink(CalendarItem item, CalendarFileLink link)
    {
        if (link.IsDirectory)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = $"重新定位 {link.DisplayName}",
                Multiselect = false
            };
            if (folderDialog.ShowDialog(this) == true)
                await TryRebindPopupFileLinkAsync(item, link, folderDialog.FolderName);
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
        if (dialog.ShowDialog(this) == true)
            await TryRebindPopupFileLinkAsync(item, link, dialog.FileName);
    }

    private async Task TryRebindPopupFileLinkAsync(
        CalendarItem item,
        CalendarFileLink link,
        string path)
    {
        try
        {
            await SavePopupFileLinksAsync(
                item, CalendarFileLinkUtilities.Rebind(item.FileLinks, link.Id, path));
        }
        catch (Exception exception)
        {
            ShowPopupFileLinksStatus(exception.Message, true);
        }
    }

    private async Task SavePopupFileLinksAsync(
        CalendarItem item,
        IReadOnlyList<CalendarFileLink> links)
    {
        try
        {
            CloseFileLinksPopup();
            await _viewModel.UpdateFileLinksAsync(item, links);
            ShowInteractionStatus("快捷链接已更新；原文件或文件夹未作修改。", delete: false);
        }
        catch (Exception exception)
        {
            ShowError("无法保存快捷链接。", exception);
        }
    }

    private void ShowPopupFileLinksStatus(string message, bool warning)
    {
        FileLinksPopupStatusText.Text = message;
        FileLinksPopupStatusText.Foreground = warning ? BrushFrom("#956C44") : BrushFrom("#5E7D6F");
        FileLinksPopupStatusText.Visibility = Visibility.Visible;
    }

    private void RenderInspirations()
    {
        InspirationPanel.Children.Clear();
        var inspirations = _viewModel.Items
            .Where(item => item.Kind == CalendarItemKind.Inspiration)
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        InspirationCountText.Text = $"{inspirations.Length} 条";

        if (inspirations.Length == 0)
        {
            InspirationPanel.Children.Add(new TextBlock
            {
                Text = "今天还没记录灵感。",
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 34, 0, 0),
                FontSize = 11
            });
            return;
        }

        foreach (var item in inspirations)
        {
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(new TextBlock
            {
                Text = item.Title,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = TextBrush
            });
            var time = new TextBlock
            {
                Text = item.CreatedAt.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture),
                FontSize = 9.5,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(time, 1);
            content.Children.Add(time);

            var normal = BrushFrom("#F0F0FA");
            var hover = BrushFrom("#E7E6F6");
            var card = new Border
            {
                Tag = item,
                Background = normal,
                BorderBrush = BrushFrom("#D9D7EC"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 9, 11, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                ToolTip = "双击编辑 · 拖出灵感区删除",
                Child = content
            };
            card.MouseEnter += (_, _) => SetItemHighlight(item.Id, true);
            card.MouseLeave += (_, _) => SetItemHighlight(item.Id, false);
            card.PreviewMouseLeftButtonDown += OnInspirationCardMouseLeftButtonDown;
            AddItemVisual(item.Id, card, normal, hover);
            InspirationPanel.Children.Add(card);
        }
    }

    private void RenderTimeline()
    {
        TimelineRowsPanel.Children.Clear();
        _rows.Clear();
        foreach (var id in _viewModel.Items
                     .Where(item => item.Kind is CalendarItemKind.Class or CalendarItemKind.Key)
                     .Select(item => item.Id))
            _itemVisuals.Remove(id);

        var allSlices = BuildSlices();
        var zoom = Math.Clamp(_pixelsPerHour / DefaultPixelsPerHour, 1, 2);
        var laneHeight = 19 * zoom;
        var barHeight = 16 * zoom;
        var viewportWidth = double.IsFinite(TimelineScrollViewer.ViewportWidth)
                            && TimelineScrollViewer.ViewportWidth > 0
            ? TimelineScrollViewer.ViewportWidth
            : TimelineScrollViewer.ActualWidth;
        var viewportHeight = double.IsFinite(TimelineScrollViewer.ViewportHeight)
                             && TimelineScrollViewer.ViewportHeight > 0
            ? TimelineScrollViewer.ViewportHeight
            : TimelineScrollViewer.ActualHeight;
        var availableWidth = Math.Max(240, viewportWidth - 20);
        var availableHeight = Math.Max(180, viewportHeight - 20);
        var rowCount = availableWidth >= 24 * MinimumFullDayPixelsPerHour ? 1 : 2;
        var visibleMinutes = VisibleMinutes;
        _viewStartMinute = ClampViewStart(_viewStartMinute, visibleMinutes);
        var minutesPerRow = visibleMinutes / rowCount;
        var fillCanvasHeight = Math.Max(84,
            (availableHeight - rowCount * 11) / rowCount);
        const double reservedTopMarkerSpace = 26;
        const double reservedBottomMarkerSpace = 29;
        _visibleItemCapacity = Math.Max(1, (int)Math.Floor(
            (fillCanvasHeight - 28 - reservedTopMarkerSpace - reservedBottomMarkerSpace)
            / laneHeight));
        var visibleSlices = SelectVisibleSlices(allSlices, _visibleItemCapacity);
        var lanes = AssignLanes(visibleSlices);
        var laneCount = lanes.Count == 0 ? 1 : lanes.Values.Max() + 1;
        var overflowMarkers = BuildOverflowMarkers(allSlices, _visibleItemCapacity);
        var nameRenderedFor = new HashSet<Guid>();

        TimelineRowsPanel.Width = availableWidth;

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var rowStart = _viewStartMinute + rowIndex * minutesPerRow;
            var rowEnd = Math.Min(1440, rowStart + minutesPerRow);
            var rowWidth = availableWidth;
            var pixelsPerMinute = rowWidth / Math.Max(1, rowEnd - rowStart);
            var rowMarkers = overflowMarkers.Where(marker =>
                marker.Minute >= rowStart
                && (marker.Minute < rowEnd
                    || rowIndex == rowCount - 1 && marker.Minute <= rowEnd)).ToArray();
            var topMarkerSpace = rowMarkers.Any(marker => marker.Above) ? 26 : 0;
            var laneBottomSpace = rowMarkers.Any(marker => !marker.Above) ? 29 : 12;
            var contentHeight = 30 + topMarkerSpace + laneCount * laneHeight + laneBottomSpace;
            var canvasHeight = Math.Max(fillCanvasHeight, contentHeight);
            var canvas = new Canvas
            {
                Width = rowWidth,
                Height = canvasHeight,
                Background = Brushes.White,
                ClipToBounds = true
            };

            AddTimeGrid(canvas, rowStart, rowEnd, canvasHeight, zoom, pixelsPerMinute);
            AddCurrentTimeLine(canvas, rowStart, rowEnd, canvasHeight, pixelsPerMinute);

            var laneAreaHeight = Math.Max(laneCount * laneHeight,
                canvasHeight - 28 - topMarkerSpace - laneBottomSpace);
            var laneStep = laneAreaHeight / laneCount;

            foreach (var slice in visibleSlices.Where(slice =>
                         slice.StartMinute < rowEnd && slice.EndMinute > rowStart))
            {
                var segmentStart = Math.Max(slice.StartMinute, rowStart);
                var segmentEnd = Math.Min(slice.EndMinute, rowEnd);
                var x = (segmentStart - rowStart) * pixelsPerMinute;
                var width = Math.Max(3, (segmentEnd - segmentStart) * pixelsPerMinute);
                var y = 28 + topMarkerSpace + lanes[slice.Item.Id] * laneStep
                    + Math.Max(0, (laneStep - barHeight) / 2);
                var startsHere = IsTrueStartOnDate(slice.Item)
                    && NearlyEqual(segmentStart, slice.StartMinute);
                var endsHere = IsTrueEndOnDate(slice.Item)
                    && NearlyEqual(segmentEnd, slice.EndMinute);
                var bar = CreateEventBar(
                    slice, segmentStart, segmentEnd, startsHere, endsHere,
                    width, barHeight, zoom, nameRenderedFor.Add(slice.Item.Id));
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, y);
                canvas.Children.Add(bar);
            }

            foreach (var marker in rowMarkers)
            {
                var badge = new Border
                {
                    Background = BrushFrom("#F4F0FA"),
                    BorderBrush = BrushFrom("#D9CDEA"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(7, 2, 7, 2),
                    Child = new TextBlock
                    {
                        Text = FormatOverflowMarker(marker),
                        FontSize = 9.5,
                        Foreground = BrushFrom("#6C5A83")
                    }
                };
                Canvas.SetLeft(badge, Math.Clamp(
                    (marker.Minute - rowStart) * pixelsPerMinute,
                    2, Math.Max(2, rowWidth - 150)));
                Canvas.SetTop(badge, marker.Above ? 29 : Math.Max(30, canvasHeight - 26));
                canvas.Children.Add(badge);
            }

            if (allSlices.Count == 0 && rowIndex == 0)
            {
                var empty = new TextBlock
                {
                    Text = "这一天还没有具有时间的 Class 或 Key。",
                    Foreground = MutedBrush,
                    FontSize = 11
                };
                Canvas.SetLeft(empty, 18);
                Canvas.SetTop(empty, Math.Max(48, canvasHeight / 2 - 8));
                canvas.Children.Add(empty);
            }

            var shell = new Border
            {
                Background = Brushes.White,
                BorderBrush = GridLineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 0, 9),
                Child = canvas
            };
            TimelineRowsPanel.Children.Add(shell);
            _rows.Add(new RowVisual(
                shell, canvas, rowStart, rowEnd, pixelsPerMinute));
        }
    }

    private void AddTimeGrid(
        Canvas canvas, double rowStart, double rowEnd, double canvasHeight, double zoom,
        double pixelsPerMinute)
    {
        var fontSize = Math.Clamp(9 * zoom, 8.5, 11);
        var measurement = new TextBlock
        {
            Text = "00:00",
            FontFamily = FontFamily,
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap
        };
        measurement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = Math.Ceiling(measurement.DesiredSize.Width);
        var labelInterval = ChooseTimeLabelInterval(pixelsPerMinute, labelWidth);
        var labels = new List<(TextBlock Element, double Right)>();
        var firstTick = Math.Ceiling(rowStart / 30) * 30;
        for (var minute = firstTick; minute <= rowEnd + 0.1; minute += 30)
        {
            var x = (minute - rowStart) * pixelsPerMinute;
            var major = ((int)Math.Round(minute)) % 60 == 0;
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 24,
                Y2 = canvasHeight,
                Stroke = major ? GridLineBrush : HalfHourBrush,
                StrokeThickness = major ? 1 : 0.8
            };
            canvas.Children.Add(line);

            var roundedMinute = (int)Math.Round(minute);
            var isRowEnd = Math.Abs(minute - rowEnd) < 0.1;
            if (roundedMinute % labelInterval != 0 && !isRowEnd) continue;

            var left = isRowEnd ? x - labelWidth : x + 3;
            left = Math.Clamp(left, 0, Math.Max(0, canvas.Width - labelWidth));
            if (!isRowEnd && left + labelWidth > canvas.Width - 3) continue;
            if (labels.Count > 0 && left < labels[^1].Right + 2)
            {
                if (!isRowEnd) continue;
                canvas.Children.Remove(labels[^1].Element);
                labels.RemoveAt(labels.Count - 1);
            }

            var label = new TextBlock
            {
                Text = FormatMinute(minute),
                Width = labelWidth,
                FontSize = fontSize,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = isRowEnd ? TextAlignment.Right : TextAlignment.Left
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, 5);
            canvas.Children.Add(label);
            labels.Add((label, left + labelWidth));
        }
    }

    private static int ChooseTimeLabelInterval(
        double pixelsPerMinute, double labelWidth)
    {
        var minimumSpacing = labelWidth * 2 + 4;
        foreach (var interval in new[] { 30, 60, 120, 180, 240, 360, 720 })
            if (interval * pixelsPerMinute >= minimumSpacing) return interval;
        return 1440;
    }

    private void AddCurrentTimeLine(
        Canvas canvas, double rowStart, double rowEnd, double height,
        double pixelsPerMinute)
    {
        if (_viewModel.Date != DateOnly.FromDateTime(DateTime.Today)) return;
        var now = DateTime.Now;
        var minute = now.Hour * 60 + now.Minute;
        if (minute < rowStart || minute > rowEnd) return;
        var x = (minute - rowStart) * pixelsPerMinute;
        var line = new Line
        {
            X1 = x,
            X2 = x,
            Y1 = 23,
            Y2 = height,
            Stroke = CurrentTimeBrush,
            StrokeThickness = 1.5,
            Opacity = 0.88
        };
        Panel.SetZIndex(line, 30);
        canvas.Children.Add(line);
        var label = new Border
        {
            Background = CurrentTimeBrush,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = now.ToString("HH:mm", CultureInfo.CurrentCulture),
                Foreground = Brushes.White,
                FontSize = 9
            }
        };
        Canvas.SetLeft(label, Math.Clamp(x - 18, 0, Math.Max(0, canvas.Width - 42)));
        Canvas.SetTop(label, 23);
        Panel.SetZIndex(label, 31);
        canvas.Children.Add(label);
    }

    private Border CreateEventBar(
        TimelineSlice slice,
        double segmentStart,
        double segmentEnd,
        bool startsHere,
        bool endsHere,
        double width,
        double height,
        double zoom,
        bool showName)
    {
        var colors = GetEventColors(slice.Item);
        var grid = new Grid();
        var text = new TextBlock
        {
            Foreground = colors.Ink,
            FontSize = Math.Clamp(10 * zoom, 9, 13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7 * zoom, 0, 7 * zoom, 0)
        };
        RenderEventBarText(
            text, slice.Item, slice.StartMinute, slice.EndMinute, width, showName);
        if (width >= 30) grid.Children.Add(text);
        if (startsHere) grid.Children.Add(CreateBoundaryHandle(HorizontalAlignment.Left, colors.Ink, zoom));
        if (endsHere) grid.Children.Add(CreateBoundaryHandle(HorizontalAlignment.Right, colors.Ink, zoom));

        var tag = new BarSegmentTag(
            slice.Item, startsHere, endsHere, segmentStart, segmentEnd);
        var bar = new Border
        {
            Tag = tag,
            Width = width,
            Height = height,
            Background = colors.Fill,
            CornerRadius = new CornerRadius(
                startsHere ? 5 * zoom : 2,
                endsHere ? 5 * zoom : 2,
                endsHere ? 5 * zoom : 2,
                startsHere ? 5 * zoom : 2),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = BuildEventTooltip(slice.Item)
        };
        bar.MouseEnter += (_, _) =>
        {
            SetItemHighlight(slice.Item.Id, true, bar);
            ShowFileLinksPopup(slice.Item, bar);
        };
        bar.MouseLeave += (_, _) =>
        {
            SetItemHighlight(slice.Item.Id, false);
            ScheduleFileLinksPopupClose();
        };
        bar.PreviewMouseLeftButtonDown += OnEventBarMouseLeftButtonDown;
        AddItemVisual(
            slice.Item.Id, bar, colors.Fill, colors.Hover,
            text, slice.Item, slice.StartMinute, slice.EndMinute, width, showName);
        return bar;
    }

    private static Border CreateBoundaryHandle(
        HorizontalAlignment alignment, Brush brush, double zoom) => new()
    {
        Width = Math.Clamp(7 * zoom, 6, 10),
        Margin = new Thickness(1),
        HorizontalAlignment = alignment,
        Background = brush,
        Opacity = 0.28,
        CornerRadius = new CornerRadius(3),
        ToolTip = alignment == HorizontalAlignment.Left
            ? "拖动修改开始时间（精确到分钟）"
            : "拖动修改结束时间（精确到分钟）"
    };

    private async void OnEventBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: BarSegmentTag tag } bar
            || e.LeftButton != MouseButtonState.Pressed)
            return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            if (e.ClickCount == 1) await ToggleCompletedFromShortcutAsync(tag.Item);
            return;
        }
        var point = e.GetPosition(bar);
        var handleWidth = Math.Clamp(13 * (_pixelsPerHour / DefaultPixelsPerHour), 11, 18);
        bool? updateStart = null;
        if (tag.CanDragStart && point.X <= handleWidth) updateStart = true;
        else if (tag.CanDragEnd && point.X >= bar.ActualWidth - handleWidth) updateStart = false;

        if (updateStart is not null)
        {
            BeginBoundaryDrag(tag.Item, updateStart.Value);
            return;
        }
        if (e.ClickCount == 2)
        {
            if (tag.Item.TaskBatch is not null) ShowTaskEditor(tag.Item);
            else ShowEditor(tag.Item.Kind, tag.Item);
            return;
        }
        BeginToss(tag.Item, bar, TimelineScrollViewer, sensitive: false);
    }

    private async Task ToggleCompletedFromShortcutAsync(CalendarItem item)
    {
        if (!_completionChanges.Add(item.Id)) return;
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

    private void OnIdeaCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CalendarItem item } card
            || e.LeftButton != MouseButtonState.Pressed
            || IsInsideCheckBox(e.OriginalSource as DependencyObject))
            return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ShowEditor(CalendarItemKind.Idea, item);
            return;
        }
        BeginToss(item, card, IdeaPanel, sensitive: true);
    }

    private void OnInspirationCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CalendarItem item } card
            || e.LeftButton != MouseButtonState.Pressed)
            return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ShowEditor(CalendarItemKind.Inspiration, item);
            return;
        }
        BeginToss(item, card, InspirationPanel, sensitive: true);
    }

    private void BeginBoundaryDrag(CalendarItem item, bool updateStart)
    {
        CancelToss(animate: false);
        _boundaryDrag = new BoundaryDragState(item, updateStart,
            updateStart ? item.StartsAt : EffectiveEnd(item));
        Mouse.Capture(this);
        ShowInteractionStatus(
            $"{item.Kind} {(updateStart ? "开始" : "结束")}时间：{FormatBoundary(_boundaryDrag.Preview)} · 松开鼠标保存",
            delete: false);
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tossState is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateToss(e);
            e.Handled = true;
            return;
        }
        if (_boundaryDrag is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!TryGetMinuteFromPointer(e, out var minute)) return;
        var boundary = AtMinute(minute);
        if (!IsValidBoundary(_boundaryDrag, boundary)
            || boundary == _boundaryDrag.Preview)
            return;
        _boundaryDrag = _boundaryDrag with { Preview = boundary };
        ShowInteractionStatus(
            $"{_boundaryDrag.Item.Kind} {(_boundaryDrag.UpdateStart ? "开始" : "结束")}时间：{FormatBoundary(boundary)} · 松开鼠标保存",
            delete: false);
        RenderTimeline();
        e.Handled = true;
    }

    private async void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_tossState is not null)
        {
            await CompleteTossAsync();
            e.Handled = true;
            return;
        }
        if (_boundaryDrag is null) return;
        await CompleteBoundaryDragAsync();
        e.Handled = true;
    }

    private async Task CompleteBoundaryDragAsync()
    {
        if (_boundaryDrag is not { } state) return;
        _boundaryDrag = null;
        Mouse.Capture(null);
        HideInteractionStatus();
        try
        {
            await _viewModel.UpdateBoundaryAsync(
                state.Item, state.UpdateStart, state.Preview);
        }
        catch (Exception exception)
        {
            ShowError("无法更新时间边界。", exception);
            RenderAll();
        }
    }

    private bool IsValidBoundary(BoundaryDragState state, DateTimeOffset boundary)
    {
        if (state.UpdateStart)
        {
            var end = EffectiveEnd(state.Item);
            return boundary <= end;
        }
        if (boundary < state.Item.StartsAt) return false;
        if (state.Item.Kind == CalendarItemKind.Key)
        {
            var latest = AtLocalMidnight(
                DateOnly.FromDateTime(state.Item.StartsAt.LocalDateTime).AddDays(1));
            return boundary <= latest;
        }
        return true;
    }

    private void BeginToss(
        CalendarItem item, Border visible, FrameworkElement deleteSurface, bool sensitive)
    {
        CancelBoundaryDrag();
        var rotation = new RotateTransform();
        var translation = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(rotation);
        group.Children.Add(translation);
        visible.RenderTransformOrigin = new Point(0.5, 0.5);
        visible.RenderTransform = group;
        _tossState = new TossState(
            item, visible, deleteSurface, eStart: Mouse.GetPosition(this),
            translation, rotation, sensitive);
        Mouse.Capture(this);
    }

    private void UpdateToss(MouseEventArgs e)
    {
        if (_tossState is not { } state) return;
        var point = e.GetPosition(this);
        var deltaX = point.X - state.StartPoint.X;
        var deltaY = point.Y - state.StartPoint.Y;
        if (!state.IsDragging
            && Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
            return;

        state.IsDragging = true;
        state.Translation.X = deltaX;
        state.Translation.Y = deltaY;
        state.Rotation.Angle = Math.Clamp(deltaX / 24, -11, 11);
        Panel.SetZIndex(state.Visual, 80);

        Rect bounds;
        try
        {
            bounds = state.Visual.TransformToVisual(state.DeleteSurface)
                .TransformBounds(new Rect(state.Visual.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return;
        }
        var outsideX = Math.Max(Math.Max(0, -bounds.Left),
            Math.Max(0, bounds.Right - state.DeleteSurface.ActualWidth));
        var outsideY = Math.Max(Math.Max(0, -bounds.Top),
            Math.Max(0, bounds.Bottom - state.DeleteSurface.ActualHeight));
        var threshold = state.Sensitive ? 0.30 : 0.50;
        var armed = outsideX >= Math.Max(10, bounds.Width * threshold)
            || outsideY >= Math.Max(8, bounds.Height * threshold);
        if (armed != state.DeleteArmed)
        {
            state.DeleteArmed = armed;
            state.Visual.Opacity = armed ? 0.48 : 0.86;
            SetItemGroupOpacity(state.Item.Id, state.Visual, armed ? 0.28 : 0.62);
        }
        ShowInteractionStatus(
            armed
                ? $"松开以丢弃 {state.Item.Kind}：{state.Item.Title} · 可在详细日程表中撤回"
                : $"将{(state.Item.Kind is CalendarItemKind.Idea or CalendarItemKind.Inspiration ? "便签" : "色块")}甩出工作区边界以删除",
            delete: armed);
    }

    private async Task CompleteTossAsync()
    {
        if (_tossState is not { } state) return;
        _tossState = null;
        Mouse.Capture(null);
        HideInteractionStatus();
        if (!state.IsDragging || !state.DeleteArmed)
        {
            ReturnToss(state);
            return;
        }

        try
        {
            await AnimateTossOutAsync(state);
            await _viewModel.DeleteAsync(state.Item);
        }
        catch (Exception exception)
        {
            ResetTossVisual(state);
            ShowError($"无法删除 {state.Item.Kind}。", exception);
            RenderAll();
        }
    }

    private static async Task AnimateTossOutAsync(TossState state)
    {
        var completed = new TaskCompletionSource<bool>();
        var duration = new Duration(TimeSpan.FromMilliseconds(230));
        var easing = new QuarticEase { EasingMode = EasingMode.EaseIn };
        var direction = state.Translation.X < 0 ? -1 : 1;
        var targetX = state.Translation.X + direction * 520;
        var animation = new DoubleAnimation(state.Translation.X, targetX, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) => completed.TrySetResult(true);
        state.Translation.BeginAnimation(TranslateTransform.XProperty, animation);
        state.Rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(state.Rotation.Angle, direction * 16, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
        state.Visual.BeginAnimation(OpacityProperty,
            new DoubleAnimation(state.Visual.Opacity, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
        await completed.Task;
    }

    private void ReturnToss(TossState state)
    {
        if (!state.IsDragging)
        {
            ResetTossVisual(state);
            return;
        }
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        var x = new DoubleAnimation(state.Translation.X, 0, duration) { EasingFunction = easing };
        x.Completed += (_, _) => ResetTossVisual(state);
        state.Translation.BeginAnimation(TranslateTransform.XProperty, x);
        state.Translation.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(state.Translation.Y, 0, duration) { EasingFunction = easing });
        state.Rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(state.Rotation.Angle, 0, duration) { EasingFunction = easing });
        state.Visual.BeginAnimation(OpacityProperty,
            new DoubleAnimation(state.Visual.Opacity, 1, duration) { EasingFunction = easing });
    }

    private void CancelToss(bool animate)
    {
        if (_tossState is not { } state) return;
        _tossState = null;
        Mouse.Capture(null);
        HideInteractionStatus();
        if (animate) ReturnToss(state);
        else ResetTossVisual(state);
    }

    private void ResetTossVisual(TossState state)
    {
        state.Translation.BeginAnimation(TranslateTransform.XProperty, null);
        state.Translation.BeginAnimation(TranslateTransform.YProperty, null);
        state.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        state.Visual.BeginAnimation(OpacityProperty, null);
        state.Visual.RenderTransform = null;
        state.Visual.Opacity = 1;
        Panel.SetZIndex(state.Visual, 0);
        SetItemGroupOpacity(state.Item.Id, state.Visual, 1);
    }

    private void CancelBoundaryDrag()
    {
        if (_boundaryDrag is null) return;
        _boundaryDrag = null;
        Mouse.Capture(null);
        HideInteractionStatus();
        RenderTimeline();
    }

    private void CancelActiveInteraction()
    {
        CancelBoundaryDrag();
        CancelToss(animate: false);
    }

    private void OnTimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_boundaryDrag is not null || _tossState is not null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            if (RotateHiddenItemsAtPointer(e, e.Delta < 0 ? 1 : -1))
                e.Handled = true;
            return;
        }

        var oldVisibleMinutes = VisibleMinutes;
        var anchor = TryGetMinuteFromPointer(e, out var minute)
            ? minute
            : _viewStartMinute + oldVisibleMinutes / 2;
        var anchorRatio = Math.Clamp(
            (anchor - _viewStartMinute) / oldVisibleMinutes, 0, 1);
        var factor = e.Delta > 0 ? 1.11 : 1 / 1.11;
        var next = Math.Clamp(_pixelsPerHour * factor,
            DefaultPixelsPerHour, MaximumPixelsPerHour);
        if (Math.Abs(next - _pixelsPerHour) < 0.01)
        {
            e.Handled = true;
            return;
        }
        _pixelsPerHour = next;
        var newVisibleMinutes = VisibleMinutes;
        _viewStartMinute = ClampViewStart(
            anchor - anchorRatio * newVisibleMinutes, newVisibleMinutes);
        RenderTimeline();
        e.Handled = true;
    }

    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _middleDown = true;
        _middleMoved = false;
        _middleStart = e.GetPosition(this);
        _middleStartViewMinute = _viewStartMinute;
        TimelineScrollViewer.Cursor = Cursors.SizeAll;
        TimelineScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (!_middleDown || e.MiddleButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        var delta = point - _middleStart;
        if (Math.Abs(delta.X) > 3 || Math.Abs(delta.Y) > 3) _middleMoved = true;
        if (_middleMoved && _rows.Count > 0)
        {
            var row = _rows[0];
            var minutesPerRow = row.EndMinute - row.StartMinute;
            var horizontalMinutes = delta.X / Math.Max(1, row.Canvas.ActualWidth)
                * minutesPerRow;
            var verticalMinutes = delta.Y / Math.Max(1, row.Canvas.ActualHeight)
                * minutesPerRow;
            _viewStartMinute = ClampViewStart(
                _middleStartViewMinute - horizontalMinutes - verticalMinutes,
                VisibleMinutes);
            RenderTimeline();
        }
        e.Handled = true;
    }

    private void OnTimelineMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_middleDown) return;
        _middleDown = false;
        TimelineScrollViewer.ReleaseMouseCapture();
        TimelineScrollViewer.Cursor = Cursors.Arrow;
        if (!_middleMoved)
        {
            _pixelsPerHour = DefaultPixelsPerHour;
            ScrollToRelevantTime();
        }
        e.Handled = true;
    }

    private bool RotateHiddenItemsAtPointer(MouseEventArgs e, int direction)
    {
        if (!TryGetMinuteFromPointer(e, out var minute)) return false;
        var slices = BuildSlices();
        var activeSlices = GetActiveSlices(slices, minute);
        if (activeSlices.Length <= _visibleItemCapacity)
        {
            var visibleStart = _rows.Count == 0 ? _viewStartMinute : _rows.Min(row => row.StartMinute);
            var visibleEnd = _rows.Count == 0
                ? _viewStartMinute + VisibleMinutes
                : _rows.Max(row => row.EndMinute);
            var nearest = BuildOverflowSnapshots(slices, _visibleItemCapacity)
                .Where(snapshot => snapshot.EndMinute > visibleStart
                                   && snapshot.StartMinute < visibleEnd)
                .OrderBy(snapshot => DistanceToInterval(
                    minute, snapshot.StartMinute, snapshot.EndMinute))
                .FirstOrDefault();
            if (nearest is null) return false;
            activeSlices = nearest.Active;
        }

        var active = activeSlices
            .Select(slice => slice.Item.Id)
            .Distinct()
            .ToArray();

        var current = _overlapSelection is { } selection
                      && selection.ActiveIds.SetEquals(active)
            ? selection.Offset
            : 0;
        var offset = Math.Clamp(
            current + direction, 0, Math.Max(0, active.Length - _visibleItemCapacity));
        if (_overlapSelection is null
            || !_overlapSelection.ActiveIds.SetEquals(active)
            || _overlapSelection.Offset != offset)
        {
            _overlapSelection = new OverlapSelection(active.ToHashSet(), offset);
            RenderTimeline();
        }
        return true;
    }

    private TimelineSlice[] GetActiveSlices(
        IEnumerable<TimelineSlice> slices,
        double minute) =>
        slices.Where(slice => slice.StartMinute <= minute && slice.EndMinute > minute)
            .OrderBy(slice => PriorityGroup(slice.Item))
            .ThenBy(slice => PriorityTime(slice.Item))
            .ThenBy(slice => slice.Item.CreatedAt)
            .ToArray();

    private static double DistanceToInterval(double value, double start, double end) =>
        value < start ? start - value : value > end ? value - end : 0;

    private bool TryGetMinuteFromPointer(MouseEventArgs e, out double minute)
    {
        minute = 0;
        if (_rows.Count == 0) return false;
        RowVisual? closest = null;
        var closestDistance = double.MaxValue;
        Point closestPoint = default;
        foreach (var row in _rows)
        {
            var point = e.GetPosition(row.Canvas);
            var distance = point.Y < 0 ? -point.Y
                : point.Y > row.Canvas.ActualHeight ? point.Y - row.Canvas.ActualHeight
                : 0;
            if (distance >= closestDistance) continue;
            closest = row;
            closestPoint = point;
            closestDistance = distance;
        }
        if (closest is null) return false;
        var x = Math.Clamp(closestPoint.X, 0, closest.Canvas.ActualWidth);
        minute = Math.Clamp(
            closest.StartMinute + Math.Round(x / closest.PixelsPerMinute),
            closest.StartMinute, closest.EndMinute);
        return true;
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_loaded || _renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _renderQueued = false;
            RenderTimeline();
        }, DispatcherPriority.Background);
    }

    private double VisibleMinutes =>
        Math.Clamp(1440 * DefaultPixelsPerHour / _pixelsPerHour, 1, 1440);

    private static double ClampViewStart(double value, double visibleMinutes) =>
        Math.Clamp(value, 0, Math.Max(0, 1440 - visibleMinutes));

    private IReadOnlyList<TimelineSlice> BuildSlices()
    {
        var dayStart = AtLocalMidnight(_viewModel.Date);
        var dayEnd = AtLocalMidnight(_viewModel.Date.AddDays(1));
        var result = new List<TimelineSlice>();
        foreach (var original in _viewModel.Items.Where(item =>
                     item.Kind is CalendarItemKind.Class or CalendarItemKind.Key))
        {
            var item = ApplyBoundaryPreview(original);
            var effectiveEnd = EffectiveEnd(item);
            var start = item.StartsAt < dayStart ? dayStart : item.StartsAt;
            var end = effectiveEnd > dayEnd ? dayEnd : effectiveEnd;
            if (start >= dayEnd || end <= dayStart) continue;
            var startMinute = LocalMinutesFrom(dayStart, start);
            var endMinute = LocalMinutesFrom(dayStart, end);
            if (endMinute <= startMinute) endMinute = Math.Min(1440, startMinute + 1);
            result.Add(new TimelineSlice(item, startMinute, endMinute));
        }
        return result
            .OrderBy(slice => PriorityGroup(slice.Item))
            .ThenBy(slice => PriorityTime(slice.Item))
            .ThenBy(slice => slice.Item.CreatedAt)
            .ToArray();
    }

    private CalendarItem ApplyBoundaryPreview(CalendarItem item)
    {
        if (_boundaryDrag is not { } drag || drag.Item.Id != item.Id) return item;
        return drag.UpdateStart
            ? item with { StartsAt = drag.Preview }
            : item with { EndsAt = drag.Preview };
    }

    private IReadOnlyList<TimelineSlice> SelectVisibleSlices(
        IReadOnlyList<TimelineSlice> source,
        int limit)
    {
        var ordered = source
            .OrderBy(slice => PriorityGroup(slice.Item))
            .ThenBy(slice => PriorityTime(slice.Item))
            .ThenBy(slice => slice.Item.CreatedAt)
            .ToList();

        if (_overlapSelection is { } selection)
        {
            var active = ordered
                .Where(slice => selection.ActiveIds.Contains(slice.Item.Id))
                .ToList();
            if (active.Count == selection.ActiveIds.Count && active.Count > limit)
            {
                var offset = Math.Clamp(selection.Offset, 0, active.Count - limit);
                var selected = active.Skip(offset).Take(limit).ToList();
                ordered = selected
                    .Concat(ordered.Where(slice => !selection.ActiveIds.Contains(slice.Item.Id)))
                    .ToList();
            }
        }

        var visible = new List<TimelineSlice>();
        foreach (var candidate in ordered)
        {
            if (WouldExceedConcurrency(candidate, visible, limit)) continue;
            visible.Add(candidate);
        }
        return visible;
    }

    private static bool WouldExceedConcurrency(
        TimelineSlice candidate,
        IReadOnlyList<TimelineSlice> visible,
        int limit)
    {
        var points = visible
            .Where(other => Overlaps(candidate, other))
            .Select(other => Math.Max(candidate.StartMinute, other.StartMinute))
            .Append(candidate.StartMinute)
            .Distinct();
        return points.Any(point =>
            visible.Count(other => other.StartMinute <= point && other.EndMinute > point) >= limit);
    }

    private IReadOnlyDictionary<Guid, int> AssignLanes(
        IReadOnlyList<TimelineSlice> slices)
    {
        var lanes = new List<List<TimelineSlice>>();
        var result = new Dictionary<Guid, int>();
        foreach (var slice in slices
                     .OrderBy(value => PriorityGroup(value.Item))
                     .ThenBy(value => PriorityTime(value.Item))
                     .ThenBy(value => value.Item.CreatedAt))
        {
            var lane = lanes.FindIndex(existing => existing.All(other => !Overlaps(slice, other)));
            if (lane < 0)
            {
                lane = lanes.Count;
                lanes.Add(new List<TimelineSlice>());
            }
            lanes[lane].Add(slice);
            result[slice.Item.Id] = lane;
        }
        return result;
    }

    private IReadOnlyList<OverflowMarker> BuildOverflowMarkers(
        IReadOnlyList<TimelineSlice> all,
        int limit)
    {
        var result = new List<OverflowMarker>();
        var region = new List<OverflowSnapshot>();
        foreach (var snapshot in BuildOverflowSnapshots(all, limit))
        {
            if (region.Count > 0
                && snapshot.StartMinute > region[^1].EndMinute + 0.01)
            {
                AddOverflowRegionMarkers(result, region, limit);
                region.Clear();
            }
            region.Add(snapshot);
        }
        AddOverflowRegionMarkers(result, region, limit);
        return result;
    }

    private IReadOnlyList<OverflowSnapshot> BuildOverflowSnapshots(
        IReadOnlyList<TimelineSlice> all,
        int limit)
    {
        var boundaries = all.Select(slice => slice.StartMinute)
            .Concat(all.Select(slice => slice.EndMinute))
            .Distinct()
            .Order()
            .ToArray();
        var result = new List<OverflowSnapshot>();
        for (var index = 0; index + 1 < boundaries.Length; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            if (end <= start) continue;
            var active = GetActiveSlices(all, start + (end - start) / 2);
            if (active.Length > limit)
                result.Add(new OverflowSnapshot(start, end, active));
        }
        return result;
    }

    private void AddOverflowRegionMarkers(
        ICollection<OverflowMarker> result,
        IReadOnlyList<OverflowSnapshot> region,
        int limit)
    {
        if (region.Count == 0) return;
        var selectedSnapshots = region.Where(snapshot =>
            _overlapSelection is { } selection
            && selection.ActiveIds.SetEquals(snapshot.Active.Select(slice => slice.Item.Id)))
            .ToArray();
        var candidates = selectedSnapshots.Length > 0 ? selectedSnapshots : region;
        var minute = region[0].StartMinute;

        var above = candidates
            .Select(snapshot => CreateOverflowMarker(
                minute, above: true, snapshot.Active.Take(GetOverlapOffset(snapshot, limit))))
            .OfType<OverflowMarker>()
            .OrderByDescending(MarkerHiddenCount)
            .FirstOrDefault();
        var below = candidates
            .Select(snapshot =>
            {
                var offset = GetOverlapOffset(snapshot, limit);
                return CreateOverflowMarker(
                    minute, above: false, snapshot.Active.Skip(offset + limit));
            })
            .OfType<OverflowMarker>()
            .OrderByDescending(MarkerHiddenCount)
            .FirstOrDefault();
        if (above is not null) result.Add(above);
        if (below is not null) result.Add(below);
    }

    private int GetOverlapOffset(OverflowSnapshot snapshot, int limit) =>
        _overlapSelection is { } selection
        && selection.ActiveIds.SetEquals(snapshot.Active.Select(slice => slice.Item.Id))
            ? Math.Clamp(selection.Offset, 0, snapshot.Active.Length - limit)
            : 0;

    private static OverflowMarker? CreateOverflowMarker(
        double minute,
        bool above,
        IEnumerable<TimelineSlice> hidden)
    {
        var hiddenItems = hidden.ToArray();
        if (hiddenItems.Length == 0) return null;
        return new OverflowMarker(
            minute,
            above,
            hiddenItems.Count(slice => slice.Item.Kind == CalendarItemKind.Class),
            hiddenItems.Count(slice => slice.Item.Kind == CalendarItemKind.Key));
    }

    private static int MarkerHiddenCount(OverflowMarker marker) =>
        marker.HiddenClassCount + marker.HiddenKeyCount;

    private static string FormatOverflowMarker(OverflowMarker marker)
    {
        var parts = new List<string>(2);
        if (marker.HiddenClassCount > 0) parts.Add($"+{marker.HiddenClassCount} Class");
        if (marker.HiddenKeyCount > 0) parts.Add($"+{marker.HiddenKeyCount} Key");
        return $"{(marker.Above ? "↑" : "↓")} {string.Join(" · ", parts)}";
    }

    private int PriorityGroup(CalendarItem item)
    {
        if (item.IsCompleted) return int.MaxValue;
        if (item.Kind == CalendarItemKind.Key) return 1;
        if (item.EndsAt is { } end)
        {
            var dayStart = AtLocalMidnight(_viewModel.Date);
            var dayEnd = AtLocalMidnight(_viewModel.Date.AddDays(1));
            if (end > dayStart && end <= dayEnd) return 0;
        }
        return 2;
    }

    private static DateTimeOffset PriorityTime(CalendarItem item) =>
        item.Kind == CalendarItemKind.Key
            ? item.StartsAt
            : item.EndsAt ?? DateTimeOffset.MaxValue;

    private DateTimeOffset EffectiveEnd(CalendarItem item)
    {
        if (item.EndsAt is { } end) return end;
        if (item.Kind == CalendarItemKind.Key)
        {
            var nextMidnight = AtLocalMidnight(
                DateOnly.FromDateTime(item.StartsAt.LocalDateTime).AddDays(1));
            var proposed = item.StartsAt.AddHours(2);
            return proposed < nextMidnight ? proposed : nextMidnight;
        }
        return AtLocalMidnight(_viewModel.Date.AddDays(1));
    }

    private bool IsTrueStartOnDate(CalendarItem item)
    {
        var start = AtLocalMidnight(_viewModel.Date);
        var end = AtLocalMidnight(_viewModel.Date.AddDays(1));
        return item.StartsAt >= start && item.StartsAt < end;
    }

    private bool IsTrueEndOnDate(CalendarItem item)
    {
        var endValue = EffectiveEnd(item);
        var start = AtLocalMidnight(_viewModel.Date);
        var end = AtLocalMidnight(_viewModel.Date.AddDays(1));
        return endValue > start && endValue <= end;
    }

    private void ShowEditor(CalendarItemKind kind, CalendarItem? item)
    {
        if (_taskEditor is { IsVisible: true } taskEditor)
        {
            taskEditor.Activate();
            return;
        }
        if (_editor is { IsVisible: true })
        {
            _editor.Activate();
            return;
        }

        var editor = item is null
            ? new CalendarItemEditorWindow(kind, _viewModel.Date)
            : new CalendarItemEditorWindow(item);
        editor.Tag = item;
        _editor = editor;
        _editorExternallyHandled = false;
        editor.Owner = this;
        ((App)System.Windows.Application.Current).RegisterCompanionWindow(editor);
        editor.Closed += async (_, _) =>
        {
            ((App)System.Windows.Application.Current).UnregisterCompanionWindow(editor);
            if (ReferenceEquals(_editor, editor)) _editor = null;
            if (_editorExternallyHandled || editor.Draft is null) return;
            try
            {
                await SaveEditorDraftAsync(editor, editor.Draft);
            }
            catch (Exception exception)
            {
                ShowError("无法保存日程。", exception);
            }
        };
        editor.Show();
        editor.Activate();
    }

    private void ShowTaskEditor(CalendarItem? occurrence = null)
    {
        if (_editor is { IsVisible: true } calendarEditor)
        {
            calendarEditor.Activate();
            return;
        }
        if (_taskEditor is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        var editor = occurrence is null
            ? new KeyTaskEditorWindow(_viewModel.Date)
            : new KeyTaskEditorWindow(occurrence);
        editor.Owner = this;
        _taskEditor = editor;
        _taskEditorOriginal = occurrence;
        _taskEditorExternallyHandled = false;
        ((App)System.Windows.Application.Current).RegisterCompanionWindow(editor);
        editor.Closed += async (_, _) =>
        {
            ((App)System.Windows.Application.Current).UnregisterCompanionWindow(editor);
            if (ReferenceEquals(_taskEditor, editor))
            {
                _taskEditor = null;
                _taskEditorOriginal = null;
            }
            if (_taskEditorExternallyHandled || editor.Schedule is null) return;
            try
            {
                if (occurrence is not null)
                    await _viewModel.EditKeyTaskAsync(occurrence, editor.Schedule);
                else
                    await _viewModel.CreateKeyTaskAsync(editor.Schedule);
            }
            catch (Exception exception)
            {
                ShowError("无法保存 Task 批次。", exception);
            }
        };
        editor.Show();
        editor.Activate();
    }

    private Task SaveEditorDraftAsync(
        CalendarItemEditorWindow editor,
        DiaryOfMine.Application.Calendar.CalendarItemDraft draft) =>
        editor.Tag is CalendarItem original
            ? _viewModel.EditAsync(original, draft)
            : _viewModel.CreateAsync(draft);

    private async void OnPreviousDayClick(object sender, RoutedEventArgs e) =>
        await NavigateToAsync(_viewModel.Date.AddDays(-1));

    private async void OnNextDayClick(object sender, RoutedEventArgs e) =>
        await NavigateToAsync(_viewModel.Date.AddDays(1));

    private async void OnTodayClick(object sender, RoutedEventArgs e) =>
        await NavigateToAsync(DateOnly.FromDateTime(DateTime.Today));

    private async void OnReturnToCalendarClick(object sender, RoutedEventArgs e) =>
        await SavePendingAndCloseAsync();

    private void OnAddClassClick(object sender, RoutedEventArgs e) =>
        ShowEditor(CalendarItemKind.Class, null);

    private void OnAddKeyClick(object sender, RoutedEventArgs e) =>
        ShowEditor(CalendarItemKind.Key, null);

    private void OnAddIdeaClick(object sender, RoutedEventArgs e) =>
        ShowEditor(CalendarItemKind.Idea, null);

    private void OnAddInspirationClick(object sender, RoutedEventArgs e) =>
        ShowEditor(CalendarItemKind.Inspiration, null);

    private void OnOpenDiaryClick(object sender, RoutedEventArgs e) =>
        DiaryRequested?.Invoke(_viewModel.Date);

    private void OnAddTaskClick(object sender, RoutedEventArgs e) => ShowTaskEditor();

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.UndoAsync(); }
        catch (Exception exception) { ShowError("无法撤回详细日程表操作。", exception); }
    }

    private async void OnRedoClick(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.RedoAsync(); }
        catch (Exception exception) { ShowError("无法重做详细日程表操作。", exception); }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (_viewModel.Date == DateOnly.FromDateTime(DateTime.Today))
            RenderTimeline();
    }

    private void ScrollToRelevantTime()
    {
        double minute;
        if (_viewModel.Date == DateOnly.FromDateTime(DateTime.Today))
        {
            minute = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        }
        else
        {
            var slices = BuildSlices();
            var unfinished = slices.Where(slice => !slice.Item.IsCompleted).ToArray();
            minute = (unfinished.Length > 0 ? unfinished : slices.ToArray())
                .Select(slice => slice.StartMinute)
                .DefaultIfEmpty(0)
                .Min();
        }
        ScrollToMinute(minute);
    }

    private void ScrollToMinute(double minute)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _viewStartMinute = ClampViewStart(
                minute - VisibleMinutes / 2, VisibleMinutes);
            RenderTimeline();
        }, DispatcherPriority.ContextIdle);
    }

    private void ShowInteractionStatus(string message, bool delete)
    {
        InteractionStatusText.Text = message;
        InteractionStatus.Background = BrushFrom(delete ? "#FFF3EF" : "#FFF8ED");
        InteractionStatus.BorderBrush = BrushFrom(delete ? "#E7B8AA" : "#E5D3B6");
        InteractionStatus.Visibility = Visibility.Visible;
    }

    private void HideInteractionStatus() => InteractionStatus.Visibility = Visibility.Collapsed;

    private void AddItemVisual(
        Guid id, Border visual, Brush normal, Brush hover,
        TextBlock? label = null,
        CalendarItem? textItem = null,
        double itemStart = 0,
        double itemEnd = 0,
        double textWidth = 0,
        bool normalShowsName = false)
    {
        if (!_itemVisuals.TryGetValue(id, out var values))
        {
            values = new List<EventVisual>();
            _itemVisuals[id] = values;
        }
        values.Add(new EventVisual(
            visual, normal, hover, label, textItem,
            itemStart, itemEnd, textWidth, normalShowsName));
    }

    private void SetItemHighlight(Guid id, bool highlighted, Border? hovered = null)
    {
        if (!_itemVisuals.TryGetValue(id, out var visuals)) return;
        foreach (var visual in visuals)
        {
            visual.Element.Background = highlighted ? visual.Hover : visual.Normal;
            if (visual.Label is null || visual.TextItem is null) continue;
            var showName = visual.NormalShowsName
                || highlighted && ReferenceEquals(visual.Element, hovered);
            RenderEventBarText(
                visual.Label, visual.TextItem, visual.ItemStart, visual.ItemEnd,
                visual.TextWidth, showName);
        }
    }

    private void SetItemGroupOpacity(Guid id, Border except, double opacity)
    {
        if (!_itemVisuals.TryGetValue(id, out var visuals)) return;
        foreach (var visual in visuals)
            if (!ReferenceEquals(visual.Element, except)) visual.Element.Opacity = opacity;
    }

    private (Brush Fill, Brush Ink, Brush Hover) GetEventColors(CalendarItem item)
    {
        if (item.IsCompleted)
            return (CompletedFillBrush, CompletedInkBrush, CompletedHoverBrush);
        var palette = item.Kind == CalendarItemKind.Class
            ? GetClassPaletteColor(item.ColorKey)
            : GetKeyPaletteColor(item.ColorKey);
        var hover = Blend(palette, Colors.Black, 0.08);
        return (BrushFrom(palette), BrushFrom(Blend(palette, Colors.Black, 0.42)), BrushFrom(hover));
    }

    private Color GetClassPaletteColor(string key)
    {
        var settings = _viewModel.Settings;
        return ParseColor(CalendarPalette.ResolveColor(
            settings, CalendarItemKind.Class, key));
    }

    private Color GetKeyPaletteColor(string key)
    {
        var settings = _viewModel.Settings;
        return ParseColor(CalendarPalette.ResolveColor(
            settings, CalendarItemKind.Key, key));
    }

    private object BuildEventTooltip(CalendarItem item)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{(item.TaskBatch is null ? item.Kind.ToString() : "Task Key")} · 色槽 {CalendarPalette.GetSlotNumber(item.ColorKey)}",
            Foreground = BrushFrom("#3D6B57"),
            FontSize = 10
        });
        panel.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0)
        });
        var start = item.StartsAt.LocalDateTime.ToString("yyyy/M/d HH:mm", CultureInfo.CurrentCulture);
        var effectiveEnd = EffectiveEnd(item);
        var endsAtNextMidnight = item.Kind == CalendarItemKind.Key
            && effectiveEnd.LocalDateTime.Date == item.StartsAt.LocalDateTime.Date.AddDays(1)
            && effectiveEnd.LocalDateTime.TimeOfDay == TimeSpan.Zero;
        var end = item.EndsAt is null && item.Kind == CalendarItemKind.Class
            ? "长期"
            : endsAtNextMidnight
                ? $"{item.StartsAt.LocalDateTime:yyyy/M/d} 24:00"
                : effectiveEnd.LocalDateTime.ToString("yyyy/M/d HH:mm", CultureInfo.CurrentCulture);
        panel.Children.Add(new TextBlock
        {
            Text = $"{start} → {end}",
            Foreground = MutedBrush,
            Margin = new Thickness(0, 3, 0, 0)
        });
        return panel;
    }

    private static void RenderEventBarText(
        TextBlock target,
        CalendarItem item,
        double itemStart,
        double itemEnd,
        double width,
        bool showName)
    {
        target.Inlines.Clear();
        var start = FormatMinute(itemStart);
        var end = FormatMinute(itemEnd);
        var keyPrefix = item.Kind == CalendarItemKind.Key ? "*" : string.Empty;
        if (!showName)
        {
            target.Inlines.Add(new Run($"{keyPrefix}{start}—{end}"));
            return;
        }

        target.Inlines.Add(new Run($"{keyPrefix}{item.Title}")
        {
            TextDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null
        });
        if (width >= 150)
            target.Inlines.Add(new Run($"  {start}—{end}"));
    }

    private DateTimeOffset AtMinute(double minute)
    {
        var local = _viewModel.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified)
            .AddMinutes(Math.Clamp(Math.Round(minute), 0, 1440));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static double LocalMinutesFrom(DateTimeOffset dayStart, DateTimeOffset value) =>
        Math.Clamp((value.LocalDateTime - dayStart.LocalDateTime).TotalMinutes, 0, 1440);

    private static string FormatMinute(double minute)
    {
        var value = (int)Math.Clamp(Math.Round(minute), 0, 1440);
        return value == 1440
            ? "24:00"
            : $"{value / 60:00}:{value % 60:00}";
    }

    private string FormatBoundary(DateTimeOffset value)
    {
        var nextMidnight = AtLocalMidnight(_viewModel.Date.AddDays(1));
        return value == nextMidnight
            ? "24:00"
            : value.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture);
    }

    private static DateTimeOffset AtLocalMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static bool Overlaps(TimelineSlice first, TimelineSlice second) =>
        first.StartMinute < second.EndMinute && first.EndMinute > second.StartMinute;

    private static bool NearlyEqual(double first, double second) =>
        Math.Abs(first - second) < 0.01;

    private static bool IsInsideCheckBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch (FormatException) { return Color.FromRgb(189, 235, 211); }
        catch (NotSupportedException) { return Color.FromRgb(189, 235, 211); }
    }

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

    private static SolidColorBrush BrushFrom(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static void ShowError(string message, Exception exception) =>
        System.Windows.MessageBox.Show(
            $"{message}\n\n{exception.Message}",
            "Diary of Mine",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeStarted) return;
        _closeStarted = true;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        _fileLinksCloseTimer.Stop();
        CloseFileLinksPopup();
        _viewModel.ViewChanged -= OnViewModelChanged;
        PreviewMouseMove -= OnWindowPreviewMouseMove;
        PreviewMouseLeftButtonUp -= OnWindowPreviewMouseLeftButtonUp;
        DiaryRequested = null;
        CancelActiveInteraction();
        if (_editor is { IsVisible: true })
            _editor.CloseAfterExternalCommit();
        if (_taskEditor is { IsVisible: true })
            _taskEditor.CloseAfterExternalCommit();
    }

    private sealed record TimelineSlice(
        CalendarItem Item,
        double StartMinute,
        double EndMinute);

    private sealed record RowVisual(
        Border Shell,
        Canvas Canvas,
        double StartMinute,
        double EndMinute,
        double PixelsPerMinute);

    private sealed record BarSegmentTag(
        CalendarItem Item,
        bool CanDragStart,
        bool CanDragEnd,
        double SegmentStart,
        double SegmentEnd);

    private sealed record BoundaryDragState(
        CalendarItem Item,
        bool UpdateStart,
        DateTimeOffset Preview);

    private sealed record EventVisual(
        Border Element,
        Brush Normal,
        Brush Hover,
        TextBlock? Label,
        CalendarItem? TextItem,
        double ItemStart,
        double ItemEnd,
        double TextWidth,
        bool NormalShowsName);

    private sealed record OverflowMarker(
        double Minute,
        bool Above,
        int HiddenClassCount,
        int HiddenKeyCount);

    private sealed record OverflowSnapshot(
        double StartMinute,
        double EndMinute,
        TimelineSlice[] Active);

    private sealed record OverlapSelection(
        HashSet<Guid> ActiveIds,
        int Offset);

    private sealed class TossState(
        CalendarItem item,
        Border visual,
        FrameworkElement deleteSurface,
        Point eStart,
        TranslateTransform translation,
        RotateTransform rotation,
        bool sensitive)
    {
        public CalendarItem Item { get; } = item;
        public Border Visual { get; } = visual;
        public FrameworkElement DeleteSurface { get; } = deleteSurface;
        public Point StartPoint { get; } = eStart;
        public TranslateTransform Translation { get; } = translation;
        public RotateTransform Rotation { get; } = rotation;
        public bool Sensitive { get; } = sensitive;
        public bool IsDragging { get; set; }
        public bool DeleteArmed { get; set; }
    }
}
