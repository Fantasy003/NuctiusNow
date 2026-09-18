using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
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
}

public class LocalLlmService : ILocalLlmService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _endpoint;
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
            _endpoint = "http://localhost:1234/v1/completions";
        }

        _maxConcurrency = config.GetValue<int?>("LocalLlm:MaxConcurrency") ?? 2;
        _batchSize = config.GetValue<int?>("LocalLlm:BatchSize") ?? 8;
        _modelId = config.GetValue<string?>("LocalLlm:Model") ?? config.GetValue<string?>("LocalLlm:ModelId") ?? "qwythos-9b-claude-mythos-5-1m";
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

        payload["plugin"] = new { type = "plugin", id = "greggg/web-search" };

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

            var payload = new { prompt = sb.ToString(), max_tokens = maxTokens, plugin = new { type = "plugin", id = "greggg/web-search" } };
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

        var sb = new StringBuilder();
        sb.AppendLine("CRITICAL INSTRUCTION: Return ONLY a valid JSON array. Your very first word must be the direct core answer, data, or content requested. No preamble, no explanation, no greeting text.");
        sb.AppendLine("Format: [{\"title\":\"Article Title\",\"url\":\"https://example.com\",\"summary\":\"One sentence summary\"}]");
        sb.AppendLine("Perform a web search for this query and return results as JSON array:");
        sb.AppendLine($"Query: {query}");
        sb.AppendLine("Return ONLY the JSON array, nothing else.");

        var payload = new { prompt = sb.ToString(), max_tokens = 600, plugin = new { type = "plugin", id = "greggg/web-search" } };
        var client = _httpFactory.CreateClient();

        await _semaphore.WaitAsync();
        try
        {
            using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions);
            if (!resp.IsSuccessStatusCode) return results;

            var text = await ParseResponseTextAsync(resp.Content) ?? string.Empty;

            try
            {
                if (_logger != null)
                    _logger.LogInformation("SearchWebAsync raw LLM response: {Response}", text.Length > 500 ? text.Substring(0, 500) + "..." : text);
            }
            catch { }

            try
            {
                var jsonToParse = text.Trim();

                // Si el LLM devolvió markdown (ej. ```json ... ```), lo limpiamos
                var matchCodeBlock = Regex.Match(jsonToParse, @"```(?:json)?\s*(\[.*?\])\s*```", RegexOptions.Singleline);
                if (matchCodeBlock.Success)
                {
                    jsonToParse = matchCodeBlock.Groups[1].Value.Trim();
                }
                else
                {
                    // Intentar extraer solo el array si hay texto basura antes o despues
                    var arrayMatch = Regex.Match(jsonToParse, @"\[\s*\{.*?\}\s*\]", RegexOptions.Singleline);
                    if (arrayMatch.Success)
                    {
                        jsonToParse = arrayMatch.Value.Trim();
                    }
                }

                using var doc = System.Text.Json.JsonDocument.Parse(jsonToParse);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray().Take(maxResults))
                    {
                        string title = string.Empty, url = string.Empty, snippet = string.Empty;
                        if (item.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String) title = t.GetString() ?? string.Empty;
                        if (item.TryGetProperty("url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String) url = NormalizeUrl(u.GetString());
                        if (item.TryGetProperty("summary", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String) snippet = s.GetString() ?? string.Empty;

                        // Solo añadir si tiene al menos url o título (para evitar vacíos)
                        if (!string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(title))
                        {
                            results.Add((title, NormalizeUrl(url), snippet));
                        }
                    }
                    if (results.Count > 0)
                    {
                        if (_logger != null)
                            _logger.LogInformation("SearchWebAsync: JSON parsing successful, found {Count} results", results.Count);
                        return results;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_logger != null)
                    _logger.LogDebug(ex, "SearchWebAsync: JSON parsing failed, falling back to heuristic parsing");
            }

            var parts = Regex.Split(text.Trim(), "\n\n+").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            // Updated Regex for LocalLlmService.cs
            var urlRegex = new Regex(@"https?://[^\s\""',\\]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (var part in parts.Take(maxResults))
            {
                var segments = part.Split('|').Select(s => s.Trim()).ToArray();
                string title = string.Empty, url = string.Empty, snippet = string.Empty;
                if (segments.Length >= 2)
                {
                    title = segments[0];
                    url = segments[1];
                    if (segments.Length >= 3) snippet = segments[2];
                }
                else
                {
                    var m = urlRegex.Match(part);
                    if (m.Success)
                    {
                        url = m.Value;
                        title = part.Replace(url, "").Trim();
                        snippet = title;
                    }
                    else
                    {
                        title = part;
                    }
                }

                results.Add((title, NormalizeUrl(url), snippet));
            }

            if (!results.Any(r => !string.IsNullOrWhiteSpace(r.Url)))
            {
                try
                {
                    var matches = urlRegex.Matches(text).Select(m => m.Value).Distinct().Take(maxResults).ToList();
                    foreach (var u in matches)
                    {
                        results.Add((u, u, string.Empty));
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (_logger != null) _logger.LogDebug(ex, "SearchWebAsync: URL-extraction fallback failed");
                    }
                    catch { }
                }
            }
        }
        catch
        {
            // ignore and return empty results
        }
        finally
        {
            _semaphore.Release();
        }

        return results;
    }


    /// <summary>
    /// Uses the LLM web-search plugin to open a specific article URL and extract
    /// only the article's main body text. The result is intentionally plain text,
    /// not a summary and not HTML.
    /// </summary>
    public async Task<string?> ExtractArticleBodyTextAsync(string articleUrl, CancellationToken cancellationToken = default)
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
You are an article extraction engine.

Open and read this exact news article URL using the available web-search tool:
{articleUrl}

Return ONLY the complete main article body text.

Rules:
- Do NOT summarize or paraphrase.
- Do NOT invent missing content.
- Preserve the article's paragraphs and normal reading order.
- Include the article's substantive body paragraphs.
- Exclude site navigation, menus, cookie notices, advertisements, comments, related articles, newsletter boxes, social widgets, and author-bio boilerplate.
- Do not include the title unless it is part of the article body.
- Do not include Markdown, HTML, JSON, labels, explanations, or quotation marks around the answer.
- If the article cannot be opened or its body cannot be extracted, return an empty response.
""";

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["max_tokens"] = 5000,
            ["plugin"] = new { type = "plugin", id = "greggg/web-search" }
        };

        if (!string.IsNullOrWhiteSpace(_modelId)) payload["model"] = _modelId;

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        string? extractedText = null;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                extractedText = (await ParseResponseTextAsync(resp.Content) ?? string.Empty).Trim();

                var fenced = Regex.Match(
                    extractedText,
                    @"^```(?:text|markdown)?\s*(.*?)\s*```$",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (fenced.Success)
                    extractedText = fenced.Groups[1].Value.Trim();
            }
            else
            {
                _logger.LogWarning(
                    "ExtractArticleBodyTextAsync: LLM returned HTTP {StatusCode} for {Url}",
                    (int)resp.StatusCode,
                    articleUrl);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExtractArticleBodyTextAsync web-search request failed for {Url}", articleUrl);
        }
        finally
        {
            _semaphore.Release();
        }

        if (!string.IsNullOrWhiteSpace(extractedText) && IsUsableArticleBody(extractedText))
        {
            _logger.LogInformation(
                "ExtractArticleBodyTextAsync: extracted {Length} characters from {Url} using web-search",
                extractedText.Length,
                articleUrl);
            return extractedText;
        }

        // Run the fallback only after releasing the first semaphore slot so the
        // fallback can acquire a slot normally and cannot deadlock.
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

            var payload = new { prompt = sb.ToString(), max_tokens = 50, plugin = new { type = "plugin", id = "greggg/web-search" } };
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
}
