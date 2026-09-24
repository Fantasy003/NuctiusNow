using System;

public class ThemeState
{
    private static readonly HashSet<string> AllowedThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "dark", "light", "retro", "office"
    };

    public string CurrentTheme { get; private set; } = "dark";

    public event Action? OnChange;

    public void SetTheme(string theme)
    {
        var normalized = AllowedThemes.Contains(theme)
            ? theme.ToLowerInvariant()
            : "dark";

        if (CurrentTheme == normalized)
            return;

        CurrentTheme = normalized;
        OnChange?.Invoke();
    }
}
