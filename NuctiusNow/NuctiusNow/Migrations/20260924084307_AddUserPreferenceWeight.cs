using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NuctiusNow.Migrations;

public partial class AddUserPreferenceWeight : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "weight",
            table: "user_preference",
            type: "integer",
            nullable: false,
            defaultValue: 1);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "weight",
            table: "user_preference");
    }
}
