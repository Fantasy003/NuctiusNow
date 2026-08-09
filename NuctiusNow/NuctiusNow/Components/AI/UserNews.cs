using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("user_preference")]
public class UserPreference
{
    [Key]
    [Column("id_user_preference")]
    public int IdUserPreference { get; set; }

    [Column("id_user")]
    public int IdUser { get; set; }

    [ForeignKey(nameof(IdUser))]
    public User? User { get; set; }

    [Column("id_preference")]
    public int IdPreference { get; set; }

    [ForeignKey(nameof(IdPreference))]
    public Preference? Preference { get; set; }
}

[Table("user_news")]
public class UserNews
{
    [Key]
    [Column("id_user_news")]
    public int IdUserNews { get; set; }

    [Column("id_user")]
    public int IdUser { get; set; }

    [ForeignKey(nameof(IdUser))]
    public User? User { get; set; }

    [Column("id_news")]
    public int IdNews { get; set; }

    [ForeignKey(nameof(IdNews))]
    public News? News { get; set; }

    [Column("seen")]
    public bool Seen { get; set; }

    [Column("interest")]
    public bool Interest { get; set; }

    [Column("date_seen")]
    public DateTime DateSeen { get; set; } = DateTime.UtcNow;
}