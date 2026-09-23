using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("tags")]
public class Tag
{
    [Key]
    [Column("id_tag")]
    public int IdTag { get; set; }

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    // Navigation Properties
    public virtual ICollection<Preference> Preferences { get; set; } = new List<Preference>();
    public virtual ICollection<News> News { get; set; } = new List<News>();
    public virtual ICollection<UserPreferenceTag> UserPreferenceTags { get; set; } = new List<UserPreferenceTag>();
}

[Table("preferences")]
public class Preference
{
    [Key]
    [Column("id_preference")]
    public int IdPreference { get; set; }

    [Column("id_tag")]
    public int IdTag { get; set; }

    [ForeignKey(nameof(IdTag))]
    public Tag? Tag { get; set; }

    [Column("preference")]
    public string? PreferenceName { get; set; }

    [Column("weight")]
    public int Weight { get; set; }

    [Column("is_sfw")]
    public bool IsSfw { get; set; } = true;

    // Navigation Properties
    public virtual ICollection<UserPreference> UserPreferences { get; set; } = new List<UserPreference>();
}