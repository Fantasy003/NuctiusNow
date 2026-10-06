using Microsoft.EntityFrameworkCore;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;

namespace NuctiusNow.Services;

public sealed class UserAwareLocalLlmService : LocalLlmService, ILocalLlmService
{
    private readonly UserSessionService _session;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;

    public UserAwareLocalLlmService(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<LocalLlmService> logger,
        UserSessionService session,
        IDbContextFactory<ApplicationDbContext> dbFactory)
        : base(httpFactory, configuration, logger)
    {
        _session = session;
        _dbFactory = dbFactory;
    }

    async Task<List<(string Title, string Url, string Snippet)>> ILocalLlmService.SearchNewsAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken,
        int startingPage)
    {
        var filteredQuery = query;

        if (_session.UserId.HasValue)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

                var blocked = await db.UserBlockedSources
                    .AsNoTracking()
                    .Where(x => x.IdUser == _session.UserId.Value)
                    .Select(x => x.SourceDomain)
                    .ToListAsync(cancellationToken);

                var domains = blocked
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(NormalizeDomain)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (domains.Count > 0)
                {
                    filteredQuery += " " +
                        string.Join(" ", domains.Select(d => $"-site:{d}"));
                }
            }
            catch
            {
                // A source-filter lookup must never break news discovery.
            }
        }

        var results = await base.SearchNewsAsync(
            filteredQuery,
            maxResults,
            cancellationToken,
            startingPage);

        return results
            .Where(r => !IsBlocked(r.Url))
            .ToList();
    }

    private bool IsBlocked(string? url)
    {
        if (!_session.UserId.HasValue ||
            string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        try
        {
            using var db = _dbFactory.CreateDbContext();
            var domains = db.UserBlockedSources
                .AsNoTracking()
                .Where(x => x.IdUser == _session.UserId.Value)
                .Select(x => x.SourceDomain)
                .ToList();

            var host = NormalizeDomain(uri.Host);

            return domains
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeDomain)
                .Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                          host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
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
        return host.StartsWith("www.", StringComparison.Ordinal)
            ? host[4..]
            : host;
    }
}
