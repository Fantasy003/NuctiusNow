using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NuctiusNow.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPreferenceTagNavigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_news_id_detail_news",
                table: "news");

            migrationBuilder.RenameColumn(
                name: "role",
                table: "users",
                newName: "password_hash");

            migrationBuilder.AlterColumn<string>(
                name: "language",
                table: "users",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<DateTime>(
                name: "birthday",
                table: "users",
                type: "timestamp",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp",
                oldRowVersion: true,
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "user_preference_tags",
                columns: table => new
                {
                    id_user_preference_tag = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_user_preference = table.Column<int>(type: "integer", nullable: false),
                    id_tag = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_preference_tags", x => x.id_user_preference_tag);
                    table.ForeignKey(
                        name: "FK_user_preference_tags_tags_id_tag",
                        column: x => x.id_tag,
                        principalTable: "tags",
                        principalColumn: "id_tag",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_preference_tags_user_preference_id_user_preference",
                        column: x => x.id_user_preference,
                        principalTable: "user_preference",
                        principalColumn: "id_user_preference",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_news_id_detail_news",
                table: "news",
                column: "id_detail_news",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_preference_tags_id_tag",
                table: "user_preference_tags",
                column: "id_tag");

            migrationBuilder.CreateIndex(
                name: "IX_user_preference_tags_id_user_preference",
                table: "user_preference_tags",
                column: "id_user_preference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_preference_tags");

            migrationBuilder.DropIndex(
                name: "IX_news_id_detail_news",
                table: "news");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "users");

            migrationBuilder.RenameColumn(
                name: "password_hash",
                table: "users",
                newName: "role");

            migrationBuilder.AlterColumn<int>(
                name: "language",
                table: "users",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<DateTime>(
                name: "birthday",
                table: "users",
                type: "timestamp",
                rowVersion: true,
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_news_id_detail_news",
                table: "news",
                column: "id_detail_news");
        }
    }
}
