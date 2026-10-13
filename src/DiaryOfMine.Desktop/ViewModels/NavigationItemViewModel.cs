using System;
using System.ComponentModel;
using System.Windows.Input;
using DiaryOfMine.Desktop.Commands;
using DiaryOfMine.Desktop.Navigation;

namespace DiaryOfMine.Desktop.ViewModels;

public sealed class NavigationItemViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly NavigationPageRegistration _registration;
    private object? _page;
    private bool _isSelected;
    private bool _disposed;

    public string Id => _registration.Id;
    public string Title => _registration.Title;
    public string Glyph => _registration.Glyph;
    public int Order => _registration.Order;
    public NavigationPlacement Placement => _registration.Placement;
    public ICommand NavigateCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NavigationItemViewModel(
        NavigationPageRegistration registration,
        Action<NavigationItemViewModel> navigate)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        ArgumentNullException.ThrowIfNull(navigate);
        NavigateCommand = new RelayCommand(() => navigate(this), () => !_disposed);
    }

    internal object GetOrCreatePage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _page ??= _registration.CreatePage();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_page is IDisposable disposablePage)
            disposablePage.Dispose();
        _page = null;
        PropertyChanged = null;
    }
}
