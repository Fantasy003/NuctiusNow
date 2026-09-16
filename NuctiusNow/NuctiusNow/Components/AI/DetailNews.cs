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

    [Column("context")]
    [MaxLength(1000)] // Increased to handle full AI summaries
    public string? Context { get; set; }

    [Column("keyword")]
    [MaxLength(255)] // Increased to handle long article titles
    public string? Keyword { get; set; }

    [Column("news_date")]
    public DateTime NewsDate { get; set; }

    [Column("ai_summary")]
    public string? AiSummary { get; set; }
}