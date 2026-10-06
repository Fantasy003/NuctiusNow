using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NuctiusNow.Data;

namespace NuctiusNow.Services;

public sealed class EmailVerificationService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IEmailSender _emailSender;
    private readonly IConfiguration _configuration;

    public EmailVerificationService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IEmailSender emailSender,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _emailSender = emailSender;
        _configuration = configuration;
    }

    public async Task<bool> IsEmailVerifiedAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Users
            .Where(u => u.IdUser == userId)
            .Select(u => u.EmailVerified)
            .FirstOrDefaultAsync();
    }

    public async Task SendActivationEmailAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.IdUser == userId, cancellationToken);
        if (user == null || string.IsNullOrWhiteSpace(user.Email))
            throw new InvalidOperationException("The user or email address could not be found.");

        if (user.EmailVerified) return;
        var baseUrl = (_configuration["Application:PublicBaseUrl"] ?? string.Empty).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var publicUri) ||
            (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Application:PublicBaseUrl is not configured with an HTTP(S) URL.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        user.EmailVerified = false;
        user.EmailVerificationTokenHash = hash;
        user.EmailVerificationExpiresAt = DateTime.UtcNow.AddHours(24);
        await db.SaveChangesAsync(cancellationToken);

        var activationUrl = WebUtility.HtmlEncode($"{baseUrl}/activate-account/{Uri.EscapeDataString(token)}");
        var username = WebUtility.HtmlEncode(user.Username);

        var html = $"""
<!doctype html>
<html>
<body style="margin:0;background:#0f0f0f;color:#f5f5f5;font-family:Arial,sans-serif;">
<div style="max-width:620px;margin:0 auto;padding:36px 20px;">
  <div style="background:#1a1a1a;border:1px solid #2a2a2a;border-radius:18px;padding:32px;">
    <div style="color:#E63946;font-size:11px;font-weight:bold;letter-spacing:3px;">NUCTIUSNOW / ACCOUNT</div>
    <h1 style="margin:10px 0 14px;font-family:Georgia,serif;font-size:30px;color:#fff;">Activate your account</h1>
    <p style="color:#aaa;line-height:1.6;">Hello {username}, click the button below to verify your email address and unlock NuctiusNow.</p>
    <a href="{activationUrl}" style="display:inline-block;margin:20px 0;padding:13px 18px;background:#E63946;color:#fff;text-decoration:none;border-radius:10px;font-weight:700;">Activate account</a>
    <p style="color:#777;font-size:13px;line-height:1.5;">This link expires in 24 hours and can be used only once.</p>
  </div>
</div>
</body>
</html>
""";

        await _emailSender.SendHtmlAsync(
            user.Email,
            "Activate your NuctiusNow account",
            html,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(bool Success, string Message)> ActivateAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, "The activation link is invalid.");

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        await using var db = await _dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.EmailVerificationTokenHash == hash,
            cancellationToken);

        if (user == null)
            return (false, "The activation link is invalid or has already been used.");

        if (!user.EmailVerificationExpiresAt.HasValue ||
            user.EmailVerificationExpiresAt.Value < DateTime.UtcNow)
        {
            return (false, "The activation link has expired. Request a new activation email.");
        }

        // Consume the token atomically so concurrent requests cannot both activate it.
        var changed = await db.Users.Where(u => u.IdUser == user.IdUser &&
                u.EmailVerificationTokenHash == hash && !u.EmailVerified && u.EmailVerificationExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.EmailVerified, true)
                .SetProperty(u => u.EmailVerificationTokenHash, (string?)null)
                .SetProperty(u => u.EmailVerificationExpiresAt, (DateTime?)null), cancellationToken);
        if (changed != 1) return (false, "The activation link is invalid or has already been used.");

        return (true, "Your email has been verified. You can now log in.");
    }

    public async Task<(bool Success, string Message)> ResendAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return (false, "Enter your email address.");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == email.Trim(),
            cancellationToken);

        if (user == null)
            return (false, "No account was found for that email address.");

        if (user.EmailVerified)
            return (true, "That account is already verified.");

        // The call creates a fresh token and persists only its hash.
        await SendActivationEmailAsync(user.IdUser, cancellationToken);
        return (true, "A new activation email has been sent.");
    }
}
