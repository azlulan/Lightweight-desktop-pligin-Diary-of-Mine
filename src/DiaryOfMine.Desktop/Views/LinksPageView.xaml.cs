using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DiaryOfMine.Desktop.ViewModels;

namespace DiaryOfMine.Desktop.Views;

public partial class LinksPageView : UserControl
{
    private LinksPageViewModel? _viewModel;

    public LinksPageView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LinksPageViewModel viewModel) return;
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        await viewModel.InitializeAsync();
        UpdateEmptyState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LinksPageViewModel.HasEntries)) UpdateEmptyState();
    }

    private void UpdateEmptyState() => EmptyState.Visibility = _viewModel?.HasEntries == true
        ? Visibility.Collapsed
        : Visibility.Visible;

    private void OnDiaryCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateOnly date }
            && DataContext is LinksPageViewModel viewModel)
            viewModel.Open(date);
    }

    private void OnOpenTodayClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LinksPageViewModel viewModel)
            viewModel.Open(DateOnly.FromDateTime(DateTime.Today));
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LinksPageViewModel viewModel)
            viewModel.SearchText = string.Empty;
    }
}
