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
                results = await llm.SearchNewsAsync($"{query} latest current news", 1);
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
                results = await llm.SearchNewsAsync($"{query} latest current news", perPref);
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
                    var searchResults = await llm.SearchNewsAsync($"{query} latest current news", 5);
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
        if (news == null)
            return;

        var userNews = await _context.UserNews
            .FirstOrDefaultAsync(un => un.IdUser == userId && un.IdNews == newsId);

        var newReaction = liked ? 1 : -1;
        var previousReaction = userNews?.Reaction ?? 0;

        // The same user may have only one reaction for a news item.
        // Clicking the same reaction again is a no-op.
        if (previousReaction == newReaction)
        {
            if (userNews != null)
            {
                userNews.Seen = true;
                userNews.DateSeen = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return;
        }

        if (previousReaction == 1)
            news.Likes = Math.Max(0, news.Likes - 1);
        else if (previousReaction == -1)
            news.Dislikes = Math.Max(0, news.Dislikes - 1);

        if (newReaction == 1)
            news.Likes++;
        else
            news.Dislikes++;

        if (userNews == null)
        {
            userNews = new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                Interest = newReaction == 1,
                Reaction = newReaction,
                DateSeen = DateTime.UtcNow
            };

            _context.UserNews.Add(userNews);
        }
        else
        {
            userNews.Seen = true;
            userNews.Interest = newReaction == 1;
            userNews.Reaction = newReaction;
            userNews.DateSeen = DateTime.UtcNow;
        }

        // Convert the reaction change into recommendation-signal weight.
        // +3 = like, -3 = dislike. Switching directly from one reaction to the other
        // reverses the old signal and applies the new one in the same transaction.
        var reactionDelta = (newReaction - previousReaction) * 3;
        await LearnFromNewsAsync(userId, newsId, reactionDelta);

        await _context.SaveChangesAsync();
    }

    public async Task<int> GetUserReactionAsync(int userId, int newsId)
    {
        var reaction = await _context.UserNews
            .AsNoTracking()
            .Where(un => un.IdUser == userId && un.IdNews == newsId)
            .Select(un => (int?)un.Reaction)
            .FirstOrDefaultAsync();

        return reaction ?? 0;
    }

    /// <summary>
    /// Registers a direct article view without creating a like/dislike reaction.
    /// A small positive learning signal is recorded so direct opens contribute to
    /// future recommendations as requested by the personalization flow.
    /// </summary>
    public async Task RegisterNewsViewAsync(int userId, int newsId)
    {
        var newsExists = await _context.News
            .AsNoTracking()
            .AnyAsync(n => n.IdNews == newsId);

        if (!newsExists)
            return;

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
                Reaction = 0,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
            if (userNews.Reaction >= 0)
                userNews.Interest = true;
        }

        await LearnFromNewsAsync(userId, newsId, 1);
        await _context.SaveChangesAsync();
    }
    public async Task RegisterCategoryClickAsync(int userId, int newsId)
    {
        var news = await _context.News.FindAsync(newsId);
        if (news == null)
            return;

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
                Reaction = 0,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
            if (userNews.Reaction >= 0)
                userNews.Interest = true;
        }

        await LearnFromNewsAsync(userId, newsId, 1);
        await _context.SaveChangesAsync();
    }

    public async Task AddCommentAsync(int userId, int newsId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        text = text.Trim();
        if (text.Length > 500)
            text = text[..500];

        var news = await _context.News.FindAsync(newsId);
        if (news == null)
            return;

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
                Reaction = 0,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
            if (userNews.Reaction >= 0)
                userNews.Interest = true;
        }

        await LearnFromNewsAsync(userId, newsId, 3, text);
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
                Reaction = 0,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
            if (userNews.Reaction >= 0)
                userNews.Interest = true;
        }

        await LearnFromNewsAsync(userId, newsId, 1);
        await _context.SaveChangesAsync();
    }

    public async Task<News?> ExecuteAiSearchForCategoryAsync(
        string categoryName,
        int? userId = null,
        bool ignoreOneYearRestriction = false,
        CancellationToken cancellationToken = default,
        bool forceRandomSelection = false)
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

        var interestTerms = userId.HasValue
            ? await GetUserInterestTermsAsync(userId.Value, cancellationToken)
            : new List<(string Keyword, int Weight)>();

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
            cancellationToken.ThrowIfCancellationRequested();

            var searchQuery = BuildCategorySearchQuery(
                mapping.SearchQuery,
                interestTerms,
                attempt,
                forceRandomSelection);

            var results = await llm.SearchNewsAsync(
                searchQuery,
                maxResults: 12,
                cancellationToken: cancellationToken,
                startingPage: (attempt - 1) * 3 + 1);

            var candidates = results
                .Select(r => (
                    Title: r.Title?.Trim() ?? string.Empty,
                    Url: NormalizeUrl(r.Url),
                    Snippet: r.Snippet?.Trim() ?? string.Empty))
                .Where(r =>
                    !string.IsNullOrWhiteSpace(r.Url) &&
                    !blockedUrls.Contains(r.Url) &&
                    LooksLikeValidNewsCandidate(r.Title, r.Url, r.Snippet) &&
                    MatchesCategoryTheme(mapping.Category, r.Title, r.Snippet))
                .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (candidates.Count == 0)
                continue;

            // Home must never turn an unmatched interest into an off-topic article.
            // Search results are first restricted to the requested category, then the
            // user's learned interests are used as a relevance preference. If none of
            // the category results match an interest, return null after the search
            // attempts so CategoryNewsService can provide a random article from the
            // same category as the explicit fallback.
            if (forceRandomSelection)
                continue;

            var selected = SelectBestInterestCandidate(candidates, interestTerms);

            if (string.IsNullOrWhiteSpace(selected.Url))
                continue;

            var summaryContext = !string.IsNullOrWhiteSpace(selected.Snippet)
                ? selected.Snippet
                : selected.Title;

            try
            {
                var news = await ingest.SaveOrUpdateNewsAsync(
                    selected.Url,
                    summaryContext,
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

                blockedUrls.Add(selected.Url);
                blockedTitles.Add(NormalizeTitle(selected.Title));

                try
                {
                    await EnsureNewsImageAsync(news.IdNews, cancellationToken);
                }
                catch (Exception imageEx)
                {
                    _logger?.LogDebug(imageEx, "Image enrichment failed for News {NewsId}", news.IdNews);
                }

                return await _context.News
                    .Include(n => n.Tag)
                    .Include(n => n.DetailNews)
                    .FirstOrDefaultAsync(n => n.IdNews == news.IdNews, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to ingest selected AI result for category {Category}", categoryName);
            }
        }

        return null;
    }

    private static string BuildCategorySearchQuery(
        string baseSearchQuery,
        IReadOnlyList<(string Keyword, int Weight)> interestTerms,
        int attempt,
        bool explorationMode)
    {
        var query = $"{baseSearchQuery} latest current news";

        if (explorationMode || interestTerms.Count == 0)
            return query;

        var preferred = interestTerms
            .OrderByDescending(x => x.Weight)
            .Skip((attempt - 1) % Math.Min(3, interestTerms.Count))
            .Take(2)
            .Select(x => x.Keyword)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (preferred.Count > 0)
            query += " " + string.Join(" ", preferred);

        return query;
    }

    private static bool LooksLikeValidNewsCandidate(string title, string url, string snippet)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        if (string.IsNullOrWhiteSpace(title) || title.Length < 10)
            return false;

        if (string.IsNullOrWhiteSpace(snippet) || snippet.Length < 30)
            return false;

        return !ArticleExtractionService.IsKnownNonArticlePage(uri);
    }

    private static (string Title, string Url, string Snippet) SelectBestInterestCandidate(
        IReadOnlyList<(string Title, string Url, string Snippet)> candidates,
        IReadOnlyList<(string Keyword, int Weight)> interestTerms)
    {
        if (candidates.Count == 0 || interestTerms.Count == 0)
            return default;

        var scored = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = interestTerms.Sum(term =>
                {
                    var keyword = term.Keyword.Trim();
                    if (keyword.Length < 2)
                        return 0;

                    var inTitle = candidate.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                    var inSnippet = candidate.Snippet.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                    var termScore = (inTitle ? 3 : 0) + (inSnippet ? 1 : 0);
                    return termScore * Math.Max(1, term.Weight);
                })
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(_ => Random.Shared.Next())
            .ToList();

        return scored.Count > 0
            ? scored[0].Candidate
            : default;
    }

    /// <summary>
    /// Enforces a deterministic theme gate before a web result can be assigned to
    /// a Home category. The search query still comes from InternetSearchService; this
    /// gate prevents a highly weighted user keyword from pulling a story whose title
    /// and snippet belong to another editorial section.
    /// </summary>
    private static bool MatchesCategoryTheme(string category, string title, string snippet)
    {
        var text = $"{title} {snippet}".ToLowerInvariant();

        if (string.Equals(category, "Technology", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(text,
                "technology", "tech", "software", "hardware", "computer", "computing",
                "artificial intelligence", "ai", "machine learning", "cybersecurity", "cyber security",
                "cyberattack", "chip", "semiconductor", "processor", "gpu", "cpu", "nvidia",
                "apple", "google", "microsoft", "amazon", "meta", "openai", "smartphone",
                "iphone", "android", "laptop", "cloud", "internet", "app", "robot", "robotics",
                "quantum", "console", "xbox", "playstation", "nintendo", "developer", "programming");

        if (string.Equals(category, "Sports", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(text,
                "sport", "sports", "football", "soccer", "basketball", "baseball", "tennis",
                "golf", "rugby", "cricket", "hockey", "formula 1", "formula one", "f1",
                "nfl", "nba", "mlb", "nhl", "uefa", "fifa", "olympic", "athlete", "team",
                "match", "tournament", "league", "championship", "playoff", "coach", "player", "race");

        if (string.Equals(category, "Politics", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(text,
                "politic", "election", "president", "presidential", "government", "senate", "senator",
                "congress", "parliament", "minister", "mayor", "governor", "campaign", "ballot",
                "vote", "voting", "legislation", "lawmaker", "policy", "political party", "party leader",
                "administration", "prime minister", "candidate");

        if (string.Equals(category, "Health", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(text,
                "health", "medical", "medicine", "disease", "hospital", "doctor", "patient",
                "vaccine", "vaccination", "drug", "treatment", "therapy", "cancer", "clinical trial",
                "healthcare", "public health", "fda", "cdc", "who", "outbreak", "virus", "infection",
                "diagnosis", "surgery", "mental health", "wellness");

        if (string.Equals(category, "Business", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(text,
                "business", "company", "companies", "market", "markets", "stock", "stocks", "shares",
                "earnings", "revenue", "profit", "investment", "investor", "economy", "economic",
                "finance", "financial", "bank", "banking", "trade", "merger", "acquisition", "ceo",
                "inflation", "interest rate", "recession", "retail", "sales", "industry", "ipo");

        if (string.Equals(category, "Entertainment", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(text,
                "entertainment", "movie", "film", "actor", "actress", "director", "music", "singer",
                "song", "album", "concert", "television", " tv ", "series", "show", "streaming",
                "netflix", "disney", "paramount", "hbo", "trailer", "celebrity", "award", "awards",
                "gaming", "video game", "video games", "xbox", "playstation", "nintendo", "game release");

        // Unknown categories remain governed by the search provider's category query.
        return true;
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        foreach (var term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the personalized Just For You feed: one AI-selected story per explicit
    /// preference, using the user's full preference and interaction context. If the user
    /// has fewer than four preferences, additional distinct stories are gathered from
    /// the same preferences until four are available whenever the web search permits it.
    /// </summary>
    public async Task<List<PersonalizedRecommendation>> GetPersonalizedRecommendationsAsync(
        int userId,
        CancellationToken cancellationToken = default,
        bool explorationMode = false)
    {
        if (explorationMode)
            return await GetExplorationRecommendationsAsync(userId, cancellationToken);

        var output = new List<PersonalizedRecommendation>();
        if (_llmService == null || _newsIngest == null)
            return output;

        var userPreferenceRows = await _context.UserPreferences
            .AsNoTracking()
            .Include(up => up.Preference)
                .ThenInclude(p => p!.Tag)
            .Where(up => up.IdUser == userId && up.Preference != null)
            .ToListAsync(cancellationToken);

        var distinctPreferences = userPreferenceRows
            .Where(up => !string.IsNullOrWhiteSpace(up.Preference?.Tag?.Name ?? up.Preference?.PreferenceName))
            .GroupBy(
                up => up.Preference!.Tag?.Name ?? up.Preference!.PreferenceName,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => x.Weight)
            .ToList();

        if (distinctPreferences.Count == 0)
            return output;

        var interestTerms = await GetUserInterestTermsAsync(userId, cancellationToken);
        var explicitPreferenceNames = distinctPreferences
            .Select(x => x.Preference!.Tag?.Name ?? x.Preference!.PreferenceName ?? "News")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var personalizationContext = await BuildUserPersonalizationContextAsync(userId, cancellationToken);

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
                interestTerms,
                explicitPreferenceNames,
                isSupplemental: false,
                blockedUrls,
                blockedTitles,
                cancellationToken);

            if (recommendation != null)
                output.Add(recommendation);
        }

        var fillPreferenceIndex = 0;
        var fillAttempts = 0;
        while (output.Count < 4 && fillAttempts < Math.Max(3, distinctPreferences.Count * 3))
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
                interestTerms,
                explicitPreferenceNames,
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

    private async Task<List<PersonalizedRecommendation>> GetExplorationRecommendationsAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var output = new List<PersonalizedRecommendation>();

        var unseen = await _context.News
            .AsNoTracking()
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .Where(n => n.DetailNews != null &&
                        !_context.UserNews.Any(un =>
                            un.IdUser == userId &&
                            un.IdNews == n.IdNews &&
                            un.Seen))
            .OrderBy(_ => EF.Functions.Random())
            .Take(4)
            .ToListAsync(cancellationToken);

        if (unseen.Count < 4)
        {
            var ids = unseen.Select(x => x.IdNews).ToHashSet();
            var fallback = await _context.News
                .AsNoTracking()
                .Include(n => n.Tag)
                .Include(n => n.DetailNews)
                .Where(n => n.DetailNews != null && !ids.Contains(n.IdNews))
                .OrderBy(_ => EF.Functions.Random())
                .Take(4 - unseen.Count)
                .ToListAsync(cancellationToken);
            unseen.AddRange(fallback);
        }

        foreach (var news in unseen)
        {
            output.Add(new PersonalizedRecommendation(
                news,
                "Exploration",
                0,
                true));
        }

        return output;
    }

    private async Task<PersonalizedRecommendation?> SearchPreferenceStoryAsync(
        int userId,
        string preferenceName,
        int idTag,
        int preferenceWeight,
        string personalizationContext,
        IReadOnlyList<(string Keyword, int Weight)> interestTerms,
        IReadOnlyList<string> explicitPreferenceNames,
        bool isSupplemental,
        HashSet<string> blockedUrls,
        HashSet<string> blockedTitles,
        CancellationToken cancellationToken)
    {
        if (_llmService == null || _newsIngest == null)
            return null;

        // Cross-preference context is deliberately limited to one secondary preference
        // per search pass. This lets us discover stories that connect preferences such as
        // "Argentina + NVIDIA" without letting a high-weight preference replace the
        // primary preference represented by the current Just For You card.
        var relatedPreferences = explicitPreferenceNames
            .Where(name => !string.Equals(name, preferenceName, StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                var weight = interestTerms
                    .Where(x => string.Equals(x.Keyword, name, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Weight)
                    .DefaultIfEmpty(1)
                    .Max();
                return (Name: name, Weight: weight);
            })
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var relatedKeywords = interestTerms
            .Where(x =>
                !string.Equals(x.Keyword, preferenceName, StringComparison.OrdinalIgnoreCase) &&
                !explicitPreferenceNames.Contains(x.Keyword, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Weight)
            .Take(3)
            .ToList();

        // Pass 1..N: anchor every query on the current preference and optionally relate
        // it to exactly one other preference. Then try one learned keyword relation,
        // followed by an anchor-only search. This keeps cross-interest context useful
        // without ever allowing it to replace the current preference.
        var relationPasses = Math.Min(relatedPreferences.Count, 2);
        var maxAttempts = relationPasses + (relatedKeywords.Count > 0 ? 2 : 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var searchQuery = $"{preferenceName} latest current news";
            string? relatedTerm = null;

            var relatedPreferenceIndex = attempt - 1;
            if (relatedPreferenceIndex < relationPasses)
            {
                relatedTerm = relatedPreferences[relatedPreferenceIndex].Name;
            }
            else if (attempt == relationPasses + 1 && relatedKeywords.Count > 0)
            {
                relatedTerm = relatedKeywords[0].Keyword;
            }

            if (!string.IsNullOrWhiteSpace(relatedTerm))
                searchQuery += $" {relatedTerm}";

            if (attempt > 1)
                searchQuery += " different story";

            var results = await _llmService.SearchNewsAsync(
                searchQuery,
                maxResults: 8,
                cancellationToken: cancellationToken,
                startingPage: (attempt - 1) * 3 + 1);

            var candidates = results
                .Select(r => (
                    Title: r.Title?.Trim() ?? string.Empty,
                    Url: NormalizeUrl(r.Url),
                    Snippet: r.Snippet?.Trim() ?? string.Empty))
                .Where(r =>
                    !string.IsNullOrWhiteSpace(r.Url) &&
                    !blockedUrls.Contains(r.Url) &&
                    LooksLikeValidNewsCandidate(r.Title, r.Url, r.Snippet))
                .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (candidates.Count == 0)
                continue;

            // Just For You is preference-driven, not category-driven. Prefer a result
            // whose title/snippet explicitly matches the requested preference or one of
            // the user's learned keywords. If no such match exists, keep the fallback
            // inside this preference query rather than selecting a result based on an
            // unrelated global keyword.
            var selected = SelectBestPreferenceCandidate(
                candidates,
                preferenceName,
                relatedTerm,
                interestTerms);

            // Never let a secondary preference select a story for the current card.
            // If the search engine did not return a result anchored to the current
            // preference, continue to the next query pass instead of crossing topics.
            if (string.IsNullOrWhiteSpace(selected.Url))
                continue;

            try
            {
                var news = await _newsIngest.SaveOrUpdateNewsAsync(
                    selected.Url,
                    string.IsNullOrWhiteSpace(selected.Snippet) ? selected.Title : selected.Snippet,
                    selected.Title,
                    idTag,
                    isSfw: true,
                    secureLink: true,
                    isAi: true,
                    newsDate: DateTime.UtcNow,
                    fullBodyText: null,
                    bodySource: "Unknown",
                    imageUrl: null,
                    additionalSourcesJson: null);

                blockedUrls.Add(selected.Url);
                blockedTitles.Add(NormalizeTitle(selected.Title));

                await EnsureNewsImageAsync(news.IdNews, cancellationToken);

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

    private static (string Title, string Url, string Snippet) SelectBestPreferenceCandidate(
        IReadOnlyList<(string Title, string Url, string Snippet)> candidates,
        string preferenceName,
        string? relatedTerm,
        IReadOnlyList<(string Keyword, int Weight)> interestTerms)
    {
        if (candidates.Count == 0)
            return default;

        var preference = preferenceName.Trim();

        // The current card preference is an absolute anchor. A secondary preference or
        // learned keyword can improve the choice only after the candidate already matches
        // the anchor. Therefore NVIDIA (24) can help choose an Argentina story that also
        // mentions NVIDIA, but NVIDIA can never turn a non-Argentina story into the
        // Argentina card simply because its weight is higher.
        var anchored = candidates
            .Where(candidate => MatchesPreferenceAnchor(candidate, preference))
            .ToList();

        if (anchored.Count == 0)
            return default;

        var related = interestTerms
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(relatedTerm) &&
                string.Equals(x.Keyword, relatedTerm, StringComparison.OrdinalIgnoreCase));

        var relatedWeight = related == default
            ? 1
            : Math.Clamp(related.Weight, 1, 5);

        var scored = anchored
            .Select(candidate =>
            {
                var score = 100;

                if (candidate.Title.Contains(preference, StringComparison.OrdinalIgnoreCase))
                    score += 12;
                if (candidate.Snippet.Contains(preference, StringComparison.OrdinalIgnoreCase))
                    score += 4;

                if (!string.IsNullOrWhiteSpace(relatedTerm))
                {
                    if (candidate.Title.Contains(relatedTerm, StringComparison.OrdinalIgnoreCase))
                        score += 6 + relatedWeight;
                    else if (candidate.Snippet.Contains(relatedTerm, StringComparison.OrdinalIgnoreCase))
                        score += 3 + Math.Min(3, relatedWeight);
                }

                return (candidate, score);
            })
            .OrderByDescending(x => x.score)
            .ThenBy(_ => Random.Shared.Next())
            .ToList();

        return scored.Count > 0
            ? scored[0].candidate
            : default;
    }

    private static bool MatchesPreferenceAnchor(
        (string Title, string Url, string Snippet) candidate,
        string preferenceName)
    {
        if (string.IsNullOrWhiteSpace(preferenceName))
            return false;

        var searchableText = $"{candidate.Title} {candidate.Snippet}";

        if (searchableText.Contains(preferenceName, StringComparison.OrdinalIgnoreCase))
            return true;

        var tokens = Regex.Matches(
                preferenceName.ToLowerInvariant(),
                @"[\p{L}\p{N}]{3,}")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tokens.Count > 1 &&
               tokens.All(token => searchableText.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<(string Keyword, int Weight)>> GetUserInterestTermsAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var terms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var preferences = await _context.UserPreferences
            .AsNoTracking()
            .Include(up => up.Preference)
                .ThenInclude(p => p!.Tag)
            .Where(up => up.IdUser == userId && up.Preference != null)
            .ToListAsync(cancellationToken);

        foreach (var row in preferences)
        {
            var term = row.Preference?.Tag?.Name ?? row.Preference?.PreferenceName;
            if (string.IsNullOrWhiteSpace(term) || row.Weight <= 0)
                continue;

            terms.TryGetValue(term.Trim(), out var current);
            terms[term.Trim()] = current + row.Weight;
        }

        var keywords = await _context.UserInterestKeywords
            .AsNoTracking()
            .Where(k => k.IdUser == userId && k.Weight > 0)
            .ToListAsync(cancellationToken);

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword.Keyword))
                continue;

            var term = keyword.Keyword.Trim();
            terms.TryGetValue(term, out var current);
            terms[term] = current + keyword.Weight;
        }

        return terms
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public async Task RegisterSearchQueryAsync(
        int userId,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        var terms = _llmService == null
            ? new List<string>()
            : await _llmService.ExtractInterestKeywordsAsync(
                query,
                query,
                maxKeywords: 6,
                cancellationToken: cancellationToken);

        foreach (var term in terms)
            await UpsertInterestKeywordAsync(userId, term, 1, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<News?> CacheSearchResultAsync(
        string title,
        string url,
        string snippet,
        string? category,
        string? imageUrl,
        CancellationToken cancellationToken = default)
    {
        if (_newsIngest == null || string.IsNullOrWhiteSpace(url))
            return null;

        var tagName = string.IsNullOrWhiteSpace(category) ? "Search" : category.Trim();
        var tag = await _context.Tags
            .FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower(), cancellationToken);

        if (tag == null)
        {
            tag = new Tag { Name = tagName };
            _context.Tags.Add(tag);
            await _context.SaveChangesAsync(cancellationToken);
        }

        var news = await _newsIngest.SaveOrUpdateNewsAsync(
            NormalizeUrl(url),
            string.IsNullOrWhiteSpace(snippet) ? title : snippet,
            title,
            tag.IdTag,
            isSfw: true,
            secureLink: true,
            isAi: true,
            newsDate: DateTime.UtcNow,
            fullBodyText: null,
            bodySource: "Unknown",
            imageUrl: imageUrl,
            additionalSourcesJson: null);

        if (!string.IsNullOrWhiteSpace(imageUrl) && news.DetailNews != null)
            news.DetailNews.ImageUrl = imageUrl.Trim();

        await _context.SaveChangesAsync(cancellationToken);

        return await _context.News
            .AsNoTracking()
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .FirstOrDefaultAsync(n => n.IdNews == news.IdNews, cancellationToken);
    }

    private async Task LearnFromNewsAsync(
        int userId,
        int newsId,
        int signalDelta,
        string? additionalContext = null)
    {
        var news = await _context.News
            .AsNoTracking()
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .FirstOrDefaultAsync(n => n.IdNews == newsId);

        if (news == null)
            return;

        var baseText = string.Join(
            "\n",
            new[]
            {
                news.DetailNews?.Keyword,
                news.DetailNews?.AiSummary,
                news.DetailNews?.Context,
                additionalContext
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

        if (!string.IsNullOrWhiteSpace(baseText) && _llmService != null)
        {
            try
            {
                var keywords = await _llmService.ExtractInterestKeywordsAsync(
                    news.DetailNews?.Keyword ?? "News",
                    baseText,
                    maxKeywords: 8,
                    cancellationToken: CancellationToken.None);

                foreach (var keyword in keywords)
                    await UpsertInterestKeywordAsync(userId, keyword, signalDelta, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Could not learn keywords from News {NewsId}", newsId);
            }
        }

        if (news.IdTag > 0 && signalDelta != 0)
        {
            var userPref = await _context.UserPreferences
                .FirstOrDefaultAsync(
                    up => up.IdUser == userId &&
                          up.Preference != null &&
                          up.Preference.IdTag == news.IdTag);

            if (userPref != null)
                userPref.Weight = Math.Clamp(userPref.Weight + signalDelta, 0, 100);
        }
    }

    private async Task UpsertInterestKeywordAsync(
        int userId,
        string? keyword,
        int delta,
        CancellationToken cancellationToken)
    {
        keyword = keyword?.Trim();
        if (string.IsNullOrWhiteSpace(keyword) || delta == 0)
            return;

        if (keyword.Length > 120)
            keyword = keyword[..120].Trim();

        var normalized = Regex.Replace(keyword, @"\s+", " ");

        var row = await _context.UserInterestKeywords
            .FirstOrDefaultAsync(
                k => k.IdUser == userId && k.Keyword.ToLower() == normalized.ToLower(),
                cancellationToken);

        if (row == null)
        {
            // A negative signal should not create a brand-new positive interest.
            if (delta <= 0)
                return;

            _context.UserInterestKeywords.Add(new UserInterestKeyword
            {
                IdUser = userId,
                Keyword = normalized,
                Weight = Math.Clamp(delta, 0, 100),
                LastUpdatedAt = DateTime.UtcNow
            });
            return;
        }

        row.Weight = Math.Clamp(row.Weight + delta, 0, 100);
        row.LastUpdatedAt = DateTime.UtcNow;
    }

    private async Task<string> BuildUserPersonalizationContextAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        var preferenceRows = await _context.UserPreferences
            .AsNoTracking()
            .Include(up => up.Preference)
                .ThenInclude(p => p!.Tag)
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
                .ThenInclude(n => n!.Tag)
            .Include(un => un.News)
                .ThenInclude(n => n!.DetailNews)
            .Where(un => un.IdUser == userId && un.Seen)
            .OrderByDescending(un => un.DateSeen)
            .Take(20)
            .ToListAsync(cancellationToken);

        var recentComments = await _context.NewsComments
            .AsNoTracking()
            .Include(comment => comment.News)
                .ThenInclude(n => n!.Tag)
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
        var learnedKeywords = await _context.UserInterestKeywords
            .AsNoTracking()
            .Where(k => k.IdUser == userId && k.Weight > 0)
            .OrderByDescending(k => k.Weight)
            .ThenBy(k => k.Keyword)
            .Take(20)
            .ToListAsync(cancellationToken);

        sb.AppendLine();
        sb.AppendLine("LEARNED INTEREST KEYWORDS (weighted):");
        if (learnedKeywords.Count == 0)
            sb.AppendLine("None recorded.");
        else
            foreach (var keyword in learnedKeywords)
                sb.AppendLine($"- {keyword.Keyword} (weight {keyword.Weight})");

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
        // Compatibility wrapper retained for older callers. This method NEVER returns
        // or stores the original publisher article body. It now delegates exclusively to
        // the original NuctiusNow AI synthesis pipeline.
        return await EnsureAiGeneratedArticleAsync(newsId, cancellationToken);
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
                .ThenInclude(p => p!.Tag)
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

        var meaningfulRead = timeSpentSeconds >= 120;

        if (userNews == null)
        {
            userNews = new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                Interest = meaningfulRead,
                Reaction = 0,
                DateSeen = DateTime.UtcNow
            };
            _context.UserNews.Add(userNews);
        }
        else
        {
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
            if (meaningfulRead && userNews.Reaction >= 0)
                userNews.Interest = true;
        }

        if (meaningfulRead)
            await LearnFromNewsAsync(userId, newsId, 2);

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

        // Article generation needs factual material, not only search-result snippets.
        // Use the dedicated News category to discover several independent publishers,
        // then try to read the actual article body from each candidate.
        if (sources.Count == 0 && _llmService != null)
        {
            var query = string.IsNullOrWhiteSpace(category)
                ? $"{title} latest current news"
                : $"{title} {category} latest current news";

            try
            {
                sources = await _llmService.SearchNewsAsync(
                    query,
                    8,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Could not gather fallback news evidence for '{Title}'",
                    title);
            }
        }

        var originalHost = string.Empty;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var originalUri))
            originalHost = originalUri.Host;

        var evidenceBlocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(originalSnippet))
        {
            evidenceBlocks.Add(
                $"INITIAL SEARCH CONTEXT:\n{originalSnippet.Trim()}");
        }

        // First try to read the selected news page itself. This gives the writer a
        // factual base even when secondary search snippets are sparse or repetitive.
        if (_llmService != null &&
            Uri.TryCreate(sourceUrl, UriKind.Absolute, out var primaryUri) &&
            !ArticleExtractionService.IsKnownNonArticlePage(primaryUri))
        {
            try
            {
                var primaryBody = await _llmService.ExtractArticleBodyTextAsync(
                    sourceUrl,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(primaryBody) &&
                    primaryBody.Length >= 400)
                {
                    var boundedPrimary = primaryBody.Trim();
                    if (boundedPrimary.Length > 6500)
                        boundedPrimary = boundedPrimary[..6500];

                    evidenceBlocks.Add(
                        $"PRIMARY VERIFIED NEWS SOURCE:\nTitle: {title}\nURL: {sourceUrl}\nArticle text:\n{boundedPrimary}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(
                    ex,
                    "Could not extract the primary article body from {Url}",
                    sourceUrl);
            }
        }

        var additionalSources = new List<ArticleSource>();
        var usedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedUrl = NormalizeUrl(result.Url);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
                continue;

            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var resultUri))
                continue;

            if (!string.Equals(
                    resultUri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    resultUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalizedUrl.Equals(
                    sourceUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var host = resultUri.Host;
            if (!string.IsNullOrWhiteSpace(originalHost) &&
                host.Equals(
                    originalHost,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // One source per publisher prevents one site from dominating the article.
            if (!usedHosts.Add(host))
                continue;

            string? articleBody = null;
            try
            {
                articleBody = await _llmService!.ExtractArticleBodyTextAsync(
                    normalizedUrl,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(
                    ex,
                    "Could not extract article body from evidence source {Url}",
                    normalizedUrl);
            }

            var usableBody = !string.IsNullOrWhiteSpace(articleBody) &&
                             articleBody.Length >= 400
                ? articleBody.Trim()
                : null;

            if (usableBody != null)
            {
                // Keep each source bounded so the local model receives independent
                // evidence without an unbounded prompt.
                if (usableBody.Length > 6500)
                    usableBody = usableBody[..6500];

                evidenceBlocks.Add(
                    $"VERIFIED NEWS SOURCE:\nTitle: {result.Title}\nURL: {normalizedUrl}\nArticle text:\n{usableBody}");
            }
            else if (!string.IsNullOrWhiteSpace(result.Snippet))
            {
                var snippet = result.Snippet.Trim();
                if (snippet.Length > 1800)
                    snippet = snippet[..1800];

                evidenceBlocks.Add(
                    $"NEWS SOURCE SUMMARY:\nTitle: {result.Title}\nURL: {normalizedUrl}\nSnippet: {snippet}");
            }
            else
            {
                continue;
            }

            additionalSources.Add(
                new ArticleSource(
                    result.Title?.Trim() ?? host,
                    normalizedUrl,
                    GetSourceOwner(normalizedUrl)));

            // Three independent publishers is enough for a strong article while
            // keeping generation latency under control.
            if (additionalSources.Count >= 4)
                break;
        }

        return new ArticleEvidence(
            string.Join(
                "\n\n==============================\n\n",
                evidenceBlocks),
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
            return (null, "Unknown", null);

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) &&
            ArticleExtractionService.IsKnownNonArticlePage(sourceUri))
        {
            return (null, "Unknown", null);
        }

        try
        {
            var evidence = await BuildArticleEvidenceAsync(
                title,
                sourceUrl,
                originalSnippet,
                category,
                searchResults,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(evidence.Text) || evidence.Text.Length < 700)
                return (null, "Unknown", null);

            var generated = await _llmService.GenerateOriginalArticleAsync(
                title,
                evidence.Text,
                category,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(generated))
                return (null, "Unknown", null);

            var json = evidence.Sources.Count == 0
                ? null
                : JsonSerializer.Serialize(evidence.Sources);

            return (generated.Trim(), "AiGenerated", json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AI-only article generation path failed for {Url}", sourceUrl);
            return (null, "Unknown", null);
        }
    }

    public async Task<string?> EnsureAiGeneratedArticleAsync(
        int newsId,
        CancellationToken cancellationToken = default)
    {
        var news = await _context.News
            .Include(n => n.DetailNews)
            .Include(n => n.Tag)
            .FirstOrDefaultAsync(n => n.IdNews == newsId, cancellationToken);

        if (news?.DetailNews == null)
            return null;

        var detail = news.DetailNews;

        // Remove source text left by the old prototype. It must not be rendered or
        // persisted as the article content anymore.
        if (detail.BodySource.Equals("Original", StringComparison.OrdinalIgnoreCase))
        {
            detail.FullBodyText = null;
            detail.AiSummary = null;
            detail.BodySource = "Unknown";
            await _context.SaveChangesAsync(cancellationToken);
        }

        // Cached NuctiusNow article: return instantly and only regenerate the missing
        // summary if necessary.
        if (detail.BodySource.Equals("AiGenerated", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(detail.FullBodyText))
        {
            var cleaned = CleanGeneratedArticle(detail.FullBodyText);
            detail.FullBodyText = cleaned;

            if (string.IsNullOrWhiteSpace(detail.AiSummary))
            {
                var summary = await _llmService!.GenerateNewsSummaryAsync(
                    detail.Keyword ?? "News article",
                    cleaned,
                    news.Tag?.Name,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(summary))
                    detail.AiSummary = summary;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return detail.FullBodyText;
        }

        if (_llmService == null || string.IsNullOrWhiteSpace(news.Hyperlink))
            return null;

        if (Uri.TryCreate(news.Hyperlink.Trim(), UriKind.Absolute, out var sourceUri) &&
            ArticleExtractionService.IsKnownNonArticlePage(sourceUri))
            return null;

        var title = string.IsNullOrWhiteSpace(detail.Keyword)
            ? "News article"
            : detail.Keyword.Trim();
        var category = news.Tag?.Name;
        var originalContext = detail.Context;

        try
        {
            // Article extraction happens inside BuildArticleEvidenceAsync only as transient
            // factual input. The extracted source body is never stored in FullBodyText.
            var evidence = await BuildArticleEvidenceAsync(
                title,
                news.Hyperlink.Trim(),
                originalContext,
                category,
                existingResults: null,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(evidence.Text) || evidence.Text.Length < 700)
            {
                detail.BodySource = "Unknown";
                detail.FullBodyText = null;
                await _context.SaveChangesAsync(cancellationToken);
                return null;
            }

            var generatedArticle = await _llmService.GenerateOriginalArticleAsync(
                title,
                evidence.Text,
                category,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(generatedArticle))
            {
                detail.BodySource = "Unknown";
                detail.FullBodyText = null;
                await _context.SaveChangesAsync(cancellationToken);
                return null;
            }

            generatedArticle = CleanGeneratedArticle(generatedArticle);
            if (generatedArticle.Length < 700)
            {
                detail.BodySource = "Unknown";
                detail.FullBodyText = null;
                await _context.SaveChangesAsync(cancellationToken);
                return null;
            }

            // The summary is generated from the newly written NuctiusNow article, not
            // copied from the original search snippet.
            var aiSummary = await _llmService.GenerateNewsSummaryAsync(
                title,
                generatedArticle,
                category,
                cancellationToken);

            detail.FullBodyText = generatedArticle;
            detail.BodySource = "AiGenerated";
            detail.AiSummary = string.IsNullOrWhiteSpace(aiSummary)
                ? null
                : aiSummary.Trim();
            detail.AdditionalSourcesJson = evidence.Sources.Count == 0
                ? null
                : JsonSerializer.Serialize(evidence.Sources);

            await _context.SaveChangesAsync(cancellationToken);

            return detail.FullBodyText;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate original article for News {NewsId}", newsId);
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
