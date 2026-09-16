using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NuctiusNow.Migrations
{
    /// <inheritdoc />
    public partial class FixDateTimeUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
           // migrationBuilder.DropTable(
             //   name: "Noticias");

            migrationBuilder.CreateTable(
                name: "detail_news",
                columns: table => new
                {
                    id_detail_news = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    context = table.Column<string>(type: "text", nullable: true),
                    keyword = table.Column<string>(type: "text", nullable: true),
                    news_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ai_summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_detail_news", x => x.id_detail_news);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    id_tag = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.id_tag);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id_user = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "text", nullable: false),
                    password = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    birthday = table.Column<DateTime>(type: "timestamp", rowVersion: true, nullable: true),
                    language = table.Column<int>(type: "integer", nullable: false),
                    is_premium = table.Column<bool>(type: "boolean", nullable: false),
                    is_banned = table.Column<bool>(type: "boolean", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id_user);
                });

            migrationBuilder.CreateTable(
                name: "news",
                columns: table => new
                {
                    id_news = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tag = table.Column<int>(type: "integer", nullable: false),
                    id_detail_news = table.Column<int>(type: "integer", nullable: false),
                    hyperlink = table.Column<string>(type: "text", nullable: false),
                    is_sfw = table.Column<bool>(type: "boolean", nullable: false),
                    secure_link = table.Column<bool>(type: "boolean", nullable: false),
                    is_ai = table.Column<bool>(type: "boolean", nullable: false),
                    captured_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    likes = table.Column<int>(type: "integer", nullable: false),
                    dislikes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news", x => x.id_news);
                    table.ForeignKey(
                        name: "FK_news_detail_news_id_detail_news",
                        column: x => x.id_detail_news,
                        principalTable: "detail_news",
                        principalColumn: "id_detail_news",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_news_tags_id_tag",
                        column: x => x.id_tag,
                        principalTable: "tags",
                        principalColumn: "id_tag",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "preferences",
                columns: table => new
                {
                    id_preference = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_tag = table.Column<int>(type: "integer", nullable: false),
                    preference = table.Column<string>(type: "text", nullable: true),
                    weight = table.Column<int>(type: "integer", nullable: false),
                    is_sfw = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preferences", x => x.id_preference);
                    table.ForeignKey(
                        name: "FK_preferences_tags_id_tag",
                        column: x => x.id_tag,
                        principalTable: "tags",
                        principalColumn: "id_tag",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "news_comments",
                columns: table => new
                {
                    id_comment = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_news = table.Column<int>(type: "integer", nullable: false),
                    id_user = table.Column<int>(type: "integer", nullable: false),
                    comment_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    likes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_news_comments", x => x.id_comment);
                    table.ForeignKey(
                        name: "FK_news_comments_news_id_news",
                        column: x => x.id_news,
                        principalTable: "news",
                        principalColumn: "id_news",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_news_comments_users_id_user",
                        column: x => x.id_user,
                        principalTable: "users",
                        principalColumn: "id_user",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_news",
                columns: table => new
                {
                    id_user_news = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_user = table.Column<int>(type: "integer", nullable: false),
                    id_news = table.Column<int>(type: "integer", nullable: false),
                    seen = table.Column<bool>(type: "boolean", nullable: false),
                    interest = table.Column<bool>(type: "boolean", nullable: false),
                    date_seen = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_news", x => x.id_user_news);
                    table.ForeignKey(
                        name: "FK_user_news_news_id_news",
                        column: x => x.id_news,
                        principalTable: "news",
                        principalColumn: "id_news",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_news_users_id_user",
                        column: x => x.id_user,
                        principalTable: "users",
                        principalColumn: "id_user",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_preference",
                columns: table => new
                {
                    id_user_preference = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_user = table.Column<int>(type: "integer", nullable: false),
                    id_preference = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_preference", x => x.id_user_preference);
                    table.ForeignKey(
                        name: "FK_user_preference_preferences_id_preference",
                        column: x => x.id_preference,
                        principalTable: "preferences",
                        principalColumn: "id_preference",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_preference_users_id_user",
                        column: x => x.id_user,
                        principalTable: "users",
                        principalColumn: "id_user",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_news_id_detail_news",
                table: "news",
                column: "id_detail_news");

            migrationBuilder.CreateIndex(
                name: "IX_news_id_tag",
                table: "news",
                column: "id_tag");

            migrationBuilder.CreateIndex(
                name: "IX_news_comments_id_news",
                table: "news_comments",
                column: "id_news");

            migrationBuilder.CreateIndex(
                name: "IX_news_comments_id_user",
                table: "news_comments",
                column: "id_user");

            migrationBuilder.CreateIndex(
                name: "IX_preferences_id_tag",
                table: "preferences",
                column: "id_tag");

            migrationBuilder.CreateIndex(
                name: "IX_user_news_id_news",
                table: "user_news",
                column: "id_news");

            migrationBuilder.CreateIndex(
                name: "IX_user_news_id_user",
                table: "user_news",
                column: "id_user");

            migrationBuilder.CreateIndex(
                name: "IX_user_preference_id_preference",
                table: "user_preference",
                column: "id_preference");

            migrationBuilder.CreateIndex(
                name: "IX_user_preference_id_user",
                table: "user_preference",
                column: "id_user");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "news_comments");

            migrationBuilder.DropTable(
                name: "user_news");

            migrationBuilder.DropTable(
                name: "user_preference");

            migrationBuilder.DropTable(
                name: "news");

            migrationBuilder.DropTable(
                name: "preferences");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "detail_news");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.CreateTable(
                name: "Noticias",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Categoria = table.Column<string>(type: "text", nullable: false),
                    CuerpoHtml = table.Column<string>(type: "text", nullable: false),
                    Fecha = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImagenUrl = table.Column<string>(type: "text", nullable: false),
                    Titulo = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Noticias", x => x.Id);
                });
        }
    }
}
