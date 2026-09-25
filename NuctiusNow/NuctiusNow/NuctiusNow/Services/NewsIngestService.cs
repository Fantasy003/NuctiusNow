using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;

namespace NuctiusNow.Services;

public class NewsIngestService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NewsIngestService> _logger;

    public NewsIngestService(
        ApplicationDbContext context,
        ILogger<NewsIngestService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Creates or updates a news record while keeping source snippets separate from
    /// the AI-generated summary/article. Full source article text is never stored here.
    /// </summary>
    public async Task<News> SaveOrUpdateNewsAsync(
        string hyperlink,
        string? contextText,
        string? keyword,
        int idTag,
        bool isSfw = true,
        bool secureLink = true,
        bool isAi = false,
        DateTime? newsDate = null,
        string? fullBodyText = null,
        string? bodySource = null,
        string? imageUrl = null,
        string? additionalSourcesJson = null)
    {
        hyperlink = hyperlink?.Trim() ?? string.Empty;
        keyword = string.IsNullOrWhiteSpace(keyword)
            ? "Untitled story"
            : keyword.Trim();
        contextText = contextText?.Trim();

        // Never persist a publisher's extracted article body under the source marker.
        // Only the NuctiusNow-generated article is allowed to occupy FullBodyText.
        if (string.Equals(bodySource, "Original", StringComparison.OrdinalIgnoreCase))
        {
            fullBodyText = null;
            bodySource = "Unknown";
        }

        var news = await _context.News
            .Include(n => n.DetailNews)
            .FirstOrDefaultAsync(n => n.Hyperlink == hyperlink);

        if (news == null)
        {
            var detail = new DetailNews
            {
                Keyword = keyword,
                Context = contextText,
                // IMPORTANT: Context is a search snippet/evidence field.
                // It must not be presented as the AI summary.
                AiSummary = null,
                NewsDate = newsDate ?? DateTime.UtcNow,
                FullBodyText = fullBodyText,
                BodySource = string.IsNullOrWhiteSpace(bodySource)
                    ? "Unknown"
                    : bodySource,
                ImageUrl = imageUrl,
                AdditionalSourcesJson = additionalSourcesJson
            };

            _context.DetailNews.Add(detail);
            await _context.SaveChangesAsync();

            news = new News
            {
                IdTag = idTag,
                IdDetailNews = detail.IdDetailNews,
                DetailNews = detail,
                Hyperlink = hyperlink,
                IsSfw = isSfw,
                SecureLink = secureLink,
                IsAi = isAi,
                CapturedDate = DateTime.UtcNow
            };

            _context.News.Add(news);
        }
        else
        {
            news.IdTag = idTag;
            news.IsSfw = isSfw;
            news.SecureLink = secureLink;
            news.IsAi = news.IsAi || isAi;

            if (news.DetailNews == null)
            {
                news.DetailNews = new DetailNews
                {
                    Keyword = keyword,
                    Context = contextText,
                    AiSummary = null,
                    NewsDate = newsDate ?? DateTime.UtcNow,
                    BodySource = string.IsNullOrWhiteSpace(bodySource)
                        ? "Unknown"
                        : bodySource,
                    ImageUrl = imageUrl,
                    AdditionalSourcesJson = additionalSourcesJson
                };

                _context.DetailNews.Add(news.DetailNews);
            }
            else
            {
                news.DetailNews.Keyword = keyword;

                if (!string.IsNullOrWhiteSpace(contextText))
                    news.DetailNews.Context = contextText;

                // Context is search evidence/snippet data, never the displayed summary.
                // Preserve an already generated NuctiusNow summary, but clear stale
                // snippet-derived summaries from legacy records.
                if (!string.Equals(news.DetailNews.BodySource, "AiGenerated", StringComparison.OrdinalIgnoreCase))
                    news.DetailNews.AiSummary = null;

                if (newsDate.HasValue)
                    news.DetailNews.NewsDate = newsDate.Value;

                if (!string.IsNullOrWhiteSpace(fullBodyText))
                {
                    news.DetailNews.FullBodyText = fullBodyText;
                }

                if (!string.IsNullOrWhiteSpace(bodySource))
                    news.DetailNews.BodySource = bodySource;

                if (!string.IsNullOrWhiteSpace(imageUrl))
                    news.DetailNews.ImageUrl = imageUrl;

                if (!string.IsNullOrWhiteSpace(additionalSourcesJson))
                    news.DetailNews.AdditionalSourcesJson = additionalSourcesJson;
            }
        }

        await _context.SaveChangesAsync();
        return news;
    }
}
