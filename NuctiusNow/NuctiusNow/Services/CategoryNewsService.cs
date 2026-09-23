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
        bool forceAiSearchFirst = false)
    {
        var oneYearAgo = DateTime.UtcNow.AddYears(-1);

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

        try
        {
            var aiNews = await recommendationEngine.ExecuteAiSearchForCategoryAsync(category, userId, ignoreOneYearRestriction: false, cancellationToken: cancellationToken);
            if (aiNews != null)
            {
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
            return new CategoryNewsResult { News = oldUnseen, Source = NewsSelectionSource.DatabaseOldUnseen, AiFailed = true };
        }

        // 4. All database articles were already seen. Run AI again without the restriction.
        try
        {
            var unrestrictedAiNews = await recommendationEngine.ExecuteAiSearchForCategoryAsync(category, userId, ignoreOneYearRestriction: true, cancellationToken: cancellationToken);
            if (unrestrictedAiNews != null)
            {
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
        var randomFallback = await GetRandomNewsAsync(category, userId: null, minimumDate: null, onlyUnseen: false, cancellationToken);

        return new CategoryNewsResult
        {
            News = randomFallback,
            Source = randomFallback == null ? NewsSelectionSource.None : NewsSelectionSource.DatabaseRandomFallback,
            AiFailed = true,
            ErrorMessage = randomFallback == null ? $"No news exists for category {category}." : "AI search failed. Showing a database fallback."
        };
    }

    private async Task<News?> GetRandomNewsAsync(string category, int? userId, DateTime? minimumDate, bool onlyUnseen, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var normalizedCategory = category.Trim().ToLower();

        var query = db.News
            .AsNoTracking()
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .Where(n => n.Tag != null && n.Tag.Name.ToLower() == normalizedCategory);

        if (minimumDate.HasValue)
        {
            // "News from the last year" is based on the article publication date,
            // not on the date when the portal captured the article.
            query = query.Where(n =>
                n.DetailNews != null &&
                n.DetailNews.NewsDate >= minimumDate.Value);
        }

        if (onlyUnseen && userId.HasValue)
        {
            // Hooking directly into your existing UserNews table
            query = query.Where(n => !db.UserNews.Any(view => view.IdUser == userId.Value && view.IdNews == n.IdNews && view.Seen));
        }

        return await query.OrderBy(n => EF.Functions.Random()).FirstOrDefaultAsync(cancellationToken);
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