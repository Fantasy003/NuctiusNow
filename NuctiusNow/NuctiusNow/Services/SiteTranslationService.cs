using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace NuctiusNow.Services;

public interface ITextTranslationBackend
{
    Task<IReadOnlyList<string>> TranslateTextsAsync(IReadOnlyList<string> texts, string language,
        CancellationToken cancellationToken = default);
}

public sealed record TranslationText(string Text, bool Content = false);

public sealed class TranslationCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> _languages;
    public TranslationCatalog(IWebHostEnvironment environment)
    {
        var file = Path.Combine(environment.ContentRootPath, "Localization", "common.json");
        _languages = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file))!
            .ToDictionary(x => x.Key, x => new Dictionary<string, string>(x.Value, StringComparer.OrdinalIgnoreCase));
    }
    public IReadOnlyDictionary<string, string> For(string language) =>
        _languages.TryGetValue(language, out var values) ? values : new Dictionary<string, string>();
    public string? Find(string source, string language)
    {
        if (language == "en") return source;
        if (!For(language).TryGetValue(source, out var result)) return null;
        return source.Any(char.IsLetter) && source == source.ToUpperInvariant() ? result.ToUpperInvariant() : result;
    }
}

// Shared cache entries are keyed by language AND source hash. No localized text is
// written back to shared News/DetailNews rows, so readers cannot overwrite each other.
public sealed class SiteTranslationService
{
    private readonly ITextTranslationBackend _backend;
    private readonly TranslationCatalog _catalog;
    private readonly IMemoryCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public SiteTranslationService(ITextTranslationBackend backend, TranslationCatalog catalog, IMemoryCache cache)
    { _backend = backend; _catalog = catalog; _cache = cache; }

    public async Task<string[]> TranslateAsync(IReadOnlyList<TranslationText> input, string language,
        CancellationToken cancellationToken = default)
    {
        var normalized = LanguageState.Normalize(language) ?? throw new ArgumentException("Unsupported language.");
        if (input.Count > 80 || input.Sum(x => x.Text?.Length ?? 0) > 128000)
            throw new ArgumentException("Translation batch is too large.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = new string[input.Count];
            var missing = new List<(int Index, string Source, string Key)>();
            for (var i = 0; i < input.Count; i++)
            {
                var text = input[i].Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter)) { result[i] = text; continue; }
                var known = input[i].Content ? null : _catalog.Find(text, normalized);
                if (known != null) { result[i] = known; continue; }
                var key = "site-translation:" + normalized + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                if (_cache.TryGetValue<string>(key, out var cached)) { result[i] = cached!; continue; }
                missing.Add((i, text, key));
            }

            // Batch labels together; split long articles at word/paragraph boundaries.
            var chunks = missing.SelectMany(item => Split(item.Source, 4500)
                .Select((text, order) => (item.Index, Order: order, Text: text))).ToList();
            var translations = new Dictionary<int, List<string>>();
            for (var cursor = 0; cursor < chunks.Count;)
            {
                var batch = new List<(int Index, int Order, string Text)>();
                var length = 0;
                while (cursor < chunks.Count && batch.Count < 20 && length + chunks[cursor].Text.Length <= 6000)
                {
                    batch.Add(chunks[cursor]);
                    length += chunks[cursor++].Text.Length;
                }
                var values = await _backend.TranslateTextsAsync(batch.Select(x => x.Text).ToArray(), normalized, cancellationToken);
                if (values.Count != batch.Count || values.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidOperationException("Translation returned incomplete text.");
                for (var j = 0; j < batch.Count; j++)
                {
                    var source = batch[j].Text;
                    var prefix = source[..(source.Length - source.TrimStart().Length)];
                    var suffix = source[(source.TrimEnd().Length)..];
                    if (!translations.TryGetValue(batch[j].Index, out var parts))
                        translations[batch[j].Index] = parts = new List<string>();
                    parts.Add(prefix + values[j].Trim() + suffix);
                }
            }
            foreach (var item in missing)
            {
                var value = string.Join("", translations[item.Index]);
                result[item.Index] = value;
                _cache.Set(item.Key, value, new MemoryCacheEntryOptions
                {
                    Size = (value.Length + item.Source.Length) * 2L,
                    SlidingExpiration = TimeSpan.FromHours(4), AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1)
                });
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    internal static List<string> Split(string text, int limit)
    {
        var output = new List<string>();
        while (text.Length > limit)
        {
            var end = text.LastIndexOf('\n', limit - 1, limit);
            if (end < limit / 2) end = text.LastIndexOf(' ', limit - 1, limit);
            if (end < limit / 2) end = limit;
            else end++;
            // Never split a Unicode surrogate pair.
            if (char.IsHighSurrogate(text[end - 1])) end--;
            output.Add(text[..end]);
            text = text[end..];
        }
        if (text.Length > 0) output.Add(text);
        return output;
    }
}
