using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reminder_preferences",
                columns: table => new
                {
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    quiet_start_minute = table.Column<int>(type: "integer", nullable: false),
                    quiet_end_minute = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminder_preferences", x => new { x.family_id, x.actor_user_id });
                });

            migrationBuilder.CreateTable(
                name: "reminder_settings_receipts",
                columns: table => new
                {
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    source_message_id = table.Column<int>(type: "integer", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminder_settings_receipts", x => new { x.bot_id, x.chat_id, x.source_message_id });
                });

            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    chat_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    source_message_id = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    daily_minute = table.Column<int>(type: "integer", nullable: true),
                    offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    preview_started = table.Column<bool>(type: "boolean", nullable: false),
                    preview_message_id = table.Column<int>(type: "integer", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_telegram_message_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reminder_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reminder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    occurrence_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminder_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_reminder_attempts_reminder_reminder_id",
                        column: x => x.reminder_id,
                        principalTable: "reminders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_attempts_family_id_role_started_at",
                table: "reminder_attempts",
                columns: new[] { "family_id", "role", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reminder_attempts_reminder_id_occurrence_due_at",
                table: "reminder_attempts",
                columns: new[] { "reminder_id", "occurrence_due_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reminder_settings_receipts_family_id_created_at",
                table: "reminder_settings_receipts",
                columns: new[] { "family_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reminders_bot_id_chat_id_source_message_id",
                table: "reminders",
                columns: new[] { "bot_id", "chat_id", "source_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reminders_family_id_bot_db_id_chat_id_topic_id_actor_user_id",
                table: "reminders",
                columns: new[] { "family_id", "bot_db_id", "chat_id", "topic_id", "actor_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reminders_family_id_bot_db_id_status_due_at",
                table: "reminders",
                columns: new[] { "family_id", "bot_db_id", "status", "due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminder_attempts");

            migrationBuilder.DropTable(
                name: "reminder_preferences");

            migrationBuilder.DropTable(
                name: "reminder_settings_receipts");

            migrationBuilder.DropTable(
                name: "reminders");
        }
    }
}
