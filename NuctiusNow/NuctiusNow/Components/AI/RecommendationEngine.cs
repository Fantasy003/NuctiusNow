using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;

namespace NuctiusNow.Services;

public class RecommendationEngine
{
    private readonly ApplicationDbContext _context;

    public RecommendationEngine(ApplicationDbContext context)
    {
        _context = context;
    }

    // Calcula y devuelve las mejores noticias recomendadas para un usuario
    public async Task<List<News>> ObtenerRecomendacionesParaUsuarioAsync(int userId, int cantidad = 10)
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

        var noticiasCandidatas = await _context.News
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .Where(n => !noticiasVistasIds.Contains(n.IdNews))
            .ToListAsync();

        // 3. Algoritmo de Scoring / Ponderación ML
        var noticiasPuntuadas = noticiasCandidatas.Select(news => new
        {
            Noticia = news,
            Score = CalcularScore(news, userPreferences)
        })
        .OrderByDescending(x => x.Score)
        .Select(x => x.Noticia)
        .Take(cantidad)
        .ToList();

        return noticiasPuntuadas;
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