using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using NuctiusNow.Data;

namespace NuctiusNow.Components.User_Related;

public class UserSessionService
{
    private const string SessionKey = "nuctiusnow_session";
    private readonly IJSRuntime _jsRuntime;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ITimeLimitedDataProtector _protector;

    public bool IsAuthenticated { get; private set; }
    public int? UserId { get; private set; }
    public string? Username { get; private set; }
    public bool IsPremium { get; private set; }
    public bool RememberMe { get; private set; }

    public UserSessionService(IJSRuntime jsRuntime, IDbContextFactory<ApplicationDbContext> dbFactory, IDataProtectionProvider protection)
    {
        _jsRuntime = jsRuntime;
        _dbFactory = dbFactory;
        _protector = protection.CreateProtector("NuctiusNow.UserSession.v1").ToTimeLimitedDataProtector();
    }

    public async Task InitializeAsync()
    {
        var ticket = await _jsRuntime.InvokeAsync<string?>("sessionStorage.getItem", SessionKey);
        RememberMe = string.IsNullOrEmpty(ticket);
        if (RememberMe)
            ticket = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", SessionKey);

        ResetState();
        if (string.IsNullOrEmpty(ticket)) return;
        try
        {
            await using var context = await _dbFactory.CreateDbContextAsync();
            var identity = JsonSerializer.Deserialize<SessionIdentity>(_protector.Unprotect(ticket, out _));
            var user = identity == null ? null : await context.Users.AsNoTracking()
                .SingleOrDefaultAsync(u => u.IdUser == identity.UserId);
            // Password changes and bans invalidate existing tickets.
            if (user == null || user.IsBanned || !user.EmailVerified || user.Password != identity!.PasswordStamp)
            {
                await ClearAsync();
                return;
            }
            SetState(user);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            await ClearAsync();
        }
    }

    // Called only by server-side components after successful login/registration.
    public async Task SetUserAsync(int userId, bool rememberMe = false)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.IdUser == userId && !u.IsBanned && u.EmailVerified);
        if (user == null) { await ClearAsync(); return; }
        var ticket = _protector.Protect(JsonSerializer.Serialize(new SessionIdentity(user.IdUser, user.Password)),
            rememberMe ? TimeSpan.FromDays(30) : TimeSpan.FromHours(12));
        await ClearAsync();
        await _jsRuntime.InvokeVoidAsync(rememberMe ? "localStorage.setItem" : "sessionStorage.setItem", SessionKey, ticket);
        RememberMe = rememberMe;
        SetState(user);
    }

    public Task SetUserAsync(int userId, string username) => SetUserAsync(userId, RememberMe);

    public async Task ClearAsync()
    {
        ResetState();
        RememberMe = false;
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", SessionKey);
        await _jsRuntime.InvokeVoidAsync("sessionStorage.removeItem", SessionKey);
        // Retire the old, editable identity fields; never trust them for authentication.
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nuctiusnow_user");
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nuctiusnow_userid");
    }

    private void ResetState()
    {
        IsAuthenticated = false;
        UserId = null;
        Username = null;
        IsPremium = false;
    }

    private void SetState(User user)
    {
        IsAuthenticated = true;
        UserId = user.IdUser;
        Username = user.Username;
        IsPremium = user.IsPremium;
    }

    private sealed record SessionIdentity(int UserId, string PasswordStamp);
}
