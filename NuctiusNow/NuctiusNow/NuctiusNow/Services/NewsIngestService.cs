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
    /// Guarda una noticia en la base de datos.
    /// Si el hyperlink ya existe, actualiza el registro existente.
    /// También permite almacenar el cuerpo completo y su origen.
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
        string? imageUrl = null)
    {
        if (string.IsNullOrWhiteSpace(hyperlink))
        {
            throw new ArgumentException(
                "hyperlink required",
                nameof(hyperlink));
        }

        const int maxKeywordLength = 255;

        if (!string.IsNullOrWhiteSpace(keyword) &&
            keyword.Length > maxKeywordLength)
        {
            keyword = keyword[..maxKeywordLength];
        }

        hyperlink = hyperlink.Trim();

        // ---------------------------------------------------------
        // Buscar noticia existente
        // ---------------------------------------------------------

        var existing = await _context.News
            .Include(n => n.DetailNews)
            .FirstOrDefaultAsync(
                n => n.Hyperlink == hyperlink);

        if (existing != null)
        {
            // -----------------------------------------------------
            // Asegurar DetailNews
            // -----------------------------------------------------

            if (existing.DetailNews == null)
            {
                var detail = new DetailNews
                {
                    Context = contextText,
                    Keyword = keyword,
                    FullBodyText = string.IsNullOrWhiteSpace(fullBodyText)
                        ? null
                        : fullBodyText.Trim(),
                    BodySource = string.IsNullOrWhiteSpace(fullBodyText)
                        ? "Unknown"
                        : (bodySource ?? "Original"),
                    NewsDate = newsDate ?? DateTime.UtcNow
                };

                _context.DetailNews.Add(detail);

                await _context.SaveChangesAsync();

                existing.IdDetailNews = detail.IdDetailNews;
                existing.DetailNews = detail;
            }
            else
            {
                // -------------------------------------------------
                // Actualizar contexto
                // -------------------------------------------------

                if (!string.IsNullOrWhiteSpace(contextText))
                {
                    existing.DetailNews.Context = contextText;
                }

                // -------------------------------------------------
                // Actualizar keyword
                // -------------------------------------------------

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    existing.DetailNews.Keyword = keyword;
                }

                // -------------------------------------------------
                // Actualizar fecha
                // -------------------------------------------------

                if (newsDate.HasValue)
                {
                    existing.DetailNews.NewsDate = newsDate.Value;
                }

                // -------------------------------------------------
                // Actualizar cuerpo completo
                // -------------------------------------------------

                if (!string.IsNullOrWhiteSpace(fullBodyText))
                {
                    existing.DetailNews.FullBodyText =
                        fullBodyText.Trim();

                    existing.DetailNews.BodySource =
                        bodySource ?? existing.DetailNews.BodySource;
                }
                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    existing.DetailNews.ImageUrl = imageUrl.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(bodySource))
                {
                    existing.DetailNews.BodySource = bodySource;
                }
            }

            // -----------------------------------------------------
            // Actualizar News
            // -----------------------------------------------------

            existing.IdTag = idTag;
            existing.IsSfw = isSfw;
            existing.SecureLink = secureLink;
            existing.IsAi = isAi;
            existing.CapturedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Updated existing News id={NewsId} for hyperlink {Hyperlink}",
                existing.IdNews,
                hyperlink);

            return existing;
        }

        // ---------------------------------------------------------
        // Crear DetailNews nuevo
        // ---------------------------------------------------------

        var newDetail = new DetailNews
        {
            Context = contextText,
            Keyword = keyword,

            FullBodyText =
                string.IsNullOrWhiteSpace(fullBodyText)
                    ? null
                    : fullBodyText.Trim(),

            BodySource =
                string.IsNullOrWhiteSpace(fullBodyText)
                    ? "Unknown"
                    : (bodySource ?? "Original"),

            ImageUrl =
                string.IsNullOrWhiteSpace(imageUrl)
                    ? null
                    : imageUrl.Trim(),

            NewsDate = newsDate ?? DateTime.UtcNow
        };

        _context.DetailNews.Add(newDetail);

        await _context.SaveChangesAsync();

        // ---------------------------------------------------------
        // Crear News
        // ---------------------------------------------------------

        var newNews = new News
        {
            Hyperlink = hyperlink,
            IdTag = idTag,
            IdDetailNews = newDetail.IdDetailNews,

            IsSfw = isSfw,
            SecureLink = secureLink,
            IsAi = isAi,

            CapturedDate = DateTime.UtcNow,

            Likes = 0,
            Dislikes = 0,

            DetailNews = newDetail
        };

        _context.News.Add(newNews);

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Created new News id={NewsId} for hyperlink {Hyperlink}",
            newNews.IdNews,
            hyperlink);

        return newNews;
    }
}