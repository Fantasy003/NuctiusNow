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

    // Calcula y devuelve las mejores noticias recomendadas para un usuario
    // days: lookback window in days (e.g. last 30 days)
    public async Task<List<News>> ObtenerRecomendacionesParaUsuarioAsync(int userId, int cantidad = 10, int days = 30)
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
            BaseScore = CalcularScore(news, userPreferences)
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

    private double CalcularScore(News news, List<Preference> userPreferences)
    {
        double score = 0.0;

        // Búsqueda de coincidencia por Tag/Categoría
        var prefCorrespondiente = userPreferences.FirstOrDefault(p => p.IdTag == news.IdTag);
        if (prefCorrespondiente != null)
        {
            score += prefCorrespondiente.Weight * 2.0; // Ponderación por peso acumulado
        }

        // Popularidad general (Likes vs Dislikes)
        int totalVotos = news.Likes + news.Dislikes;
        if (totalVotos > 0)
        {
            double ratioPopularidad = (double)news.Likes / totalVotos;
            score += ratioPopularidad * 1.5;
        }

        // Penalización/Filtro SFW
        if (!news.IsSfw) score -= 10.0;

        return score;
    }

    // 4. RETROALIMENTACIÓN (El usuario "le enseña" a la IA)
    public async Task RegistrarInteraccionAsync(int userId, int newsId, bool liked, bool interested)
    {
        var noticia = await _context.News.FindAsync(newsId);
        if (noticia == null) return;

        // Actualizar estadísticas de la noticia
        if (liked) noticia.Likes++;
        else noticia.Dislikes++;

        // Registrar en User_News
        var userNews = new UserNews
        {
            IdUser = userId,
            IdNews = newsId,
            Seen = true,
            Interest = interested,
            DateSeen = DateTime.UtcNow
        };
        _context.UserNews.Add(userNews);

        // Ajustar dinámicamente el PESO (Weight) del Tag en Preferences del usuario
        var userPref = await _context.UserPreferences
            .Include(up => up.Preference)
            .FirstOrDefaultAsync(up => up.IdUser == userId && up.Preference.IdTag == noticia.IdTag);

        if (userPref != null)
        {
            // Entrenar el peso: +1 si le gustó, -1 si le dio dislike
            userPref.Preference.Weight += liked ? 1 : -1;
        }

        await _context.SaveChangesAsync();
    }
}