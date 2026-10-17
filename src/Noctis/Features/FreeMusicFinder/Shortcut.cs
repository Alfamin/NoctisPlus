using Avalonia.Input;

namespace FreeMusicFinder;

/// <summary>The keyboard shortcut that opens the search window anywhere in Noctis.</summary>
internal static class Shortcut
{
    /// <summary>Free in Noctis (its own library search is Ctrl+F).</summary>
    public const string Default = "Ctrl+Shift+F";

    /// <summary>
    /// The gesture the setting names, or null when it is empty or "off". On macOS Ctrl stands for
    /// Cmd, as in Noctis's own shortcuts.
    /// </summary>
    /// <exception cref="FormatException">Not a shortcut, or one that would get in the way of typing.</exception>
    public static KeyGesture? Parse(string? text, bool isMac)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;

        KeyGesture gesture;
        try { gesture = KeyGesture.Parse(text); }
        catch (Exception ex) { throw new FormatException($"\"{text}\" is not a shortcut. Example: {Default}.", ex); }

        if (gesture.Key is Key.None or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            throw new FormatException($"\"{text}\" has no key besides Ctrl, Shift or Alt. Example: {Default}.");
        // Without Ctrl, Alt or Cmd it would fire while typing in a text box; F1 to F24 are fine alone.
        var function = gesture.Key is >= Key.F1 and <= Key.F24;
        if (!function && (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
            throw new FormatException($"\"{text}\" would get in the way of typing: add Ctrl or Alt. Example: {Default}.");

        if (isMac && gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
            gesture = new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
        return gesture;
    }
}
