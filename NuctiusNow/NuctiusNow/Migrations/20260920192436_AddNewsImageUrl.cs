using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NuctiusNow.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsImageUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "detail_news",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image_url",
                table: "detail_news");
        }
    }
}
