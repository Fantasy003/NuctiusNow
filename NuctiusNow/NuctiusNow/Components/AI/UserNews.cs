using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace NuctiusNow.Components.User_Related;
#pragma warning restore IDE0130

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

    // User-specific recommendation weight. Unlike Preference.Weight, this value
    // belongs only to this user's relationship with the preference.
    [Column("weight")]
    public int Weight { get; set; } = 1;

    [ForeignKey(nameof(IdPreference))]
    public Preference? Preference { get; set; }

    // Navigation Properties
    public virtual ICollection<UserPreferenceTag> UserPreferenceTags { get; set; } = new List<UserPreferenceTag>();
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

    // 0 = no reaction, 1 = like, -1 = dislike.
    [Column("reaction")]
    public int Reaction { get; set; } = 0;

    [Column("date_seen")]
    public DateTime DateSeen { get; set; } = DateTime.UtcNow;
}

