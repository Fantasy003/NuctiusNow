﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace NuctiusNow.Components.User_Related;
#pragma warning restore IDE0130

[Table("news")]
public class News
{
    [Key]
    [Column("id_news")]
    public int IdNews { get; set; }

    [Column("id_tag")]
    public int IdTag { get; set; }

    [ForeignKey(nameof(IdTag))]
    public Tag? Tag { get; set; }

    [Column("id_detail_news")]
    public int IdDetailNews { get; set; }

    [ForeignKey(nameof(IdDetailNews))]
    public DetailNews? DetailNews { get; set; }

    [Column("hyperlink")]
    public string Hyperlink { get; set; } = string.Empty;

    [Column("is_sfw")]
    public bool IsSfw { get; set; } = true;

    [Column("secure_link")]
    public bool SecureLink { get; set; } = true;

    [Column("is_ai")]
    public bool IsAi { get; set; } = false;

    [Column("captured_date")]
    public DateTime CapturedDate { get; set; } = DateTime.UtcNow;

    [Column("likes")]
    public int Likes { get; set; } = 0;

    [Column("dislikes")]
    public int Dislikes { get; set; } = 0;
}