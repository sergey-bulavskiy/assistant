using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVetTextDiary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vet_diary_actions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_decision_id = table.Column<long>(type: "bigint", nullable: true),
                    photo_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reverses_action_id = table.Column<long>(type: "bigint", nullable: true),
                    reversed_by_action_id = table.Column<long>(type: "bigint", nullable: true),
                    outcome_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_diary_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vet_pending_decisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_user_id = table.Column<long>(type: "bigint", nullable: false),
                    review_revision = table.Column<int>(type: "integer", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    proposal_json = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    prompt_message_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_by_user_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_pending_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vet_profiles",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    glucose_unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    insulin_unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    insulin_product = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    owner_context_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reported_vet_guidance = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    field_provenance_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vet_text_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    chat_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: false),
                    source_slot = table.Column<int>(type: "integer", nullable: false),
                    source_author_user_id = table.Column<long>(type: "bigint", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_message_db_id = table.Column<long>(type: "bigint", nullable: true),
                    current_input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_ordinal = table.Column<int>(type: "integer", nullable: false),
                    reply_to_message_id = table.Column<int>(type: "integer", nullable: true),
                    reply_to_user_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_text_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vet_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<decimal>(type: "numeric", nullable: false),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    local_time = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    time_zone_snapshot = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    occurred_at_source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    value_unit_source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_ordinal = table.Column<int>(type: "integer", nullable: false),
                    text_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    photo_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    photo_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_author_user_id = table.Column<long>(type: "bigint", nullable: false),
                    source_message_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delete_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    deleted_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    last_mutation_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_events_vet_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "vet_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_text_source_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    update_id = table.Column<long>(type: "bigint", nullable: false),
                    admitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_edit = table.Column<bool>(type: "boolean", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    extraction_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    explicit_retry_count = table.Column<int>(type: "integer", nullable: false),
                    failure_category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    answer_text = table.Column<string>(type: "text", nullable: true),
                    answer_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    work_json = table.Column<string>(type: "text", nullable: true),
                    history_json = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_text_source_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_text_source_revisions_vet_text_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "vet_text_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_diary_action_changes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    action_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    before_revision = table.Column<int>(type: "integer", nullable: true),
                    after_revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_diary_action_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_diary_action_changes_vet_diary_actions_action_id",
                        column: x => x.action_id,
                        principalTable: "vet_diary_actions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_diary_action_changes_vet_events_event_id",
                        column: x => x.event_id,
                        principalTable: "vet_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_extraction_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    json = table.Column<string>(type: "jsonb", nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_extraction_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_extraction_results_vet_text_source_revisions_input_revi",
                        column: x => x.input_revision_id,
                        principalTable: "vet_text_source_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vet_diary_action_changes_action_id_event_id",
                table: "vet_diary_action_changes",
                columns: new[] { "action_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_diary_action_changes_event_id",
                table: "vet_diary_action_changes",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_diary_actions_family_id_bot_db_id_chat_id_topic_id_acto",
                table: "vet_diary_actions",
                columns: new[] { "family_id", "bot_db_id", "chat_id", "topic_id", "actor_user_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_diary_actions_family_id_bot_db_id_operation_key",
                table: "vet_diary_actions",
                columns: new[] { "family_id", "bot_db_id", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_events_family_id_bot_db_id_chat_id_topic_id_source_id",
                table: "vet_events",
                columns: new[] { "family_id", "bot_db_id", "chat_id", "topic_id", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_events_family_id_bot_db_id_profile_id_occurred_at_id",
                table: "vet_events",
                columns: new[] { "family_id", "bot_db_id", "profile_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_events_family_id_bot_db_id_source_kind_source_id_event_",
                table: "vet_events",
                columns: new[] { "family_id", "bot_db_id", "source_kind", "source_id", "event_type", "candidate_ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_events_profile_id",
                table: "vet_events",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_extraction_results_input_revision_id",
                table: "vet_extraction_results",
                column: "input_revision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_pending_decisions_family_id_bot_db_id_chat_id_topic_id_",
                table: "vet_pending_decisions",
                columns: new[] { "family_id", "bot_db_id", "chat_id", "topic_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_pending_decisions_input_revision_id",
                table: "vet_pending_decisions",
                column: "input_revision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_profiles_family_id_bot_db_id",
                table: "vet_profiles",
                columns: new[] { "family_id", "bot_db_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_text_source_revisions_family_id_bot_db_id_update_id",
                table: "vet_text_source_revisions",
                columns: new[] { "family_id", "bot_db_id", "update_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_text_source_revisions_source_id_ordinal",
                table: "vet_text_source_revisions",
                columns: new[] { "source_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_text_sources_family_id_bot_db_id_telegram_bot_id_chat_i",
                table: "vet_text_sources",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id", "telegram_message_id", "source_slot" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vet_diary_action_changes");

            migrationBuilder.DropTable(
                name: "vet_extraction_results");

            migrationBuilder.DropTable(
                name: "vet_pending_decisions");

            migrationBuilder.DropTable(
                name: "vet_diary_actions");

            migrationBuilder.DropTable(
                name: "vet_events");

            migrationBuilder.DropTable(
                name: "vet_text_source_revisions");

            migrationBuilder.DropTable(
                name: "vet_profiles");

            migrationBuilder.DropTable(
                name: "vet_text_sources");
        }
    }
}
