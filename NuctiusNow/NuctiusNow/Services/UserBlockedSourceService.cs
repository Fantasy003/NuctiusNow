using Microsoft.EntityFrameworkCore;
using NuctiusNow.Data;
using NuctiusNow.Components.User_Related;

namespace NuctiusNow.Services;

public sealed class UserBlockedSourceService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public UserBlockedSourceService(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<string>> GetAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.UserBlockedSources
            .AsNoTracking()
            .Where(x => x.IdUser == userId)
            .OrderBy(x => x.SourceDomain)
            .Select(x => x.SourceDomain)
            .ToListAsync();
    }

    public async Task<(bool Success, string Message)> ReplaceAsync(int userId, IEnumerable<string> domains)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        if (!await db.Users.AnyAsync(x => x.IdUser == userId))
            return (false, "User not found.");

        var normalized = domains
            .Select(NormalizeDomain)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

        db.UserBlockedSources.RemoveRange(
            db.UserBlockedSources.Where(x => x.IdUser == userId));

        foreach (var domain in normalized)
        {
            db.UserBlockedSources.Add(new UserBlockedSource
            {
                IdUser = userId,
                SourceDomain = domain,
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return (true, "Source filters saved.");
    }

    private static string NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var input = value.Trim();
        if (!input.Contains("://", StringComparison.Ordinal))
            input = "https://" + input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return string.Empty;

        var host = uri.Host.Trim().ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        return host.Length >= 3 && host.Contains('.', StringComparison.Ordinal)
            ? host
            : string.Empty;
    }
}
