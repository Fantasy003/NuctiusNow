using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NuctiusNow.Migrations
{
    /// <inheritdoc />
    public partial class AlignEmailVerificationSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schema is applied by AddEmailVerificationAndBlockedSources.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
