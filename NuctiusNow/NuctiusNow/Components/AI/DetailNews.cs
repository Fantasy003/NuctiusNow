using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace NuctiusNow.Components.User_Related;
#pragma warning restore IDE0130

[Table("detail_news")]
public class DetailNews
{
    [Key]
    [Column("id_detail_news")]
    public int IdDetailNews { get; set; }

    [Column("image_url")]
    public string? ImageUrl { get; set; }

    [Column("context")]
    public string? Context { get; set; }

    [Column("keyword")]
    [MaxLength(255)]
    public string? Keyword { get; set; }

    [Column("full_body_text")]
    public string? FullBodyText { get; set; }

    [Column("news_date")]
    public DateTime NewsDate { get; set; }

    [Column("ai_summary")]
    public string? AiSummary { get; set; }

    [Column("body_source")]
    [MaxLength(32)]
    public string BodySource { get; set; } = "Unknown";

    // Navigation Properties
    public virtual News? News { get; set; }
}