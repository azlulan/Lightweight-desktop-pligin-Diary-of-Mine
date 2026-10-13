using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiaryOfMine.Desktop.Services;
using DiaryOfMine.Desktop.ViewModels;

namespace DiaryOfMine.Desktop.Views;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView() => InitializeComponent();

    private void OnExpandHotKeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) return;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (DataContext is SettingsPageViewModel viewModel
            && HotKeyGesture.TryCreate(Keyboard.Modifiers, key, out var gesture))
            viewModel.ExpandHotKey = gesture!.DisplayText;
        e.Handled = true;
    }

    private void OnResetExpandHotKeyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsPageViewModel viewModel)
            viewModel.ExpandHotKey = "F8";
        ExpandHotKeyTextBox.Focus();
    }
}
