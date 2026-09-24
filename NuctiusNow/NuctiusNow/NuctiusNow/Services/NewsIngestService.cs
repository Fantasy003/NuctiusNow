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
    /// Creates or updates a news record while preserving compatibility with all
    /// existing callers. Full article text, image and source metadata are optional
    /// and can be filled later by NewsDetails.
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
        keyword = string.IsNullOrWhiteSpace(keyword) ? "Untitled story" : keyword.Trim();
        contextText = contextText?.Trim();

        var news = await _context.News
            .Include(n => n.DetailNews)
            .FirstOrDefaultAsync(n => n.Hyperlink == hyperlink);

        if (news == null)
        {
            var detail = new DetailNews
            {
                Keyword = keyword,
                Context = contextText,
                AiSummary = contextText,
                NewsDate = newsDate ?? DateTime.UtcNow,
                FullBodyText = fullBodyText,
                BodySource = string.IsNullOrWhiteSpace(bodySource) ? "Unknown" : bodySource,
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
                    AiSummary = contextText,
                    NewsDate = newsDate ?? DateTime.UtcNow,
                    BodySource = string.IsNullOrWhiteSpace(bodySource) ? "Unknown" : bodySource
                };
                _context.DetailNews.Add(news.DetailNews);
            }
            else
            {
                news.DetailNews.Keyword = keyword;

                if (!string.IsNullOrWhiteSpace(contextText))
                {
                    news.DetailNews.Context = contextText;
                    news.DetailNews.AiSummary = contextText;
                }

                if (newsDate.HasValue)
                    news.DetailNews.NewsDate = newsDate.Value;

                if (!string.IsNullOrWhiteSpace(fullBodyText))
                    news.DetailNews.FullBodyText = fullBodyText;

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
