using System.Net.Http.Json;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading;

namespace NuctiusNow.Services;

public interface ILocalLlmService
{
    Task<string?> SummarizeAsync(string text, int maxTokens = 220);
    Task<double?> ScoreCandidateAsync(string userProfile, string candidateText);
    Task<List<string?>> SummarizeBatchAsync(List<string> texts, int maxTokens = 220);
    Task<List<double?>> BatchScoreAsync(string userProfile, List<string> candidates);
    Task<List<(string Title, string Url, string Snippet)>> SearchWebAsync(string query, int maxResults = 5);
}

/// <summary>
/// Simple HTTP client wrapper to call a local LLM REST endpoint.
/// The local LLM is expected to expose a JSON API that accepts { prompt, max_tokens }
/// and returns { text: "..." } or similar. This implementation is intentionally
/// minimal: adapt the request/response shape to the server you run locally (e.g. llama.cpp HTTP server, LLM REST API).
/// </summary>
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
            // fallback to a safe default if config value is invalid
            _endpoint = "http://localhost:1234/v1/completions";
        }

        _maxConcurrency = config.GetValue<int?>("LocalLlm:MaxConcurrency") ?? 2;
        _batchSize = config.GetValue<int?>("LocalLlm:BatchSize") ?? 8;
        _modelId = config.GetValue<string?>("LocalLlm:Model") ?? config.GetValue<string?>("LocalLlm:ModelId") ?? "minicpm5-1b-claude-opus-fable5-thinking";
        _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    public async Task<string?> SummarizeAsync(string text, int maxTokens = 220)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var client = _httpFactory.CreateClient();
        var prompt = $"Summarize the following news article in one short paragraph (max {maxTokens} characters):\n\n{text}";

        // include model id if configured
        var payload = new Dictionary<string, object?>()
        {
            ["prompt"] = prompt,
            ["max_tokens"] = maxTokens
        };
        if (!string.IsNullOrWhiteSpace(_modelId)) payload["model"] = _modelId;
        // include plugin metadata so the LLM can use web-search if supported
        payload["plugin"] = new { type = "plugin", id = "greggg/web-search" };
        // Plugin integration: include web-search plugin descriptor in the request payload
        // This field is optional for servers that support plugin metadata in the prompt request.
        payload["plugin"] = new { type = "plugin", id = "greggg/web-search" };

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
    }

    public async Task<double?> ScoreCandidateAsync(string userProfile, string candidateText)
    {
        if (string.IsNullOrWhiteSpace(candidateText)) return null;

        var client = _httpFactory.CreateClient();
        // Ask the model for a numeric relevance score between 0 and 100
        var prompt = $"Given the user profile or preferences:\n{userProfile}\n\nRate the relevance of this candidate news article on a scale 0-100 and only return the numeric score:\n{candidateText}";
        var payload = new Dictionary<string, object?>()
        {
            ["prompt"] = prompt,
            ["max_tokens"] = 10
        };
        if (!string.IsNullOrWhiteSpace(_modelId)) payload["model"] = _modelId;

        // Use semaphore for rate-limiting
        await _semaphore.WaitAsync();
        try
        {
            using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions);
            if (!resp.IsSuccessStatusCode) return null;

            var resultText = await ParseResponseTextAsync(resp.Content);
            if (string.IsNullOrWhiteSpace(resultText)) return null;

            // Extract number from response
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

        // Process in batches to avoid huge prompts
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
                _logger.LogInformation("LocalLlmService.SearchWebAsync raw response: {ResponseText}", text);
            }
            catch
            {
                // swallow logging errors
            }
                var parts = text.Split(marker, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

                // If marker not present, fallback to splitting by double newlines
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

    public async Task<List<(string Title, string Url, string Snippet)>> SearchWebAsync(string query, int maxResults = 5)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        var marker = "<|RESULT|>";
        var sb = new StringBuilder();
        sb.AppendLine($"Search the web for the following query and return up to {maxResults} results. For each result output in the format: Title | URL | Snippet and separate results exactly by the token {marker} with no extra commentary.");
        sb.AppendLine();
        sb.AppendLine(query);

        var payload = new { prompt = sb.ToString(), max_tokens = 300, plugin = new { type = "plugin", id = "greggg/web-search" } };

        var client = _httpFactory.CreateClient();
        await _semaphore.WaitAsync();
        try
        {
            using var resp = await client.PostAsJsonAsync(_endpoint, payload, _jsonOptions);
            if (!resp.IsSuccessStatusCode) return results;

            var text = await ParseResponseTextAsync(resp.Content) ?? string.Empty;
            var parts = text.Split(marker, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
            if (parts.Count == 0)
            {
                // fallback: split by newlines and try to parse lines that look like URL containing
                var lines = Regex.Split(text.Trim(), "\n+").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                parts = lines;
            }

            var urlRegex = new Regex(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            foreach (var part in parts.Take(maxResults))
            {
                // Try parse Title | URL | Snippet
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
                    // try extract URL from text and assign remainder as title/snippet
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

                results.Add((title, url, snippet));
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
                // Extract numbers in order
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
                    // If parsing failed, add nulls for this batch
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

    private class LocalLlmResponse
    {
        public string? Text { get; set; }
    }

    private async Task<string?> ParseResponseTextAsync(HttpContent content)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            // common shapes: { "text": "..." }
            if (root.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                return t.GetString();

            // { "choices": [ { "text": "..." } ] }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("text", out var chText) && chText.ValueKind == System.Text.Json.JsonValueKind.String)
                    return chText.GetString();
                // chat style: choices[0].message.content
                if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    return contentProp.GetString();
            }

            // { "output": [ { "content": "..." } ] } or { "output": [ {"data":"..."} ] }
            if (root.TryGetProperty("output", out var output) && output.ValueKind == System.Text.Json.JsonValueKind.Array && output.GetArrayLength() > 0)
            {
                var first = output[0];
                if (first.TryGetProperty("content", out var oc) && oc.ValueKind == System.Text.Json.JsonValueKind.String)
                    return oc.GetString();
                if (first.TryGetProperty("data", out var od) && od.ValueKind == System.Text.Json.JsonValueKind.String)
                    return od.GetString();
            }

            // Try flat concatenation of all string properties
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
