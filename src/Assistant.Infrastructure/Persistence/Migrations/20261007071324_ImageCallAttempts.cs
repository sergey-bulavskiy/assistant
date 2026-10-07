using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImageCallAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "attempt_key",
                table: "llm_calls",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_attempt_key",
                table: "llm_calls",
                column: "attempt_key",
                unique: true,
                filter: "attempt_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_llm_calls_attempt_key",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "attempt_key",
                table: "llm_calls");
        }
    }
}
