using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivateDebugTraces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "debug_trace_coverage",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    retained_bytes = table.Column<long>(type: "bigint", nullable: false),
                    evicted_count = table.Column<long>(type: "bigint", nullable: false),
                    last_evicted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debug_trace_coverage", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "debug_traces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    update_id = table.Column<long>(type: "bigint", nullable: true),
                    source_message_id = table.Column<long>(type: "bigint", nullable: true),
                    is_edit = table.Column<bool>(type: "boolean", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    build_identity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accounted_bytes = table.Column<long>(type: "bigint", nullable: false),
                    payload_bytes = table.Column<long>(type: "bigint", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    omitted_event_count = table.Column<int>(type: "integer", nullable: false),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    redacted = table.Column<bool>(type: "boolean", nullable: false),
                    final_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_disposition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debug_traces", x => x.id);
                    table.ForeignKey(
                        name: "fk_debug_traces_messages_source_message_id",
                        column: x => x.source_message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "debug_trace_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    trace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    source_message_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    llm_call_id = table.Column<long>(type: "bigint", nullable: true),
                    pending_record_id = table.Column<long>(type: "bigint", nullable: true),
                    related_source_message_id = table.Column<long>(type: "bigint", nullable: true),
                    actor_id = table.Column<long>(type: "bigint", nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: false),
                    payload_bytes = table.Column<int>(type: "integer", nullable: false),
                    accounted_bytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debug_trace_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_debug_trace_events_debug_traces_trace_id",
                        column: x => x.trace_id,
                        principalTable: "debug_traces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_debug_trace_events_llm_calls_llm_call_id",
                        column: x => x.llm_call_id,
                        principalTable: "llm_calls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_debug_trace_events_llm_call_id",
                table: "debug_trace_events",
                column: "llm_call_id");

            migrationBuilder.CreateIndex(
                name: "ix_debug_trace_events_related_source_message_id",
                table: "debug_trace_events",
                column: "related_source_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_debug_trace_events_source_message_id",
                table: "debug_trace_events",
                column: "source_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_debug_trace_events_trace_id_id",
                table: "debug_trace_events",
                columns: new[] { "trace_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_debug_traces_bot_id_update_id",
                table: "debug_traces",
                columns: new[] { "bot_id", "update_id" },
                unique: true,
                filter: "update_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_debug_traces_created_at_id",
                table: "debug_traces",
                columns: new[] { "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_debug_traces_source_message_id",
                table: "debug_traces",
                column: "source_message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "debug_trace_coverage");

            migrationBuilder.DropTable(
                name: "debug_trace_events");

            migrationBuilder.DropTable(
                name: "debug_traces");
        }
    }
}
