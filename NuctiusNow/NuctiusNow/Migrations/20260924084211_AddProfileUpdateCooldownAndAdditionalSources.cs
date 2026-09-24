using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NuctiusNow.Migrations;

public partial class AddProfileUpdateCooldownAndAdditionalSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "additional_sources_json",
            table: "detail_news",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "last_profile_update_at",
            table: "users",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "additional_sources_json",
            table: "detail_news");

        migrationBuilder.DropColumn(
            name: "last_profile_update_at",
            table: "users");
    }
}
