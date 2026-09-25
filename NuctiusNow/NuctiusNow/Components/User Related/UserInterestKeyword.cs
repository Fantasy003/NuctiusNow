using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("user_interest_keywords")]
public class UserInterestKeyword
{
    [Key]
    [Column("id_user_interest_keyword")]
    public int IdUserInterestKeyword { get; set; }

    [Column("id_user")]
    public int IdUser { get; set; }

    [ForeignKey(nameof(IdUser))]
    public User? User { get; set; }

    [Column("keyword")]
    [MaxLength(120)]
    public string Keyword { get; set; } = string.Empty;

    [Column("weight")]
    public int Weight { get; set; } = 1;

    [Column("last_updated_at")]
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}
