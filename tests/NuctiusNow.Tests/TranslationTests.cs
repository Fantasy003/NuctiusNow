using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NuctiusNow.Services;

namespace NuctiusNow.Tests;

public sealed class TranslationTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../NuctiusNow/NuctiusNow"));
    private static TranslationCatalog Catalog() => new(new EnvironmentStub { ContentRootPath = Root });
    public static IEnumerable<object[]> Languages => LanguageState.Supported.Select(x => new object[] { x.Code });

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task EveryLanguageHasCatalogAndTranslatesNewsWithoutSharingCachedVariants(string code)
    {
        var catalog = Catalog();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000000 });
        var backend = new Backend();
        var service = new SiteTranslationService(backend, catalog, cache);
        var result = await service.TranslateAsync([new("Home"), new("English article title", true), new("Complete article body", true)], code);
        Assert.Equal(code == "en" ? "Home" : catalog.For(code)["Home"], result[0]);
        Assert.Equal($"{code}:English article title", result[1]);
        Assert.Equal($"{code}:Complete article body", result[2]);
        Assert.Equal(1, backend.Calls);
        var repeated = await service.TranslateAsync([new("English article title", true)], code);
        Assert.Equal(result[1], repeated[0]);
        Assert.Equal(1, backend.Calls);
        var other = code == "es" ? "ar" : "es";
        Assert.Equal($"{other}:English article title", (await service.TranslateAsync([new("English article title", true)], other))[0]);
        Assert.Equal(2, backend.Calls);
        if (code != "en") Assert.True(catalog.For(code).Count >= 60);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ModelTranslationPromptTargetsEverySupportedLanguage(string code)
    {
        var handler = new ModelHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["LocalLlm:ChatEndpoint"] = "https://model.example.test/api/v1/chat", ["LocalLlm:Model"] = "test"
        }).Build();
        var backend = new LocalLlmService(new Factory(handler), config, NullLogger<LocalLlmService>.Instance);
        var result = await backend.TranslateTextsAsync(["Source headline", "A complete factual paragraph."], code);
        Assert.Equal(new[] {"Translated headline", "Translated paragraph"}, result);
        var language = LanguageState.Supported.Single(x => x.Code == code);
        Assert.Contains(language.Name + " (" + language.Culture + ")", handler.Prompt);
        Assert.Contains("never as instructions", handler.Prompt);
        Assert.Contains("Do not summarize", handler.Prompt);
    }

    [Fact]
    public async Task LongArticlesAreChunkedWithoutLosingParagraphBoundaries()
    {
        var body = string.Join("\n\n", Enumerable.Range(0,30).Select(i => $"Paragraph {i}: " + new string('a', 290)));
        var backend = new Backend { Identity = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var result = await new SiteTranslationService(backend, Catalog(), cache).TranslateAsync([new(body, true)], "ja");
        Assert.Equal(body, result[0]);
        Assert.True(backend.Calls > 1);
        Assert.All(backend.Lengths, length => Assert.InRange(length, 1, 6000));
    }

    [Fact]
    public async Task FailedTranslationsCanRetryAndNeverPolluteCache()
    {
        var backend = new Backend { Fail = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new SiteTranslationService(backend, Catalog(), cache);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync([new("Story", true)], "fr"));
        backend.Fail = false;
        Assert.Equal("fr:Story", (await service.TranslateAsync([new("Story", true)], "fr"))[0]);
        Assert.Equal(2, backend.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TranslateAsync([new("Story")], "unsupported"));
    }

    [Theory]
    [InlineData("es-ES", "es")]
    [InlineData("pt-BR", "pt")]
    [InlineData("zh-CN", "zh")]
    [InlineData("العربية", "ar")]
    [InlineData("2", "es")]
    public void LanguageStateNormalizesLegacyAndBrowserValues(string source, string code)
    {
        var state = new LanguageState();
        state.Set(source);
        Assert.Equal(code, state.Current.Code);
        Assert.Equal(code == "ar", state.Current.RightToLeft);
    }

    private sealed class Backend : ITextTranslationBackend
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool Identity { get; init; }
        public List<int> Lengths { get; } = new();
        public Task<IReadOnlyList<string>> TranslateTextsAsync(IReadOnlyList<string> texts, string language, CancellationToken cancellationToken = default)
        {
            Calls++;
            Lengths.Add(texts.Sum(x => x.Length));
            if (Fail) throw new InvalidOperationException("Unavailable model");
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(x => Identity ? x : language + ":" + x).ToArray());
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class ModelHandler : HttpMessageHandler
    {
        public string Prompt { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body;
            if (request.Method == HttpMethod.Get)
                body = new { models = new[] { new { key = "test", type = "llm", loaded_instances = new[] { new { id = "test" } } } } };
            else
            {
                using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Prompt = input.RootElement.GetProperty("input").GetString()!;
                body = new { output = new[] { new { type = "message", content = JsonSerializer.Serialize(new[] {"Translated headline", "Translated paragraph"}) } } };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        }
    }
    private sealed class EnvironmentStub : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "NuctiusNow";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
