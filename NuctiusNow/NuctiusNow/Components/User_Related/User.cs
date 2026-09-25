using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("users")]
public class User
{
    [Key]
    [Column("id_user")]
    public int IdUser { get; set; }

    [Column("username")]
    public string Username { get; set; } = string.Empty;

    [Column("email")]
    public string Email { get; set; } = string.Empty;

    [Column("password")]
    public string Password { get; set; } = string.Empty;

    [Column("password_hash")]
    public string PasswordHash { get; set; } = string.Empty;

    [Column("is_premium")]
    public bool IsPremium { get; set; } = false;

    [Column("is_banned")]
    public bool IsBanned { get; set; } = false;

    [Column("language")]
    public string Language { get; set; } = "en";

    [Column("birthday")]
    public DateTime Birthday { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("last_profile_update_at")]
    public DateTime? LastProfileUpdateAt { get; set; }

    public virtual ICollection<UserPreference> UserPreferences { get; set; } = new List<UserPreference>();
    public virtual ICollection<UserNews> UserNews { get; set; } = new List<UserNews>();
    public virtual ICollection<UserInterestKeyword> InterestKeywords { get; set; } = new List<UserInterestKeyword>();
}
