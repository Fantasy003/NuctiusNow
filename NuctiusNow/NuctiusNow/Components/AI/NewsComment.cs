using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NuctiusNow.Components.User_Related;

[Table("news_comments")]
public class NewsComment
{
    [Key]
    [Column("id_comment")]
    public int IdComment { get; set; }

    [Column("id_news")]
    public int IdNews { get; set; }

    [ForeignKey(nameof(IdNews))]
    public News? News { get; set; }

    [Column("id_user")]
    public int IdUser { get; set; }

    [ForeignKey(nameof(IdUser))]
    public User? User { get; set; }

    [Column("comment_text")]
    [MaxLength(500)]
    public string CommentText { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("likes")]
    public int Likes { get; set; } = 0;
}
