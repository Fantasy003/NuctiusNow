using Microsoft.EntityFrameworkCore.Infrastructure;
using NuctiusNow.Data;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NuctiusNow.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261001050000_AddEmailVerificationAndBlockedSources")]
public partial class AddEmailVerificationAndBlockedSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "email_verified", table: "users", type: "boolean",
            nullable: false, defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "email_verification_token_hash", table: "users", type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "email_verification_expires_at", table: "users",
            type: "timestamp with time zone", nullable: true);

        migrationBuilder.Sql("UPDATE users SET email_verified = TRUE;");

        migrationBuilder.CreateTable(
            name: "user_blocked_sources",
            columns: table => new
            {
                id_user_blocked_source = table.Column<int>(
                    type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                id_user = table.Column<int>(type: "integer", nullable: false),
                source_domain = table.Column<string>(
                    type: "character varying(160)", maxLength: 160, nullable: false),
                created_at = table.Column<DateTime>(
                    type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_blocked_sources", x => x.id_user_blocked_source);
                table.ForeignKey(
                    name: "FK_user_blocked_sources_users_id_user",
                    column: x => x.id_user,
                    principalTable: "users",
                    principalColumn: "id_user",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_user_blocked_sources_id_user",
            table: "user_blocked_sources", column: "id_user");

        migrationBuilder.CreateIndex(
            name: "IX_user_blocked_sources_id_user_source_domain",
            table: "user_blocked_sources",
            columns: new[] { "id_user", "source_domain" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "user_blocked_sources");
        migrationBuilder.DropColumn(name: "email_verified", table: "users");
        migrationBuilder.DropColumn(name: "email_verification_token_hash", table: "users");
        migrationBuilder.DropColumn(name: "email_verification_expires_at", table: "users");
    }
}
