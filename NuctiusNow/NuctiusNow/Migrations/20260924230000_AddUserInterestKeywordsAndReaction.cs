using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NuctiusNow.Migrations;

public partial class AddUserInterestKeywordsAndReaction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "reaction",
            table: "user_news",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "user_interest_keywords",
            columns: table => new
            {
                id_user_interest_keyword = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                id_user = table.Column<int>(type: "integer", nullable: false),
                keyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                weight = table.Column<int>(type: "integer", nullable: false),
                last_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_interest_keywords", x => x.id_user_interest_keyword);
                table.ForeignKey(
                    name: "FK_user_interest_keywords_users_id_user",
                    column: x => x.id_user,
                    principalTable: "users",
                    principalColumn: "id_user",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_user_interest_keywords_id_user",
            table: "user_interest_keywords",
            column: "id_user");

        migrationBuilder.CreateIndex(
            name: "IX_user_interest_keywords_id_user_keyword",
            table: "user_interest_keywords",
            columns: new[] { "id_user", "keyword" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "user_interest_keywords");

        migrationBuilder.DropColumn(
            name: "reaction",
            table: "user_news");
    }
}
