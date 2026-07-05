using System;

public class ThemeState
{
    // El tema por defecto es dark
    public string CurrentTheme { get; private set; } = "dark";

    // Evento para avisarle a las pantallas que algo cambió
    public event Action? OnChange;

    public void SetTheme(string theme)
    {
        CurrentTheme = theme;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}