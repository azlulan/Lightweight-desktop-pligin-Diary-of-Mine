using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Views;

public partial class FloatingWindow : Window
{
    public event EventHandler? RestoreRequested;
    public event EventHandler? PositionCommitted;
    public event Action<CalendarItemKind>? QuickCreateRequested;
    public event EventHandler? QuickTaskCreateRequested;
    public event EventHandler? WorkRecordingRequested;
    public event Action<Guid>? WorkGoalSelected;
    public event EventHandler? SettingsRequested;

    public FloatingWindow() => InitializeComponent();

    public void SetExpandHotKeyText(string hotKey)
    {
        FloatingStatusText.Text = $"休眠模式 · {hotKey} 展开";
        ExpandButton.ToolTip = $"展开主界面（{hotKey}）";
    }

    public void SetWorkRecordingState(bool isRecording)
    {
        WorkRecordButton.IsEnabled = !isRecording;
        WorkRecordButton.ToolTip = isRecording ? "工作模式正在录制" : "工作模式 · 录制";
    }

    public void ShowWorkGoals(IReadOnlyCollection<FloatingWorkGoalOption> goals, bool isRecording)
    {
        WorkGoalsPanel.Children.Clear();
        WorkPanelStatusText.Visibility = Visibility.Collapsed;
        if (isRecording)
        {
            WorkPanelStatusText.Text = "当前已有工作正在录制。";
            WorkPanelStatusText.Visibility = Visibility.Visible;
        }
        else if (goals.Count == 0)
        {
            WorkPanelStatusText.Text = "请先在工作模式中添加目标。";
            WorkPanelStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            foreach (var goal in goals)
            {
                var button = new Button
                {
                    Height = 32,
                    Margin = new Thickness(1, 2, 1, 2),
                    Padding = new Thickness(8, 0, 8, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(goal.Color)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(205, 215, 208)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = goal.Id
                };
                var template = new ControlTemplate(typeof(Button));
                var shell = new FrameworkElementFactory(typeof(Border));
                shell.SetValue(Border.BackgroundProperty,
                    new TemplateBindingExtension(Control.BackgroundProperty));
                shell.SetValue(Border.BorderBrushProperty,
                    new TemplateBindingExtension(Control.BorderBrushProperty));
                shell.SetValue(Border.BorderThicknessProperty,
                    new TemplateBindingExtension(Control.BorderThicknessProperty));
                shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                shell.SetValue(Border.PaddingProperty,
                    new TemplateBindingExtension(Control.PaddingProperty));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                    HorizontalAlignment.Stretch);
                presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,
                    VerticalAlignment.Center);
                shell.AppendChild(presenter);
                template.VisualTree = shell;
                button.Template = template;
                var content = new Grid();
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.ColumnDefinitions.Add(new ColumnDefinition());
                content.Children.Add(new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(Color.FromRgb(78, 103, 91)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var label = new TextBlock
                {
                    Text = goal.Name,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(48, 69, 59))
                };
                Grid.SetColumn(label, 1);
                content.Children.Add(label);
                button.Content = content;
                button.Click += OnWorkGoalClick;
                WorkGoalsPanel.Children.Add(button);
            }
        }
        WorkPanelPopup.IsOpen = true;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void OnClassClick(object sender, RoutedEventArgs e)
    {
        CalendarPanelPopup.IsOpen = false;
        QuickCreateRequested?.Invoke(CalendarItemKind.Class);
    }

    private void OnKeyClick(object sender, RoutedEventArgs e)
    {
        CalendarPanelPopup.IsOpen = false;
        QuickCreateRequested?.Invoke(CalendarItemKind.Key);
    }

    private void OnIdeaClick(object sender, RoutedEventArgs e)
    {
        CalendarPanelPopup.IsOpen = false;
        QuickCreateRequested?.Invoke(CalendarItemKind.Idea);
    }

    private void OnTaskClick(object sender, RoutedEventArgs e)
    {
        CalendarPanelPopup.IsOpen = false;
        QuickTaskCreateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCalendarMenuClick(object sender, RoutedEventArgs e)
    {
        WorkPanelPopup.IsOpen = false;
        CalendarPanelPopup.IsOpen = !CalendarPanelPopup.IsOpen;
    }

    private void OnWorkRecordClick(object sender, RoutedEventArgs e)
    {
        CalendarPanelPopup.IsOpen = false;
        WorkRecordingRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnWorkGoalClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid goalId }) return;
        WorkPanelPopup.IsOpen = false;
        WorkGoalSelected?.Invoke(goalId);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnDragAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            RestoreRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;
        DragMove();
        PositionCommitted?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record FloatingWorkGoalOption(Guid Id, string Name, string Color);
