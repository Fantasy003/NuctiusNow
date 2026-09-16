using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("users")]
public class User
{
    [Key]
    [Column("id_user")]
    public int IdUser { get; set; }

    [Column("username")]
    public string Username { get; set; } = string.Empty;

    [Column("password")]
    public string Password { get; set; } = string.Empty;

    [Column("email")]
    public string Email { get; set; } = string.Empty;

    [Column("birthday")]
    [Timestamp]
    public DateTime? Birthday { get; set; }

    [Column("language")]
    public int Language { get; set; } = 1;

    [Column("is_premium")]
    public bool IsPremium { get; set; } = false;

    [Column("is_banned")]
    public bool IsBanned { get; set; } = false;

    [Column("role")]
    public string Role { get; set; } = "User";
}