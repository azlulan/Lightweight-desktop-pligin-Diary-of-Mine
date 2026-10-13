using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using DiaryOfMine.Application.Reminders;

namespace DiaryOfMine.Desktop.Views;

public partial class ReminderWindow : Window
{
    private readonly ObservableCollection<ReminderDisplayItem> _items = new();
    private bool _allowClose;

    internal event EventHandler<ReminderConfirmationRequestedEventArgs>? ConfirmationRequested;
    internal event EventHandler? AllConfirmed;

    public ReminderWindow()
    {
        InitializeComponent();
        ReminderItems.ItemsSource = _items;
    }

    internal bool HasPendingItems => _items.Any(item => !item.IsConfirmed);

    internal void AddReminders(System.Collections.Generic.IEnumerable<ReminderNotification> reminders)
    {
        foreach (var reminder in reminders)
        {
            if (_items.Any(item => string.Equals(
                    item.Notification.Key, reminder.Key, StringComparison.Ordinal)))
                continue;
            _items.Add(new ReminderDisplayItem(reminder));
        }
        UpdateCount();
    }

    internal void RemoveWhere(Func<ReminderNotification, bool> predicate)
    {
        foreach (var item in _items.Where(item => predicate(item.Notification)).ToArray())
            _items.Remove(item);
        UpdateCount();
        if (!HasPendingItems) Hide();
    }

    internal void ClearAndHide()
    {
        Hide();
        _items.Clear();
        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
        UpdateCount();
    }

    internal void SetPresentationMode(bool floating)
    {
        Width = floating ? 326 : 410;
        PlacementText.Text = floating
            ? "悬浮模式 · 请逐项确认"
            : "软件已唤醒 · 请逐项确认";
    }

    internal void CloseForApplicationExit()
    {
        _allowClose = true;
        Close();
    }

    private void OnConfirmationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReminderDisplayItem item }
            || item.IsConfirmed)
            return;

        var request = new ReminderConfirmationRequestedEventArgs(item.Notification);
        ConfirmationRequested?.Invoke(this, request);
        if (!request.Accepted)
        {
            StatusText.Text = request.ErrorMessage ?? "确认记录保存失败，请重试。";
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        item.IsConfirmed = true;
        StatusText.Text = string.Empty;
        StatusText.Visibility = Visibility.Collapsed;
        UpdateCount();
        if (_items.All(candidate => candidate.IsConfirmed))
            AllConfirmed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCount()
    {
        var remaining = _items.Count(item => !item.IsConfirmed);
        CountText.Text = $"{remaining} 项待确认";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
    }
}

internal sealed class ReminderConfirmationRequestedEventArgs(
    ReminderNotification notification) : EventArgs
{
    public ReminderNotification Notification { get; } = notification;
    public bool Accepted { get; set; }
    public string? ErrorMessage { get; set; }
}

internal sealed class ReminderDisplayItem : INotifyPropertyChanged
{
    private bool _isConfirmed;

    public ReminderNotification Notification { get; }
    public string Title => Notification.Title;
    public string KindText => Notification.Kind switch
    {
        ReminderKind.ClassDeadline => "CLASS · 截止提醒",
        ReminderKind.KeyStart when Notification.IsTaskKey => "TASK · KEY 开始提醒",
        ReminderKind.KeyStart => "KEY · 开始提醒",
        ReminderKind.IdeaDaily => "IDEA · 当日未完成",
        _ => "日程提醒"
    };
    public string TimeText => Notification.Kind switch
    {
        ReminderKind.ClassDeadline => $"截止时间：{Notification.EventTime.LocalDateTime:MM月dd日 HH:mm}（提前 30 分钟提醒）",
        ReminderKind.KeyStart => $"开始时间：{Notification.EventTime.LocalDateTime:MM月dd日 HH:mm}（提前 30 分钟提醒）",
        ReminderKind.IdeaDaily => $"{Notification.EventTime.LocalDateTime:MM月dd日} 仍未勾选完成",
        _ => Notification.EventTime.LocalDateTime.ToString("MM月dd日 HH:mm")
    };
    public string AccentColor => Notification.Kind switch
    {
        ReminderKind.ClassDeadline => "#A3483F",
        ReminderKind.KeyStart => "#346F79",
        ReminderKind.IdeaDaily => "#8A6823",
        _ => "#52635B"
    };
    public string CardBackground => Notification.Kind switch
    {
        ReminderKind.ClassDeadline => "#FFF4F2",
        ReminderKind.KeyStart => "#F2FAFB",
        ReminderKind.IdeaDaily => "#FFF9E9",
        _ => "#F7F8F5"
    };
    public string CardBorder => Notification.Kind switch
    {
        ReminderKind.ClassDeadline => "#E9BBB5",
        ReminderKind.KeyStart => "#B9DDE1",
        ReminderKind.IdeaDaily => "#E8D69E",
        _ => "#CDD5CF"
    };

    public bool IsConfirmed
    {
        get => _isConfirmed;
        set
        {
            if (_isConfirmed == value) return;
            _isConfirmed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(ConfirmationText));
        }
    }

    public bool CanConfirm => !IsConfirmed;
    public string ConfirmationText => IsConfirmed ? "已确认" : "确认";
    public event PropertyChangedEventHandler? PropertyChanged;

    public ReminderDisplayItem(ReminderNotification notification) =>
        Notification = notification ?? throw new ArgumentNullException(nameof(notification));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
