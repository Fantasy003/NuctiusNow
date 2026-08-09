using Microsoft.EntityFrameworkCore;
using BCrypt.Net;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NuctiusNow;
using NuctiusNow.Data;

namespace NuctiusNow.Components.User_Related;

public class AuthService
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public AuthService(ApplicationDbContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Registers a new user by hashing the password and saving to the database.
    /// </summary>
    public async Task<(bool success, string message)> RegistrarAsync(User usuarioNuevo, string passwordPlana)
    {
        // 1. Validation Checks: Check for unique email or username existence
        if (await _context.Users.AnyAsync(u => u.Email == usuarioNuevo.Email))
            return (false, "El email ya está registrado.");
        if (await _context.Users.AnyAsync(u => u.Username == usuarioNuevo.Username))
            return (false, "El nombre de usuario ya existe.");

        // 2. Hash password and set initial state
        usuarioNuevo.Password = BCrypt.Net.BCrypt.HashPassword(passwordPlana);
        usuarioNuevo.IsBanned = false;

        try
        {
            _context.Users.Add(usuarioNuevo);
            await _context.SaveChangesAsync();
            return (true, "Usuario registrado con éxito.");
        }
        catch (Exception ex)
        {
            // In a real application, use a proper logging framework instead of Console.WriteLine
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
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(token)) return false;

            var client = _httpClientFactory.CreateClient();
            var values = new Dictionary<string, string>
            {
                { "secret", secret },
                { "response", token }
            };

            var response = await client.PostAsync("https://www.google.com/recaptcha/api/siteverify", new FormUrlEncodedContent(values));
            if (!response.IsSuccessStatusCode) return false;

            var payload = await response.Content.ReadFromJsonAsync<RecaptchaVerifyResponse>();
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
    public async Task<(bool success, string message, int? userId)> RegistrarConCaptchaAsync(User usuarioNuevo, string passwordPlana, string recaptchaToken)
    {
        var recaptchaOk = await VerifyRecaptchaAsync(recaptchaToken);
        if (!recaptchaOk) return (false, "Captcha verification failed.", null);

        var (success, message) = await RegistrarAsync(usuarioNuevo, passwordPlana);
        if (!success) return (false, message, null);

        // After RegistrarAsync we expect the user to be saved and have an Id
        var savedUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == usuarioNuevo.Email || u.Username == usuarioNuevo.Username);
        if (savedUser == null) return (false, "Failed to retrieve created user.", null);

        return (true, "Usuario registrado con éxito.", savedUser.IdUser);
    }

    /// <summary>
    /// Saves a list of textual preferences for a user by creating Tags, Preferences and UserPreference links as needed.
    /// </summary>
    public async Task<(bool success, string message)> SavePreferencesForUserAsync(int userId, List<string> preferences)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return (false, "User not found.");

        foreach (var prefName in preferences.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
        {
            // Find or create tag
            var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Name == prefName);
            if (tag == null)
            {
                tag = new Tag { Name = prefName };
                _context.Tags.Add(tag);
                await _context.SaveChangesAsync();
            }

            // Find or create preference for that tag
            var preference = await _context.Preferences.FirstOrDefaultAsync(p => p.IdTag == tag.IdTag);
            if (preference == null)
            {
                preference = new Preference { IdTag = tag.IdTag, PreferenceName = prefName, Weight = 1, IsSfw = true };
                _context.Preferences.Add(preference);
                await _context.SaveChangesAsync();
            }

            // Link to user if not already linked
            var existing = await _context.UserPreferences.FirstOrDefaultAsync(up => up.IdUser == userId && up.IdPreference == preference.IdPreference);
            if (existing == null)
            {
                var userPref = new UserPreference { IdUser = userId, IdPreference = preference.IdPreference };
                _context.UserPreferences.Add(userPref);
                await _context.SaveChangesAsync();
            }
        }

        return (true, "Preferences saved.");
    }

    /// <summary>
    /// Authenticates user using username or email and verifies account status.
    /// </summary>
    public async Task<(User? user, string message)> LoginAsync(string usernameOrEmail, string passwordPlana)
    {
        // Attempt to find user by username OR email
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == usernameOrEmail || u.Email == usernameOrEmail);

        if (user == null)
            return (null, "Usuario o contraseña incorrectos.");

        // Check if banned status is active
        if (user.IsBanned)
            return (user, "Cuenta suspendida temporalmente.");
            
        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(passwordPlana, user.Password))
            return (null, "Usuario o contraseña incorrectos.");

        // Success: Return the found user object
        return (user, "Login exitoso.");
    }

    /// <summary>
    /// Finds a user by primary key.
    /// </summary>
    public async Task<User?> GetUserByIdAsync(int id)
    {
        return await _context.Users.FindAsync(id);
    }

    /// <summary>
    /// Loads the textual preferences linked to a user.
    /// </summary>
    public async Task<List<string>> GetUserPreferencesAsync(int userId)
    {
        return await _context.UserPreferences
            .Where(up => up.IdUser == userId)
            .Join(_context.Preferences,
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
    public async Task<bool> ReplacePreferencesForUserAsync(int userId, List<string> preferences)
    {
        var existingPreferences = _context.UserPreferences.Where(up => up.IdUser == userId);
        _context.UserPreferences.RemoveRange(existingPreferences);
        await _context.SaveChangesAsync();

        foreach (var prefName in preferences
            .Select(p => p?.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => p!))
        {
            var tag = await _context.Tags.FirstOrDefaultAsync(t => t.Name == prefName);
            if (tag == null)
            {
                tag = new Tag { Name = prefName };
                _context.Tags.Add(tag);
                await _context.SaveChangesAsync();
            }

            var preference = await _context.Preferences.FirstOrDefaultAsync(p => p.IdTag == tag.IdTag);
            if (preference == null)
            {
                preference = new Preference { IdTag = tag.IdTag, PreferenceName = prefName, Weight = 1, IsSfw = true };
                _context.Preferences.Add(preference);
                await _context.SaveChangesAsync();
            }

            var userPref = new UserPreference { IdUser = userId, IdPreference = preference.IdPreference };
            _context.UserPreferences.Add(userPref);
            await _context.SaveChangesAsync();
        }

        return true;
    }

    /// <summary>
    /// Updates user profile information for an existing user.
    /// </summary>
    public async Task<bool> ActualizarPerfilAsync(int idUser, string? newUsername = null, string? newEmail = null, DateTime? newBirthday = null, string? newLanguage = null, string? nuevaPassword = null)
    {
        var userToUpdate = await _context.Users.FindAsync(idUser);

        if (userToUpdate == null) return false;

        // Update fields if provided and changed
        if (!string.IsNullOrEmpty(newUsername) && userToUpdate.Username != newUsername)
            userToUpdate.Username = newUsername;
        
        if (!string.IsNullOrEmpty(newEmail) && userToUpdate.Email != newEmail)
            userToUpdate.Email = newEmail;

        if (newBirthday.HasValue && userToUpdate.Birthday != newBirthday.Value)
            userToUpdate.Birthday = newBirthday.Value;
        
        if (!string.IsNullOrEmpty(newLanguage))
        {
            if (int.TryParse(newLanguage, out int newLangId) && userToUpdate.Language != newLangId)
            {
                userToUpdate.Language = newLangId;
            }
        }

        if (!string.IsNullOrEmpty(nuevaPassword))
        {
            userToUpdate.Password = BCrypt.Net.BCrypt.HashPassword(nuevaPassword);
        }

        try
        {
            await _context.SaveChangesAsync();
            return true; 
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating profile: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Changes the ban status of a user account by administrator action.
    /// </summary>
    public async Task<bool> CambiarEstadoBanAsync(int idUser, bool banear)
    {
        var user = await _context.Users.FindAsync(idUser);
        if (user == null) return false;

        user.IsBanned = banear;
        
        try
        {
            await _context.SaveChangesAsync();
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
    public async Task<List<User>> ObtenerTodosLosUsuariosAsync()
    {
        // Returns all users, including those potentially banned or needing review.
        return await _context.Users.ToListAsync();
    }
}