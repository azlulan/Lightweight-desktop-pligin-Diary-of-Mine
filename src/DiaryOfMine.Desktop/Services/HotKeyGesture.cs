using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace DiaryOfMine.Desktop.Services;

internal sealed record HotKeyGesture(ModifierKeys Modifiers, Key Key)
{
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(2);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            else if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            else if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            else if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(FormatKey(Key));
            return string.Join("+", parts);
        }
    }

    public static bool TryParse(
        string? text, out HotKeyGesture? gesture, out string error)
    {
        gesture = null;
        error = string.Empty;
        var parts = (text ?? string.Empty)
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            error = "请输入一个按键，或一个修饰键加一个按键。";
            return false;
        }

        var modifiers = ModifierKeys.None;
        string keyText;
        if (parts.Length == 2)
        {
            if (!TryParseModifier(parts[0], out modifiers))
            {
                error = "双键快捷键的第一个按键必须是 Ctrl、Alt、Shift 或 Win。";
                return false;
            }
            keyText = parts[1];
        }
        else
        {
            keyText = parts[0];
        }

        if (!TryParseKey(keyText, out var key) || IsModifierKey(key))
        {
            error = "请选择一个有效的普通按键。";
            return false;
        }
        if (key == Key.Escape)
        {
            error = "Esc 已用于逐级关闭窗口和进入悬浮模式，不能设为展开键。";
            return false;
        }

        gesture = new HotKeyGesture(modifiers, key);
        return true;
    }

    public static bool TryCreate(
        ModifierKeys modifiers, Key key, out HotKeyGesture? gesture)
    {
        gesture = null;
        var supported = modifiers & (ModifierKeys.Control | ModifierKeys.Alt
            | ModifierKeys.Shift | ModifierKeys.Windows);
        if (CountModifiers(supported) > 1 || key == Key.None
            || IsModifierKey(key) || key == Key.Escape)
            return false;
        gesture = new HotKeyGesture(supported, key);
        return true;
    }

    private static int CountModifiers(ModifierKeys modifiers) =>
        new[] { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Windows }
            .Count(modifier => modifiers.HasFlag(modifier));

    private static bool TryParseModifier(string text, out ModifierKeys modifier)
    {
        modifier = text.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => ModifierKeys.Control,
            "ALT" => ModifierKeys.Alt,
            "SHIFT" => ModifierKeys.Shift,
            "WIN" or "WINDOWS" => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };
        return modifier != ModifierKeys.None;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsDigit(text[0])) text = $"D{text}";
        return Enum.TryParse(text, true, out key) && key != Key.None;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatKey(Key key) => key is >= Key.D0 and <= Key.D9
        ? ((int)key - (int)Key.D0).ToString()
        : key.ToString();
}
