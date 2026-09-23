using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NuctiusNow.Migrations
{
    /// <inheritdoc />
    public partial class AddArticleBodySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "body_source",
                table: "detail_news",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "body_source",
                table: "detail_news");
        }
    }
}
