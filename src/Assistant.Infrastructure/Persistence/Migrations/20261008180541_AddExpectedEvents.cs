using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExpectedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "expectation_receipts",
                columns: table => new
                {
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    source_message_id = table.Column<int>(type: "integer", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    expectation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    draft_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expectation_receipts", x => new { x.bot_id, x.chat_id, x.source_message_id });
                });

            migrationBuilder.CreateTable(
                name: "expectations",
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
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    next_version = table.Column<int>(type: "integer", nullable: true),
                    last_version = table.Column<int>(type: "integer", nullable: false),
                    first_date = table.Column<DateOnly>(type: "date", nullable: true),
                    next_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    draft_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_telegram_message_id = table.Column<int>(type: "integer", nullable: true),
                    skipped_from = table.Column<DateOnly>(type: "date", nullable: true),
                    skipped_through = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expectations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "expectation_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    expectation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expected_revision = table.Column<long>(type: "bigint", nullable: false),
                    expected_current_version = table.Column<int>(type: "integer", nullable: false),
                    deadline_minute = table.Column<int>(type: "integer", nullable: false),
                    grace_minutes = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    preview_started = table.Column<bool>(type: "boolean", nullable: false),
                    preview_message_id = table.Column<int>(type: "integer", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expectation_drafts", x => x.id);
                    table.ForeignKey(
                        name: "fk_expectation_drafts_expectations_expectation_id",
                        column: x => x.expectation_id,
                        principalTable: "expectations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expectation_versions",
                columns: table => new
                {
                    expectation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    deadline_minute = table.Column<int>(type: "integer", nullable: false),
                    grace_minutes = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("ak_expectation_version_expectation_id_number", x => new { x.expectation_id, x.number });
                    table.ForeignKey(
                        name: "fk_expectation_versions_expectations_expectation_id",
                        column: x => x.expectation_id,
                        principalTable: "expectations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expectation_occurrences",
                columns: table => new
                {
                    expectation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    matched_event_id = table.Column<long>(type: "bigint", nullable: true),
                    matched_event_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expectation_occurrences", x => new { x.expectation_id, x.local_date });
                    table.ForeignKey(
                        name: "fk_expectation_occurrences_expectation_version_expectation_id_",
                        columns: x => new { x.expectation_id, x.version },
                        principalTable: "expectation_versions",
                        principalColumns: new[] { "expectation_id", "number" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_expectation_occurrences_expectations_expectation_id",
                        column: x => x.expectation_id,
                        principalTable: "expectations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "expectation_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expectation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expectation_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_expectation_attempts_expectation_occurrences_expectation_id",
                        columns: x => new { x.expectation_id, x.local_date },
                        principalTable: "expectation_occurrences",
                        principalColumns: new[] { "expectation_id", "local_date" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_expectation_attempts_expectation_id_local_date",
                table: "expectation_attempts",
                columns: new[] { "expectation_id", "local_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_expectation_attempts_family_id_role_started_at",
                table: "expectation_attempts",
                columns: new[] { "family_id", "role", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_expectation_drafts_expectation_id",
                table: "expectation_drafts",
                column: "expectation_id",
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_expectation_drafts_family_id_updated_at",
                table: "expectation_drafts",
                columns: new[] { "family_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_expectation_occurrences_expectation_id_version",
                table: "expectation_occurrences",
                columns: new[] { "expectation_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_expectation_occurrences_family_id_updated_at",
                table: "expectation_occurrences",
                columns: new[] { "family_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_expectation_receipts_family_id_created_at",
                table: "expectation_receipts",
                columns: new[] { "family_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_expectations_family_id_bot_db_id_bot_id_role_chat_id_topic_",
                table: "expectations",
                columns: new[] { "family_id", "bot_db_id", "bot_id", "role", "chat_id", "topic_id", "profile_id", "event_type" },
                unique: true,
                filter: "status IN ('draft', 'active', 'paused')")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_expectations_family_id_bot_db_id_chat_id_topic_id_actor_use",
                table: "expectations",
                columns: new[] { "family_id", "bot_db_id", "chat_id", "topic_id", "actor_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_expectations_family_id_bot_db_id_status_due_at",
                table: "expectations",
                columns: new[] { "family_id", "bot_db_id", "status", "due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expectation_attempts");

            migrationBuilder.DropTable(
                name: "expectation_drafts");

            migrationBuilder.DropTable(
                name: "expectation_receipts");

            migrationBuilder.DropTable(
                name: "expectation_occurrences");

            migrationBuilder.DropTable(
                name: "expectation_versions");

            migrationBuilder.DropTable(
                name: "expectations");
        }
    }
}
