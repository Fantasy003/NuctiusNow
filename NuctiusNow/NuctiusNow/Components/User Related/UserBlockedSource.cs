using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("user_blocked_sources")]
public class UserBlockedSource
{
    [Key]
    [Column("id_user_blocked_source")]
    public int IdUserBlockedSource { get; set; }

    [Column("id_user")]
    public int IdUser { get; set; }

    [ForeignKey(nameof(IdUser))]
    public User? User { get; set; }

    [Column("source_domain")]
    [MaxLength(160)]
    public string SourceDomain { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
