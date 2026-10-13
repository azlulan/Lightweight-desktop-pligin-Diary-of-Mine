using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiaryOfMine.Desktop.ViewModels;
using DiaryOfMine.Desktop.Services;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Views;

internal sealed class WorkInterruptEditorWindow : Window
{
    private readonly WorkModePageViewModel _viewModel;
    private readonly IReadOnlyList<WorkSession> _sessions;
    private readonly ComboBox _session = new();
    private readonly DatePicker _startDate = new();
    private readonly DatePicker _endDate = new();
    private readonly TextBox _startTime = new() { Text = "09:00" };
    private readonly TextBox _endTime = new() { Text = "09:30" };
    private readonly TextBlock _message = new() { Foreground = new SolidColorBrush(Color.FromRgb(154, 89, 89)), FontSize = 11 };

    public WorkInterruptEditorWindow(WorkModePageViewModel viewModel, IReadOnlyList<WorkSession> sessions)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        Title = "添加中断区块";
        Width = 540;
        Height = 620;
        MinWidth = 470;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BrushOf("#FAFAF8");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent();
        PopulateSessions();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(26, 22, 26, 22) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "手动添加中断", FontSize = 21, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = "选择一条已完成记录，并输入位于该记录内部的分钟级时间。", FontSize = 11.5, Foreground = BrushOf("#6C766F"), Margin = new Thickness(0, 7, 0, 20) });
        form.Children.Add(Label("工作记录"));
        _session.Height = 38;
        _session.Margin = new Thickness(0, 7, 0, 15);
        _session.SelectionChanged += SessionChanged;
        form.Children.Add(_session);
        form.Children.Add(Label("开始时间"));
        form.Children.Add(TimeRow(_startDate, _startTime));
        form.Children.Add(Label("结束时间"));
        form.Children.Add(TimeRow(_endDate, _endTime));
        root.Children.Add(new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

        _message.Margin = new Thickness(0, 10, 0, 8);
        _message.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_message, 1);
        root.Children.Add(_message);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = RoundedButton("取消", "#F0F3F0");
        cancel.Click += (_, _) => Close();
        var save = RoundedButton("添加中断", "#F3DADA");
        save.IsDefault = true;
        save.Margin = new Thickness(9, 0, 0, 0);
        save.Click += SaveClicked;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        return root;
    }

    private void PopulateSessions()
    {
        foreach (var session in _sessions)
        {
            var goal = _viewModel.FindGoal(session.GoalId);
            _session.Items.Add(new SessionOption(session, $"{goal?.Name ?? "目标"}  {session.StartsAt:MM-dd HH:mm} — {session.EndsAt:MM-dd HH:mm}"));
        }
        _session.DisplayMemberPath = nameof(SessionOption.Label);
        if (_session.Items.Count > 0) _session.SelectedIndex = 0;
        else _message.Text = "所选日期没有可添加中断的已完成工作记录。";
    }

    private void SessionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session.SelectedItem is not SessionOption option || option.Session.EndsAt is not { } end) return;
        _startDate.SelectedDate = option.Session.StartsAt.LocalDateTime.Date;
        _endDate.SelectedDate = end.LocalDateTime.Date;
        _startTime.Text = option.Session.StartsAt.ToString("HH:mm");
        _endTime.Text = end.ToString("HH:mm");
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_session.SelectedItem is not SessionOption option)
        {
            _message.Text = "请先选择工作记录。";
            return;
        }
        if (!TryDateTime(_startDate.SelectedDate, _startTime.Text, out var start)
            || !TryDateTime(_endDate.SelectedDate, _endTime.Text, out var end))
        {
            _message.Text = "请输入合法时间，例如 08:05。";
            return;
        }
        await _viewModel.AddManualInterruptionAsync(option.Session.Id, start, end);
        _message.Text = _viewModel.StatusMessage;
        if (_viewModel.StatusMessage == "中断区块已添加。") Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) =>
        FormKeyboardNavigation.HandleEnter(
            e,
            () => SaveClicked(this, new RoutedEventArgs()),
            _session,
            _startDate,
            _startTime,
            _endDate,
            _endTime);

    private static bool TryDateTime(DateTime? date, string text, out DateTimeOffset value)
    {
        value = default;
        if (date is null || !TimeOnly.TryParseExact(text.Trim(), "H:mm", CultureInfo.CurrentCulture, DateTimeStyles.None, out var time)
            && !TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.CurrentCulture, DateTimeStyles.None, out time)) return false;
        value = new DateTimeOffset(DateOnly.FromDateTime(date.Value).ToDateTime(time, DateTimeKind.Local));
        return true;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12 };

    private static UIElement TimeRow(DatePicker date, TextBox time)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 15) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) });
        date.Height = 38;
        grid.Children.Add(date);
        var shell = new Border { Margin = new Thickness(9, 0, 0, 0), Background = Brushes.White, BorderBrush = BrushOf("#D7DED8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 10, 0), Child = time };
        time.BorderThickness = new Thickness(0);
        time.VerticalContentAlignment = VerticalAlignment.Center;
        Grid.SetColumn(shell, 1);
        grid.Children.Add(shell);
        return grid;
    }

    private static Button RoundedButton(string text, string color)
    {
        var button = new Button { Content = text, Height = 36, Padding = new Thickness(14, 0, 14, 0), Background = BrushOf(color), BorderBrush = BrushOf("#D6DED7"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
        return button;
    }

    private static SolidColorBrush BrushOf(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private sealed record SessionOption(WorkSession Session, string Label);
}
