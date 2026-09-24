using Microsoft.EntityFrameworkCore;
using BCrypt.Net;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NuctiusNow;
using NuctiusNow.Data;
using NuctiusNow.Components.User_Related;

namespace NuctiusNow.Components.User_Related;

public class AuthService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public AuthService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Registers a new user by hashing the password and saving to the database.
    /// </summary>
    public async Task<(bool success, string message)> RegistrarAsync(
        NuctiusNow.Components.User_Related.User usuarioNuevo,
        string passwordPlana)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        // 1. Validation Checks: Check for unique email or username existence
        if (await context.Users.AnyAsync(u => u.Email == usuarioNuevo.Email))
            return (false, "El email ya está registrado.");

        if (await context.Users.AnyAsync(u => u.Username == usuarioNuevo.Username))
            return (false, "El nombre de usuario ya existe.");

        // 2. Hash password and set initial state
        usuarioNuevo.Password = BCrypt.Net.BCrypt.HashPassword(passwordPlana);
        usuarioNuevo.IsBanned = false;

        try
        {
            context.Users.Add(usuarioNuevo);
            await context.SaveChangesAsync();
            return (true, "Usuario registrado con éxito.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DB Error during registration: {ex.Message}");
            return (false, $"Error desconocido al registrar el usuario: {ex.Message}");
        }
    }

    private class RecaptchaVerifyResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("challenge_ts")]
        public string? ChallengeTimestamp { get; set; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; set; }

        [JsonPropertyName("error-codes")]
        public List<string>? ErrorCodes { get; set; }
    }

    /// <summary>
    /// Verifies a reCAPTCHA token server-side using Google's verify endpoint.
    /// </summary>
    public async Task<bool> VerifyRecaptchaAsync(string token)
    {
        try
        {
            var secret = _configuration["Recaptcha:Secret"];
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(token))
                return false;

            var client = _httpClientFactory.CreateClient();

            var values = new Dictionary<string, string>
            {
                { "secret", secret },
                { "response", token }
            };

            var response = await client.PostAsync(
                "https://www.google.com/recaptcha/api/siteverify",
                new FormUrlEncodedContent(values));

            if (!response.IsSuccessStatusCode)
                return false;

            var payload = await response.Content
                .ReadFromJsonAsync<RecaptchaVerifyResponse>();

            return payload?.Success ?? false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"reCAPTCHA verification error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Registers a new user after verifying reCAPTCHA token. Returns created user id on success.
    /// </summary>
    public async Task<(bool success, string message, int? userId)> RegistrarConCaptchaAsync(
        User usuarioNuevo,
        string passwordPlana,
        string recaptchaToken)
    {
        var recaptchaOk = await VerifyRecaptchaAsync(recaptchaToken);
        if (!recaptchaOk)
            return (false, "Captcha verification failed.", null);

        var (success, message) = await RegistrarAsync(usuarioNuevo, passwordPlana);
        if (!success)
            return (false, message, null);

        await using var context = await _dbFactory.CreateDbContextAsync();

        // After RegistrarAsync we expect the user to be saved and have an Id
        var savedUser = await context.Users.FirstOrDefaultAsync(
            u => u.Email == usuarioNuevo.Email ||
                 u.Username == usuarioNuevo.Username);

        if (savedUser == null)
            return (false, "Failed to retrieve created user.", null);

        return (true, "Usuario registrado con éxito.", savedUser.IdUser);
    }

    /// <summary>
    /// Saves a list of textual preferences for a user by creating Tags,
    /// Preferences and UserPreference links as needed.
    /// </summary>
    public async Task<(bool success, string message)> SavePreferencesForUserAsync(
        int userId,
        List<string> preferences)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        var user = await context.Users.FindAsync(userId);
        if (user == null)
            return (false, "User not found.");

        foreach (var prefName in preferences
            .Select(p => p?.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!))
        {
            // Find or create tag
            var tag = await context.Tags
                .FirstOrDefaultAsync(t => t.Name == prefName);

            if (tag == null)
            {
                tag = new Tag { Name = prefName };
                context.Tags.Add(tag);
                await context.SaveChangesAsync();
            }

            // Find or create preference for that tag
            var preference = await context.Preferences
                .FirstOrDefaultAsync(p => p.IdTag == tag.IdTag);

            if (preference == null)
            {
                preference = new Preference
                {
                    IdTag = tag.IdTag,
                    PreferenceName = prefName,
                    Weight = 1,
                    IsSfw = true
                };

                context.Preferences.Add(preference);
                await context.SaveChangesAsync();
            }

            // Link to user if not already linked
            var existing = await context.UserPreferences
                .FirstOrDefaultAsync(up =>
                    up.IdUser == userId &&
                    up.IdPreference == preference.IdPreference);

            if (existing == null)
            {
                var userPref = new UserPreference
                {
                    IdUser = userId,
                    IdPreference = preference.IdPreference,
                    Weight = 1
                };

                context.UserPreferences.Add(userPref);
                await context.SaveChangesAsync();
            }
        }

        return (true, "Preferences saved.");
    }

    /// <summary>
    /// Authenticates user using username or email and verifies account status.
    /// </summary>
    public async Task<(NuctiusNow.Components.User_Related.User? user, string message)> LoginAsync(
        string usernameOrEmail,
        string passwordPlana)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        // Attempt to find user by username OR email
        var user = await context.Users.FirstOrDefaultAsync(
            u => u.Username == usernameOrEmail ||
                 u.Email == usernameOrEmail);

        if (user == null)
        {
            return (
                (NuctiusNow.Components.User_Related.User?)null,
                "Usuario o contraseña incorrectos.");
        }

        // Check if banned status is active
        if (user.IsBanned)
            return (user, "Cuenta suspendida temporalmente.");

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(passwordPlana, user.Password))
        {
            return (
                (NuctiusNow.Components.User_Related.User?)null,
                "Usuario o contraseña incorrectos.");
        }

        // Success: Return the found user object
        return (user, "Login exitoso.");
    }

    /// <summary>
    /// Finds a user by primary key.
    /// </summary>
    public async Task<NuctiusNow.Components.User_Related.User?> GetUserByIdAsync(int id)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        return await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.IdUser == id);
    }

    /// <summary>
    /// Loads the textual preferences linked to a user.
    /// </summary>
    public async Task<List<string>> GetUserPreferencesAsync(int userId)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        return await context.UserPreferences
            .Where(up => up.IdUser == userId)
            .Join(
                context.Preferences,
                up => up.IdPreference,
                p => p.IdPreference,
                (up, p) => p.PreferenceName)
            .Where(preferenceName => !string.IsNullOrWhiteSpace(preferenceName))
            .Select(preferenceName => preferenceName!)
            .ToListAsync();
    }

    /// <summary>
    /// Replaces the user's preference list in the database.
    /// </summary>
    public async Task<bool> ReplacePreferencesForUserAsync(
        int userId,
        List<string> preferences)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        var existingPreferences = context.UserPreferences
            .Where(up => up.IdUser == userId);

        context.UserPreferences.RemoveRange(existingPreferences);
        await context.SaveChangesAsync();

        foreach (var prefName in preferences
            .Select(p => p?.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => p!))
        {
            var tag = await context.Tags
                .FirstOrDefaultAsync(t => t.Name == prefName);

            if (tag == null)
            {
                tag = new Tag { Name = prefName };
                context.Tags.Add(tag);
                await context.SaveChangesAsync();
            }

            var preference = await context.Preferences
                .FirstOrDefaultAsync(p => p.IdTag == tag.IdTag);

            if (preference == null)
            {
                preference = new Preference
                {
                    IdTag = tag.IdTag,
                    PreferenceName = prefName,
                    Weight = 1,
                    IsSfw = true
                };

                context.Preferences.Add(preference);
                await context.SaveChangesAsync();
            }

            var userPref = new UserPreference
            {
                IdUser = userId,
                IdPreference = preference.IdPreference,
                Weight = 1
            };

            context.UserPreferences.Add(userPref);
            await context.SaveChangesAsync();
        }

        return true;
    }

    /// <summary>
    /// Updates user profile information for an existing user.
    /// </summary>
    public async Task<bool> ActualizarPerfilAsync(
        int idUser,
        string? newUsername = null,
        string? newEmail = null,
        DateTime? newBirthday = null,
        string? newLanguage = null,
        string? nuevaPassword = null)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        var userToUpdate = await context.Users.FindAsync(idUser);

        if (userToUpdate == null)
            return false;

        // Update fields if provided and changed
        if (!string.IsNullOrEmpty(newUsername) &&
            userToUpdate.Username != newUsername)
        {
            userToUpdate.Username = newUsername;
        }

        if (!string.IsNullOrEmpty(newEmail) &&
            userToUpdate.Email != newEmail)
        {
            userToUpdate.Email = newEmail;
        }

        if (newBirthday.HasValue &&
            userToUpdate.Birthday != newBirthday.Value)
        {
            userToUpdate.Birthday = newBirthday.Value;
        }

        if (!string.IsNullOrEmpty(newLanguage))
        {
            var normalizedLanguage =
                newLanguage.Equals("Spanish", StringComparison.OrdinalIgnoreCase) ||
                newLanguage.Equals("es", StringComparison.OrdinalIgnoreCase) ||
                newLanguage == "2"
                    ? "es"
                    : "en";

            userToUpdate.Language = normalizedLanguage;
        }

        if (!string.IsNullOrEmpty(nuevaPassword))
        {
            userToUpdate.Password =
                BCrypt.Net.BCrypt.HashPassword(nuevaPassword);
        }

        try
        {
            await context.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating profile: {ex.Message}");
            return false;
        }
    }

    public sealed record ProfileUpdateStatus(
        bool CanUpdate,
        DateTime? NextAvailableAt,
        TimeSpan Remaining);

    public sealed record ProfileUpdateResult(
        bool Success,
        string Message,
        DateTime? NextAvailableAt = null);

    /// <summary>
    /// Returns whether the user may update their profile. The cooldown is persisted
    /// in PostgreSQL so restarting the application cannot bypass it.
    /// </summary>
    public async Task<ProfileUpdateStatus> GetProfileUpdateStatusAsync(int userId)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.IdUser == userId);

        if (user == null)
            return new ProfileUpdateStatus(false, null, TimeSpan.Zero);

        if (!user.LastProfileUpdateAt.HasValue)
            return new ProfileUpdateStatus(true, null, TimeSpan.Zero);

        var next = user.LastProfileUpdateAt.Value
            .ToUniversalTime()
            .AddHours(3);

        var remaining = next - DateTime.UtcNow;

        return remaining <= TimeSpan.Zero
            ? new ProfileUpdateStatus(true, null, TimeSpan.Zero)
            : new ProfileUpdateStatus(false, next, remaining);
    }

    /// <summary>
    /// Atomically updates all editable user profile values and preferences.
    /// A successful save starts a three-hour server-side cooldown.
    /// </summary>
    public async Task<ProfileUpdateResult> UpdateProfileAsync(
        int userId,
        string username,
        string email,
        DateTime birthday,
        string language,
        string? newPassword,
        List<string> preferences)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        var user = await context.Users
            .FirstOrDefaultAsync(u => u.IdUser == userId);

        if (user == null)
            return new ProfileUpdateResult(false, "User not found.");

        var now = DateTime.UtcNow;

        if (user.LastProfileUpdateAt.HasValue)
        {
            var next = user.LastProfileUpdateAt.Value
                .ToUniversalTime()
                .AddHours(3);

            if (next > now)
            {
                return new ProfileUpdateResult(
                    false,
                    $"Profile changes are locked until {next.ToLocalTime():dd/MM/yyyy HH:mm}.",
                    next);
            }
        }

        username = username?.Trim() ?? string.Empty;
        email = email?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
            return new ProfileUpdateResult(
                false,
                "Username must contain at least 3 characters.");

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return new ProfileUpdateResult(
                false,
                "Enter a valid email address.");

        if (await context.Users.AnyAsync(
                u => u.IdUser != userId && u.Username == username))
        {
            return new ProfileUpdateResult(
                false,
                "The username is already in use.");
        }

        if (await context.Users.AnyAsync(
                u => u.IdUser != userId && u.Email == email))
        {
            return new ProfileUpdateResult(
                false,
                "The email is already in use.");
        }

        var normalizedLanguage =
            language.Equals("Spanish", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("es", StringComparison.OrdinalIgnoreCase)
                ? "es"
                : "en";

        user.Username = username;
        user.Email = email;
        user.Birthday = birthday.Date;
        user.Language = normalizedLanguage;

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            user.Password =
                BCrypt.Net.BCrypt.HashPassword(newPassword);
        }

        var cleanPreferences = preferences
            .Select(p => p?.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => p!)
            .Take(30)
            .ToList();

        // Preserve the user's learned weight when a preference remains in the profile.
        // Replacing the links must not erase recommendation history.
        var existingLinks = await context.UserPreferences
            .Include(up => up.Preference)
                .ThenInclude(p => p.Tag)
            .Where(up => up.IdUser == userId)
            .ToListAsync();

        var previousWeights = existingLinks
            .Where(up => up.Preference != null)
            .GroupBy(
                up => up.Preference!.Tag?.Name ??
                      up.Preference!.PreferenceName ??
                      string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.First().Weight,
                StringComparer.OrdinalIgnoreCase);

        context.UserPreferences.RemoveRange(existingLinks);

        foreach (var prefName in cleanPreferences)
        {
            var tag = await context.Tags
                .FirstOrDefaultAsync(t => t.Name == prefName);

            if (tag == null)
            {
                tag = new Tag { Name = prefName };
                context.Tags.Add(tag);
            }

            var preference = await context.Preferences
                .FirstOrDefaultAsync(p => p.IdTag == tag.IdTag);

            if (preference == null)
            {
                preference = new Preference
                {
                    Tag = tag,
                    PreferenceName = prefName,
                    Weight = 1,
                    IsSfw = true
                };

                context.Preferences.Add(preference);
            }

            previousWeights.TryGetValue(
                prefName,
                out var previousWeight);

            if (previousWeight == 0)
                previousWeight = 1;

            context.UserPreferences.Add(new UserPreference
            {
                IdUser = userId,
                User = user,
                Preference = preference,
                Weight = previousWeight
            });
        }

        user.LastProfileUpdateAt = now;

        try
        {
            await context.SaveChangesAsync();

            return new ProfileUpdateResult(
                true,
                "Profile updated successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating profile: {ex.Message}");

            return new ProfileUpdateResult(
                false,
                "Could not save profile changes.");
        }
    }

    /// <summary>
    /// Changes the ban status of a user account by administrator action.
    /// </summary>
    public async Task<bool> CambiarEstadoBanAsync(
        int idUser,
        bool banear)
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        var user = await context.Users.FindAsync(idUser);

        if (user == null)
            return false;

        user.IsBanned = banear;

        try
        {
            await context.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error changing ban status: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Retrieves all registered users from the database for administrative views.
    /// </summary>
    public async Task<List<NuctiusNow.Components.User_Related.User>>
        ObtenerTodosLosUsuariosAsync()
    {
        await using var context = await _dbFactory.CreateDbContextAsync();

        // Returns all users, including those potentially banned or needing review.
        return await context.Users
            .AsNoTracking()
            .ToListAsync();
    }
}
