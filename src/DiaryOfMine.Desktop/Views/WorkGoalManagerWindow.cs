using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiaryOfMine.Desktop.ViewModels;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.Views;

internal sealed class WorkGoalManagerWindow : Window
{
    private readonly WorkModePageViewModel _viewModel;
    private readonly StackPanel _rows = new();
    private readonly TextBox _newName = new() { MaxLength = 4, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { Foreground = BrushOf("#9A6262"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };

    public WorkGoalManagerWindow(WorkModePageViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Title = "工作目标";
        Width = 580;
        Height = 640;
        MinWidth = 500;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BrushOf("#FAFAF8");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent();
        Loaded += (_, _) =>
        {
            RenderRows();
            _newName.Focus();
        };
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "工作目标", FontSize = 21, FontWeight = FontWeights.SemiBold });

        var hint = new TextBlock { Text = "默认目标与自定义目标都可修改；名称最多 4 个字符。删除已使用目标不会破坏历史统计。",
            Foreground = BrushOf("#6C766F"), FontSize = 11.5, Margin = new Thickness(0, 7, 0, 16) };
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(0, 15, 0, 0) };
        var addGrid = new Grid();
        addGrid.ColumnDefinitions.Add(new ColumnDefinition());
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var inputShell = new Border { Height = 38, Background = Brushes.White, BorderBrush = BrushOf("#D7DED8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 10, 0), Child = _newName };
        _newName.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return) || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            await AddNewGoalAsync();
        };
        addGrid.Children.Add(inputShell);
        var add = RoundedButton("添加", "#DDEAE1");
        add.Width = 82;
        add.Margin = new Thickness(10, 0, 0, 0);
        add.Click += AddClicked;
        Grid.SetColumn(add, 1);
        addGrid.Children.Add(add);
        footer.Children.Add(addGrid);
        footer.Children.Add(_message);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        foreach (var goal in _viewModel.ActiveGoals)
            _rows.Children.Add(BuildGoalRow(goal));
    }

    private UIElement BuildGoalRow(WorkGoal goal)
    {
        var shell = new Border { Background = Brushes.White, BorderBrush = BrushOf("#E0E5E0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 9) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox { Text = goal.Name, MaxLength = 4, BorderThickness = new Thickness(0), Background = BrushOf("#F7F9F7"), Padding = new Thickness(9, 7, 9, 7), VerticalContentAlignment = VerticalAlignment.Center };
        grid.Children.Add(name);
        var save = RoundedButton("保存", "#E7F0E9");
        save.Margin = new Thickness(9, 0, 0, 0);
        async Task SaveNameAsync()
        {
            await _viewModel.RenameGoalAsync(goal.Id, name.Text);
            _message.Text = _viewModel.StatusMessage;
            RenderRows();
        }
        save.Click += async (_, _) => await SaveNameAsync();
        name.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return) || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            await SaveNameAsync();
        };
        Grid.SetColumn(save, 1);
        grid.Children.Add(save);
        var remove = RoundedButton("删除", "#F8E7E7");
        remove.Margin = new Thickness(7, 0, 0, 0);
        remove.Click += async (_, _) =>
        {
            await _viewModel.DeleteGoalAsync(goal.Id);
            _message.Text = _viewModel.StatusMessage;
            RenderRows();
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        shell.Child = grid;
        return shell;
    }

    private async void AddClicked(object sender, RoutedEventArgs e)
        => await AddNewGoalAsync();

    private async Task AddNewGoalAsync()
    {
        await _viewModel.AddGoalAsync(_newName.Text);
        _message.Text = _viewModel.StatusMessage;
        if (_viewModel.ActiveGoals.Any(goal => goal.Name == _newName.Text.Trim())) _newName.Clear();
        RenderRows();
        _newName.Focus();
    }

    private static Button RoundedButton(string text, string color)
    {
        var button = new Button { Content = text, Height = 34, Padding = new Thickness(12, 0, 12, 0), Background = BrushOf(color), BorderBrush = BrushOf("#D6DED7"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
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
}
