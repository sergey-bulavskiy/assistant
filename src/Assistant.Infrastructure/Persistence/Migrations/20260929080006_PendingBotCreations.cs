using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PendingBotCreations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_bot_creations",
                columns: table => new
                {
                    creator_telegram_user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_bot_creations", x => x.creator_telegram_user_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_bot_creations");
        }
    }
}
