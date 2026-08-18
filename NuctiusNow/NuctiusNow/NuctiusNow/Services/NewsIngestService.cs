using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NuctiusNow.Data;
using NuctiusNow.Components.User_Related;

namespace NuctiusNow.Services;

public class NewsIngestService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NewsIngestService> _logger;

    public NewsIngestService(ApplicationDbContext context, ILogger<NewsIngestService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Save a captured news item into the database. If the same hyperlink exists, updates the existing record.
    /// Creates a DetailNews record for the article body and links it from News.
    /// </summary>
    public async Task<News> SaveOrUpdateNewsAsync(string hyperlink, string? contextText, string? keyword, int idTag,
        bool isSfw = true, bool secureLink = true, bool isAi = false, DateTime? newsDate = null)
    {
        if (string.IsNullOrWhiteSpace(hyperlink)) throw new ArgumentException("hyperlink required", nameof(hyperlink));

        // Defensive truncation: some DB columns (e.g. hyperlink) are limited to 150 chars in the schema.
        // Truncate inputs to avoid DB exceptions (22001) and keep app stable. If you prefer, update DB schema instead.
        const int MaxHyperlinkLength = 150;
        const int MaxKeywordLength = 150;
        if (hyperlink.Length > MaxHyperlinkLength) hyperlink = hyperlink.Substring(0, MaxHyperlinkLength);
        if (!string.IsNullOrWhiteSpace(keyword) && keyword.Length > MaxKeywordLength) keyword = keyword.Substring(0, MaxKeywordLength);

        // Try to find existing news by hyperlink
        var existing = await _context.News.Include(n => n.DetailNews).FirstOrDefaultAsync(n => n.Hyperlink == hyperlink);
        if (existing != null)
        {
            // Update detail text if provided
            if (!string.IsNullOrWhiteSpace(contextText))
            {
                if (existing.DetailNews == null)
                {
                    var detail = new DetailNews
                    {
                        Context = contextText,
                        Keyword = keyword,
                        NewsDate = newsDate ?? DateTime.UtcNow
                    };
                    _context.DetailNews.Add(detail);
                    await _context.SaveChangesAsync();
                    existing.IdDetailNews = detail.IdDetailNews;
                    existing.DetailNews = detail;
                }
                else
                {
                    existing.DetailNews.Context = contextText;
                    existing.DetailNews.Keyword = keyword ?? existing.DetailNews.Keyword;
                    existing.DetailNews.NewsDate = newsDate ?? existing.DetailNews.NewsDate;
                }
            }

            existing.IdTag = idTag;
            existing.IsSfw = isSfw;
            existing.SecureLink = secureLink;
            existing.IsAi = isAi;
            existing.CapturedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Updated existing News id={NewsId} for hyperlink {Hyperlink}", existing.IdNews, hyperlink);
            return existing;
        }

        // Create new detail
        var newDetail = new DetailNews
        {
            Context = contextText,
            Keyword = keyword,
            NewsDate = newsDate ?? DateTime.UtcNow
        };
        _context.DetailNews.Add(newDetail);
        await _context.SaveChangesAsync();

        var news = new News
        {
            IdTag = idTag,
            IdDetailNews = newDetail.IdDetailNews,
            DetailNews = newDetail,
            Hyperlink = hyperlink,
            IsSfw = isSfw,
            SecureLink = secureLink,
            IsAi = isAi,
            CapturedDate = DateTime.UtcNow,
            Likes = 0,
            Dislikes = 0
        };

        _context.News.Add(news);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Inserted new News id={NewsId} for hyperlink {Hyperlink}", news.IdNews, hyperlink);
        return news;
    }
}
