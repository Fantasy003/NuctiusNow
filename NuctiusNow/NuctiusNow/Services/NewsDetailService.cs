using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;
using System.Threading.Tasks;

namespace NuctiusNow.Services;

public class NewsDetailService
{
    private readonly ApplicationDbContext _context;

    public NewsDetailService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<News?> GetNewsByIdAsync(int id)
    {
        return await _context.News
            .Include(n => n.Tag)
            .Include(n => n.DetailNews)
            .FirstOrDefaultAsync(n => n.IdNews == id);
    }
}