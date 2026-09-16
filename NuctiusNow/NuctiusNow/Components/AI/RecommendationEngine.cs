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

        if (userPref?.Preference != null)
        {
            userPref.Preference.Weight += liked ? 1 : -1;
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

        if (userPref?.Preference != null)
        {
            userPref.Preference.Weight += 1;
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

        if (userPref?.Preference != null)
        {
            userPref.Preference.Weight += 1;
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
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Busca noticias vía LM Studio para una categoría de Home, resume, categoriza y persiste en BD.
    /// </summary>
    public async Task<News?> ExecuteAiSearchForCategoryAsync(string categoryName)
    {
        var llm = _llmService;
        var ingest = _newsIngest;
        if (llm == null || ingest == null)
        {
            _logger?.LogWarning("ExecuteAiSearchForCategoryAsync: LLM or ingest unavailable for {Category}", categoryName);
            return null;
        }

        var mappings = InternetSearchService.GetCategoryMapping();
        var match = mappings.FirstOrDefault(m =>
            m.Category.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(match.Category))
        {
            return null;
        }

        var tag = await _context.Tags
            .FirstOrDefaultAsync(t => t.Name.ToLower() == categoryName.ToLower());
        if (tag == null)
        {
            tag = new Tag { Name = match.Category };
            _context.Tags.Add(tag);
            await _context.SaveChangesAsync();
        }

        List<(string Title, string Url, string Snippet)> results;
        try
        {
            results = await llm.SearchWebAsync(match.SearchQuery, 5);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Category search failed for {Category}", categoryName);
            return null;
        }

        if (results.Count == 0) return null;

        var picked = results[Random.Shared.Next(results.Count)];
        if (string.IsNullOrWhiteSpace(picked.Url)) return null;

        var bodyText = string.IsNullOrWhiteSpace(picked.Snippet) ? picked.Title : picked.Snippet;
        string aiSummary;
        try
        {
            aiSummary = await SummarizeTextAsync(bodyText, 220);
        }
        catch
        {
            aiSummary = bodyText.Length <= 220 ? bodyText : bodyText[..220] + "...";
        }

        News news;
        try
        {
            news = await ingest.SaveOrUpdateNewsAsync(
                picked.Url,
                bodyText,
                picked.Title,
                tag.IdTag,
                isSfw: true,
                secureLink: true,
                isAi: true,
                newsDate: DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to ingest category news for {Category}", categoryName);
            return null;
        }

        if (news.DetailNews != null)
        {
            news.DetailNews.AiSummary = aiSummary;
            await _context.SaveChangesAsync();
        }

        return await _context.News
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .FirstOrDefaultAsync(n => n.IdNews == news.IdNews);
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

        if (preferences.Count == 0)
        {
            return new List<News>();
        }

        var tagIds = preferences!
            .Select(p => p!.IdTag)
            .Distinct()
            .ToList();

        var seenIds = await _context.UserNews
            .Where(un => un.IdUser == userId && un.Seen)
            .Select(un => un.IdNews)
            .ToListAsync();

        var fechaLimite = DateTime.UtcNow.AddDays(-30);
        var result = new List<News>();

        foreach (var tagId in tagIds)
        {
            var item = await _context.News
                .Include(n => n.Tag)
                .Include(n => n.DetailNews)
                .Where(n => n.IdTag == tagId && n.CapturedDate >= fechaLimite)
                .OrderByDescending(n => n.CapturedDate)
                .FirstOrDefaultAsync();

            if (item != null && !seenIds.Contains(item.IdNews))
            {
                result.Add(item);
            }
        }

        if (result.Count < tagIds.Count && _llmService != null && _newsIngest != null)
        {
            try
            {
                await ExecuteNewsSearchWorkflowAsync(userId, numSearches: Math.Max(tagIds.Count, 4));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "AI workflow failed while filling personalized feed");
            }

            result.Clear();
            foreach (var tagId in tagIds)
            {
                var item = await _context.News
                    .Include(n => n.Tag)
                    .Include(n => n.DetailNews)
                    .Where(n => n.IdTag == tagId && n.CapturedDate >= fechaLimite)
                    .OrderByDescending(n => n.CapturedDate)
                    .FirstOrDefaultAsync();

                if (item != null)
                {
                    result.Add(item);
                }
            }
        }

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
                .ThenInclude(p => p != null ? p.Tag : null)
            .Where(up => up.IdUser == userId)
            .ToListAsync();
            
        var userPreferences = userPreferencesList.Select(up => up.Preference!).Where(p => p != null).ToList();

        return userPreferences
            .OrderByDescending(p => p.Weight)
            .Select(p => p.Tag?.Name ?? p.IdTag.ToString())
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
        else if (timeSpentSeconds >= 120)
        {
            userNews.Interest = true;
        }

        await _context.SaveChangesAsync();
    }
}