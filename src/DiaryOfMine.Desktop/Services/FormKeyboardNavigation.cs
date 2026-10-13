using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DiaryOfMine.Desktop.Services;

internal static class FormKeyboardNavigation
{
    internal static bool HandleEnter(
        KeyEventArgs e,
        Action confirm,
        params Control[] orderedInputs)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.Enter or Key.Return)) return false;

        var source = Keyboard.FocusedElement as DependencyObject;
        var isMultiline = FindAncestor<TextBox>(source) is { AcceptsReturn: true }
                          || FindAncestor<RichTextBox>(source) is not null;
        if (isMultiline)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;
            e.Handled = true;
            confirm();
            return true;
        }

        if (Keyboard.Modifiers != ModifierKeys.None) return false;
        if (FindAncestor<Button>(source) is not null) return false;

        var inputs = orderedInputs
            .Where(input => input.IsVisible && input.IsEnabled && input.Focusable)
            .ToArray();
        var currentIndex = Array.FindIndex(inputs, input => IsInside(source, input));
        if (currentIndex < 0) return false;

        e.Handled = true;
        if (currentIndex == inputs.Length - 1)
        {
            confirm();
            return true;
        }

        inputs[currentIndex + 1].Focus();
        Keyboard.Focus(inputs[currentIndex + 1]);
        return true;
    }

    private static bool IsInside(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = ParentOf(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = ParentOf(current))
            if (current is T match) return match;
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject current)
    {
        if (current is FrameworkContentElement content) return content.Parent;
        return VisualTreeHelper.GetParent(current);
    }
}
