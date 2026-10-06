
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NuctiusNow.Services;

public interface ILocalLlmService
{
    Task<string?> SummarizeAsync(string text, int maxTokens = 220);
    Task<double?> ScoreCandidateAsync(string userProfile, string candidateText);
    Task<List<string?>> SummarizeBatchAsync(List<string> texts, int maxTokens = 220);
    Task<List<double?>> BatchScoreAsync(string userProfile, List<string> candidates);
    Task<List<(string Title, string Url, string Snippet)>> SearchWebAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);
    Task<List<(string Title, string Url, string Snippet)>> SearchNewsAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default, int startingPage = 1);
    Task<string?> ExtractArticleBodyTextAsync(string articleUrl, CancellationToken cancellationToken = default);

    Task<string?> GenerateNewsSummaryAsync(
        string title,
        string evidence,
        string? category = null,
        CancellationToken cancellationToken = default);

    Task<List<string>> ExtractInterestKeywordsAsync(
        string title,
        string context,
        int maxKeywords = 8,
        CancellationToken cancellationToken = default);

    Task<string?> GenerateOriginalArticleAsync(
        string title,
        string evidence,
        string? category = null,
        CancellationToken cancellationToken = default);

    Task<string?> SearchImageUrlAsync(
        string query,
    CancellationToken cancellationToken = default);

    Task<string?> ChatAsync(
    string userMessage,
    string? systemContext = null,
    IReadOnlyList<(string Role, string Content)>? history = null,
    CancellationToken cancellationToken = default);
}

public class LocalLlmService : ILocalLlmService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _endpoint;
    private readonly string _chatEndpoint;
    private readonly string _webSearchPluginId;
    private readonly string _searxngUrl;
    private readonly string? _apiToken;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxConcurrency;
    private readonly int _batchSize;
    private readonly string? _configuredModelHint;
    private string? _resolvedModelKey;
    private readonly SemaphoreSlim _modelResolveSemaphore = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ILogger<LocalLlmService> _logger;

    public LocalLlmService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<LocalLlmService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;

        var endpointFromConfig = config.GetValue<string>("LocalLlm:Endpoint");
        var chatEndpointFromConfig = config.GetValue<string>("LocalLlm:ChatEndpoint");
        _webSearchPluginId = config.GetValue<string>("LocalLlm:WebSearchPluginId") ?? "rzk/searxng-search";
        _searxngUrl = (config.GetValue<string>("LocalLlm:SearXngUrl") ?? "http://localhost:8081").TrimEnd('/');
        _apiToken = config.GetValue<string>("LocalLlm:ApiToken");
        if (string.IsNullOrWhiteSpace(endpointFromConfig))
        {
            _endpoint = "http://26.209.188.105:1234/v1/completions";
        }
        else if (Uri.IsWellFormedUriString(endpointFromConfig, UriKind.Absolute))
        {
            _endpoint = endpointFromConfig!;
        }
        else
        {
            _endpoint = "http://127.0.0.1:1234/v1/completions";
        }

        _maxConcurrency = config.GetValue<int?>("LocalLlm:MaxConcurrency") ?? 2;
        _batchSize = config.GetValue<int?>("LocalLlm:BatchSize") ?? 8;

        // Optional model hint. The service will resolve the exact loaded model key
        // from LM Studio instead of guessing or triggering JIT loading.
        _configuredModelHint = config.GetValue<string?>("LocalLlm:Model")
            ?? config.GetValue<string?>("LocalLlm:ModelId");

        _logger.LogInformation(
            "LocalLlmService initialized. Configured model hint: {ModelHint}",
            string.IsNullOrWhiteSpace(_configuredModelHint) ? "(auto-detect loaded model)" : _configuredModelHint);

        // Always normalize the configured chat endpoint to LM Studio's native
        // /api/v1/chat route. This also protects against an old configuration
        // that still contains /v1/completions.
        _chatEndpoint = !string.IsNullOrWhiteSpace(chatEndpointFromConfig)
            ? BuildNativeChatEndpoint(chatEndpointFromConfig!)
            : BuildNativeChatEndpoint(_endpoint);

        _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

    }

    public async Task<string?> SummarizeAsync(string text, int maxTokens = 220)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var prompt = $"Summarize the following news article in one short paragraph (max {maxTokens} characters):\n\n{text}";

        try
        {
            return (await CallPlainNativeChatAsync(
                prompt,
                maxOutputTokens: Math.Clamp(maxTokens, 32, 512),
                temperature: 0.1,
                CancellationToken.None))?.Trim();
        }
        catch
        {
            return null;
        }
    }

    public async Task<double?> ScoreCandidateAsync(string userProfile, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(candidateText)) return null;

        var prompt = $"Given the user profile or preferences:\n{userProfile}\n\nRate the relevance of this candidate news article on a scale 0-100 and only return the numeric score:\n{candidateText}";

        try
        {
            var resultText = await CallPlainNativeChatAsync(
                prompt,
                maxOutputTokens: 10,
                temperature: 0,
                CancellationToken.None);

            if (string.IsNullOrWhiteSpace(resultText)) return null;

            var txt = new string(resultText.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
            return double.TryParse(
                txt,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var score)
                ? Math.Clamp(score, 0.0, 100.0)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<string?>> SummarizeBatchAsync(List<string> texts, int maxTokens = 220)
    {
        var results = new List<string?>();
        if (texts == null || texts.Count == 0) return results;

        for (var i = 0; i < texts.Count; i += _batchSize)
        {
            var batch = texts.Skip(i).Take(_batchSize).ToList();
            var marker = "<|SUMMARY|>";
            var sb = new StringBuilder();
            sb.AppendLine($"For each article below, return one short paragraph summary (max {maxTokens} characters).\nOutput the summaries in the same order separated exactly by the token {marker} with no extra commentary.");
            sb.AppendLine();

            for (var idx = 0; idx < batch.Count; idx++)
            {
                sb.AppendLine($"Article {idx + 1}:");
                sb.AppendLine(batch[idx]);
                sb.AppendLine();
            }

            try
            {
                var text = await CallPlainNativeChatAsync(
                    sb.ToString(),
                    maxOutputTokens: Math.Clamp(maxTokens * batch.Count, 64, 2000),
                    temperature: 0.1,
                    CancellationToken.None) ?? string.Empty;

                _logger.LogInformation(
                    "LocalLlmService.SummarizeBatchAsync raw response: {ResponseText}",
                    text);

                var parts = text
                    .Split(new[] { marker }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .ToList();

                if (parts.Count != batch.Count)
                {
                    parts = Regex.Split(text.Trim(), "\\n\\n+")
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0)
                        .ToList();
                }

                for (var j = 0; j < batch.Count; j++)
                    results.Add(j < parts.Count ? parts[j] : null);
            }
            catch
            {
                results.AddRange(batch.Select(_ => (string?)null));
            }
        }

        return results;
    }

    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var value = url.Trim();

        // Convert Markdown links such as [https://example.com](https://example.com)
        // into the actual URL before storing them in News.Hyperlink.
        var markdown = Regex.Match(
            value,
            @"^\s*\[[^\]]+\]\((?<url>https?://[^\)]+)\)\s*$",
            RegexOptions.IgnoreCase);

        if (markdown.Success)
            return markdown.Groups["url"].Value.Trim();

        var angleBrackets = Regex.Match(
            value,
            @"^\s*<(?<url>https?://[^>]+)>\s*$",
            RegexOptions.IgnoreCase);

        if (angleBrackets.Success)
            return angleBrackets.Groups["url"].Value.Trim();

        return value.Trim('\"', '\'');
    }

    public async Task<List<(string Title, string Url, string Snippet)>> SearchWebAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        maxResults = Math.Clamp(maxResults, 1, 20);

        // The argument is a SEARCH TASK. It may contain a category, topic,
        // preferences or other context. The model must infer useful search terms
        // and use the configured SearXNG search plugin.
        var prompt = $"""
You are NuctiusNow's web-search agent.

Your job is to search the public internet using the available SearXNG search tool.
The text below is a SEARCH TASK, not necessarily a literal search string.
Infer concise search terms from the task and look for current, relevant news.
For category searches, explore indirect but relevant entities and topics instead of
searching only the category word itself. For example, Politics may include governments,
leaders, legislation or diplomacy; Technology may include AI, NVIDIA, semiconductors,
software or cybersecurity; Sports may include soccer, athletics, basketball, etc.

MANDATORY:
1. Use the available search_web tool EXACTLY ONCE before answering.
2. Search the public internet; do not answer from model memory.
3. Return the real results supplied by the tool; do not invent URLs or stories.
4. Prefer distinct articles and publishers when possible.
5. Do not retry the tool and do not use any other tool.
6. NuctiusNow parses the tool output directly; do not create or reformat a second JSON response.

MAXIMUM RESULTS: {maxResults}

SEARCH TASK:
{query}
""";

        try
        {
            var nativeResponse = await CallWebIntegratedChatAsync(
                prompt,
                maxTokens: 256,
                cancellationToken: cancellationToken,
                includePageFetchTool: false);

            var searchTool = nativeResponse.ToolCalls
                .LastOrDefault(tc => tc.Tool.EndsWith("search_web", StringComparison.OrdinalIgnoreCase));

            if (searchTool != null &&
                TryParseSearchToolOutput(searchTool.Output, maxResults, results))
            {
                _logger.LogInformation(
                    "SearchWebAsync: parsed {Count} results directly from search_web for task '{Task}'",
                    results.Count,
                    query);
                return results;
            }

            // Fallback for plugin/API variants that place the usable JSON in the final message.
            if (!string.IsNullOrWhiteSpace(nativeResponse.Message))
            {
                TryParseSearchResultsFromText(nativeResponse.Message, maxResults, results);
            }

            if (results.Count == 0)
            {
                _logger.LogWarning(
                    "SearchWebAsync: LM Studio returned no usable tool result or message for search task '{Task}'",
                    query);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SearchWebAsync failed for search task '{Task}' using plugin {PluginId}",
                query,
                _webSearchPluginId);
        }

        return results;
    }

    /// <summary>
    /// Searches SearXNG's dedicated News category directly. This is intentionally
    /// separate from SearchWebAsync so recommendation/article selection cannot drift
    /// into arbitrary pages such as product landing pages, downloads or documentation.
    /// The direct API uses categories=news, then applies a second application-level
    /// article-page filter. If the local SearXNG JSON API is unavailable, the method
    /// falls back to the LM Studio plugin with the !news syntax.
    /// </summary>
    public async Task<List<(string Title, string Url, string Snippet)>> SearchNewsAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default,
        int startingPage = 1)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        maxResults = Math.Clamp(maxResults, 1, 20);

        // Primary path: direct SearXNG News category. We deliberately search several
        // pages/time windows so this behaves like a deep news search instead of trusting
        // a single result page.
        foreach (var timeRange in new[] { "month", "year" })
        {
            for (var page = Math.Max(1, startingPage); page < Math.Max(1, startingPage) + 3 && results.Count < maxResults; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var pageResults = await SearchSearxngCategoryAsync(
                        query,
                        "news",
                        maxResults,
                        timeRange,
                        page,
                        cancellationToken);

                    foreach (var item in pageResults)
                    {
                        if (results.Any(x => x.Url.Equals(item.Url, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        results.Add(item);
                        if (results.Count >= maxResults)
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Direct SearXNG news search failed for '{Query}' (range={TimeRange}, page={Page}).",
                        query,
                        timeRange,
                        page);
                    break;
                }
            }

            if (results.Count >= maxResults)
                break;
        }

        if (results.Count > 0)
        {
            _logger.LogInformation(
                "SearchNewsAsync: accepted {Count} article pages from SearXNG News for '{Query}'.",
                results.Count,
                query);

            return results.Take(maxResults).ToList();
        }

        // Fallback only when the direct API is unavailable. The !news bang is passed
        // through by the rzk plugin to SearXNG and gives the same category semantics.
        try
        {
            var fallbackTask = $"""
!news {query}

STRICT REQUIREMENT: return ONLY real news/article pages about the requested topic. Reject home pages, category pages, download pages, product/marketing pages, documentation, guides, wikis, forums, account pages, search pages, stores and unrelated utility pages.
""";

            var nativeResponse = await CallWebIntegratedChatAsync(
                fallbackTask,
                maxTokens: 384,
                cancellationToken: cancellationToken,
                includePageFetchTool: false);

            var searchTool = nativeResponse.ToolCalls
                .LastOrDefault(tc => tc.Tool.EndsWith("search_web", StringComparison.OrdinalIgnoreCase));

            if (searchTool != null)
                TryParseSearchToolOutput(searchTool.Output, maxResults, results);

            if (results.Count == 0 && !string.IsNullOrWhiteSpace(nativeResponse.Message))
                TryParseSearchResultsFromText(nativeResponse.Message, maxResults, results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio !news fallback failed for '{Query}'", query);
        }

        return results
            .Where(r => LooksLikeNewsArticle(r.Title, r.Url, r.Snippet))
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(maxResults)
            .ToList();
    }

    private async Task<List<(string Title, string Url, string Snippet)>> SearchSearxngCategoryAsync(
        string query,
        string category,
        int maxResults,
        string? timeRange,
        int page,
        CancellationToken cancellationToken)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        if (string.IsNullOrWhiteSpace(_searxngUrl))
            return results;

        var searchParams = new List<string>
        {
            $"q={Uri.EscapeDataString(query.Trim())}",
            "format=json",
            $"categories={Uri.EscapeDataString(category)}",
            $"pageno={Math.Max(page, 1)}",
            "safesearch=1"
        };

        if (!string.IsNullOrWhiteSpace(timeRange))
            searchParams.Add($"time_range={Uri.EscapeDataString(timeRange)}");

        var url = $"{_searxngUrl}/search?{string.Join("&", searchParams)}";
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        using var response = await client.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"SearXNG returned HTTP {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("results", out var resultArray) ||
            resultArray.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in resultArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var title = GetJsonString(item, "title").Trim();
            var urlValue = NormalizeUrl(GetJsonString(item, "url"));
            var snippet = GetJsonString(item, "content");

            if (string.IsNullOrWhiteSpace(snippet))
                snippet = GetJsonString(item, "snippet");

            if (!LooksLikeNewsArticle(title, urlValue, snippet))
                continue;

            if (results.Any(x => x.Url.Equals(urlValue, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add((title, urlValue, snippet.Trim()));

            if (results.Count >= maxResults)
                break;
        }

        _logger.LogDebug(
            "Direct SearXNG category search '{Category}' range={TimeRange} page={Page} returned {Count} article pages for '{Query}'.",
            category,
            timeRange,
            page,
            results.Count,
            query);

        return results;
    }

    private static bool LooksLikeNewsArticle(string? title, string? url, string? snippet)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var titleText = title?.Trim() ?? string.Empty;
        var snippetText = snippet?.Trim() ?? string.Empty;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return false;

        // A news-category engine can still surface utility/marketing pages. Reject them
        // before they ever reach the database.
        var blockedHosts = new[]
        {
            "youtube.com", "www.youtube.com", "youtu.be",
            "reddit.com", "www.reddit.com",
            "facebook.com", "www.facebook.com",
            "x.com", "www.x.com", "twitter.com", "www.twitter.com",
            "instagram.com", "www.instagram.com",
            "tiktok.com", "www.tiktok.com",
            "linkedin.com", "www.linkedin.com"
        };

        if (blockedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return false;

        var blockedPathTokens = new[]
        {
            "/download", "/downloads", "/store", "/shop", "/pricing", "/support",
            "/help", "/docs", "/documentation", "/wiki", "/forum", "/forums",
            "/careers", "/jobs", "/login", "/signin", "/signup", "/account",
            "/search", "/products", "/product", "/features", "/system-requirements",
            "/contact", "/community", "/events", "/newsletter", "/subscribe"
        };

        if (blockedPathTokens.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return false;

        var blockedTitleTokens = new[]
        {
            "documentation", "wiki", "user guide", "getting started",
            "system requirements", "pricing", "sign in", "log in", "login",
            "register", "careers", "faq", "manual", "official website"
        };

        if (blockedTitleTokens.Any(token =>
                titleText.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (titleText.StartsWith("download", StringComparison.OrdinalIgnoreCase) ||
            titleText.StartsWith("support", StringComparison.OrdinalIgnoreCase) ||
            titleText.StartsWith("store", StringComparison.OrdinalIgnoreCase) ||
            titleText.StartsWith("shop", StringComparison.OrdinalIgnoreCase) ||
            titleText.StartsWith("jobs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Specific example reported during testing: Bungie's Destiny New Light page is
        // a product/download landing page, not a news article.
        if (host.Equals("bungie.net", StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith("/7/en/destiny/", StringComparison.OrdinalIgnoreCase))
            return false;

        // Avoid obvious generic category/home/portal destinations even when they carry a
        // description. A story URL should normally contain at least one meaningful segment.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var genericLastSegments = new[]
        {
            "news", "latest", "home", "index", "default", "all", "topics", "category"
        };

        if (segments.Length == 1 &&
            genericLastSegments.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // A genuine search result should carry both a meaningful title and a description.
        return titleText.Length >= 10 && snippetText.Length >= 30;
    }

    public async Task<string?> SearchImageUrlAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        // Preferred path: ask SearXNG directly for image results so we can read img_src.
        // The rzk plugin currently discards img_src from SearXNG's image result schema,
        // so using the API directly is the reliable way to obtain the actual image URL.
        try
        {
            var imageUrl = await SearchSearxngImageUrlAsync(query, cancellationToken);
            if (IsUsableImageUrl(imageUrl))
                return imageUrl!.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Direct SearXNG image search failed for '{Query}'. Falling back to LM Studio.",
                query);
        }

        // Fallback for installations where the app cannot directly reach SearXNG.
        var prompt = $$$"""
You are NuctiusNow's image-search assistant.

MANDATORY: use the available search_web tool EXACTLY ONCE before answering.

Search this topic in the SearXNG Images category using the !images search modifier:
!images {{{query}}}

Find ONE relevant editorial/news photograph.
Do NOT return an article page, product page, download page, search page, thumbnail page,
or an explanation.
Return ONLY JSON:
{"imageUrl":"https://example.com/image.jpg"}

If no direct image URL is available:
{"imageUrl":""}
""";

        try
        {
            var nativeResponse = await CallWebIntegratedChatAsync(
                prompt,
                maxTokens: 500,
                cancellationToken: cancellationToken,
                includePageFetchTool: false);

            var text = nativeResponse.Message;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            text = Regex.Replace(
                text.Trim(),
                @"^```(?:json)?\s*|\s*```$",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();

            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.TryGetProperty("imageUrl", out var imageUrlProperty))
                {
                    var imageUrl = imageUrlProperty.GetString();
                    if (IsUsableImageUrlFromModel(imageUrl))
                        return imageUrl!.Trim();
                }
            }
            catch
            {
                // Continue to URL fallback.
            }

            foreach (var url in Regex.Matches(
                text,
                @"https?://[^\s""'<>]+",
                RegexOptions.IgnoreCase)
                .Select(m => m.Value.Trim())
                .Distinct())
            {
                if (IsUsableImageUrlFromModel(url))
                    return url;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SearchImageUrlAsync failed for '{Query}'", query);
        }

        return null;
    }

    private async Task<string?> SearchSearxngImageUrlAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var searchParams = new[]
        {
            $"q={Uri.EscapeDataString(query.Trim())}",
            "format=json",
            "categories=images",
            "pageno=1",
            "safesearch=1"
        };

        var url = $"{_searxngUrl}/search?{string.Join("&", searchParams)}";
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        using var response = await client.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"SearXNG returned HTTP {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("results", out var resultArray) ||
            resultArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in resultArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            // SearXNG image results expose img_src / thumbnail_src in addition to url.
            // img_src is the actual image; url is generally the landing page.
            var direct = GetJsonString(item, "img_src");

            if (!IsUsableImageUrl(direct))
                direct = GetJsonString(item, "thumbnail_src");

            if (!IsUsableImageUrl(direct) &&
                item.TryGetProperty("formats", out var formats) &&
                formats.ValueKind == JsonValueKind.Array)
            {
                foreach (var format in formats.EnumerateArray())
                {
                    if (format.ValueKind != JsonValueKind.Object)
                        continue;

                    var formatUrl = GetJsonString(format, "url");
                    if (IsUsableImageUrl(formatUrl))
                    {
                        direct = formatUrl;
                        break;
                    }
                }
            }

            if (IsUsableImageUrl(direct))
                return direct.Trim();
        }

        return null;
    }

    public async Task<string?> GenerateOriginalArticleAsync(
        string title,
        string evidence,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(evidence))
        {
            return null;
        }

        var categoryText = string.IsNullOrWhiteSpace(category)
            ? "General"
            : category.Trim();

        // The evidence passed here comes from the application's News-category search
        // and article-body extraction. The model's job is synthesis, not web browsing.
        var prompt = $"""
You are the senior writer for NuctiusNow.

Write ONE original, factual news article about the story identified by the title below.
The supplied evidence contains text obtained from real news/article pages found through
a dedicated News-category web search. Use that evidence as the factual record.

TITLE:
{title}

CATEGORY:
{categoryText}

VERIFIED NEWS EVIDENCE:
{evidence}

WRITING REQUIREMENTS:
- Produce one complete article, not a summary and not a list.
- Use a clear news structure: lead, relevant developments, context, and closing paragraph.
- Write 500 to 900 words when the evidence supports that level of detail.
- Use 6 to 10 substantive paragraphs.
- Every paragraph must add new information.
- Use the source material only to understand and verify facts. Express every sentence in completely original wording.
- Do not copy, reproduce, closely paraphrase, or reconstruct the wording of any source.
- The requested result is a new explanatory NuctiusNow article, NOT the extracted source article.
- Treat extracted source text as transient factual evidence only; never reproduce it as the answer.
- Focus on what happened, who or what is involved, what changed, why it matters, and the relevant context supported by the evidence.
- Do not invent names, dates, numbers, quotes, motives, causes or events.
- Never create a direct quotation unless that quotation is explicitly present in the evidence and is genuinely necessary.
- Do not mention the evidence, the web search, SearXNG, the AI, the model, prompts, or article-generation process.
- Do not discuss whether the original publisher was accessible.
- Do not refuse the task merely because one source is incomplete. Use the factual material that is actually present.
- Only return an insufficiency marker when the supplied evidence contains no usable factual material for an article.
- Return ONLY the article body.
- Plain text only: no Markdown, no HTML, no JSON, no headings such as "Article" or "Summary", and no preamble.

If the evidence truly contains no usable facts, return exactly:
[INSUFFICIENT_EVIDENCE]
""";

        try
        {
            var article = (await CallPlainNativeChatAsync(
                prompt,
                maxOutputTokens: 2400,
                temperature: 0.15,
                cancellationToken) ?? string.Empty).Trim();

            var fenced = Regex.Match(
                article,
                @"^```(?:text|markdown)?\s*(.*?)\s*```$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (fenced.Success)
                article = fenced.Groups[1].Value.Trim();

            if (article.Equals("[INSUFFICIENT_EVIDENCE]", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "GenerateOriginalArticleAsync: model reported insufficient evidence for '{Title}'",
                    title);
                return null;
            }

            if (ContainsArticleRefusal(article))
            {
                _logger.LogWarning(
                    "GenerateOriginalArticleAsync: model returned a refusal/non-article response for '{Title}'",
                    title);
                return null;
            }

            if (article.Length < 700)
            {
                _logger.LogWarning(
                    "GenerateOriginalArticleAsync: generated article was too short ({Length} chars) for '{Title}'",
                    article.Length,
                    title);
                return null;
            }

            _logger.LogInformation(
                "GenerateOriginalArticleAsync: generated {Length} characters for '{Title}'",
                article.Length,
                title);

            return article;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "GenerateOriginalArticleAsync failed for '{Title}'",
                title);
            return null;
        }
    }


    public async Task<string?> GenerateNewsSummaryAsync(
        string title,
        string evidence,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(evidence))
            return null;

        var categoryText = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        var prompt = $"""
You are the NuctiusNow news summarization editor.

Create ONE very short original summary of the news story below.

TITLE:
{title}

CATEGORY:
{categoryText}

FACTUAL EVIDENCE:
{evidence}

Rules:
- Summarize the story as a whole, not just one detail.
- Explain in simple language what happened and the most important point.
- Use only facts supported by the evidence.
- Write 30 to 55 words.
- Do not copy wording from any source.
- Do not quote the source.
- Do not mention sources, evidence, SearXNG, AI, prompts, or this task.
- Do not produce a title or label.
- Return ONLY the summary paragraph.
""";

        try
        {
            var summary = (await CallPlainNativeChatAsync(
                prompt,
                maxOutputTokens: 180,
                temperature: 0.05,
                cancellationToken) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(summary) || summary.StartsWith("[", StringComparison.Ordinal))
                return null;

            return summary.Length > 420 ? summary[..420].TrimEnd() : summary;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GenerateNewsSummaryAsync failed for '{Title}'", title);
            return null;
        }
    }

    public async Task<List<string>> ExtractInterestKeywordsAsync(
        string title,
        string context,
        int maxKeywords = 8,
        CancellationToken cancellationToken = default)
    {
        var fallback = ExtractInterestKeywordsFallback(title, context, maxKeywords);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(context))
            return fallback;

        maxKeywords = Math.Clamp(maxKeywords, 3, 12);
        var prompt = $"""
You are NuctiusNow's user-interest keyword extractor.

From the news title and context below, identify {maxKeywords} or fewer concrete
interests that a reader could reasonably care about in future news searches.

TITLE:
{title}

CONTEXT:
{context}

Rules:
- Return a JSON array of strings only.
- Extract specific entities and durable topics, not generic words.
- Preserve proper names and multi-word concepts.
- Prefer useful terms such as companies, products, games, teams, people, technologies,
  franchises, diseases, places, events, industries and major topics.
- Avoid generic words such as news, article, today, update, information, report, latest.
- Do not invent an entity that is not supported by the supplied text.
""";

        try
        {
            var raw = (await CallPlainNativeChatAsync(
                prompt,
                maxOutputTokens: 220,
                temperature: 0,
                cancellationToken) ?? string.Empty).Trim();
            raw = Regex.Replace(raw, @"^```(?:json)?\s*|\s*```$", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var values = doc.RootElement.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => NormalizeInterestKeyword(x.GetString()))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(maxKeywords)
                        .ToList();

                    return values.Count >= 2 ? values : fallback;
                }
            }
            catch (JsonException)
            {
                // Use deterministic extraction below.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExtractInterestKeywordsAsync failed for '{Title}'", title);
        }

        return fallback;
    }

    private static List<string> ExtractInterestKeywordsFallback(
        string title,
        string context,
        int maxKeywords)
    {
        var combined = $"{title} {context}";
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "from", "this", "that", "about", "into", "after",
            "before", "over", "under", "news", "article", "latest", "current", "update",
            "information", "report", "says", "said", "will", "would", "could", "have", "has",
            "are", "was", "were", "their", "they", "them", "been", "being", "what", "when",
            "where", "which", "while", "than", "also", "more", "some", "new", "today"
        };

        var phrases = Regex.Matches(combined, @"\b[A-Z][\p{L}\p{N}_-]*(?:\s+[A-Z][\p{L}\p{N}_-]*){0,3}")
            .Select(m => NormalizeInterestKeyword(m.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => x!.Length >= 2 && !stop.Contains(x!))
            .ToList();

        var words = Regex.Matches(combined.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}_-]{2,}")
            .Select(m => m.Value)
            .Where(x => !stop.Contains(x))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(maxKeywords * 2)
            .ToList();

        return phrases.Concat(words)
            .Select(NormalizeInterestKeyword)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxKeywords, 3, 12))
            .ToList()!;
    }

    private static string NormalizeInterestKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var cleaned = Regex.Replace(value.Trim(), @"\s+", " ");
        cleaned = cleaned.Trim(' ', ',', '.', ':', ';', '\"', '\'', '`', '[', ']');
        return cleaned.Length > 120 ? cleaned[..120].Trim() : cleaned;
    }

    private static bool ContainsArticleRefusal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var normalized = text.Trim();

        var refusalPrefixes = new[]
        {
            "i can't provide",
            "i cannot provide",
            "i can't write",
            "i cannot write",
            "i'm unable to",
            "i am unable to",
            "unable to provide",
            "unable to write",
            "cannot create an article",
            "can't create an article",
            "i don't have enough information",
            "i do not have enough information"
        };

        return refusalPrefixes.Any(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsableImageUrlFromModel(string? url)
    {
        if (!IsUsableImageUrl(url))
            return false;

        if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri))
            return false;

        var path = uri.AbsolutePath.ToLowerInvariant();
        var query = uri.Query.ToLowerInvariant();

        var imageExtensions = new[]
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif" };

        if (imageExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        return path.Contains("/images/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/image/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/media/", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("format=jpg", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("format=jpeg", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("format=png", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("fm=jpg", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("fm=jpeg", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("fm=png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(
                url.Trim(),
                UriKind.Absolute,
                out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();

        var imageExtensions = new[]
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".svg"
        };

        if (imageExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Many image CDNs use extensionless URLs. SearXNG's img_src field already
        // identifies these as image results, so accept an extensionless HTTPS URL here.
        return uri.Scheme == Uri.UriSchemeHttps && path.Length > 1;
    }
    /// <summary>
    /// Uses the LLM web-search plugin to open a specific article URL and extract
    /// only the article's main body text. The result is intentionally plain text,
    /// not a summary and not HTML.
    /// </summary>
    public async Task<string?> ExtractArticleBodyTextAsync(
        string articleUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(articleUrl)) return null;

        articleUrl = NormalizeUrl(articleUrl);
        if (!Uri.TryCreate(articleUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("ExtractArticleBodyTextAsync: invalid article URL: {Url}", articleUrl);
            return null;
        }

        var prompt = $"""
You are NuctiusNow's article extraction agent.

MANDATORY: use the available web-search tool before answering.

Locate and read this exact article URL:
{articleUrl}

Return ONLY the complete substantive article body text.
Do not summarize, paraphrase, invent, or add commentary.
Exclude navigation, advertisements, cookie notices, comments, related-story widgets,
author biography boilerplate, Markdown, HTML, JSON, and the title unless it is part
of the actual article body.
If the exact article cannot be read, return an empty response.
""";

        try
        {
            var nativeResponse = await CallWebIntegratedChatAsync(
                prompt,
                maxTokens: 5000,
                cancellationToken: cancellationToken,
                includePageFetchTool: true);

            var extractedText = nativeResponse.Message?.Trim();

            if (!string.IsNullOrWhiteSpace(extractedText) && IsUsableArticleBody(extractedText))
            {
                _logger.LogInformation(
                    "ExtractArticleBodyTextAsync: extracted {Length} characters from {Url} using native LM Studio web integration",
                    extractedText.Length,
                    articleUrl);
                return extractedText;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExtractArticleBodyTextAsync web integration failed for {Url}", articleUrl);
        }

        // The deterministic HTML/Playwright fallback is intentionally kept.
        var extractedFromHtml = await ExtractArticleBodyFromHtmlAsync(articleUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(extractedFromHtml))
        {
            _logger.LogInformation(
                "ExtractArticleBodyTextAsync: extracted {Length} characters from {Url} using HTML fallback",
                extractedFromHtml.Length,
                articleUrl);
            return extractedFromHtml;
        }

        _logger.LogWarning("ExtractArticleBodyTextAsync: no usable article body was extracted from {Url}", articleUrl);
        return null;
    }

    private async Task<string?> ExtractArticleBodyFromHtmlAsync(string articleUrl, CancellationToken cancellationToken)
    {
        try
        {
            var pageClient = _httpFactory.CreateClient();
            pageClient.Timeout = TimeSpan.FromSeconds(30);
            pageClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/156.0 Safari/537.36 NuctiusNow/1.0");

            using var response = await pageClient.GetAsync(articleUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "ExtractArticleBodyFromHtmlAsync: article HTTP status {StatusCode} for {Url}",
                    (int)response.StatusCode,
                    articleUrl);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var readableText = ExtractReadableHtmlText(html);

            if (string.IsNullOrWhiteSpace(readableText))
                return null;

            // Do not send an unbounded web page to the local model.
            const int maxHtmlChars = 120_000;
            if (readableText.Length > maxHtmlChars)
                readableText = readableText[..maxHtmlChars];

            var prompt = $"""
You are an article extraction engine.

The following text was extracted from the HTML of this exact news page:
{articleUrl}

Extract ONLY the complete main article body from the supplied page text.

Rules:
- Do NOT summarize or paraphrase.
- Preserve the article's paragraphs and normal reading order.
- Keep substantive article paragraphs only.
- Exclude navigation, menus, advertisements, cookie notices, related stories, comments, newsletter boxes, social widgets, and author-bio boilerplate.
- Do not include the title unless it is part of the article body.
- Do not invent missing content.
- Return only plain article text. No Markdown, HTML, JSON, labels, or explanations.

PAGE TEXT:
{readableText}
""";

            var extracted = (await CallPlainNativeChatAsync(
                prompt,
                maxOutputTokens: 5000,
                temperature: 0,
                cancellationToken) ?? string.Empty).Trim();

            return IsUsableArticleBody(extracted) ? extracted : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExtractArticleBodyFromHtmlAsync failed for {Url}", articleUrl);
            return null;
        }
    }

    private static string ExtractReadableHtmlText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var articleMatch = Regex.Match(
            html,
            @"<article\b[^>]*>(?<article>.*?)</article>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var source = articleMatch.Success ? articleMatch.Groups["article"].Value : html;

        source = Regex.Replace(source, @"<script\b[^>]*>.*?</script>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<style\b[^>]*>.*?</style>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<noscript\b[^>]*>.*?</noscript>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<svg\b[^>]*>.*?</svg>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Keep paragraph/block boundaries after removing HTML tags.
        source = Regex.Replace(source, @"<\s*/?(?:p|div|br|li|h[1-6]|section|article)[^>]*>", "\n", RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"<[^>]+>", " ");
        source = WebUtility.HtmlDecode(source);
        source = Regex.Replace(source, @"[ \t]+", " ");
        source = Regex.Replace(source, @"\n[ \t]+", "\n");
        source = Regex.Replace(source, @"\n{3,}", "\n\n");

        return source.Trim();
    }

    private static bool IsUsableArticleBody(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.Length < 100) return false;
        if (value.StartsWith("[{", StringComparison.Ordinal) || value.StartsWith("[", StringComparison.Ordinal)) return false;
        if (value.StartsWith("{", StringComparison.Ordinal)) return false;
        if (value.StartsWith("I can't", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.StartsWith("I cannot", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.StartsWith("Unable to", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.StartsWith("I’m unable", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public async Task<List<double?>> BatchScoreAsync(string userProfile, List<string> candidates)
    {
        var results = new List<double?>();
        if (candidates == null || candidates.Count == 0) return results;

        for (var i = 0; i < candidates.Count; i += _batchSize)
        {
            var batch = candidates.Skip(i).Take(_batchSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Given the user profile and the following candidate articles, rate the relevance of each candidate on a scale from 0 to 100. Return only comma-separated numeric scores in the same order, with no extra commentary.");
            sb.AppendLine();
            sb.AppendLine("User profile:");
            sb.AppendLine(userProfile);
            sb.AppendLine();

            for (var idx = 0; idx < batch.Count; idx++)
                sb.AppendLine($"{idx + 1}) {batch[idx]}");

            try
            {
                var text = await CallPlainNativeChatAsync(
                    sb.ToString(),
                    maxOutputTokens: 64,
                    temperature: 0,
                    CancellationToken.None) ?? string.Empty;

                var nums = Regex.Matches(text, "[0-9]+(?:\\.[0-9]+)?")
                    .Select(m => m.Value)
                    .ToList();

                if (nums.Count >= batch.Count)
                {
                    for (var j = 0; j < batch.Count; j++)
                    {
                        if (double.TryParse(
                            nums[j].Replace(',', '.'),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var v))
                        {
                            results.Add(Math.Clamp(v, 0.0, 100.0));
                        }
                        else
                        {
                            results.Add(null);
                        }
                    }
                }
                else
                {
                    results.AddRange(batch.Select(_ => (double?)null));
                }
            }
            catch
            {
                results.AddRange(batch.Select(_ => (double?)null));
            }
        }

        return results;
    }

    /// <summary>
    /// Sends a normal generation request through LM Studio's native /api/v1/chat
    /// endpoint. All non-tool LLM generation in NuctiusNow uses this path so it
    /// matches the native response contract and returns the final assistant message
    /// rather than a reasoning item.
    /// </summary>
    private async Task<string?> CallPlainNativeChatAsync(
        string prompt,
        int maxOutputTokens,
        double temperature,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var modelKey = await ResolveLoadedModelKeyAsync(cancellationToken);

        var nativePayload = new Dictionary<string, object?>
        {
            ["model"] = modelKey,
            ["input"] = prompt,
            ["store"] = false,
            ["temperature"] = temperature,
            ["max_output_tokens"] = maxOutputTokens,
            // Qwen's thinking/reasoning stream can consume the complete output
            // allowance before it emits a final message. These calls need the
            // actual answer, not the hidden reasoning transcript.
            ["reasoning"] = "off"
        };

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        if (!string.IsNullOrWhiteSpace(_apiToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiToken);
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // -------------------------------------------------------------
            // Primary path: LM Studio native v1 chat API.
            // -------------------------------------------------------------
            try
            {
                using var response = await client.PostAsJsonAsync(
                    _chatEndpoint,
                    nativePayload,
                    _jsonOptions,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var native = await ParseNativeChatResponseAsync(response.Content);
                    var message = native.Message?.Trim();

                    if (!string.IsNullOrWhiteSpace(message))
                        return message;

                    _logger.LogWarning(
                        "LM Studio native chat returned no final message for a plain generation request. ToolCalls={ToolCalls}. Retrying with the OpenAI-compatible chat endpoint.",
                        native.ToolCalls.Count);
                }
                else
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning(
                        "LM Studio native generation failed: HTTP {StatusCode}. Response={Response}",
                        (int)response.StatusCode,
                        errorText.Length > 800 ? errorText[..800] : errorText);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "LM Studio native plain generation failed. Trying OpenAI-compatible chat as fallback.");
            }

            // -------------------------------------------------------------
            // Fallback path: /v1/chat/completions is a supported LM Studio
            // compatibility endpoint. This is ONLY used when the native call
            // does not yield a final message; search/tool workflows remain on
            // the native API above.
            // -------------------------------------------------------------
            var compatibilityEndpoint = BuildOpenAiChatCompletionsEndpoint(_chatEndpoint, _endpoint);
            var compatibilityPayload = new Dictionary<string, object?>
            {
                ["model"] = modelKey,
                ["messages"] = new object[]
                {
                    new Dictionary<string, string>
                    {
                        ["role"] = "user",
                        ["content"] = prompt
                    }
                },
                ["temperature"] = temperature,
                ["max_tokens"] = maxOutputTokens,
                ["stream"] = false
            };

            try
            {
                using var compatibilityResponse = await client.PostAsJsonAsync(
                    compatibilityEndpoint,
                    compatibilityPayload,
                    _jsonOptions,
                    cancellationToken);

                var raw = await compatibilityResponse.Content.ReadAsStringAsync(cancellationToken);

                if (!compatibilityResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "LM Studio OpenAI-compatible fallback failed: HTTP {StatusCode}. Response={Response}",
                        (int)compatibilityResponse.StatusCode,
                        raw.Length > 800 ? raw[..800] : raw);
                    return null;
                }

                var fallbackMessage = ParseOpenAiChatCompletionMessage(raw);
                if (!string.IsNullOrWhiteSpace(fallbackMessage))
                {
                    _logger.LogInformation(
                        "LM Studio OpenAI-compatible fallback returned a final message successfully.");
                    return fallbackMessage.Trim();
                }

                _logger.LogWarning(
                    "LM Studio OpenAI-compatible fallback returned no usable message. Raw response={Response}",
                    raw.Length > 2000 ? raw[..2000] : raw);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LM Studio OpenAI-compatible chat fallback failed.");
            }

            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string BuildOpenAiChatCompletionsEndpoint(
        string nativeChatEndpoint,
        string configuredEndpoint)
    {
        string candidate = !string.IsNullOrWhiteSpace(configuredEndpoint)
            ? configuredEndpoint.TrimEnd('/')
            : nativeChatEndpoint.TrimEnd('/');

        if (candidate.EndsWith("/v1/completions", StringComparison.OrdinalIgnoreCase))
            return candidate[..^"/v1/completions".Length] + "/v1/chat/completions";

        if (candidate.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return candidate;

        if (candidate.EndsWith("/api/v1/chat", StringComparison.OrdinalIgnoreCase))
            return candidate[..^"/api/v1/chat".Length] + "/v1/chat/completions";

        var uri = new Uri(candidate, UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Path = "/v1/chat/completions",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString();
    }

    private static string? ParseOpenAiChatCompletionMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    return content.GetString();

                // A few compatible servers return content blocks instead of one string.
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var parts = content.EnumerateArray()
                        .Select(item =>
                        {
                            if (item.ValueKind != JsonValueKind.Object)
                                return null;

                            if (item.TryGetProperty("text", out var text) &&
                                text.ValueKind == JsonValueKind.String)
                                return text.GetString();

                            if (item.TryGetProperty("content", out var nestedContent) &&
                                nestedContent.ValueKind == JsonValueKind.String)
                                return nestedContent.GetString();

                            return null;
                        })
                        .Where(part => !string.IsNullOrWhiteSpace(part))
                        .ToList();

                    return parts.Count == 0 ? null : string.Join("\n", parts);
                }
            }
        }
        catch (JsonException)
        {
            // The fallback response must be valid JSON; ignore malformed variants.
        }

        return null;
    }

    private static string BuildNativeChatEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "http://localhost:1234/api/v1/chat";

        var normalized = endpoint.TrimEnd('/');

        if (normalized.EndsWith("/v1/completions", StringComparison.OrdinalIgnoreCase))
            return normalized[..^"/v1/completions".Length] + "/api/v1/chat";

        if (normalized.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return normalized[..^"/v1/chat/completions".Length] + "/api/v1/chat";

        return normalized.Contains("/api/v1/chat", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized.TrimEnd('/') + "/api/v1/chat";
    }

    private sealed class NativeToolCallResult
    {
        public string Tool { get; init; } = string.Empty;
        public string? Output { get; init; }
    }

    private sealed class NativeChatResponse
    {
        public string? Message { get; set; }
        public List<NativeToolCallResult> ToolCalls { get; } = new();
    }

private async Task<NativeChatResponse> ParseNativeChatResponseAsync(
    HttpContent content)
    {
        var result = new NativeChatResponse();

        string raw;

        try
        {
            raw = await content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to read LM Studio native chat response body.");

            return result;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            _logger.LogWarning(
                "LM Studio native chat returned an empty response body.");

            return result;
        }

        try
        {
            _logger.LogDebug(
                "LM Studio native chat response length: {Length}",
                raw.Length);

            // Remove an eventual UTF-8 BOM.
            raw = raw.TrimStart('\uFEFF');

            using var doc = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            var root = doc.RootElement;

            JsonElement output;

            // Normal LM Studio /api/v1/chat response:
            // {
            //   "output": [...]
            // }
            if (root.TryGetProperty("output", out var directOutput) &&
                directOutput.ValueKind == JsonValueKind.Array)
            {
                output = directOutput;
            }
            // Defensive support in case a wrapper returns:
            // {
            //   "result": {
            //      "output": [...]
            //   }
            // }
            else if (root.TryGetProperty("result", out var resultElement) &&
                     resultElement.ValueKind == JsonValueKind.Object &&
                     resultElement.TryGetProperty("output", out var nestedOutput) &&
                     nestedOutput.ValueKind == JsonValueKind.Array)
            {
                output = nestedOutput;
            }
            else
            {
                _logger.LogWarning(
                    "LM Studio native chat response does not contain an output array. Raw response: {Response}",
                    raw.Length > 4000 ? raw[..4000] : raw);

                return result;
            }

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeProp) ||
                    typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = typeProp.GetString();

                // ---------------------------------------------------------
                // Tool call
                // ---------------------------------------------------------
                if (string.Equals(
                        type,
                        "tool_call",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var tool = item.TryGetProperty("tool", out var toolProp) &&
                               toolProp.ValueKind == JsonValueKind.String
                        ? toolProp.GetString() ?? string.Empty
                        : string.Empty;

                    string? outputText = null;

                    if (item.TryGetProperty(
                            "output",
                            out var outputProp))
                    {
                        outputText = outputProp.ValueKind switch
                        {
                            JsonValueKind.String =>
                                outputProp.GetString(),

                            JsonValueKind.Null =>
                                null,

                            _ =>
                                outputProp.GetRawText()
                        };
                    }

                    result.ToolCalls.Add(
                        new NativeToolCallResult
                        {
                            Tool = tool,
                            Output = outputText
                        });

                    _logger.LogInformation(
                        "LM Studio tool call received: Tool={Tool}, OutputLength={Length}",
                        tool,
                        outputText?.Length ?? 0);

                    continue;
                }

                // ---------------------------------------------------------
                // Final model message
                // ---------------------------------------------------------
                if (string.Equals(
                        type,
                        "message",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty(
                            "content",
                            out var contentProp) &&
                        contentProp.ValueKind == JsonValueKind.String)
                    {
                        var message = contentProp.GetString();

                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            // Keep the LAST message.
                            //
                            // This matters because LM Studio can return:
                            // reasoning
                            // tool_call
                            // reasoning
                            // message
                            //
                            // The final message is the useful answer.
                            result.Message = message;
                        }
                    }
                }
            }

            _logger.LogInformation(
                "LM Studio native response parsed successfully. ToolCalls={ToolCalls}, HasMessage={HasMessage}",
                result.ToolCalls.Count,
                !string.IsNullOrWhiteSpace(result.Message));

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "JSON parsing failed for LM Studio native /api/v1/chat response. " +
                "Raw response: {Response}",
                raw.Length > 8000 ? raw[..8000] : raw);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error while parsing LM Studio native chat response.");

            return result;
        }
    }

    private bool TryParseSearchToolOutput(
        string? toolOutput,
        int maxResults,
        List<(string Title, string Url, string Snippet)> destination)
    {
        if (string.IsNullOrWhiteSpace(toolOutput))
            return false;

        var text = toolOutput.Trim();

        // ---------------------------------------------------------
        // rzk/searxng-search can return the formatted search output
        // wrapped inside a JSON string.
        //
        // Example:
        //
        // "Search results for \"politics news\" ...
        // [1] POLITICO
        // URL: https://...
        // Snippet: ...
        // Source: brave"
        //
        // Decode that outer JSON string first.
        // ---------------------------------------------------------
        if (text.Length >= 2 &&
            text[0] == '"' &&
            text[^1] == '"')
        {
            try
            {
                var decoded = JsonSerializer.Deserialize<string>(text);

                if (!string.IsNullOrWhiteSpace(decoded))
                    text = decoded.Trim();
            }
            catch (JsonException)
            {
                _logger.LogDebug(
                    "TryParseSearchToolOutput: could not decode outer JSON string.");
            }
        }

        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        // ---------------------------------------------------------
        // CURRENT rzk/searxng-search FORMAT
        //
        // Search results for "query" (...):
        //
        // [1] Article title
        // URL: https://example.com/article
        // Snippet: Article description...
        // Source: brave
        //
        // [2] Another article
        // ...
        // ---------------------------------------------------------
        var resultRegex = new Regex(
            @"^\[(?<index>\d+)\]\s*(?<title>[^\n]+)\n" +
            @"URL:\s*(?<url>https?://[^\s]+)\n" +
            @"Snippet:\s*(?<snippet>.*?)\n" +
            @"Source:\s*(?<source>[^\n]+)",
            RegexOptions.Multiline |
            RegexOptions.Singleline |
            RegexOptions.IgnoreCase);

        var matches = resultRegex.Matches(text);

        foreach (Match match in matches)
        {
            if (destination.Count >= maxResults)
                break;

            var title = match.Groups["title"]
                .Value
                .Trim();

            var url = NormalizeUrl(
                match.Groups["url"]
                    .Value
                    .Trim());

            var snippet = match.Groups["snippet"]
                .Value
                .Trim();

            if (string.IsNullOrWhiteSpace(url))
                continue;

            // Prevent duplicate URLs.
            if (destination.Any(x =>
                    string.Equals(
                        x.Url,
                        url,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            destination.Add((
                title,
                url,
                snippet));
        }

        if (destination.Count > 0)
        {
            _logger.LogInformation(
                "TryParseSearchToolOutput: parsed {Count} formatted search results.",
                destination.Count);

            return true;
        }

        // ---------------------------------------------------------
        // FALLBACK:
        // Support actual JSON results too.
        //
        // This is intentionally attempted ONLY when the response
        // looks like JSON, preventing unnecessary JsonReaderException
        // messages for normal plugin text.
        // ---------------------------------------------------------
        if (text.StartsWith("{") ||
            text.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);

                if (AppendSearchResults(
                        doc.RootElement,
                        maxResults,
                        destination))
                {
                    _logger.LogInformation(
                        "TryParseSearchToolOutput: parsed {Count} JSON search results.",
                        destination.Count);

                    return true;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(
                    ex,
                    "TryParseSearchToolOutput: JSON fallback could not parse the output.");
            }
        }

        _logger.LogWarning(
            "TryParseSearchToolOutput: could not parse search tool output. " +
            "Output preview: {Output}",
            text.Length > 1500
                ? text[..1500]
                : text);

        return false;
    }

    private static bool TryParseSearchResultsFromText(
        string text,
        int maxResults,
        List<(string Title, string Url, string Snippet)> destination)
    {
        var jsonToParse = text.Trim();

        var matchCodeBlock = Regex.Match(
            jsonToParse,
            @"```(?:json)?\s*(\[.*?\])\s*```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (matchCodeBlock.Success)
        {
            jsonToParse = matchCodeBlock.Groups[1].Value.Trim();
        }
        else
        {
            var arrayMatch = Regex.Match(
                jsonToParse,
                @"\[\s*\{.*?\}\s*\]",
                RegexOptions.Singleline);

            if (arrayMatch.Success)
                jsonToParse = arrayMatch.Value.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonToParse);
            return AppendSearchResults(doc.RootElement, maxResults, destination);
        }
        catch
        {
            return false;
        }
    }

    private static bool AppendSearchResults(
        JsonElement root,
        int maxResults,
        List<(string Title, string Url, string Snippet)> destination)
    {
        JsonElement array;

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("results", out var resultsProp) &&
                 resultsProp.ValueKind == JsonValueKind.Array)
        {
            array = resultsProp;
        }
        else
        {
            return false;
        }

        foreach (var item in array.EnumerateArray().Take(maxResults))
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var title = GetJsonString(item, "title");
            var url = NormalizeUrl(GetJsonString(item, "url"));
            var snippet = GetJsonString(item, "content");

            if (string.IsNullOrWhiteSpace(snippet))
                snippet = GetJsonString(item, "snippet");

            if (string.IsNullOrWhiteSpace(snippet))
                snippet = GetJsonString(item, "summary");

            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(url))
            {
                destination.Add((title.Trim(), url, snippet.Trim()));
            }
        }

        return destination.Count > 0;
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) &&
               prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task<string?> ParseResponseTextAsync(HttpContent content)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                return t.GetString();

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("text", out var chText) && chText.ValueKind == System.Text.Json.JsonValueKind.String)
                    return chText.GetString();

                if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    return contentProp.GetString();
            }

            if (root.TryGetProperty("output", out var output) &&
                output.ValueKind == System.Text.Json.JsonValueKind.Array &&
                output.GetArrayLength() > 0)
            {
                string? lastMessage = null;
                string? firstContent = null;

                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (item.TryGetProperty("type", out var typeProp) &&
                        typeProp.ValueKind == JsonValueKind.String &&
                        string.Equals(typeProp.GetString(), "message", StringComparison.OrdinalIgnoreCase) &&
                        item.TryGetProperty("content", out var messageContent) &&
                        messageContent.ValueKind == JsonValueKind.String)
                    {
                        var value = messageContent.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            lastMessage = value;
                        continue;
                    }

                    if (firstContent == null &&
                        item.TryGetProperty("content", out var genericContent) &&
                        genericContent.ValueKind == JsonValueKind.String)
                    {
                        firstContent = genericContent.GetString();
                    }

                    if (firstContent == null &&
                        item.TryGetProperty("data", out var dataContent) &&
                        dataContent.ValueKind == JsonValueKind.String)
                    {
                        firstContent = dataContent.GetString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(lastMessage))
                    return lastMessage;

                if (!string.IsNullOrWhiteSpace(firstContent))
                    return firstContent;
            }

            var sb = new StringBuilder();
            CollectStrings(root, sb);
            var outText = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(outText) ? null : outText;
        }
        catch
        {
            return null;
        }
    }

    private void CollectStrings(System.Text.Json.JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                sb.Append(element.GetString());
                break;
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject()) CollectStrings(prop.Value, sb);
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectStrings(item, sb);
                break;
        }
    }

    private static string? ExtractLmStudioAssistantText(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? lastMessage = null;

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeElement))
                    continue;

                var type = typeElement.GetString();

                if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!item.TryGetProperty("content", out var contentElement))
                    continue;

                if (contentElement.ValueKind != JsonValueKind.String)
                    continue;

                var content = contentElement.GetString();

                if (!string.IsNullOrWhiteSpace(content))
                    lastMessage = content;
            }

            return lastMessage;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<NativeChatResponse> CallWebIntegratedChatAsync(
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken,
        bool includePageFetchTool = false)
    {
        var modelKey = await ResolveLoadedModelKeyAsync(cancellationToken);

        var allowedTools = includePageFetchTool &&
                           _webSearchPluginId.Contains("searxng", StringComparison.OrdinalIgnoreCase)
            ? new[] { "search_web", "fetch_page_content" }
            : new[] { "search_web" };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelKey,
            ["input"] = prompt,
            ["integrations"] = new object[]
            {
                new
                {
                    type = "plugin",
                    id = _webSearchPluginId,
                    allowed_tools = allowedTools
                }
            },
            ["store"] = false,
            ["temperature"] = 0,
            ["max_output_tokens"] = maxTokens
        };

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        if (!string.IsNullOrWhiteSpace(_apiToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiToken);
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var response = await client.PostAsJsonAsync(
                _chatEndpoint,
                payload,
                _jsonOptions,
                cancellationToken);

            var errorText = string.Empty;
            if (!response.IsSuccessStatusCode)
            {
                errorText = await response.Content.ReadAsStringAsync(cancellationToken);

                // If LM Studio says the selected key is not loaded, refresh the key once.
                if (errorText.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
                    errorText.Contains("No matching loaded model found", StringComparison.OrdinalIgnoreCase))
                {
                    _resolvedModelKey = null;
                    var refreshedKey = await ResolveLoadedModelKeyAsync(cancellationToken, forceRefresh: true);

                    if (!string.Equals(refreshedKey, modelKey, StringComparison.Ordinal))
                    {
                        payload["model"] = refreshedKey;

                        using var retry = await client.PostAsJsonAsync(
                            _chatEndpoint,
                            payload,
                            _jsonOptions,
                            cancellationToken);

                        if (retry.IsSuccessStatusCode)
                            return await ParseNativeChatResponseAsync(retry.Content);

                        errorText = await retry.Content.ReadAsStringAsync(cancellationToken);
                    }
                }

                _logger.LogWarning(
                    "LM Studio native web request failed: HTTP {StatusCode}. Response={Response}",
                    (int)response.StatusCode,
                    errorText.Length > 600 ? errorText[..600] : errorText);

                return new NativeChatResponse();
            }

            return await ParseNativeChatResponseAsync(response.Content);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<string> ResolveLoadedModelKeyAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_resolvedModelKey))
            return _resolvedModelKey!;

        await _modelResolveSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_resolvedModelKey))
                return _resolvedModelKey!;

            var modelsEndpoint = BuildModelsEndpoint(_chatEndpoint);
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            if (!string.IsNullOrWhiteSpace(_apiToken))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _apiToken);
            }

            using var response = await client.GetAsync(modelsEndpoint, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"LM Studio model list failed with HTTP {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "LM Studio /api/v1/models returned no 'models' array.");
            }

            var loadedLlmKeys = new List<string>();
            var matchingCandidates = new List<string>();

            foreach (var model in models.EnumerateArray())
            {
                if (!model.TryGetProperty("type", out var typeProp) ||
                    !string.Equals(typeProp.GetString(), "llm", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!model.TryGetProperty("loaded_instances", out var instances) ||
                    instances.ValueKind != JsonValueKind.Array ||
                    instances.GetArrayLength() == 0)
                    continue;

                if (!model.TryGetProperty("key", out var keyProp) ||
                    keyProp.ValueKind != JsonValueKind.String)
                    continue;

                var key = keyProp.GetString();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                loadedLlmKeys.Add(key);

                var displayName = model.TryGetProperty("display_name", out var displayProp) &&
                                   displayProp.ValueKind == JsonValueKind.String
                    ? displayProp.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(_configuredModelHint) &&
                    (key.Equals(_configuredModelHint, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(displayName) &&
                      displayName.Equals(_configuredModelHint, StringComparison.OrdinalIgnoreCase)) ||
                     key.Contains(_configuredModelHint, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(displayName) &&
                      displayName.Contains(_configuredModelHint, StringComparison.OrdinalIgnoreCase))))
                {
                    matchingCandidates.Add(key);
                }
            }

            string? selected;

            if (matchingCandidates.Count > 0)
            {
                selected = matchingCandidates[0];
            }
            else if (!string.IsNullOrWhiteSpace(_configuredModelHint))
            {
                var available = loadedLlmKeys.Count == 0
                    ? "(none)"
                    : string.Join(", ", loadedLlmKeys);

                throw new InvalidOperationException(
                    $"LM Studio has no loaded LLM matching '{_configuredModelHint}'. " +
                    $"Loaded model keys: {available}. Set LocalLlm:Model to the exact model key shown by /api/v1/models, or remove LocalLlm:Model to use auto-detection when exactly one LLM is loaded.");
            }
            else if (loadedLlmKeys.Count == 1)
            {
                selected = loadedLlmKeys[0];
            }
            else if (loadedLlmKeys.Count == 0)
            {
                throw new InvalidOperationException(
                    "LM Studio has no loaded LLM instance. Load the desired model in LM Studio before starting NuctiusNow; JIT loading is intentionally not used.");
            }
            else
            {
                throw new InvalidOperationException(
                    "LM Studio has multiple loaded LLMs and LocalLlm:Model does not identify which one NuctiusNow should use. Set LocalLlm:Model to the exact model key.");
            }

            _resolvedModelKey = selected;

            _logger.LogInformation(
                "Using already-loaded LM Studio model key: {ModelKey}",
                _resolvedModelKey);

            return _resolvedModelKey;
        }
        finally
        {
            _modelResolveSemaphore.Release();
        }
    }

    private static string BuildModelsEndpoint(string chatEndpoint)
    {
        var uri = new Uri(chatEndpoint, UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Path = "/api/v1/models",
            Query = string.Empty
        };
        return builder.Uri.ToString();
    }

    public async Task<string?> ChatAsync(
    string userMessage,
    string? systemContext = null,
    IReadOnlyList<(string Role, string Content)>? history = null,
    CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        var prompt = new StringBuilder();

        prompt.AppendLine(
            systemContext ??
            """
        You are NuctiusNow's AI assistant.

        Answer the user's question clearly and naturally.
        Do not invent facts.
        If you do not know something, say so.
        Keep answers concise unless the user asks for detail.
        """);

        prompt.AppendLine();
        prompt.AppendLine("CONVERSATION:");

        if (history != null)
        {
            foreach (var item in history.TakeLast(10))
            {
                var role = item.Role.Equals("assistant",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Assistant"
                    : "User";

                prompt.AppendLine($"{role}: {item.Content}");
            }
        }

        prompt.AppendLine();
        prompt.AppendLine($"User: {userMessage}");
        prompt.AppendLine();
        prompt.AppendLine("Assistant:");

        return await CallPlainNativeChatAsync(
            prompt.ToString(),
            maxOutputTokens: 700,
            temperature: 0.2,
            cancellationToken);
    }
}


