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
    Task<List<(string Title, string Url, string Snippet)>> SearchWebAsync(string query, int maxResults = 5);
    Task<string?> ExtractArticleBodyTextAsync(string articleUrl, CancellationToken cancellationToken = default);

    Task<string?> GenerateOriginalArticleAsync(
        string title,
        string evidence,
        string? category = null,
        CancellationToken cancellationToken = default);

    Task<string?> SearchImageUrlAsync(
    string query,
    CancellationToken cancellationToken = default);
}

public class LocalLlmService : ILocalLlmService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _endpoint;
    private readonly string _chatEndpoint;
    private readonly string _webSearchPluginId;
    private readonly string? _apiToken;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxConcurrency;
    private readonly int _batchSize;
    private readonly string? _modelId;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ILogger<LocalLlmService> _logger;

    public LocalLlmService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<LocalLlmService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;

        var endpointFromConfig = config.GetValue<string>("LocalLlm:Endpoint");
        var chatEndpointFromConfig = config.GetValue<string>("LocalLlm:ChatEndpoint");
        _webSearchPluginId = config.GetValue<string>("LocalLlm:WebSearchPluginId") ?? "rzk/searxng-search";
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

        // Model ID configuration with fallback chain
        var configuredModel = config.GetValue<string?>("LocalLlm:Model") ??
                             config.GetValue<string?>("LocalLlm:ModelId");
        _modelId = string.IsNullOrWhiteSpace(configuredModel)
            ? "qwen3.5-9b-the-defiant-fable-uncensored-heretic-neo-imatrix-max-mtp"  // Common, reliable default model
            : configuredModel;

        _logger.LogInformation("LocalLlmService initialized with model: {ModelId}", _modelId);

        _chatEndpoint = !string.IsNullOrWhiteSpace(chatEndpointFromConfig)
            ? chatEndpointFromConfig!
            : BuildNativeChatEndpoint(_endpoint);

        _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

    }

    public async Task<string?> SummarizeAsync(string text, int maxTokens = 220)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var client = _httpFactory.CreateClient();
        var prompt = $"Summarize the following news article in one short paragraph (max {maxTokens} characters):\n\n{text}";

        var payload = new Dictionary<string, object?>()
        {
            ["prompt"] = prompt,
            ["max_tokens"] = maxTokens
        };

        if (!string.IsNullOrWhiteSpace(_modelId)) payload["model"] = _modelId;

        await _semaphore.WaitAsync();
        try
        {
            using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions);
            if (!resp.IsSuccessStatusCode) return null;

            var respText = await ParseResponseTextAsync(resp.Content);
            return respText?.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<double?> ScoreCandidateAsync(string userProfile, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(candidateText)) return null;

        var client = _httpFactory.CreateClient();
        var prompt = $"Given the user profile or preferences:\n{userProfile}\n\nRate the relevance of this candidate news article on a scale 0-100 and only return the numeric score:\n{candidateText}";
        var payload = new Dictionary<string, object?>()
        {
            ["prompt"] = prompt,
            ["max_tokens"] = 10
        };

        if (!string.IsNullOrWhiteSpace(_modelId)) payload["model"] = _modelId;

        await _semaphore.WaitAsync();
        try
        {
            using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions);
            if (!resp.IsSuccessStatusCode) return null;

            var resultText = await ParseResponseTextAsync(resp.Content);
            if (string.IsNullOrWhiteSpace(resultText)) return null;

            var txt = new string(resultText.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
            if (double.TryParse(txt, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var score))
            {
                return Math.Clamp(score, 0.0, 100.0);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<string?>> SummarizeBatchAsync(List<string> texts, int maxTokens = 220)
    {
        var results = new List<string?>();
        if (texts == null || texts.Count == 0) return results;

        for (int i = 0; i < texts.Count; i += _batchSize)
        {
            var batch = texts.Skip(i).Take(_batchSize).ToList();
            var marker = "<|SUMMARY|>";
            var sb = new StringBuilder();
            sb.AppendLine($"For each article below, return one short paragraph summary (max {maxTokens} characters).\nOutput the summaries in the same order separated exactly by the token {marker} with no extra commentary.");
            sb.AppendLine();
            for (int idx = 0; idx < batch.Count; idx++)
            {
                sb.AppendLine($"Article {idx + 1}:");
                sb.AppendLine(batch[idx]);
                sb.AppendLine();
            }

            var payload = new { prompt = sb.ToString(), max_tokens = maxTokens };
            var client = _httpFactory.CreateClient();

            await _semaphore.WaitAsync();
            try
            {
                using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions);
                if (!resp.IsSuccessStatusCode)
                {
                    results.AddRange(batch.Select(_ => (string?)null));
                    continue;
                }

                var text = await ParseResponseTextAsync(resp.Content) ?? string.Empty;

                try
                {
                    if (_logger != null)
                        _logger.LogInformation("LocalLlmService.SummarizeBatchAsync raw response: {ResponseText}", text);
                }
                catch
                {
                }

                var parts = text.Split(new[] { marker }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

                if (parts.Count != batch.Count)
                {
                    parts = Regex.Split(text.Trim(), "\n\n+").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                }

                for (int j = 0; j < batch.Count; j++)
                {
                    if (j < parts.Count) results.Add(parts[j]);
                    else results.Add(null);
                }
            }
            catch
            {
                results.AddRange(batch.Select(_ => (string?)null));
            }
            finally
            {
                _semaphore.Release();
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

    public async Task<List<(string Title, string Url, string Snippet)>> SearchWebAsync(string query, int maxResults = 5)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        var prompt = $"""
You are NuctiusNow's web-search agent.

MANDATORY:
1. Use the available web-search tool exactly once BEFORE answering.
2. Do NOT answer from model memory.
3. Search the public internet for the exact query below.
4. NuctiusNow will read the tool result directly. Do not invent or rewrite search results.
5. Do not use any other tool.

Maximum results: {maxResults}

QUERY:
{query}

After the tool returns, no detailed prose is necessary.
""";

        try
        {
            var nativeResponse = await CallWebIntegratedChatAsync(
                prompt,
                maxTokens: 1800,
                cancellationToken: CancellationToken.None);

            // Fast path: parse the actual search_web tool result returned by LM Studio.
            var searchTool = nativeResponse.ToolCalls
                .LastOrDefault(tc => tc.Tool.EndsWith("search_web", StringComparison.OrdinalIgnoreCase));

            nativeResponse = await CallWebIntegratedChatAsync(
            prompt,
            maxTokens: 1800,
            cancellationToken: CancellationToken.None);

            _logger.LogInformation(
            "SearchWebAsync: LM Studio returned {ToolCallCount} tool calls. HasMessage={HasMessage}",
            nativeResponse.ToolCalls.Count,
            !string.IsNullOrWhiteSpace(nativeResponse.Message));

            foreach (var toolCall in nativeResponse.ToolCalls)
            {
                _logger.LogInformation(
                    "SearchWebAsync tool: {Tool}, OutputLength={Length}",
                    toolCall.Tool,
                    toolCall.Output?.Length ?? 0);
            }

            if (searchTool != null &&
                TryParseSearchToolOutput(searchTool.Output, maxResults, results))
            {
                _logger.LogInformation(
                    "SearchWebAsync: parsed {Count} web results directly from tool_call for '{Query}' using {PluginId}",
                    results.Count,
                    query,
                    _webSearchPluginId);

                return results;
            }

            // Compatibility fallback: if the tool result was not exposed by the API,
            // parse the final model message exactly as a normal JSON response.
            var text = nativeResponse.Message;
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning(
                    "SearchWebAsync: LM Studio returned no usable tool result or message for query {Query}",
                    query);
                return results;
            }

            if (TryParseSearchResultsFromText(text, maxResults, results))
            {
                _logger.LogInformation(
                    "SearchWebAsync: parsed {Count} web results from final message for '{Query}' using {PluginId}",
                    results.Count,
                    query,
                    _webSearchPluginId);
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
                "SearchWebAsync failed for query '{Query}' using LM Studio native chat + MCP integration {PluginId}",
                query,
                _webSearchPluginId);
        }

        return results;
    }

    public async Task<string?> SearchImageUrlAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var prompt = $$$"""
You are an image-search assistant for a news website.

MANDATORY: use the available web-search tool before answering.

Find ONE relevant direct image URL for this news topic:
{query}

Prefer an editorial photograph or a reusable image from a reputable source.
Do NOT return an article URL, a search page, Markdown, or explanations.

Return ONLY JSON:
{{"imageUrl":"https://example.com/image.jpg"}}

If no suitable image is found:
{{"imageUrl":""}}
""";

        try
        {
            var nativeResponse = await CallWebIntegratedChatAsync(
                prompt,
                maxTokens: 500,
                cancellationToken);

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
                    if (IsUsableImageUrl(imageUrl))
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
                if (IsUsableImageUrl(url))
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

        var prompt = $"""
You are NuctiusNow's original news article writer.

Write one complete, original news article based ONLY on the factual
evidence supplied below.

The original publisher's article may be inaccessible.
Do NOT pretend that you accessed it.

TITLE:
{title}

CATEGORY:
{categoryText}

EVIDENCE:
{evidence}

STRICT RULES:

- Write exactly ONE article.
- The article must have one continuous logical progression from introduction
  to development to conclusion.
- Do NOT restart the article after completing a section.
- Do NOT repeat the introduction.
- Do NOT repeat the conclusion.
- Do NOT repeat paragraphs.
- Do NOT repeat sections using different wording.
- Every paragraph must add new information or develop the previous point.
- Once a fact has been explained, do not explain the same fact again unless
  additional information is genuinely introduced.
- Do NOT produce multiple versions of the same article.
- Do NOT produce a summary followed by the same article.
- Do NOT produce an article followed by another article.
- Do NOT copy or reproduce wording from any source.
- Do NOT attempt to reconstruct inaccessible publisher text verbatim.
- Use only facts supported by the supplied evidence.
- Do NOT invent names, dates, numbers, events, quotes, places, statistics,
  causes, motives, or other information not supported by the evidence.
- Do NOT fabricate direct quotations.
- Prefer original journalistic wording.
- Do NOT mention the evidence, sources, prompt, model, or generation process.
- Do NOT mention that the original article was inaccessible.
- Do NOT write bullet points.
- Do NOT write a short summary.
- Do NOT create a separate "context" section.
- Write approximately 700 to 1200 words when the evidence supports that
  amount of detail.
- Write in the same language as the title and evidence.
- Before returning the article, silently check that no paragraph or section
  is repeated.
- Return ONLY the final article body.
- No Markdown.
- No HTML.
- No JSON.
- No preamble.
- No explanation.

If the supplied evidence is insufficient to write one factual article,
return exactly:

[INSUFFICIENT_EVIDENCE]
""";

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["max_tokens"] = 2200,
            ["temperature"] = 0.1
        };

        if (!string.IsNullOrWhiteSpace(_modelId))
            payload["model"] = _modelId;

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            using var response = await client.PostAsJsonAsync(
                _endpoint,
                payload,
                _jsonOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GenerateOriginalArticleAsync: LLM returned HTTP {StatusCode}",
                    (int)response.StatusCode);

                return null;
            }

            var article =
                (await ParseResponseTextAsync(response.Content) ?? string.Empty)
                .Trim();

            var fenced = Regex.Match(
                article,
                @"^```(?:text|markdown)?\s*(.*?)\s*```$",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (fenced.Success)
                article = fenced.Groups[1].Value.Trim();

            if (article.Equals(
                    "[INSUFFICIENT_EVIDENCE]",
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "GenerateOriginalArticleAsync: insufficient evidence for '{Title}'",
                    title);

                return null;
            }

            if (article.Length < 500)
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
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
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
        finally
        {
            _semaphore.Release();
        }
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
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif",
        ".avif"
    };

        return imageExtensions.Any(
            extension => path.EndsWith(extension));
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
                cancellationToken);

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

            var payload = new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["max_tokens"] = 5000
            };

            if (!string.IsNullOrWhiteSpace(_modelId)) payload["model"] = _modelId;

            var llmClient = _httpFactory.CreateClient();
            llmClient.Timeout = TimeSpan.FromMinutes(5);

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                using var llmResponse = await llmClient.PostAsJsonAsync(
                    _endpoint,
                    payload,
                    _jsonOptions,
                    cancellationToken);

                if (!llmResponse.IsSuccessStatusCode)
                    return null;

                var extracted = (await ParseResponseTextAsync(llmResponse.Content) ?? string.Empty).Trim();
                return IsUsableArticleBody(extracted) ? extracted : null;
            }
            finally
            {
                _semaphore.Release();
            }
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

        for (int i = 0; i < candidates.Count; i += _batchSize)
        {
            var batch = candidates.Skip(i).Take(_batchSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Given the user profile and the following candidate articles, rate the relevance of each candidate on a scale from 0 to 100. Return only comma-separated numeric scores in the same order, with no extra commentary.");
            sb.AppendLine();
            sb.AppendLine("User profile:");
            sb.AppendLine(userProfile);
            sb.AppendLine();
            for (int idx = 0; idx < batch.Count; idx++)
            {
                sb.AppendLine($"{idx + 1}) {batch[idx]}");
            }

            var payload = new { prompt = sb.ToString(), max_tokens = 50 };
            var client = _httpFactory.CreateClient();

            await _semaphore.WaitAsync();
            try
            {
                using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions);
                if (!resp.IsSuccessStatusCode)
                {
                    results.AddRange(batch.Select(_ => (double?)null));
                    continue;
                }

                var text = await ParseResponseTextAsync(resp.Content) ?? string.Empty;
                var nums = Regex.Matches(text, "[0-9]+(?:\\.[0-9]+)?").Select(m => m.Value).ToList();

                if (nums.Count >= batch.Count)
                {
                    for (int j = 0; j < batch.Count; j++)
                    {
                        if (double.TryParse(nums[j].Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        {
                            results.Add(Math.Clamp(v, 0.0, 100.0));
                        }
                        else results.Add(null);
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
            finally
            {
                _semaphore.Release();
            }
        }

        return results;
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

    private async Task<NativeChatResponse> CallWebIntegratedChatAsync(
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _modelId,
            ["input"] = prompt,
            ["integrations"] = new object[]
            {
                new
                {
                    type = "plugin",
                    id = _webSearchPluginId,
                    allowed_tools = new[] { "search_web" }
                }
            },
            ["context_length"] = 125184,
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

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
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

            if (root.TryGetProperty("output", out var output) && output.ValueKind == System.Text.Json.JsonValueKind.Array && output.GetArrayLength() > 0)
            {
                var first = output[0];
                if (first.TryGetProperty("content", out var oc) && oc.ValueKind == System.Text.Json.JsonValueKind.String)
                    return oc.GetString();
                if (first.TryGetProperty("data", out var od) && od.ValueKind == System.Text.Json.JsonValueKind.String)
                    return od.GetString();
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
}