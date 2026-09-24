using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NuctiusNow.Services;

public class RecommendationEngine
{
    private readonly ApplicationDbContext _context;
    private readonly ILocalLlmService? _llmService;
    private readonly NewsIngestService? _newsIngest;
    private readonly ArticleExtractionService? _articleExtractor;
    private readonly ILogger<RecommendationEngine>? _logger;
    public RecommendationEngine(
        ApplicationDbContext context,
        ILocalLlmService? llmService = null,
        NewsIngestService? newsIngest = null,
        ILogger<RecommendationEngine>? logger = null,
        ArticleExtractionService? articleExtractor = null)
    {
        _context = context;
        _llmService = llmService;
        _newsIngest = newsIngest;
        _logger = logger;
        _articleExtractor = articleExtractor;
    }

    public async Task<string> SummarizeTextAsync(string text, int maxChars = 220)
    {
        if (_llmService == null) return text.Length <= maxChars ? text : text.Substring(0, maxChars) + "...";
        try
        {
            var s = await _llmService.SummarizeAsync(text, maxChars);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        catch { }
        return text.Length <= maxChars ? text : text.Substring(0, maxChars) + "...";
    }

    public async Task<List<string>> ExecuteNewsSearchWorkflowAsync(int userId, int numSearches = 4)
    {
        var ingested = new List<string>();

        // Setup local references to remove "possibly null" dereference warnings later on
        var llm = _llmService;
        var ingest = _newsIngest;
        if (llm == null || ingest == null)
        {
            _logger?.LogError("ExecuteNewsSearchWorkflowAsync: LLM or NewsIngestService not available");
            return ingested;
        }

        _logger?.LogInformation("STEP 1: Loading user preferences for userId={UserId}", userId);

        var userPreferencesList = await _context.UserPreferences
            .Include(up => up.Preference)
            .Where(up => up.IdUser == userId)
            .ToListAsync();

        // Clean out possible nulls structurally to ensure the engine functions correctly
        var userPreferences = userPreferencesList.Select(up => up.Preference!).Where(p => p != null).ToList();

        if (userPreferences.Count == 0)
        {
            _logger?.LogError("STEP 1: No preferences found for user {UserId}", userId);
            return ingested;
        }

        _logger?.LogInformation("STEP 1: Found {Count} preferences for user {UserId}", userPreferences.Count, userId);

        var topPrefs = userPreferences.OrderByDescending(p => p.Weight).Take(3).ToList();

        int searchCount = 0;
        int prefIndex = 0;

        while (ingested.Count < numSearches && searchCount < numSearches * 2)
        {
            var pref = topPrefs.Count > 0 ? topPrefs[prefIndex % topPrefs.Count] : userPreferences.First();

            // Fixed CS8602: added null propagation
            var topic = pref?.Tag?.Name ?? pref?.IdTag.ToString() ?? "News";
            var query = $"{topic}";

            _logger?.LogInformation("STEP 2: Searching for preference '{Topic}' (query='{Query}')", topic, query);
            List<(string Title, string Url, string Snippet)> results = new();
            try
            {
                results = await llm.SearchWebAsync(query, 1);
                _logger?.LogInformation("STEP 2: Search returned {Count} results for query '{Query}'", results.Count, query);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "STEP 2: Search failed for query '{Query}'", query);
                prefIndex++;
                searchCount++;
                continue;
            }

            if (results.Count == 0)
            {
                _logger?.LogWarning("STEP 3: No results returned for query '{Query}'", query);
                prefIndex++;
                searchCount++;
                continue;
            }

            var firstResult = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Url) && !ingested.Contains(r.Url));
            if (string.IsNullOrWhiteSpace(firstResult.Url))
            {
                _logger?.LogWarning("STEP 3: No valid or new URL found in results");
                prefIndex++;
                searchCount++;
                continue;
            }

            _logger?.LogInformation("STEP 3: Selected result - Title: {Title}, URL: {Url}", firstResult.Title, firstResult.Url);

            _logger?.LogInformation("STEP 4: Summarizing content from URL {Url}", firstResult.Url);
            string summary = firstResult.Snippet ?? string.Empty;
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = firstResult.Title;
            }
            _logger?.LogInformation("STEP 4: Summary obtained (length={Length})", summary.Length);

            _logger?.LogInformation("STEP 5: Ingesting news - URL: {Url}, Title: {Title}, TagId: {TagId}", firstResult.Url, firstResult.Title, pref?.IdTag);
            try
            {
                await ingest.SaveOrUpdateNewsAsync(
                    firstResult.Url,
                    summary,
                    firstResult.Title,
                    pref?.IdTag ?? 1,
                    isSfw: true,
                    secureLink: true,
                    isAi: true,
                    newsDate: DateTime.UtcNow
                );
                ingested.Add(firstResult.Url);
                _logger?.LogInformation("STEP 5: Successfully ingested URL {Url}", firstResult.Url);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "STEP 5: Failed to ingest URL {Url}", firstResult.Url);
            }

            prefIndex++;
            searchCount++;
        }

        _logger?.LogInformation("ExecuteNewsSearchWorkflowAsync: Complete - ingested {Count} items", ingested.Count);
        return ingested;
    }


    public async Task<List<string>> SearchAndIngestForUserAsync(int userId, int perPref = 1, int maxTotal = 4)
    {
        var ingested = new List<string>();

        var llm = _llmService;
        var ingest = _newsIngest;
        if (llm == null || ingest == null)
        {
            _logger?.LogInformation("SearchAndIngestForUserAsync: LLM service or NewsIngestService not available");
            return ingested;
        }

        var userPreferencesList = await _context.UserPreferences
            .Include(up => up.Preference)
            .Where(up => up.IdUser == userId)
            .ToListAsync();

        var userPreferences = userPreferencesList.Select(up => up.Preference!).Where(p => p != null).ToList();

        if (userPreferences.Count == 0)
        {
            _logger?.LogInformation("SearchAndIngestForUserAsync: no preferences for user {UserId}", userId);
            return ingested;
        }

        var topPrefs = userPreferences.OrderByDescending(p => p.Weight).Take(3).ToList();

        int attempts = 0;
        int prefIndex = 0;
        while (ingested.Count < maxTotal && attempts < maxTotal * 2)
        {
            var pref = topPrefs.Count > 0 ? topPrefs[prefIndex % topPrefs.Count] : userPreferences.First();
            var topic = pref?.Tag?.Name ?? pref?.IdTag.ToString() ?? "News";
            var query = $"{topic} news";
            List<(string Title, string Url, string Snippet)> results = new();
            try
            {
                results = await llm.SearchWebAsync(query, perPref);
                _logger?.LogInformation("SearchAndIngestForUserAsync: search '{Query}' returned {Count}", query, results.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SearchAndIngestForUserAsync: search failed for query {Query}", query);
            }

            var first = results.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(first.Url) && !ingested.Contains(first.Url))
            {
                try
                {
                    await ingest.SaveOrUpdateNewsAsync(first.Url, first.Snippet, first.Title, pref?.IdTag ?? 1, isSfw: true, secureLink: true, isAi: true, newsDate: DateTime.UtcNow);
                    ingested.Add(first.Url);
                    _logger?.LogInformation("SearchAndIngestForUserAsync: ingested url {Url}", first.Url);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "SearchAndIngestForUserAsync: failed to ingest {Url}", first.Url);
                }
            }

            prefIndex++;
            attempts++;
        }

        return ingested;
    }

    public async Task<List<News>> GetRecommendationsForUserAsync(int userId, int cantidad = 10, int days = 30)
    {
        var userPreferencesList = await _context.UserPreferences
            .Include(up => up.Preference)
            .Where(up => up.IdUser == userId)
            .ToListAsync();

        var userPreferences = userPreferencesList.Select(up => up.Preference!).Where(p => p != null).ToList();

        var noticiasVistasIds = await _context.UserNews
            .Where(un => un.IdUser == userId && un.Seen)
            .Select(un => un.IdNews)
            .ToListAsync();

        var fechaLimite = DateTime.UtcNow.AddDays(-days);

        var noticiasCandidatas = await _context.News
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .Where(n => !noticiasVistasIds.Contains(n.IdNews) && n.CapturedDate >= fechaLimite)
            .ToListAsync();

        var llm = _llmService;
        var ingest = _newsIngest;

        if (noticiasCandidatas.Count == 0 && llm != null && ingest != null)
        {
            try
            {
                var topPrefs = userPreferences.OrderByDescending(p => p.Weight).Take(3).ToList();
                if (topPrefs.Count == 0) topPrefs = userPreferences.Take(1).ToList();

                foreach (var pref in topPrefs)
                {
                    var topic = pref?.Tag?.Name ?? pref?.IdTag.ToString() ?? "News";
                    var query = $"{topic} news";
                    var searchResults = await llm.SearchWebAsync(query, 5);
                    try
                    {
                        _logger?.LogInformation("Search for '{Query}' returned {Count} results", query, searchResults.Count);
                    }
                    catch { }
                    foreach (var res in searchResults)
                    {
                        if (string.IsNullOrWhiteSpace(res.Url)) continue;
                        try
                        {
                            await ingest.SaveOrUpdateNewsAsync(res.Url, res.Snippet, res.Title, pref?.IdTag ?? 1, isSfw: true, secureLink: true, isAi: true, newsDate: DateTime.UtcNow);
                            try { _logger?.LogInformation("Ingested candidate: {Url}", res.Url); } catch { }
                        }
                        catch
                        {
                            _logger?.LogWarning("Failed to ingest URL {Url}", res.Url);
                        }
                    }
                }

                noticiasCandidatas = await _context.News
                    .Include(n => n.Tag)
                    .Include(n => n.DetailNews)
                    .Where(n => !noticiasVistasIds.Contains(n.IdNews) && n.CapturedDate >= fechaLimite)
                    .ToListAsync();
            }
            catch
            {
            }
        }

        var scored = noticiasCandidatas.Select(news => new
        {
            Noticia = news,
            BaseScore = CalculateScore(news, userPreferences)
        }).ToList();

        if (llm == null)
        {
            return scored.OrderByDescending(x => x.BaseScore).Select(x => x.Noticia).Take(cantidad).ToList();
        }

        var user = await _context.Users.FindAsync(userId);
        var profileParts = userPreferences.Select(p => $"{p.Tag?.Name ?? p.IdTag.ToString()}:{p.Weight}").ToList();
        if (user != null)
        {
            profileParts.Add($"premium:{user.IsPremium}");
            profileParts.Add($"language:{user.Language}");
        }
        var userProfile = string.Join("; ", profileParts);

        var rerankPoolSize = Math.Min(scored.Count, Math.Max(cantidad * 3, 20));
        var rerankPool = scored.OrderByDescending(x => x.BaseScore).Take(rerankPoolSize).ToList();

        var candidateTexts = new List<string>();
        var detailsToSummarize = new List<DetailNews>();

        foreach (var item in rerankPool)
        {
            var n = item.Noticia;
            var detail = n?.DetailNews;
            var excerpt = detail?.Context ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(excerpt) && excerpt.Length > 600) excerpt = excerpt.Substring(0, 600) + "...";

            var text = $"Title: {detail?.Keyword ?? ""}\nTag: {n?.Tag?.Name ?? ""}\nExcerpt: {excerpt}";
            candidateTexts.Add(text);

            if (detail != null && string.IsNullOrWhiteSpace(detail.AiSummary))
            {
                detailsToSummarize.Add(detail);
            }
        }

        if (detailsToSummarize.Count > 0)
        {
            var textsForSumm = detailsToSummarize.Select(d => d.Context ?? string.Empty).ToList();
            var summaries = await llm.SummarizeBatchAsync(textsForSumm, 220);
            var changed = false;
            for (int i = 0; i < detailsToSummarize.Count; i++)
            {
                var s = summaries.ElementAtOrDefault(i);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    detailsToSummarize[i].AiSummary = s;
                    changed = true;
                }
            }
            if (changed)
            {
                await _context.SaveChangesAsync();
            }
        }

        var llmScores = new List<double?>();
        const int localBatchSize = 8;
        for (int i = 0; i < candidateTexts.Count; i += localBatchSize)
        {
            var batch = candidateTexts.Skip(i).Take(localBatchSize).ToList();
            var batchScores = await llm.BatchScoreAsync(userProfile, batch);
            llmScores.AddRange(batchScores);
        }

        var combined = new List<(News Noticia, double Score)>();
        for (int i = 0; i < rerankPool.Count; i++)
        {
            var baseScore = rerankPool[i].BaseScore;
            double llmPart = 0.0;
            var llmScore = (i < llmScores.Count) ? llmScores[i] : null;
            if (llmScore.HasValue)
            {
                llmPart = (llmScore.Value / 100.0) * 5.0;
            }

            combined.Add((rerankPool[i].Noticia, baseScore + llmPart));
        }

        var final = combined.OrderByDescending(x => x.Score).Select(x => x.Noticia).Take(cantidad).ToList();
        return final;
    }

    public async Task<string> SummarizeAsync(DetailNews? detail, int maxChars = 220)
    {
        var fallback = Summarize(detail, maxChars);
        var llm = _llmService;

        if (llm == null) return fallback;

        var text = detail?.Context ?? string.Empty;
        try
        {
            var llmResult = await llm.SummarizeAsync(text, maxChars);
            if (!string.IsNullOrWhiteSpace(llmResult)) return llmResult;
        }
        catch
        {
        }

        return fallback;
    }

    public async Task<string> SummarizeAsync(string prompt, int maxChars = 220)
    {
        var llm = _llmService;
        if (llm == null) return string.Empty;

        try
        {
            var llmResult = await llm.SummarizeAsync(prompt, maxChars);
            return string.IsNullOrWhiteSpace(llmResult) ? string.Empty : llmResult;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate summary for prompt: {Prompt}", prompt);
            return string.Empty;
        }
    }

    public string Summarize(DetailNews? detail, int maxChars = 220)
    {
        if (detail == null) return string.Empty;

        var text = detail.Context ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sentences = text.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        if (sentences.Length >= 2)
        {
            var summary = string.Join(". ", sentences.Take(2)) + ".";
            if (summary.Length <= maxChars) return summary;
        }

        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars).TrimEnd() + "...";
    }

    private double CalculateScore(News? news, List<Preference> userPreferences)
    {
        if (news == null) return 0.0;

        double score = 0.0;

        var matchingPref = userPreferences.FirstOrDefault(p => p != null && p.IdTag == news.IdTag);
        if (matchingPref != null)
        {
            score += matchingPref.Weight * 2.0;
        }

        int totalVotes = news.Likes + news.Dislikes;
        if (totalVotes > 0)
        {
            double popularityRatio = (double)news.Likes / totalVotes;
            score += popularityRatio * 1.5;
        }

        if (!news.IsSfw) score -= 10.0;

        return score;
    }

    public async Task RegisterInteractionAsync(int userId, int newsId, bool liked, bool interested)
    {
        var news = await _context.News.FindAsync(newsId);
        if (news == null) return;

        if (liked) news.Likes++;
        else news.Dislikes++;

        var userNews = await _context.UserNews
            .FirstOrDefaultAsync(un => un.IdUser == userId && un.IdNews == newsId);

        if (userNews == null)
        {
            userNews = new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                Interest = interested,
                DateSeen = DateTime.UtcNow
            };
            _context.UserNews.Add(userNews);
        }
        else
        {
            userNews.Seen = true;
            userNews.Interest = userNews.Interest || interested;
            userNews.DateSeen = DateTime.UtcNow;
        }

        var userPref = await _context.UserPreferences
            .Include(up => up.Preference)
            .FirstOrDefaultAsync(up => up.IdUser == userId && up.Preference != null && up.Preference.IdTag == news.IdTag);

        if (userPref != null)
        {
            userPref.Weight += liked ? 1 : -1;
        }

        await _context.SaveChangesAsync();
    }

    public async Task RegisterCategoryClickAsync(int userId, int newsId)
    {
        var news = await _context.News.FindAsync(newsId);
        if (news == null) return;

        var userNews = await _context.UserNews
            .FirstOrDefaultAsync(un => un.IdUser == userId && un.IdNews == newsId);

        if (userNews == null)
        {
            _context.UserNews.Add(new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                Interest = true,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            userNews.Seen = true;
            userNews.Interest = true;
            userNews.DateSeen = DateTime.UtcNow;
        }

        var userPref = await _context.UserPreferences
            .Include(up => up.Preference)
            .FirstOrDefaultAsync(up => up.IdUser == userId && up.Preference != null && up.Preference.IdTag == news.IdTag);

        if (userPref != null)
        {
            userPref.Weight += 1;
        }

        await _context.SaveChangesAsync();
    }

    public async Task AddCommentAsync(int userId, int newsId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.Length > 500) text = text[..500];

        var news = await _context.News.FindAsync(newsId);
        if (news == null) return;

        _context.NewsComments.Add(new NewsComment
        {
            IdUser = userId,
            IdNews = newsId,
            CommentText = text,
            CreatedAt = DateTime.UtcNow
        });

        var userNews = await _context.UserNews
            .FirstOrDefaultAsync(un => un.IdUser == userId && un.IdNews == newsId);

        if (userNews == null)
        {
            _context.UserNews.Add(new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                Interest = true,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            userNews.Interest = true;
            userNews.Seen = true;
        }

        var userPref = await _context.UserPreferences
            .Include(up => up.Preference)
            .FirstOrDefaultAsync(up => up.IdUser == userId && up.Preference != null && up.Preference.IdTag == news.IdTag);

        if (userPref != null)
        {
            userPref.Weight += 1;
        }

        await _context.SaveChangesAsync();
    }

    public async Task<List<NewsComment>> GetCommentsForNewsAsync(int newsId)
    {
        return await _context.NewsComments
            .Include(c => c.User)
            .Where(c => c.IdNews == newsId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }

    public async Task MarkNewsInterestAsync(int userId, int newsId)
    {
        var userNews = await _context.UserNews
            .FirstOrDefaultAsync(un => un.IdUser == userId && un.IdNews == newsId);

        if (userNews == null)
        {
            _context.UserNews.Add(new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                Interest = true,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            userNews.Interest = true;
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Busca noticias vía LM Studio para una categoría de Home, resume, categoriza y persiste en BD.
    /// </summary>
    /// <summary>
    /// Performs a broad AI-driven category search through LM Studio + rzk/searxng-search,
    /// selects one fresh result and stores only the lightweight metadata needed by Home.
    /// Full article generation is deferred until NewsDetails is opened.
    /// </summary>
    public async Task<News?> ExecuteAiSearchForCategoryAsync(
        string categoryName,
        int? userId = null,
        bool ignoreOneYearRestriction = false,
        CancellationToken cancellationToken = default)
    {
        var llm = _llmService;
        var ingest = _newsIngest;
        if (llm == null || ingest == null)
            return null;

        var mapping = InternetSearchService.GetCategoryMapping()
            .FirstOrDefault(m => m.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(mapping.Category))
            return null;

        var tag = await _context.Tags
            .FirstOrDefaultAsync(t => t.Name.ToLower() == mapping.Category.ToLower(), cancellationToken);

        if (tag == null)
        {
            tag = new Tag { Name = mapping.Category };
            _context.Tags.Add(tag);
            await _context.SaveChangesAsync(cancellationToken);
        }

        var blockedUrls = (await _context.News
                .AsNoTracking()
                .Select(n => n.Hyperlink)
                .ToListAsync(cancellationToken))
            .Select(NormalizeUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blockedTitles = (await _context.News
                .AsNoTracking()
                .Where(n => n.DetailNews != null)
                .Select(n => n.DetailNews!.Keyword)
                .ToListAsync(cancellationToken))
            .Select(NormalizeTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var searchTask = BuildCategorySearchTask(
                mapping.Category,
                mapping.SearchQuery,
                ignoreOneYearRestriction,
                attempt > 1);

            var results = await llm.SearchWebAsync(
                searchTask,
                maxResults: 10,
                cancellationToken: cancellationToken);

            var candidates = results
                .Select(r => (
                    Title: r.Title?.Trim() ?? string.Empty,
                    Url: NormalizeUrl(r.Url),
                    Snippet: r.Snippet?.Trim() ?? string.Empty))
                .Where(r =>
                    !string.IsNullOrWhiteSpace(r.Url) &&
                    !blockedUrls.Contains(r.Url) &&
                    (string.IsNullOrWhiteSpace(r.Title) ||
                     !blockedTitles.Contains(NormalizeTitle(r.Title))))
                .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (candidates.Count == 0)
                continue;

            var selected = candidates[Random.Shared.Next(candidates.Count)];
            var summary = !string.IsNullOrWhiteSpace(selected.Snippet)
                ? selected.Snippet
                : selected.Title;

            try
            {
                var news = await ingest.SaveOrUpdateNewsAsync(
                    selected.Url,
                    summary,
                    selected.Title,
                    tag.IdTag,
                    isSfw: true,
                    secureLink: true,
                    isAi: true,
                    newsDate: DateTime.UtcNow,
                    fullBodyText: null,
                    bodySource: "Unknown",
                    imageUrl: null,
                    additionalSourcesJson: null);

                return await _context.News
                    .Include(n => n.Tag)
                    .Include(n => n.DetailNews)
                    .FirstOrDefaultAsync(n => n.IdNews == news.IdNews, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to ingest category result for {Category}", categoryName);
            }
        }

        return null;
    }

    private static string BuildCategorySearchTask(
        string category,
        string baseSearchQuery,
        bool ignoreOneYearRestriction,
        bool retry)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Search the public internet for current news related to this broad category.");
        sb.AppendLine("The category is a semantic domain, not the literal search phrase. Infer concrete people, companies, events, organizations, issues, products, places and topics that belong to it, then use those associations to form the search.");
        sb.AppendLine("Examples:");
        sb.AppendLine("- Politics: governments, presidents, leaders, elections, legislation, diplomacy, public policy.");
        sb.AppendLine("- Technology: AI, NVIDIA, semiconductors, software, cybersecurity, devices, major technology companies.");
        sb.AppendLine("- Health: cancer, medicine, treatments, research, hospitals, public health, diseases.");
        sb.AppendLine("- Sports: soccer, athletics, basketball, tennis, motorsport, competitions and athletes.");
        sb.AppendLine("- Business: companies, markets, finance, products, trade, investment and economic activity.");
        sb.AppendLine("- Entertainment: film, television, music, gaming, streaming, celebrities and cultural events.");
        sb.AppendLine();
        sb.AppendLine($"CATEGORY: {category}");
        sb.AppendLine($"STARTING SEARCH CONCEPT: {baseSearchQuery}");
        if (!ignoreOneYearRestriction)
            sb.AppendLine("Prefer stories from the last 12 months and prioritize recent/current reporting.");
        else
            sb.AppendLine("Prioritize recent/current reporting, without a strict one-year restriction.");
        if (retry)
            sb.AppendLine("Find a different story than previous attempts; prefer a different publisher when possible.");
        sb.AppendLine("Use the search_web tool exactly once. Return the real results from that tool. Do not invent, summarize, retry, or create a second JSON object.");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the personalized Just For You feed: one AI-selected story per explicit
    /// preference, using the user's full preference and interaction context. If the user
    /// has fewer than four preferences, additional distinct stories are gathered from
    /// the same preferences until four are available whenever the web search permits it.
    /// </summary>
    public async Task<List<PersonalizedRecommendation>> GetPersonalizedRecommendationsAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var output = new List<PersonalizedRecommendation>();
        if (_llmService == null || _newsIngest == null)
            return output;

        var userPreferenceRows = await _context.UserPreferences
            .AsNoTracking()
            .Include(up => up.Preference)
                .ThenInclude(p => p.Tag)
            .Where(up => up.IdUser == userId && up.Preference != null)
            .ToListAsync(cancellationToken);

        var distinctPreferences = userPreferenceRows
            .Where(up => !string.IsNullOrWhiteSpace(up.Preference?.Tag?.Name ?? up.Preference?.PreferenceName))
            .GroupBy(
                up => up.Preference!.Tag?.Name ?? up.Preference!.PreferenceName,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (distinctPreferences.Count == 0)
            return output;

        var personalizationContext = await BuildUserPersonalizationContextAsync(
            userId,
            cancellationToken);

        var blockedUrls = (await _context.News
                .AsNoTracking()
                .Select(n => n.Hyperlink)
                .ToListAsync(cancellationToken))
            .Select(NormalizeUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blockedTitles = (await _context.News
                .AsNoTracking()
                .Where(n => n.DetailNews != null)
                .Select(n => n.DetailNews!.Keyword)
                .ToListAsync(cancellationToken))
            .Select(NormalizeTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // First pass: exactly one story for every explicit preference.
        foreach (var userPreference in distinctPreferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preference = userPreference.Preference!;
            var preferenceName = preference.Tag?.Name ?? preference.PreferenceName ?? "News";
            var recommendation = await SearchPreferenceStoryAsync(
                userId,
                preferenceName,
                preference.IdTag,
                userPreference.Weight,
                personalizationContext,
                isSupplemental: false,
                blockedUrls,
                blockedTitles,
                cancellationToken);

            if (recommendation != null)
                output.Add(recommendation);
        }

        // Second pass: fill to four with additional DISTINCT stories from the user's
        // existing preferences. Cycle through preferences so the fill does not repeat
        // the same preference whenever possible.
        var fillPreferenceIndex = 0;
        var fillAttempts = 0;
        while (output.Count < 4 && fillAttempts < distinctPreferences.Count * 3)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var userPreference = distinctPreferences[fillPreferenceIndex % distinctPreferences.Count];
            var preference = userPreference.Preference!;
            var preferenceName = preference.Tag?.Name ?? preference.PreferenceName ?? "News";

            var recommendation = await SearchPreferenceStoryAsync(
                userId,
                preferenceName,
                preference.IdTag,
                userPreference.Weight,
                personalizationContext,
                isSupplemental: true,
                blockedUrls,
                blockedTitles,
                cancellationToken);

            if (recommendation != null)
                output.Add(recommendation);

            fillPreferenceIndex++;
            fillAttempts++;
        }

        return output;
    }

    public sealed record PersonalizedRecommendation(
        News News,
        string Preference,
        int Weight,
        bool IsSupplemental);

    private async Task<PersonalizedRecommendation?> SearchPreferenceStoryAsync(
        int userId,
        string preferenceName,
        int idTag,
        int preferenceWeight,
        string personalizationContext,
        bool isSupplemental,
        HashSet<string> blockedUrls,
        HashSet<string> blockedTitles,
        CancellationToken cancellationToken)
    {
        if (_llmService == null || _newsIngest == null)
            return null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var task = $"""
You are NuctiusNow's personalized news search agent.

The user has an explicit preference for: {preferenceName}

Use the user's complete preference and interaction history below to infer WHICH CURRENT
ANGLE of this preference is most relevant to this person. You may connect the preference
to companies, products, people, sports, diseases, technologies, events or related topics.
Do not simply search the preference word; reason about likely current interests first.
Then use the search_web tool to find current public-internet news about that angle.

USER CONTEXT:
{personalizationContext}

Search exactly for ONE strong current news story for this preference.
Prefer a distinct article and publisher. Do not use or repeat articles already stored
in NuctiusNow. Do not invent URLs. Return the actual search tool results; NuctiusNow
will read the tool output directly.
""";

            var results = await _llmService.SearchWebAsync(
                task,
                maxResults: 6,
                cancellationToken: cancellationToken);

            var candidate = results
                .Select(r => (
                    Title: r.Title?.Trim() ?? string.Empty,
                    Url: NormalizeUrl(r.Url),
                    Snippet: r.Snippet?.Trim() ?? string.Empty))
                .Where(r =>
                    !string.IsNullOrWhiteSpace(r.Url) &&
                    !blockedUrls.Contains(r.Url) &&
                    (string.IsNullOrWhiteSpace(r.Title) ||
                     !blockedTitles.Contains(NormalizeTitle(r.Title))))
                .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(candidate.Url))
                continue;

            var summary = !string.IsNullOrWhiteSpace(candidate.Snippet)
                ? candidate.Snippet
                : candidate.Title;

            try
            {
                var news = await _newsIngest.SaveOrUpdateNewsAsync(
                    candidate.Url,
                    summary,
                    candidate.Title,
                    idTag,
                    isSfw: true,
                    secureLink: true,
                    isAi: true,
                    newsDate: DateTime.UtcNow,
                    fullBodyText: null,
                    bodySource: "Unknown",
                    imageUrl: null,
                    additionalSourcesJson: null);

                blockedUrls.Add(candidate.Url);
                if (!string.IsNullOrWhiteSpace(candidate.Title))
                    blockedTitles.Add(NormalizeTitle(candidate.Title));

                return new PersonalizedRecommendation(
                    await _context.News
                        .Include(n => n.Tag)
                        .Include(n => n.DetailNews)
                        .FirstAsync(n => n.IdNews == news.IdNews, CancellationToken.None),
                    preferenceName,
                    preferenceWeight,
                    isSupplemental);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not ingest personalized result for {Preference}", preferenceName);
            }
        }

        return null;
    }

    private async Task<string> BuildUserPersonalizationContextAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var preferenceRows = await _context.UserPreferences
            .AsNoTracking()
            .Include(up => up.Preference)
                .ThenInclude(p => p.Tag)
            .Where(up => up.IdUser == userId)
            .ToListAsync(cancellationToken);

        var preferenceProfile = preferenceRows
            .Select(up =>
            {
                var name = up.Preference?.Tag?.Name ?? up.Preference?.PreferenceName;
                var weight = up.Weight;
                return string.IsNullOrWhiteSpace(name)
                    ? null
                    : $"{name!.Trim()} (weight {weight})";
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var interactionSummary = await _context.UserNews
            .AsNoTracking()
            .Where(un =>
                un.IdUser == userId &&
                un.Seen &&
                un.News != null &&
                un.News.Tag != null)
            .GroupBy(un => un.News!.Tag!.Name)
            .Select(g => new
            {
                Category = g.Key,
                TotalInteractions = g.Count(),
                Interested = g.Count(x => x.Interest),
                NotInterested = g.Count(x => !x.Interest),
                LastInteraction = g.Max(x => x.DateSeen)
            })
            .OrderByDescending(x => x.LastInteraction)
            .ToListAsync(cancellationToken);

        var recentInteractions = await _context.UserNews
            .AsNoTracking()
            .Include(un => un.News)
                .ThenInclude(n => n.Tag)
            .Include(un => un.News)
                .ThenInclude(n => n.DetailNews)
            .Where(un => un.IdUser == userId && un.Seen)
            .OrderByDescending(un => un.DateSeen)
            .Take(20)
            .ToListAsync(cancellationToken);

        var recentComments = await _context.NewsComments
            .AsNoTracking()
            .Include(comment => comment.News)
                .ThenInclude(n => n.Tag)
            .Where(comment => comment.IdUser == userId)
            .OrderByDescending(comment => comment.CreatedAt)
            .Take(8)
            .ToListAsync(cancellationToken);

        var sb = new StringBuilder();

        sb.AppendLine("ALL STORED USER PREFERENCES:");
        if (preferenceProfile.Count == 0)
            sb.AppendLine("None recorded.");
        else
            foreach (var preference in preferenceProfile)
                sb.AppendLine($"- {preference}");

        sb.AppendLine();
        sb.AppendLine("ALL STORED INTERACTION COUNTS BY CATEGORY:");
        if (interactionSummary.Count == 0)
            sb.AppendLine("No prior interactions recorded.");
        else
            foreach (var item in interactionSummary)
            {
                sb.AppendLine(
                    $"- {item.Category}: {item.TotalInteractions} interactions, " +
                    $"{item.Interested} marked interested, {item.NotInterested} seen/not-marked-interest, " +
                    $"last activity {item.LastInteraction:yyyy-MM-dd HH:mm} UTC");
            }

        sb.AppendLine();
        sb.AppendLine("RECENT INTERACTION DETAILS (latest 20):");
        if (recentInteractions.Count == 0)
            sb.AppendLine("None.");
        else
        {
            foreach (var interaction in recentInteractions)
            {
                var category = interaction.News?.Tag?.Name ?? "Unknown";
                var title = interaction.News?.DetailNews?.Keyword ?? "Untitled";
                var signal = interaction.Interest ? "interested" : "seen/not-marked-interest";
                sb.AppendLine(
                    $"- {category} | {signal} | {interaction.DateSeen:yyyy-MM-dd} | {title}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("RECENT USER COMMENTS (latest 8):");
        if (recentComments.Count == 0)
            sb.AppendLine("None.");
        else
        {
            foreach (var comment in recentComments)
            {
                var category = comment.News?.Tag?.Name ?? "Unknown";
                var title = comment.News?.DetailNews?.Keyword ?? "Untitled";
                var text = comment.CommentText.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 160)
                    text = text[..160] + "…";
                sb.AppendLine($"- {category} | {title} | comment: {text}");
            }
        }

        // Keep the profile compact enough for a 9B model while preserving
        // the complete preference list and complete category-level interaction
        // aggregates. Detailed history is intentionally the newest subset.
        var context = sb.ToString().Trim();
        return context.Length <= 9000 ? context : context[..9000];
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var normalized = Regex.Replace(
            title.ToLowerInvariant(),
            @"[^\p{L}\p{N}]+",
            " ");

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var value = url.Trim();

        // Convert markdown links returned by the local model to the real URL.
        var markdownStart = value.IndexOf("](", StringComparison.Ordinal);
        if (value.StartsWith("[") && markdownStart > 0 && value.EndsWith(")"))
        {
            value = value[(markdownStart + 2)..^1];
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return value.Trim();
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool HasUsableFullBodyText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        var value = body.Trim();

        // Old prototype records used this placeholder instead of real article text.
        if (value.StartsWith("This is placeholder raw data gathered from analyzing the article", StringComparison.OrdinalIgnoreCase))
            return false;

        return value.Length >= 100;
    }

    /// <summary>
    /// Ensures an existing news record has its full article body. This is used by
    /// NewsDetails so older database rows created before full-body extraction was
    /// implemented are also backfilled when the user opens them.
    /// </summary>
    public async Task<string?> EnsureFullBodyTextAsync(
        int newsId,
        CancellationToken cancellationToken = default)
    {
        var news = await _context.News
            .Include(n => n.DetailNews)
            .Include(n => n.Tag)
            .FirstOrDefaultAsync(
                n => n.IdNews == newsId,
                cancellationToken);

        if (news == null || news.DetailNews == null)
            return null;

        var detail = news.DetailNews;

        // Already have a valid body.
        if (!string.IsNullOrWhiteSpace(detail.FullBodyText))
        {
            if (detail.BodySource == "Original" ||
                detail.BodySource == "AiGenerated")
            {
                return detail.FullBodyText;
            }

            // Old records: verify whether their existing text is usable.
            if (!ArticleExtractionService.NeedsReExtraction(
                    detail.FullBodyText))
            {
                if (detail.BodySource == "Unknown")
                    detail.BodySource = "Original";

                await _context.SaveChangesAsync(
                    cancellationToken);

                return detail.FullBodyText;
            }
        }

        if (string.IsNullOrWhiteSpace(news.Hyperlink))
            return null;

        if (detail.BodySource == "Unavailable")
        {
            return null;
        }

        var title =
            string.IsNullOrWhiteSpace(detail.Keyword)
                ? "News article"
                : detail.Keyword.Trim();

        var category = news.Tag?.Name;

        var originalSnippet =
            !string.IsNullOrWhiteSpace(detail.AiSummary)
                ? detail.AiSummary
                : detail.Context;

        _logger?.LogInformation(
            "Obtaining article body for News {NewsId}: {Url}",
            newsId,
            news.Hyperlink);

        var result =
            await GetArticleBodyWithFallbackAsync(
                title,
                news.Hyperlink.Trim(),
                originalSnippet,
                category,
                searchResults: null,
                cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.BodyText))
        {
            detail.FullBodyText = result.BodyText;
            detail.BodySource = result.BodySource;
            detail.AdditionalSourcesJson = result.AdditionalSourcesJson;

            await _context.SaveChangesAsync(
                cancellationToken);

            _logger?.LogInformation(
                "Stored {BodySource} article body for News {NewsId}: {Length} characters",
                result.BodySource,
                newsId,
                result.BodyText.Length);

            return result.BodyText;
        }

        // Don't keep a corrupted previous body around.
        detail.FullBodyText = null;
        detail.BodySource = "Unavailable";

        await _context.SaveChangesAsync(
            cancellationToken);

        _logger?.LogWarning(
            "No article body could be obtained or generated for News {NewsId}",
            newsId);

        return null;
    }
    private static bool IsKnownProtectedSource(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        return host switch
        {
            "reuters.com" => true,
            "www.reuters.com" => true,
            "wsj.com" => true,
            "www.wsj.com" => true,
            _ => false
        };
    }
    /// <summary>
    /// Al menos una noticia por preferencia/tag del usuario; dispara búsqueda IA si falta contenido.
    /// </summary>
    public async Task<List<News>> GetPersonalizedNewsOnePerPreferenceAsync(int userId)
    {
        var userPreferencesList = await _context.UserPreferences
            .Include(up => up.Preference)
                .ThenInclude(p => p!.Tag)
            .Where(up => up.IdUser == userId)
            .ToListAsync();

        var preferences = userPreferencesList
            .Select(up => up.Preference)
            .Where(p => p != null)
            .ToList();

        if (preferences.Count == 0) return new List<News>();

        var tagIds = preferences.Select(p => p!.IdTag).Distinct().ToList();

        var seenIds = await _context.UserNews
            .Where(un => un.IdUser == userId && un.Seen)
            .Select(un => un.IdNews)
            .ToListAsync();

        var oneYearAgo = DateTime.UtcNow.AddYears(-1);
        var result = new List<News>();

        // 1. Try to bring random, unseen news < 1 year old
        foreach (var tagId in tagIds)
        {
            var item = await _context.News
                .Include(n => n.Tag)
                .Include(n => n.DetailNews)
                .Where(n => n.IdTag == tagId && n.CapturedDate >= oneYearAgo && !seenIds.Contains(n.IdNews))
                .OrderBy(n => EF.Functions.Random())
                .FirstOrDefaultAsync();

            if (item != null) result.Add(item);
        }

        // 2. If the AI is missing categories (none left unseen or < 1 year old), run AI search
        if (result.Count < tagIds.Count && _llmService != null && _newsIngest != null)
        {
            var missingTags = tagIds.Except(result.Select(r => r.IdTag)).ToList();

            try
            {
                await ExecuteNewsSearchWorkflowAsync(userId, numSearches: Math.Max(missingTags.Count, 4));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "AI workflow failed while filling personalized feed");
            }

            // Re-check DB for the newly ingested news
            foreach (var tagId in missingTags)
            {
                var item = await _context.News
                    .Include(n => n.Tag)
                    .Include(n => n.DetailNews)
                    .Where(n => n.IdTag == tagId && n.CapturedDate >= oneYearAgo && !seenIds.Contains(n.IdNews))
                    .OrderBy(n => EF.Functions.Random())
                    .FirstOrDefaultAsync();

                if (item != null)
                {
                    result.Add(item);
                }
                else
                {
                    // 3. Absolute fallback: bring ONE from the database
                    var fallbackItem = await _context.News
                        .Include(n => n.Tag)
                        .Include(n => n.DetailNews)
                        .Where(n => n.IdTag == tagId)
                        .OrderBy(n => EF.Functions.Random())
                        .FirstOrDefaultAsync();

                    if (fallbackItem != null) result.Add(fallbackItem);
                }
            }
        }
        else if (result.Count < tagIds.Count)
        {
            // If AI service is down, still apply absolute DB fallback for missing categories
            var missingTags = tagIds.Except(result.Select(r => r.IdTag)).ToList();
            foreach (var tagId in missingTags)
            {
                var fallbackItem = await _context.News
                    .Include(n => n.Tag)
                    .Include(n => n.DetailNews)
                    .Where(n => n.IdTag == tagId)
                    .OrderBy(n => EF.Functions.Random())
                    .FirstOrDefaultAsync();

                if (fallbackItem != null) result.Add(fallbackItem);
            }
        }

        // (The AI summary generation loop remains the same at the bottom of this method...)
        var llm = _llmService;
        if (llm != null)
        {
            foreach (var news in result)
            {
                if (news.DetailNews != null && string.IsNullOrWhiteSpace(news.DetailNews.AiSummary))
                {
                    var summary = await SummarizeTextAsync(news.DetailNews.Context ?? string.Empty, 220);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        news.DetailNews.AiSummary = summary;
                    }
                }
            }
            await _context.SaveChangesAsync();
        }

        return result;
    }

    public async Task<List<string>> GetUserPreferencesAsync(int userId)
    {
        var userPreferencesList = await _context.UserPreferences
            .Include(up => up.Preference)
                .ThenInclude(p => p.Tag)
            .Where(up => up.IdUser == userId)
            .ToListAsync();

        return userPreferencesList
            .Where(up => up.Preference != null)
            .OrderByDescending(up => up.Weight)
            .Select(up => up.Preference!.Tag?.Name ?? up.Preference!.IdTag.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();
    }

    public async Task RegisterReadingTimeAsync(int userId, int newsId, double timeSpentSeconds)
    {
        var userNews = await _context.UserNews
            .FirstOrDefaultAsync(un => un.IdUser == userId && un.IdNews == newsId);

        if (userNews == null)
        {
            userNews = new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                Interest = timeSpentSeconds >= 120,
                DateSeen = DateTime.UtcNow
            };
            _context.UserNews.Add(userNews);
        }
        else
        {
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
            if (timeSpentSeconds >= 120)
                userNews.Interest = true;
        }

        await _context.SaveChangesAsync();
    }

    private sealed record ArticleEvidence(
        string Text,
        List<ArticleSource> Sources);

    private sealed record ArticleSource(
        string Title,
        string Url,
        string Owner);

    private async Task<ArticleEvidence> BuildArticleEvidenceAsync(
        string title,
        string sourceUrl,
        string? originalSnippet,
        string? category,
        IEnumerable<(string Title, string Url, string Snippet)>? existingResults,
        CancellationToken cancellationToken)
    {
        var sources = existingResults?.ToList()
            ?? new List<(string Title, string Url, string Snippet)>();

        if (sources.Count == 0 && _llmService != null)
        {
            var query = string.IsNullOrWhiteSpace(category)
                ? $"{title} current news"
                : $"{title} {category} current news";

            try
            {
                sources = await _llmService.SearchWebAsync(
                    query,
                    8,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not gather fallback evidence for '{Title}'", title);
            }
        }

        var originalHost = string.Empty;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var originalUri))
            originalHost = originalUri.Host;

        var evidenceBlocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(originalSnippet))
            evidenceBlocks.Add($"ORIGINAL SEARCH SNIPPET:\n{originalSnippet.Trim()}");

        var additionalSources = new List<ArticleSource>();
        var usedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedUrl = NormalizeUrl(result.Url);
            if (string.IsNullOrWhiteSpace(normalizedUrl) ||
                string.IsNullOrWhiteSpace(result.Snippet))
                continue;

            if (normalizedUrl.Equals(sourceUrl, StringComparison.OrdinalIgnoreCase))
                continue;

            string host = string.Empty;
            if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var resultUri))
                host = resultUri.Host;

            if (!string.IsNullOrWhiteSpace(originalHost) &&
                host.Equals(originalHost, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(host) && !usedHosts.Add(host))
                continue;

            var snippet = result.Snippet.Trim();
            if (snippet.Length > 1600)
                snippet = snippet[..1600];

            evidenceBlocks.Add(
                $"SOURCE:\nTitle: {result.Title}\nURL: {normalizedUrl}\nSnippet: {snippet}");

            additionalSources.Add(new ArticleSource(
                result.Title?.Trim() ?? host,
                normalizedUrl,
                GetSourceOwner(normalizedUrl)));

            if (additionalSources.Count >= 6)
                break;
        }

        return new ArticleEvidence(
            string.Join("\n\n------------------------------\n\n", evidenceBlocks),
            additionalSources);
    }

    private static string GetSourceOwner(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Source";

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];

        return host switch
        {
            "clarin.com" => "Clarín",
            "cronica.com.ar" => "Crónica",
            "perfil.com" => "Perfil",
            "lanacion.com.ar" => "La Nación",
            "infobae.com" => "Infobae",
            "pagina12.com.ar" => "Página/12",
            "tn.com.ar" => "TN",
            "bbc.com" or "bbc.co.uk" => "BBC",
            "reuters.com" => "Reuters",
            "apnews.com" => "AP",
            "nytimes.com" => "The New York Times",
            "theguardian.com" => "The Guardian",
            _ => host
        };
    }

    private async Task<(string? BodyText, string BodySource, string? AdditionalSourcesJson)>
        GetArticleBodyWithFallbackAsync(
            string title,
            string sourceUrl,
            string? originalSnippet,
            string? category,
            IEnumerable<(string Title, string Url, string Snippet)>? searchResults,
            CancellationToken cancellationToken)
    {
        if (_llmService == null)
            return (null, "Unavailable", null);

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) &&
            ArticleExtractionService.IsKnownNonArticlePage(sourceUri))
        {
            _logger?.LogWarning("Skipping article generation for non-article URL: {Url}", sourceUrl);
            return (null, "Unavailable", null);
        }

        // Generate the requested NuctiusNow article first from multiple independent sources.
        // Direct extraction remains a fallback when the AI evidence route is unavailable.
        try
        {
            var evidence = await BuildArticleEvidenceAsync(
                title,
                sourceUrl,
                originalSnippet,
                category,
                searchResults,
                cancellationToken);

            if (evidence.Text.Length >= 500)
            {
                var generated = await _llmService.GenerateOriginalArticleAsync(
                    title,
                    evidence.Text,
                    category,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(generated))
                {
                    var json = evidence.Sources.Count == 0
                        ? null
                        : JsonSerializer.Serialize(evidence.Sources);

                    return (
                        generated.Trim(),
                        "AiGenerated",
                        json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AI article generation path failed for {Url}", sourceUrl);
        }

        // Fallback to deterministic extraction for sources where the AI route cannot generate.
        if (_articleExtractor != null)
        {
            try
            {
                var extraction = await _articleExtractor.ExtractAsync(
                    sourceUrl,
                    cancellationToken);

                if (extraction.Success &&
                    !string.IsNullOrWhiteSpace(extraction.Text) &&
                    extraction.Text.Length >= 500)
                {
                    return (
                        extraction.Text.Trim(),
                        "Original",
                        null);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Original article extraction failed for {Url}", sourceUrl);
            }
        }

        return (null, "Unavailable", null);
    }

    public async Task<string?> EnsureAiGeneratedArticleAsync(
    int newsId,
    CancellationToken cancellationToken = default)
    {
        var news = await _context.News
            .Include(n => n.DetailNews)
            .Include(n => n.Tag)
            .FirstOrDefaultAsync(
                n => n.IdNews == newsId,
                cancellationToken);

        if (news == null || news.DetailNews == null)
        {
            return null;
        }

        var detail = news.DetailNews;

        // Si ya existe un artículo generado por IA, utilizarlo.
        if (detail.BodySource == "AiGenerated" &&
            !string.IsNullOrWhiteSpace(detail.FullBodyText))
        {
            var cleanedExistingArticle =
                CleanGeneratedArticle(detail.FullBodyText);

            if (!string.Equals(
                    cleanedExistingArticle,
                    detail.FullBodyText,
                    StringComparison.Ordinal))
            {
                detail.FullBodyText = cleanedExistingArticle;

                await _context.SaveChangesAsync(
                    cancellationToken);

                _logger?.LogInformation(
                    "Cleaned duplicated content from existing AI article for News {NewsId}",
                    newsId);
            }

            return detail.FullBodyText;
        }

        if (_llmService == null)
        {
            _logger?.LogWarning(
                "AI service unavailable for News {NewsId}",
                newsId);

            return null;
        }

        if (string.IsNullOrWhiteSpace(news.Hyperlink))
        {
            _logger?.LogWarning(
                "News {NewsId} has no source URL.",
                newsId);

            return null;
        }

        var title =
            string.IsNullOrWhiteSpace(detail.Keyword)
                ? "News article"
                : detail.Keyword.Trim();

        var category = news.Tag?.Name;

        // Preferimos el resumen generado previamente.
        var originalSnippet =
            !string.IsNullOrWhiteSpace(detail.AiSummary)
                ? detail.AiSummary
                : detail.Context;

        _logger?.LogInformation(
            "Generating AI article for News {NewsId}: {Title}",
            newsId,
            title);

        try
        {
            // IMPORTANTE:
            // No se intenta abrir la URL original.
            // La IA trabaja solamente con información obtenida
            // mediante búsquedas de otras fuentes.
            var evidence = await BuildArticleEvidenceAsync(
                title,
                news.Hyperlink.Trim(),
                originalSnippet,
                category,
                existingResults: null,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(evidence.Text) ||
                evidence.Text.Length < 500)
            {
                _logger?.LogWarning(
                    "Insufficient evidence to generate AI article for News {NewsId}",
                    newsId);

                detail.BodySource = "Unavailable";
                detail.FullBodyText = null;

                await _context.SaveChangesAsync(cancellationToken);

                return null;
            }

            var generatedArticle =
                await _llmService.GenerateOriginalArticleAsync(
                    title,
                    evidence.Text,
                    category,
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(generatedArticle))
            {
                _logger?.LogWarning(
                    "LLM could not generate article for News {NewsId}",
                    newsId);

                detail.BodySource = "Unavailable";
                detail.FullBodyText = null;

                await _context.SaveChangesAsync(cancellationToken);

                return null;
            }

            detail.FullBodyText =
                CleanGeneratedArticle(generatedArticle);

            detail.BodySource = "AiGenerated";
            detail.AdditionalSourcesJson = evidence.Sources.Count == 0
                ? null
                : JsonSerializer.Serialize(evidence.Sources);

            await _context.SaveChangesAsync(cancellationToken);

            _logger?.LogInformation(
                "AI article generated and stored for News {NewsId}. Length={Length}",
                newsId,
                detail.FullBodyText.Length);

            return detail.FullBodyText;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Failed to generate AI article for News {NewsId}",
                newsId);

            return null;
        }
    }

    private static string CleanGeneratedArticle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        // Remove accidental Markdown fences.
        text = Regex.Replace(
            text,
            @"^\s*```(?:text|markdown)?\s*",
            string.Empty,
            RegexOptions.IgnoreCase);

        text = Regex.Replace(
            text,
            @"\s*```\s*$",
            string.Empty,
            RegexOptions.IgnoreCase);

        // Split into paragraphs.
        var paragraphs = Regex
            .Split(text, @"\n\s*\n+")
            .Select(p => Regex.Replace(p, @"\s+", " ").Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (paragraphs.Count == 0)
            return string.Empty;

        var cleaned = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            // Ignore very short paragraphs when checking repetition.
            if (paragraph.Length < 40)
            {
                cleaned.Add(paragraph);
                continue;
            }

            bool duplicate = false;

            foreach (var existing in cleaned)
            {
                if (existing.Length < 40)
                    continue;

                if (AreVerySimilar(
                        existing,
                        paragraph))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                cleaned.Add(paragraph);
            }
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            cleaned).Trim();
    }

    private static bool AreVerySimilar(
        string first,
        string second)
    {
        var firstWords = Regex
            .Matches(
                first.ToLowerInvariant(),
                @"[\p{L}\p{N}]{3,}")
            .Select(m => m.Value)
            .ToHashSet();

        var secondWords = Regex
            .Matches(
                second.ToLowerInvariant(),
                @"[\p{L}\p{N}]{3,}")
            .Select(m => m.Value)
            .ToHashSet();

        if (firstWords.Count == 0 ||
            secondWords.Count == 0)
        {
            return false;
        }

        var intersection =
            firstWords.Intersect(secondWords).Count();

        var union =
            firstWords.Union(secondWords).Count();

        if (union == 0)
            return false;

        var similarity =
            (double)intersection / union;

        return similarity >= 0.90;
    }

    public async Task<string?> EnsureNewsImageAsync(
    int newsId,
    CancellationToken cancellationToken = default)
    {
        var news = await _context.News
            .Include(n => n.DetailNews)
            .Include(n => n.Tag)
            .FirstOrDefaultAsync(
                n => n.IdNews == newsId,
                cancellationToken);

        if (news?.DetailNews == null)
            return null;

        if (!string.IsNullOrWhiteSpace(
                news.DetailNews.ImageUrl))
        {
            return news.DetailNews.ImageUrl;
        }

        if (_llmService == null)
            return null;

        var title =
            string.IsNullOrWhiteSpace(
                news.DetailNews.Keyword)
                ? "News"
                : news.DetailNews.Keyword.Trim();

        var category =
            news.Tag?.Name ?? "News";

        try
        {
            var imageUrl =
                await _llmService.SearchImageUrlAsync(
                    $"{title} {category}",
                    cancellationToken);

            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;

            news.DetailNews.ImageUrl = imageUrl.Trim();

            await _context.SaveChangesAsync(
                cancellationToken);

            return news.DetailNews.ImageUrl;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Could not generate image for News {NewsId}",
                newsId);

            return null;
        }
    }
}