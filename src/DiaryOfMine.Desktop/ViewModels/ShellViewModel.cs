using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Desktop.Navigation;

namespace DiaryOfMine.Desktop.ViewModels;

public sealed class ShellViewModel : INotifyPropertyChanged, IDisposable
{
    private NavigationItemViewModel? _selectedItem;
    private readonly IAppLogger _logger;
    private object? _currentPage;
    private string _currentPageTitle = string.Empty;
    private bool _disposed;

    public ObservableCollection<NavigationItemViewModel> MainNavigation { get; }
    public ObservableCollection<NavigationItemViewModel> FooterNavigation { get; }
    public string EnvironmentLabel { get; }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageTitle
    {
        get => _currentPageTitle;
        private set => SetProperty(ref _currentPageTitle, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShellViewModel(NavigationRegistry registry, bool development, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var items = registry.Registrations
            .OrderBy(registration => registration.Order)
            .Select(registration => new NavigationItemViewModel(registration, NavigateTo))
            .ToArray();

        MainNavigation = new ObservableCollection<NavigationItemViewModel>(
            items.Where(item => item.Placement == NavigationPlacement.Main));
        FooterNavigation = new ObservableCollection<NavigationItemViewModel>(
            items.Where(item => item.Placement == NavigationPlacement.Footer));
        EnvironmentLabel = development ? "开发预览" : "本地模式";

        var initialPage = MainNavigation.FirstOrDefault() ?? FooterNavigation.FirstOrDefault()
            ?? throw new InvalidOperationException("至少需要注册一个导航页面。");
        NavigateTo(initialPage);
    }

    public bool TryNavigateTo(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(id)) return false;

        var item = MainNavigation.Concat(FooterNavigation)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (item is null) return false;

        NavigateTo(item);
        return true;
    }

    private void NavigateTo(NavigationItemViewModel item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_selectedItem, item)) return;

        using var operation = _logger.BeginOperation("navigation.change");
        try
        {
            var page = item.GetOrCreatePage();
            if (_selectedItem is not null)
                _selectedItem.IsSelected = false;

            _selectedItem = item;
            _selectedItem.IsSelected = true;
            CurrentPageTitle = item.Title;
            CurrentPage = page;
            operation.Complete();
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var item in MainNavigation.Concat(FooterNavigation))
            item.Dispose();

        _selectedItem = null;
        CurrentPage = null;
        PropertyChanged = null;
    }
}
