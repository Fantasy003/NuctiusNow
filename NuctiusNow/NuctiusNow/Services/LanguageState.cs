namespace NuctiusNow.Services;

public sealed record SiteLanguage(string Code, string Name, string NativeName, string Culture, bool RightToLeft = false);

public sealed class LanguageState
{
    public static readonly IReadOnlyList<SiteLanguage> Supported = new[]
    {
        new SiteLanguage("en", "English", "English", "en-US"),
        new SiteLanguage("es", "Spanish", "Español", "es-AR"),
        new SiteLanguage("pt", "Portuguese", "Português", "pt-BR"),
        new SiteLanguage("ru", "Russian", "Русский", "ru-RU"),
        new SiteLanguage("zh", "Chinese (Simplified)", "简体中文", "zh-CN"),
        new SiteLanguage("ja", "Japanese", "日本語", "ja-JP"),
        new SiteLanguage("hi", "Hindi", "हिन्दी", "hi-IN"),
        new SiteLanguage("ar", "Arabic", "العربية", "ar" , true),
        new SiteLanguage("fr", "French", "Français", "fr-FR"),
        new SiteLanguage("de", "German", "Deutsch", "de-DE"),
        new SiteLanguage("it", "Italian", "Italiano", "it-IT")
    };

    public SiteLanguage Current { get; private set; } = Supported[0];
    public event Action? Changed;

    public static string? Normalize(string? value)
    {
        var input = value?.Trim();
        if (input == "1") return "en";
        if (input == "2") return "es";
        var baseCode = input?.Split('-', '_')[0];
        return Supported.FirstOrDefault(x =>
            x.Code.Equals(baseCode, StringComparison.OrdinalIgnoreCase) ||
            x.Code.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            x.Culture.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            x.Name.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            x.NativeName.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            (x.Code == "zh" && string.Equals(input, "Chinese", StringComparison.OrdinalIgnoreCase)))?.Code;
    }

    public void Set(string? language)
    {
        var code = Normalize(language) ?? "en";
        if (Current.Code == code) return;
        Current = Supported.Single(x => x.Code == code);
        Changed?.Invoke();
    }
}
