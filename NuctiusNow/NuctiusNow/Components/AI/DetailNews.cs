using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("detail_news")]
public class DetailNews
{
    [Key]
    [Column("id_detail_news")]
    public int IdDetailNews { get; set; }

    [Column("context")]
    public string? Context { get; set; }

    [Column("keyword")]
    public string? Keyword { get; set; }

    [Column("news_date")]
    public DateTime NewsDate { get; set; }
}