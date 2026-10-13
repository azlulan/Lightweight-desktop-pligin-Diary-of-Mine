using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop;

public partial class MainWindow : Window
{
    internal IAppLogger? Logger { private get; init; }
    internal event Action<CalendarItemKind, DateOnly?>? CalendarItemCreationRequested;
    internal event Action<DateOnly?>? KeyTaskCreationRequested;
    internal event Action<CalendarItem>? KeyTaskEditRequested;
    internal event Action<CalendarItem>? CalendarItemEditRequested;
    internal event Action<DateOnly>? CalendarDayDetailsRequested;
    internal event Action? EscapeRequested;
    internal event Action? CloseToBackgroundRequested;

    public MainWindow() => InitializeComponent();

    internal void RequestCalendarItemCreation(CalendarItemKind kind, DateOnly? initialDate) =>
        CalendarItemCreationRequested?.Invoke(kind, initialDate);

    internal void RequestKeyTaskCreation(DateOnly? initialDate) =>
        KeyTaskCreationRequested?.Invoke(initialDate);

    internal void RequestKeyTaskEdit(CalendarItem item) =>
        KeyTaskEditRequested?.Invoke(item);

    internal void RequestCalendarItemEdit(CalendarItem item) =>
        CalendarItemEditRequested?.Invoke(item);

    internal void RequestCalendarDayDetails(DateOnly date) =>
        CalendarDayDetailsRequested?.Invoke(date);

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SidebarColumn is null) return;
        SidebarColumn.Width = new GridLength(Math.Clamp(ActualWidth * 0.144, 138, 176));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (((App)System.Windows.Application.Current).IsExiting) return;
        using var operation = Logger?.BeginOperation("window.main.hide-close");
        try
        {
            e.Cancel = true;
            if (CloseToBackgroundRequested is { } requested)
                requested();
            else
                Hide();
            operation?.Complete();
        }
        catch (System.Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        using var operation = Logger?.BeginOperation("window.main.hide-escape");
        try
        {
            EscapeRequested?.Invoke();
            e.Handled = true;
            operation?.Complete();
        }
        catch (System.Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
    }
}
