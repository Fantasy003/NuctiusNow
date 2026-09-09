using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;

namespace NuctiusNow.Services;

public class RecommendationEngine
{
    private readonly ApplicationDbContext _context;
    private readonly ILocalLlmService? _llmService;
    private readonly ILogger<RecommendationEngine>? _logger;
    private readonly NewsIngestService? _newsIngest;

    public RecommendationEngine(ApplicationDbContext context, ILocalLlmService? llmService = null, NewsIngestService? newsIngest = null, ILogger<RecommendationEngine>? logger = null)
    {
        _context = context;
        _llmService = llmService;
        _newsIngest = newsIngest;
        _logger = logger;
    }

    // Helper methods for preference lookup and text summarization are defined below.

    // Helper: summarize plain text via underlying LLM service
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

    /// <summary>
    /// Explicit 5-step workflow:
    /// 1. Receive user preferences from DB
    /// 2. Search the internet for those preferences
    /// 3. Select the first matching news
    /// 4. Read and summarize the body content
    /// 5. Save to DetailNews
    /// </summary>
    public async Task<List<string>> ExecuteNewsSearchWorkflowAsync(int userId, int numSearches = 4)
    {
        var ingested = new List<string>();
        if (_llmService == null || _newsIngest == null)
        {
            _logger?.LogError("ExecuteNewsSearchWorkflowAsync: LLM or NewsIngestService not available");
            return ingested;
        }

        // STEP 1: Load user preferences from DB
        _logger?.LogInformation("STEP 1: Loading user preferences for userId={UserId}", userId);
        var userPreferences = await _context.UserPreferences
            .Include(up => up.Preference)
            .Where(up => up.IdUser == userId)
            .Select(up => up.Preference)
            .ToListAsync();

        if (userPreferences == null || userPreferences.Count == 0)
        {
            _logger?.LogError("STEP 1: No preferences found for user {UserId}", userId);
            return ingested;
        }

        _logger?.LogInformation("STEP 1: Found {Count} preferences for user {UserId}", userPreferences.Count, userId);

        var topPrefs = userPreferences.OrderByDescending(p => p.Weight).Take(3).ToList();

        int searchCount = 0;
        int prefIndex = 0;

        // STEP 2-5: Search and ingest for each preference
        while (ingested.Count < numSearches && searchCount < numSearches * 2)
        {
            var pref = topPrefs.Count > 0 ? topPrefs[prefIndex % topPrefs.Count] : userPreferences.First();
            var topic = pref.Tag?.Name ?? pref.IdTag.ToString();
            var query = $"{topic}";

            // STEP 2: Search the internet
            _logger?.LogInformation("STEP 2: Searching for preference '{Topic}' (query='{Query}')", topic, query);
            List<(string Title, string Url, string Snippet)> results = new();
            try
            {
                results = await _llmService.SearchWebAsync(query, 1);
                _logger?.LogInformation("STEP 2: Search returned {Count} results for query '{Query}'", results?.Count ?? 0, query);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "STEP 2: Search failed for query '{Query}'", query);
                prefIndex++;
                searchCount++;
                continue;
            }

            // STEP 3: Select first matching news
            if (results == null || results.Count == 0)
            {
                _logger?.LogWarning("STEP 3: No results returned for query '{Query}'", query);
                prefIndex++;
                searchCount++;
                continue;
            }

            var firstResult = results[0];
            _logger?.LogInformation("STEP 3: Selected first result - Title: {Title}, URL: {Url}", firstResult.Title, firstResult.Url);

            if (string.IsNullOrWhiteSpace(firstResult.Url))
            {
                _logger?.LogWarning("STEP 3: Selected result has no URL");
                prefIndex++;
                searchCount++;
                continue;
            }

            if (ingested.Contains(firstResult.Url))
            {
                _logger?.LogInformation("STEP 3: URL already ingested, skipping: {Url}", firstResult.Url);
                prefIndex++;
                searchCount++;
                continue;
            }

            // STEP 4: Summarize the body content
            _logger?.LogInformation("STEP 4: Summarizing content from URL {Url}", firstResult.Url);
            string summary = firstResult.Snippet ?? string.Empty;
            if (string.IsNullOrWhiteSpace(summary))
            {
                // If snippet is empty, use title as fallback
                summary = firstResult.Title;
            }
            _logger?.LogInformation("STEP 4: Summary obtained (length={Length})", summary.Length);

            // STEP 5: Save to DetailNews via NewsIngestService
            _logger?.LogInformation("STEP 5: Ingesting news - URL: {Url}, Title: {Title}, TagId: {TagId}", firstResult.Url, firstResult.Title, pref.IdTag);
            try
            {
                await _newsIngest.SaveOrUpdateNewsAsync(
                    firstResult.Url,
                    summary,
                    firstResult.Title,
                    pref.IdTag,
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

    /// <summary>
    /// Trigger a plugin-enabled web search using top user preferences and ingest found results into the DB.
    /// Returns the list of ingested URLs (may be empty).
    /// </summary>

    public async Task<List<string>> SearchAndIngestForUserAsync(int userId, int perPref = 1, int maxTotal = 4)
    {
        var ingested = new List<string>();
        if (_llmService == null || _newsIngest == null)
        {
            _logger?.LogInformation("SearchAndIngestForUserAsync: LLM service or NewsIngestService not available");
            return ingested;
        }

        var userPreferences = await _context.UserPreferences
            .Include(up => up.Preference)
            .Where(up => up.IdUser == userId)
            .Select(up => up.Preference)
            .ToListAsync();

        if (userPreferences == null || userPreferences.Count == 0)
        {
            _logger?.LogInformation("SearchAndIngestForUserAsync: no preferences for user {UserId}", userId);
            return ingested;
        }

        var topPrefs = userPreferences.OrderByDescending(p => p.Weight).Take(3).ToList();

        // Perform up to maxTotal searches, rotating across top preferences so we get varied topics.
        int attempts = 0;
        int prefIndex = 0;
        while (ingested.Count < maxTotal && attempts < maxTotal * 2)
        {
            var pref = topPrefs.Count > 0 ? topPrefs[prefIndex % topPrefs.Count] : userPreferences.First();
            var topic = pref.Tag?.Name ?? pref.IdTag.ToString();
            var query = $"{topic} news";
            List<(string Title, string Url, string Snippet)> results = new();
            try
            {
                results = await _llmService.SearchWebAsync(query, perPref);
                _logger?.LogInformation("SearchAndIngestForUserAsync: search '{Query}' returned {Count}", query, results?.Count ?? 0);
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
                    await _newsIngest.SaveOrUpdateNewsAsync(first.Url, first.Snippet, first.Title, pref.IdTag, isSfw: true, secureLink: true, isAi: true, newsDate: DateTime.UtcNow);
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

    // Returns the top recommended news for a user
    // days: lookback window in days (e.g. last 30 days)
    public async Task<List<News>> GetRecommendationsForUserAsync(int userId, int cantidad = 10, int days = 30)
    {
        // 1. Cargar preferencias del usuario (Tags y sus pesos acumulados)
        var userPreferences = await _context.UserPreferences
            .Include(up => up.Preference)
            .Where(up => up.IdUser == userId)
            .Select(up => up.Preference)
            .ToListAsync();

        // 2. Obtener noticias que el usuario AÚN NO HA VISTO
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

        // If no local news candidates are available, try to use the LLM+plugin to search the web
        if ((noticiasCandidatas == null || noticiasCandidatas.Count == 0) && _llmService != null && _newsIngest != null)
        {
            try
            {
                // Build queries from top user preferences
                var topPrefs = userPreferences.OrderByDescending(p => p.Weight).Take(3).ToList();
                if (topPrefs.Count == 0) topPrefs = userPreferences.Take(1).ToList();

                foreach (var pref in topPrefs)
                {
                    var topic = pref.Tag?.Name ?? pref.IdTag.ToString();
                    var query = $"{topic} news";
                    var searchResults = await _llmService.SearchWebAsync(query, 5);
                    try
                    {
                        _logger?.LogInformation("Search for '{Query}' returned {Count} results", query, searchResults?.Count ?? 0);
                    }
                    catch { }
                    foreach (var res in searchResults)
                    {
                        if (string.IsNullOrWhiteSpace(res.Url)) continue;
                        // Save or update into DB as AI-ingested news so RecommendationEngine can later rank them
                        try
                        {
                            await _newsIngest.SaveOrUpdateNewsAsync(res.Url, res.Snippet, res.Title, pref.IdTag, isSfw: true, secureLink: true, isAi: true, newsDate: DateTime.UtcNow);
                            try { _logger?.LogInformation("Ingested candidate: {Url}", res.Url); } catch { }
                        }
                        catch
                        {
                            _logger?.LogWarning("Failed to ingest URL {Url}", res.Url);
                        }
                    }
                }

                // Refresh candidates after ingestion
                noticiasCandidatas = await _context.News
                    .Include(n => n.Tag)
                    .Include(n => n.DetailNews)
                    .Where(n => !noticiasVistasIds.Contains(n.IdNews) && n.CapturedDate >= fechaLimite)
                    .ToListAsync();
            }
            catch
            {
                // ignore search/ingest errors and continue with empty candidates
            }
        }

        // 3. Algoritmo de Scoring / Ponderación ML (base score)
        var scored = noticiasCandidatas.Select(news => new
        {
            Noticia = news,
            BaseScore = CalculateScore(news, userPreferences)
        }).ToList();

        // If no LLM available, fall back to base scoring
        if (_llmService == null)
        {
            return scored.OrderByDescending(x => x.BaseScore).Select(x => x.Noticia).Take(cantidad).ToList();
        }

        // Build a lightweight user profile string for the LLM
        var user = await _context.Users.FindAsync(userId);
        var profileParts = userPreferences.Select(p => $"{p.Tag?.Name ?? p.IdTag.ToString()}:{p.Weight}").ToList();
        if (user != null)
        {
            profileParts.Add($"premium:{user.IsPremium}");
            profileParts.Add($"language:{user.Language}");
        }
        var userProfile = string.Join("; ", profileParts);

        // Choose a pool of top candidates by base score to send to the LLM for re-ranking
        var rerankPoolSize = Math.Min(scored.Count, Math.Max(cantidad * 3, 20));
        var rerankPool = scored.OrderByDescending(x => x.BaseScore).Take(rerankPoolSize).ToList();

        // Prepare candidate texts for the LLM and collect detail news that may need summaries cached
        var candidateTexts = new List<string>();
        var detailsToSummarize = new List<DetailNews>();
        foreach (var item in rerankPool)
        {
            var n = item.Noticia;
            var detail = n.DetailNews;
            var excerpt = detail?.Context ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(excerpt) && excerpt.Length > 600) excerpt = excerpt.Substring(0, 600) + "...";

            var text = $"Title: {detail?.Keyword ?? ""}\nTag: {n.Tag?.Name ?? ""}\nExcerpt: {excerpt}";
            candidateTexts.Add(text);

            if (detail != null && string.IsNullOrWhiteSpace(detail.AiSummary))
            {
                detailsToSummarize.Add(detail);
            }
        }

        // 4. Caching: summarize missing detail news via LLM in batches and persist the summaries
        if (detailsToSummarize.Count > 0)
        {
            var textsForSumm = detailsToSummarize.Select(d => d.Context ?? string.Empty).ToList();
            var summaries = await _llmService.SummarizeBatchAsync(textsForSumm, 220);
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

        // 5. Use the LLM to score / re-rank candidates in batches
        var llmScores = new List<double?>();
        const int localBatchSize = 8; // fallback batch size
        for (int i = 0; i < candidateTexts.Count; i += localBatchSize)
        {
            var batch = candidateTexts.Skip(i).Take(localBatchSize).ToList();
            var batchScores = await _llmService.BatchScoreAsync(userProfile, batch);
            llmScores.AddRange(batchScores);
        }

        // Combine base score and LLM score
        var combined = new List<(News Noticia, double Score)>();
        for (int i = 0; i < rerankPool.Count; i++)
        {
            var baseScore = rerankPool[i].BaseScore;
            double llmPart = 0.0;
            var llmScore = (i < llmScores.Count) ? llmScores[i] : null;
            if (llmScore.HasValue)
            {
                // Scale LLM score to influence final ranking (tunable multiplier)
                llmPart = (llmScore.Value / 100.0) * 5.0;
            }

            combined.Add((rerankPool[i].Noticia, baseScore + llmPart));
        }

        var final = combined.OrderByDescending(x => x.Score).Select(x => x.Noticia).Take(cantidad).ToList();
        return final;
    }

    // Async summarization: tries to use the local LLM service if registered, falls back to the simple Summarize()
    public async Task<string> SummarizeAsync(DetailNews? detail, int maxChars = 220)
    {
        var fallback = Summarize(detail, maxChars);
        if (_llmService == null) return fallback;

        var text = detail?.Context ?? string.Empty;
        try
        {
            var llmResult = await _llmService.SummarizeAsync(text, maxChars);
            if (!string.IsNullOrWhiteSpace(llmResult)) return llmResult;
        }
        catch
        {
            // ignore LLM errors and return fallback summary
        }

        return fallback;
    }

    /// <summary>
    /// Summarizes a direct text prompt using the LLM service.
    /// </summary>
    public async Task<string> SummarizeAsync(string prompt, int maxChars = 220)
    {
        if (_llmService == null) return string.Empty;

        try
        {
            var llmResult = await _llmService.SummarizeAsync(prompt, maxChars);
            return string.IsNullOrWhiteSpace(llmResult) ? string.Empty : llmResult;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate summary for prompt: {Prompt}", prompt);
            return string.Empty;
        }
    }

    // Simple summarization helper: returns the first few sentences or a trimmed substring
    public string Summarize(DetailNews? detail, int maxChars = 220)
    {
        if (detail == null) return string.Empty;

        var text = detail.Context ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Try to take the first 2 sentences
        var sentences = text.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        if (sentences.Length >= 2)
        {
            var summary = string.Join(". ", sentences.Take(2)) + ".";
            if (summary.Length <= maxChars) return summary;
        }

        // Fallback: trim to maxChars
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars).TrimEnd() + "...";
    }

    private double CalculateScore(News news, List<Preference> userPreferences)
    {
        double score = 0.0;

        // Match by Tag/Category
        var matchingPref = userPreferences.FirstOrDefault(p => p.IdTag == news.IdTag);
        if (matchingPref != null)
        {
            score += matchingPref.Weight * 2.0; // weight by accumulated preference weight
        }

        // General popularity (likes vs dislikes)
        int totalVotes = news.Likes + news.Dislikes;
        if (totalVotes > 0)
        {
            double popularityRatio = (double)news.Likes / totalVotes;
            score += popularityRatio * 1.5;
        }

        // SFW penalty
        if (!news.IsSfw) score -= 10.0;

        return score;
    }

    // 4. Feedback: user teaches the recommender
    public async Task RegisterInteractionAsync(int userId, int newsId, bool liked, bool interested)
    {
        var news = await _context.News.FindAsync(newsId);
        if (news == null) return;

        // Update news statistics
        if (liked) news.Likes++;
        else news.Dislikes++;

        // Record in UserNews
        var userNews = new UserNews
        {
            IdUser = userId,
            IdNews = newsId,
            Seen = true,
            Interest = interested,
            DateSeen = DateTime.UtcNow
        };
        _context.UserNews.Add(userNews);

        // Adjust dynamic weight for the user's preference tag
        var userPref = await _context.UserPreferences
            .Include(up => up.Preference)
            .FirstOrDefaultAsync(up => up.IdUser == userId && up.Preference.IdTag == news.IdTag);

        if (userPref != null)
        {
            // Train weight: +1 if liked, -1 if disliked
            userPref.Preference.Weight += liked ? 1 : -1;
        }

        await _context.SaveChangesAsync();
    }
    
    /// <summary>
    /// Retrieves a list of tag names representing a user's preferences, ordered by weight.
    /// </summary>
    public async Task<List<string>> GetUserPreferencesAsync(int userId)
    {
        var userPreferences = await _context.UserPreferences
            .Include(up => up.Preference)
                // Assuming Tag is a navigation property on your Preference entity
                .ThenInclude(p => p.Tag)
            .Where(up => up.IdUser == userId)
            .Select(up => up.Preference)
            .ToListAsync();

        return userPreferences
            .OrderByDescending(p => p.Weight)
            .Select(p => p.Tag?.Name ?? p.IdTag.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToList();
    }
}

