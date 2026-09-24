using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("user_preference_tags")]
public class UserPreferenceTag
{
    [Key]
    [Column("id_user_preference_tag")]
    public int IdUserPreferenceTag { get; set; }

    [Column("id_user_preference")]
    public int IdUserPreference { get; set; }

    [ForeignKey(nameof(IdUserPreference))]
    public UserPreference? UserPreference { get; set; }

    [Column("id_tag")]
    public int IdTag { get; set; }

    [ForeignKey(nameof(IdTag))]
    public Tag? Tag { get; set; }
}
