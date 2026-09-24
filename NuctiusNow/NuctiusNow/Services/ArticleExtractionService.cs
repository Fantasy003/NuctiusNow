using Microsoft.Playwright;
using SmartReader;
using System.Text;
using System.Text.RegularExpressions;

namespace NuctiusNow.Services;

public sealed class ArticleExtractionService : IAsyncDisposable
{
    private readonly ILogger<ArticleExtractionService> _logger;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/151.0.0.0 Safari/537.36";

    private static readonly string[] ModelFailurePhrases =
    {
        "appears to be some duplication or error",
        "appears to be a duplication or error",
        "there appears to be some duplication",
        "i cannot fulfill your request",
        "i am unable to fulfill your request",
        "i'm unable to fulfill your request",
        "i cannot open the url",
        "i cannot access the url",
        "i cannot extract",
        "requires javascript to load content",
        "dynamic web page",
        "raw html source code",
        "cookie notices, advertisements",
        "i can only return the raw html"
    };

    public ArticleExtractionService(
        ILogger<ArticleExtractionService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ArticleExtractionResult> ExtractAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.InvalidUrl,
                FinalUrl = url
            };
        }

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.InvalidUrl,
                FinalUrl = url
            };
        }

        if (IsKnownNonArticlePage(uri))
        {
            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.NonArticleUrl,
                FinalUrl = uri.ToString()
            };
        }

        var staticResult =
            await TryExtractWithSmartReaderAsync(
                uri,
                cancellationToken);

        if (staticResult.Success)
            return staticResult;

        // Don't send known HTTP access failures through Playwright again.
        if (staticResult.Status is
            ArticleExtractionStatus.Unauthorized or
            ArticleExtractionStatus.Forbidden or
            ArticleExtractionStatus.NotFound or
            ArticleExtractionStatus.RateLimited)
        {
            return staticResult;
        }

        var browserResult =
            await TryExtractWithPlaywrightAsync(
                uri,
                cancellationToken);

        if (browserResult.Success)
            return browserResult;

        return browserResult.Status != ArticleExtractionStatus.NoArticleContent
            ? browserResult
            : staticResult;
    }

    private async Task<ArticleExtractionResult> TryExtractWithSmartReaderAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();

            httpClient.Timeout = TimeSpan.FromSeconds(15);

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                BrowserUserAgent);

            httpClient.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            _logger.LogInformation(
                "HTTP fetch started: {Url}",
                uri);

            using var response = await httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var finalUrl =
                response.RequestMessage?.RequestUri?.ToString()
                ?? uri.ToString();

            var status = (int)response.StatusCode;

            _logger.LogInformation(
                "HTTP fetch completed: Status={StatusCode}, FinalUrl={FinalUrl}",
                status,
                finalUrl);

            if (status == 401)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.Unauthorized,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (status == 403)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.Forbidden,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (status == 404)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NotFound,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (status == 429)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.RateLimited,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.Unavailable,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            var html = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (string.IsNullOrWhiteSpace(html))
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            var reader = new Reader(
                uri.ToString(),
                html)
            {
                ContinueIfNotReadable = true,
                CharThreshold = 300
            };

            var article = reader.GetArticle();

            if (!article.IsReadable)
            {
                _logger.LogDebug(
                    "SmartReader could not identify readable article content: {Url}",
                    uri);

                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            var rawText = article.TextContent;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            var cleanedText = CleanArticleText(rawText);

            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            _logger.LogInformation(
                "SmartReader extracted {Length} characters from {Url}",
                cleanedText.Length,
                finalUrl);

            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.Success,
                Text = cleanedText,
                HttpStatusCode = status,
                FinalUrl = finalUrl
            };
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(
                ex,
                "Article HTTP request timed out: {Url}",
                uri);

            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.Timeout,
                FinalUrl = uri.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Article HTTP request failed: {Url}",
                uri);

            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.Error,
                FinalUrl = uri.ToString()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SmartReader extraction failed for {Url}",
                uri);

            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.Error,
                FinalUrl = uri.ToString()
            };
        }
    }

    private async Task<ArticleExtractionResult> TryExtractWithPlaywrightAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;

        try
        {
            browser = await GetBrowserAsync(cancellationToken);

            context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    UserAgent = BrowserUserAgent,
                    Locale = "en-US"
                });

            page = await context.NewPageAsync();

            page.SetDefaultNavigationTimeout(30_000);
            page.SetDefaultTimeout(15_000);

            var response = await page.GotoAsync(
                uri.ToString(),
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20_000
                });

            if (response == null)
            {
                _logger.LogWarning(
                    "Playwright returned no navigation response for {Url}",
                    uri);

                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.Unavailable,
                    FinalUrl = page.Url
                };
            }

            var status = response.Status;
            var finalUrl = page.Url;

            _logger.LogInformation(
                "Playwright navigation: Status={Status}, FinalUrl={FinalUrl}",
                status,
                finalUrl);

            if (status == 401)
            {
                _logger.LogWarning(
                    "Playwright received HTTP 401 from {Url}",
                    uri);

                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.Unauthorized,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (status == 403)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.Forbidden,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (status == 404)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NotFound,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (status == 429)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.RateLimited,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            if (status < 200 || status >= 300)
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.Unavailable,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            try
            {
                await page.WaitForLoadStateAsync(
                    LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions
                    {
                        Timeout = 15_000
                    });
            }
            catch (TimeoutException)
            {
                _logger.LogDebug(
                    "DOMContentLoaded wait timed out for {Url}; using current DOM.",
                    uri);
            }

            await page.WaitForTimeoutAsync(2500);

            cancellationToken.ThrowIfCancellationRequested();

            var html = await page.ContentAsync();

            if (string.IsNullOrWhiteSpace(html))
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            var reader = new Reader(
                finalUrl,
                html)
            {
                ContinueIfNotReadable = true,
                CharThreshold = 300
            };

            var article = reader.GetArticle();

            if (!article.IsReadable)
            {
                _logger.LogDebug(
                    "SmartReader could not extract article from rendered HTML: {Url}",
                    uri);

                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            var rawText = article.TextContent;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            var cleanedText = CleanArticleText(rawText);

            if (string.IsNullOrWhiteSpace(cleanedText))
            {
                return new ArticleExtractionResult
                {
                    Status = ArticleExtractionStatus.NoArticleContent,
                    HttpStatusCode = status,
                    FinalUrl = finalUrl
                };
            }

            _logger.LogInformation(
                "Playwright + SmartReader extracted {Length} characters from {Url}",
                cleanedText.Length,
                finalUrl);

            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.Success,
                Text = cleanedText,
                HttpStatusCode = status,
                FinalUrl = finalUrl
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(
                ex,
                "Playwright extraction failed for {Url}",
                uri);

            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.Error,
                FinalUrl = uri.ToString()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Browser-based article extraction failed for {Url}",
                uri);

            return new ArticleExtractionResult
            {
                Status = ArticleExtractionStatus.Error,
                FinalUrl = uri.ToString()
            };
        }
        finally
        {
            if (page != null)
            {
                try
                {
                    await page.CloseAsync();
                }
                catch
                {
                    // Ignore cleanup errors.
                }
            }

            if (context != null)
            {
                try
                {
                    await context.CloseAsync();
                }
                catch
                {
                    // Ignore cleanup errors.
                }
            }
        }
    }

    private async Task<IBrowser> GetBrowserAsync(
        CancellationToken cancellationToken)
    {
        if (_browser != null)
            return _browser;

        await _browserLock.WaitAsync(cancellationToken);

        try
        {
            if (_browser != null)
                return _browser;

            _playwright = await Playwright.CreateAsync();

            _browser = await _playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Headless = true
                });

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public static bool IsKnownNonArticlePage(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        // Yahoo Finance company quote pages are not article pages.
        if (host == "finance.yahoo.com" &&
            (path.StartsWith("/quote/") ||
             path.Equals("/quote")))
        {
            return true;
        }

        // Common non-editorial destinations should never be treated as article pages.
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
            return true;

        var blockedPathTokens = new[]
        {
            "/download", "/downloads", "/store", "/shop", "/pricing", "/support",
            "/help", "/docs", "/documentation", "/wiki", "/forum", "/forums",
            "/careers", "/jobs", "/login", "/signin", "/signup", "/account",
            "/search", "/products", "/product", "/features", "/system-requirements",
            "/contact", "/community", "/events", "/newsletter", "/subscribe"
        };

        if (blockedPathTokens.Any(token => path.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Specific project example: Bungie's Destiny New Light landing/download page.
        if (host.Equals("bungie.net", StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith("/7/en/destiny/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool NeedsReExtraction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var normalized = NormalizeWhitespace(text);

        if (normalized.Length < 200)
            return true;

        foreach (var phrase in ModelFailurePhrases)
        {
            if (normalized.Contains(
                    phrase,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return HasHighSentenceDuplication(normalized);
    }

    private static string CleanArticleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = text.Replace("\r\n", "\n");
        text = text.Replace("\r", "\n");

        var paragraphs = Regex
            .Split(text, @"\n\s*\n+")
            .Select(p => NormalizeWhitespace(p))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var cleanParagraphs = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            if (ContainsModelFailurePhrase(paragraph))
                continue;

            var sentences = SplitIntoSentences(paragraph);

            var cleanSentences = new List<string>();

            foreach (var sentence in sentences)
            {
                var normalizedSentence =
                    NormalizeWhitespace(sentence);

                if (string.IsNullOrWhiteSpace(normalizedSentence))
                    continue;

                if (ContainsModelFailurePhrase(
                        normalizedSentence))
                {
                    continue;
                }

                bool duplicate = false;

                foreach (var existing in cleanSentences)
                {
                    if (existing.Length < 40 ||
                        normalizedSentence.Length < 40)
                    {
                        continue;
                    }

                    if (SentenceSimilarity(
                            existing,
                            normalizedSentence) >= 0.92)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    cleanSentences.Add(normalizedSentence);
                }
            }

            if (cleanSentences.Count > 0)
            {
                cleanParagraphs.Add(
                    string.Join(" ", cleanSentences));
            }
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            cleanParagraphs).Trim();
    }

    private static List<string> SplitIntoSentences(
        string paragraph)
    {
        return Regex
            .Split(
                paragraph,
                @"(?<=[.!?])\s+(?=[A-ZÁÉÍÓÚÑÜ0-9""“¿¡])")
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string NormalizeWhitespace(string text)
    {
        return Regex
            .Replace(text ?? string.Empty, @"\s+", " ")
            .Trim();
    }

    private static bool ContainsModelFailurePhrase(
        string text)
    {
        foreach (var phrase in ModelFailurePhrases)
        {
            if (text.Contains(
                    phrase,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHighSentenceDuplication(
        string text)
    {
        var sentences = SplitIntoSentences(text);

        if (sentences.Count < 5)
            return false;

        var duplicateCount = 0;

        for (var i = 0; i < sentences.Count; i++)
        {
            if (sentences[i].Length < 40)
                continue;

            for (var j = 0; j < i; j++)
            {
                if (sentences[j].Length < 40)
                    continue;

                if (SentenceSimilarity(
                        sentences[i],
                        sentences[j]) >= 0.92)
                {
                    duplicateCount++;
                    break;
                }
            }
        }

        return (double)duplicateCount / sentences.Count >= 0.20;
    }

    private static double SentenceSimilarity(
        string first,
        string second)
    {
        var firstWords = GetWordSet(first);
        var secondWords = GetWordSet(second);

        if (firstWords.Count == 0 ||
            secondWords.Count == 0)
        {
            return 0;
        }

        var intersection = firstWords.Intersect(secondWords).Count();
        var union = firstWords.Union(secondWords).Count();

        if (union == 0)
            return 0;

        return (double)intersection / union;
    }

    private static HashSet<string> GetWordSet(
        string text)
    {
        return Regex
            .Matches(
                text.ToLowerInvariant(),
                @"[\p{L}\p{N}]{3,}")
            .Select(m => m.Value)
            .ToHashSet();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch
            {
                // Ignore browser shutdown errors.
            }

            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        _browserLock.Dispose();
    }
    public enum ArticleExtractionStatus
    {
        Success,
        InvalidUrl,
        NonArticleUrl,
        NotFound,
        Unauthorized,
        Forbidden,
        RateLimited,
        Unavailable,
        NoArticleContent,
        Timeout,
        Error
    }

    public sealed class ArticleExtractionResult
    {
        public ArticleExtractionStatus Status { get; init; }
        public string? Text { get; init; }
        public int? HttpStatusCode { get; init; }
        public string? FinalUrl { get; init; }

        public bool Success =>
            Status == ArticleExtractionStatus.Success &&
            !string.IsNullOrWhiteSpace(Text);
    }
}

