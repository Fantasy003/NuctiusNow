using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;

namespace NuctiusNow.Services;

public enum NewsSelectionSource
{
    DatabaseRecentUnseen,
    DatabaseOldUnseen,
    AiSearch,
    DatabaseRandomFallback,
    None
}

public sealed class CategoryNewsResult
{
    public News? News { get; init; }
    public NewsSelectionSource Source { get; init; }
    public bool AiFailed { get; init; }
    public string? ErrorMessage { get; init; }
}

public class CategoryNewsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CategoryNewsService> _logger;

    public CategoryNewsService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<CategoryNewsService> logger)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<CategoryNewsResult> GetNewsForCategoryAsync(
        string category,
        int? userId,
        CancellationToken cancellationToken = default,
        bool forceAiSearchFirst = false,
        bool forceRandomSelection = false)
    {
        var oneYearAgo = DateTime.UtcNow.AddYears(-1);

        // Exploration mode intentionally ignores personalization and AI selection.
        // Every fifth feed refresh can use this path to expose unrelated news and
        // create new learning signals when the user chooses to interact with it.
        if (forceRandomSelection)
        {
            var exploratory = await GetRandomNewsAsync(
                category,
                userId,
                minimumDate: null,
                onlyUnseen: true,
                cancellationToken, personalize: false);

            if (exploratory == null)
            {
                exploratory = await GetRandomNewsAsync(
                    category,
                    userId: userId,
                    minimumDate: null,
                    onlyUnseen: false,
                    cancellationToken, personalize: false);
            }

            if (exploratory != null)
                await EnsureNewsImageIfMissingAsync(exploratory, cancellationToken);

            return new CategoryNewsResult
            {
                News = exploratory,
                Source = exploratory == null
                    ? NewsSelectionSource.None
                    : NewsSelectionSource.DatabaseRandomFallback
            };
        }

        // Normal mode: prefer something unseen from the last year.
        // Refresh mode: deliberately ask the AI first so the feed cycles.
        if (!forceAiSearchFirst)
        {
            var recentUnseen = await GetRandomNewsAsync(
                category,
                userId,
                minimumDate: oneYearAgo,
                onlyUnseen: true,
                cancellationToken);

            if (recentUnseen != null)
            {
                await EnsureNewsImageIfMissingAsync(recentUnseen, cancellationToken);

                return new CategoryNewsResult
                {
                    News = recentUnseen,
                    Source = NewsSelectionSource.DatabaseRecentUnseen
                };
            }
        }

        // Ask AI to find and save a new article.
        // We create a fresh scope so RecommendationEngine gets its own DbContext, preventing thread crashes.
        using var scope = _scopeFactory.CreateScope();
        var recommendationEngine = scope.ServiceProvider.GetRequiredService<RecommendationEngine>();
        if (scope.ServiceProvider.GetService<ILocalLlmService>() is UserAwareLocalLlmService userAwareSearch)
            userAwareSearch.SearchUserId = userId;

        try
        {
            var aiNews = await recommendationEngine.ExecuteAiSearchForCategoryAsync(category, userId, ignoreOneYearRestriction: false, cancellationToken: cancellationToken, forceRandomSelection: false);
            if (aiNews != null)
            {
                // The AI-selected story is immediately paired with a direct image
                // from SearXNG's Images category so Home can render it.
                await EnsureNewsImageIfMissingAsync(aiNews, cancellationToken);

                // UserNews with Seen=false explicitly represents an article that is
                // available in the user's feed but has not been opened yet.
                if (userId.HasValue)
                {
                    // The AI has already produced and persisted the article.
                    // Do not let an expired UI timeout prevent the availability
                    // record from being created for the user.
                    await MarkNewsAvailableForUserAsync(
                        userId.Value,
                        aiNews.IdNews,
                        CancellationToken.None);
                }

                return new CategoryNewsResult
                {
                    News = aiNews,
                    Source = NewsSelectionSource.AiSearch
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI search failed for category {Category}", category);
        }

        // 3. AI failed. Try an old but unseen database article.
        var oldUnseen = await GetRandomNewsAsync(category, userId, minimumDate: null, onlyUnseen: true, cancellationToken);
        if (oldUnseen != null)
        {
            await EnsureNewsImageIfMissingAsync(oldUnseen, cancellationToken);

            return new CategoryNewsResult { News = oldUnseen, Source = NewsSelectionSource.DatabaseOldUnseen, AiFailed = true };
        }

        // 4. All database articles were already seen. Run AI again without the restriction.
        try
        {
            var unrestrictedAiNews = await recommendationEngine.ExecuteAiSearchForCategoryAsync(category, userId, ignoreOneYearRestriction: true, cancellationToken: cancellationToken, forceRandomSelection: false);
            if (unrestrictedAiNews != null)
            {
                await EnsureNewsImageIfMissingAsync(unrestrictedAiNews, cancellationToken);

                // The unrestricted AI retry is also a new/available article, so register it
                // as unseen for this user before it is displayed.
                if (userId.HasValue)
                {
                    // Same rule for the unrestricted retry.
                    await MarkNewsAvailableForUserAsync(
                        userId.Value,
                        unrestrictedAiNews.IdNews,
                        CancellationToken.None);
                }

                return new CategoryNewsResult
                {
                    News = unrestrictedAiNews,
                    Source = NewsSelectionSource.AiSearch
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unrestricted AI search failed for category {Category}", category);
        }

        // 5. Final fallback: any random article, even if already seen.
        var randomFallback = await GetRandomNewsAsync(category, userId: userId, minimumDate: null, onlyUnseen: false, cancellationToken);
        if (randomFallback != null)
            await EnsureNewsImageIfMissingAsync(randomFallback, cancellationToken);

        return new CategoryNewsResult
        {
            News = randomFallback,
            Source = randomFallback == null ? NewsSelectionSource.None : NewsSelectionSource.DatabaseRandomFallback,
            AiFailed = true,
            ErrorMessage = randomFallback == null ? $"No news exists for category {category}." : "AI search failed. Showing a database fallback."
        };
    }

    private async Task EnsureNewsImageIfMissingAsync(
        News news,
        CancellationToken cancellationToken)
    {
        if (news.DetailNews != null &&
            !string.IsNullOrWhiteSpace(news.DetailNews.ImageUrl))
        {
            return;
        }

        try
        {
            // Use a fresh RecommendationEngine scope because CategoryNewsService itself
            // intentionally uses isolated DbContext instances.
            using var scope = _scopeFactory.CreateScope();
            var recommendationEngine = scope.ServiceProvider
                .GetRequiredService<RecommendationEngine>();

            var imageUrl = await recommendationEngine.EnsureNewsImageAsync(
                news.IdNews,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(imageUrl) && news.DetailNews != null)
                news.DetailNews.ImageUrl = imageUrl;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not obtain an image for News {NewsId}",
                news.IdNews);
        }
    }

    private async Task<News?> GetRandomNewsAsync(
        string category,
        int? userId,
        DateTime? minimumDate,
        bool onlyUnseen,
        CancellationToken cancellationToken, bool personalize = true)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var normalizedCategory = category.Trim().ToLowerInvariant();

        var query = db.News
            .AsNoTracking()
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .Where(n => n.Tag != null && n.Tag.Name.ToLower() == normalizedCategory);

        if (minimumDate.HasValue)
        {
            query = query.Where(n =>
                n.DetailNews != null &&
                n.DetailNews.NewsDate >= minimumDate.Value);
        }

        if (onlyUnseen && userId.HasValue)
        {
            query = query.Where(n =>
                !db.UserNews.Any(view =>
                    view.IdUser == userId.Value &&
                    view.IdNews == n.IdNews &&
                    view.Seen));
        }

        // Pull a bounded candidate pool into memory, then apply the user's learned
        // preference/keyword weights to the candidate title and summary/context.
        var candidates = await query
            .OrderByDescending(n => n.DetailNews!.NewsDate)
            .Take(100)
            .ToListAsync(cancellationToken);

        if (userId.HasValue)
        {
            var blocked = await db.UserBlockedSources.AsNoTracking()
                .Where(s => s.IdUser == userId.Value).Select(s => s.SourceDomain).ToListAsync(cancellationToken);
            candidates = candidates.Where(n => !UserBlockedSourceService.IsBlocked(n.Hyperlink, blocked)).ToList();
        }

        if (candidates.Count == 0)
            return null;

        if (!userId.HasValue || !personalize)
            return candidates[Random.Shared.Next(candidates.Count)];

        var termWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var preferenceRows = await db.UserPreferences
            .AsNoTracking()
            .Include(up => up.Preference)
                .ThenInclude(p => p!.Tag)
            .Where(up => up.IdUser == userId.Value && up.Preference != null && up.Weight > 0)
            .ToListAsync(cancellationToken);

        foreach (var row in preferenceRows)
        {
            var term = row.Preference?.Tag?.Name ?? row.Preference?.PreferenceName;
            if (string.IsNullOrWhiteSpace(term))
                continue;

            var clean = term.Trim();
            termWeights.TryGetValue(clean, out var current);
            termWeights[clean] = current + Math.Max(1, row.Weight);
        }

        var keywordRows = await db.UserInterestKeywords
            .AsNoTracking()
            .Where(k => k.IdUser == userId.Value && k.Weight > 0)
            .ToListAsync(cancellationToken);

        foreach (var row in keywordRows)
        {
            if (string.IsNullOrWhiteSpace(row.Keyword))
                continue;

            var clean = row.Keyword.Trim();
            termWeights.TryGetValue(clean, out var current);
            termWeights[clean] = current + row.Weight;
        }

        if (termWeights.Count == 0)
            return candidates[Random.Shared.Next(candidates.Count)];

        var scored = candidates
            .Select(news =>
            {
                var searchable = string.Join(
                    " ",
                    news.DetailNews?.Keyword,
                    news.DetailNews?.AiSummary,
                    news.DetailNews?.Context)
                    .ToLowerInvariant();

                var score = termWeights.Sum(term =>
                {
                    var value = term.Key.ToLowerInvariant();
                    if (!searchable.Contains(value, StringComparison.Ordinal))
                        return 0;

                    var titleMatch = news.DetailNews?.Keyword?.Contains(term.Key, StringComparison.OrdinalIgnoreCase) == true;
                    return term.Value * (titleMatch ? 3 : 1);
                });

                return (news, score);
            })
            .ToList();

        var matched = scored
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();

        // The user's requested rule: filter out unmatched stories when matches exist;
        // if there is no match, return any available story so the feed never becomes empty.
        return matched.Count > 0
            ? matched[Random.Shared.Next(matched.Count)].news
            : candidates[Random.Shared.Next(candidates.Count)];
    }

    private async Task MarkNewsAvailableForUserAsync(
        int userId,
        int newsId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var userNews = await db.UserNews
            .FirstOrDefaultAsync(
                x => x.IdUser == userId && x.IdNews == newsId,
                cancellationToken);

        if (userNews == null)
        {
            db.UserNews.Add(new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = false,
                Interest = false,
                // The existing column is named date_seen. For an unseen item,
                // this stores when the article became available to the user.
                DateSeen = DateTime.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RegisterNewsViewAsync(int userId, int newsId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var userNews = await db.UserNews.FirstOrDefaultAsync(x => x.IdUser == userId && x.IdNews == newsId, cancellationToken);

        if (userNews == null)
        {
            db.UserNews.Add(new UserNews
            {
                IdUser = userId,
                IdNews = newsId,
                Seen = true,
                DateSeen = DateTime.UtcNow
            });
        }
        else
        {
            if (userNews.Seen) return;
            userNews.Seen = true;
            userNews.DateSeen = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
