using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VetMeterPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vet_photo_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_revision = table.Column<int>(type: "integer", nullable: false),
                    starter_user_id = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    intake_kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    review_revision = table.Column<int>(type: "integer", nullable: false),
                    next_item_number = table.Column<int>(type: "integer", nullable: false),
                    progress_message_id = table.Column<int>(type: "integer", nullable: true),
                    assumptions_json = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    intake_opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    intake_closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_batches_vet_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "vet_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_blobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actual_bytes = table.Column<long>(type: "bigint", nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reclaim_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reclaimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_blobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposed_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: false),
                    source_slot = table.Column<int>(type: "integer", nullable: false),
                    chat_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_author_user_id = table.Column<long>(type: "bigint", nullable: false),
                    source_message_db_id = table.Column<long>(type: "bigint", nullable: true),
                    media_group_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    reply_to_message_id = table.Column<int>(type: "integer", nullable: true),
                    item_number = table.Column<int>(type: "integer", nullable: true),
                    association = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    current_input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_ordinal = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    admitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_sources_vet_photo_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "vet_photo_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_sources_vet_photo_batches_proposed_batch_id",
                        column: x => x.proposed_batch_id,
                        principalTable: "vet_photo_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_input_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    update_id = table.Column<long>(type: "bigint", nullable: false),
                    is_edit = table.Column<bool>(type: "boolean", nullable: false),
                    file_id = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    file_unique_id = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    reported_mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reported_size = table.Column<long>(type: "bigint", nullable: true),
                    reported_width = table.Column<int>(type: "integer", nullable: true),
                    reported_height = table.Column<int>(type: "integer", nullable: true),
                    caption = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    text_input_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reuses_image_input_id = table.Column<Guid>(type: "uuid", nullable: true),
                    input_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_input_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_input_revisions_vet_photo_input_revisions_reuses_",
                        column: x => x.reuses_image_input_id,
                        principalTable: "vet_photo_input_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_input_revisions_vet_photo_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "vet_photo_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_original_references",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    actual_bytes = table.Column<long>(type: "bigint", nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    retained_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    deletion_review_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_original_references", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_original_references_vet_photo_blobs_blob_id",
                        column: x => x.blob_id,
                        principalTable: "vet_photo_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_original_references_vet_photo_input_revisions_inp",
                        column: x => x.input_revision_id,
                        principalTable: "vet_photo_input_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_window_id = table.Column<Guid>(type: "uuid", nullable: true),
                    extraction_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reserved_bytes = table.Column<long>(type: "bigint", nullable: false),
                    reserved_input_slot = table.Column<bool>(type: "boolean", nullable: false),
                    reserved_result_slot = table.Column<bool>(type: "boolean", nullable: false),
                    download_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    expected_source_ordinal = table.Column<int>(type: "integer", nullable: false),
                    expected_current_input_id = table.Column<Guid>(type: "uuid", nullable: false),
                    historical_selection = table.Column<bool>(type: "boolean", nullable: false),
                    failure_category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    retry_not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_attempts_vet_photo_input_revisions_input_revision",
                        column: x => x.input_revision_id,
                        principalTable: "vet_photo_input_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_attempts_vet_photo_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "vet_photo_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_extractions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reuses_extraction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    structured_json = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    failure_category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    diagnostic_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_extractions", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_extractions_vet_photo_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "vet_photo_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_extractions_vet_photo_extractions_reuses_extracti",
                        column: x => x.reuses_extraction_id,
                        principalTable: "vet_photo_extractions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_extractions_vet_photo_input_revisions_input_revis",
                        column: x => x.input_revision_id,
                        principalTable: "vet_photo_input_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_extractions_vet_photo_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "vet_photo_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_reader_leases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    blob_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_reference_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_reference_revision = table.Column<int>(type: "integer", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_reader_leases", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_reader_leases_vet_photo_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "vet_photo_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_reader_leases_vet_photo_blobs_blob_id",
                        column: x => x.blob_id,
                        principalTable: "vet_photo_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_reader_leases_vet_photo_original_references_origi",
                        column: x => x.original_reference_id,
                        principalTable: "vet_photo_original_references",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_ordinal = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    input_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    extraction_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    requires_explicit_restoration = table.Column<bool>(type: "boolean", nullable: false),
                    manually_corrected = table.Column<bool>(type: "boolean", nullable: false),
                    correction_provenance_json = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    effective_json = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    reasons_json = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    duplicate_decision = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    duplicate_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duplicate_event_id = table.Column<long>(type: "bigint", nullable: true),
                    duplicate_event_revision = table.Column<int>(type: "integer", nullable: true),
                    event_id = table.Column<long>(type: "bigint", nullable: true),
                    event_revision = table.Column<int>(type: "integer", nullable: true),
                    last_review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_candidates", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_candidates_vet_events_event_id",
                        column: x => x.event_id,
                        principalTable: "vet_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_candidates_vet_photo_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "vet_photo_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_candidates_vet_photo_extractions_extraction_resul",
                        column: x => x.extraction_result_id,
                        principalTable: "vet_photo_extractions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_candidates_vet_photo_input_revisions_input_revisi",
                        column: x => x.input_revision_id,
                        principalTable: "vet_photo_input_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_candidates_vet_photo_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "vet_photo_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    run_window_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    batch_review_revision = table.Column<int>(type: "integer", nullable: true),
                    requester_user_id = table.Column<long>(type: "bigint", nullable: false),
                    decision_actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    selection_json = table.Column<string>(type: "character varying(2097152)", maxLength: 2097152, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    preview_pages_json = table.Column<string>(type: "character varying(4194304)", maxLength: 4194304, nullable: false),
                    delivered_pages_json = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: false),
                    complete_preview_delivered = table.Column<bool>(type: "boolean", nullable: false),
                    acceptance_prompt_message_id = table.Column<int>(type: "integer", nullable: true),
                    action_id = table.Column<long>(type: "bigint", nullable: true),
                    outcome_json = table.Column<string>(type: "character varying(2097152)", maxLength: 2097152, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_reviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_reviews_vet_diary_actions_action_id",
                        column: x => x.action_id,
                        principalTable: "vet_diary_actions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_reviews_vet_photo_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "vet_photo_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    operation_key = table.Column<Guid>(type: "uuid", nullable: false),
                    selection_review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selection_mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    selection_json = table.Column<string>(type: "character varying(2097152)", maxLength: 2097152, nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    next_window_ordinal = table.Column<int>(type: "integer", nullable: false),
                    selected_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_runs_vet_photo_reviews_selection_review_id",
                        column: x => x.selection_review_id,
                        principalTable: "vet_photo_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vet_photo_run_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    selection_json = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    comparison_review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vet_photo_run_windows", x => x.id);
                    table.ForeignKey(
                        name: "fk_vet_photo_run_windows_vet_diary_actions_action_id",
                        column: x => x.action_id,
                        principalTable: "vet_diary_actions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vet_photo_run_windows_vet_photo_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "vet_photo_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_attempts_family_id_bot_db_id_telegram_bot_id_chat",
                table: "vet_photo_attempts",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_attempts_input_revision_id",
                table: "vet_photo_attempts",
                column: "input_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_attempts_kind_state_retry_not_before_created_at",
                table: "vet_photo_attempts",
                columns: new[] { "kind", "state", "retry_not_before", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_attempts_run_window_id",
                table: "vet_photo_attempts",
                column: "run_window_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_attempts_source_id",
                table: "vet_photo_attempts",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_batches_family_id_bot_db_id_telegram_bot_id_chat_",
                table: "vet_photo_batches",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" },
                unique: true,
                filter: "state = 'collecting'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_batches_profile_id",
                table: "vet_photo_batches",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_blobs_family_id_content_hash",
                table: "vet_photo_blobs",
                columns: new[] { "family_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_blobs_state_reclaim_requested_at",
                table: "vet_photo_blobs",
                columns: new[] { "state", "reclaim_requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_candidates_batch_id",
                table: "vet_photo_candidates",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_candidates_event_id",
                table: "vet_photo_candidates",
                column: "event_id",
                unique: true,
                filter: "event_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_candidates_extraction_result_id",
                table: "vet_photo_candidates",
                column: "extraction_result_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_candidates_family_id_bot_db_id_telegram_bot_id_ch",
                table: "vet_photo_candidates",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_candidates_input_revision_id",
                table: "vet_photo_candidates",
                column: "input_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_candidates_source_id_candidate_ordinal",
                table: "vet_photo_candidates",
                columns: new[] { "source_id", "candidate_ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_extractions_attempt_id",
                table: "vet_photo_extractions",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_extractions_family_id_bot_db_id_telegram_bot_id_c",
                table: "vet_photo_extractions",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_extractions_input_revision_id_created_at",
                table: "vet_photo_extractions",
                columns: new[] { "input_revision_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_extractions_reuses_extraction_id",
                table: "vet_photo_extractions",
                column: "reuses_extraction_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_extractions_source_id",
                table: "vet_photo_extractions",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_input_revisions_family_id_bot_db_id_telegram_bot_",
                table: "vet_photo_input_revisions",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_input_revisions_reuses_image_input_id",
                table: "vet_photo_input_revisions",
                column: "reuses_image_input_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_input_revisions_source_id_ordinal",
                table: "vet_photo_input_revisions",
                columns: new[] { "source_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_input_revisions_source_id_update_id",
                table: "vet_photo_input_revisions",
                columns: new[] { "source_id", "update_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_original_references_blob_id_state",
                table: "vet_photo_original_references",
                columns: new[] { "blob_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_original_references_family_id_bot_db_id_telegram_",
                table: "vet_photo_original_references",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_original_references_input_revision_id",
                table: "vet_photo_original_references",
                column: "input_revision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reader_leases_attempt_id_claim_token",
                table: "vet_photo_reader_leases",
                columns: new[] { "attempt_id", "claim_token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reader_leases_blob_id_released_at_expires_at",
                table: "vet_photo_reader_leases",
                columns: new[] { "blob_id", "released_at", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reader_leases_family_id_bot_db_id_telegram_bot_id",
                table: "vet_photo_reader_leases",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reader_leases_original_reference_id",
                table: "vet_photo_reader_leases",
                column: "original_reference_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reviews_action_id",
                table: "vet_photo_reviews",
                column: "action_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reviews_batch_id_state_created_at",
                table: "vet_photo_reviews",
                columns: new[] { "batch_id", "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reviews_family_id_bot_db_id_operation_key",
                table: "vet_photo_reviews",
                columns: new[] { "family_id", "bot_db_id", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reviews_family_id_bot_db_id_telegram_bot_id_chat_",
                table: "vet_photo_reviews",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reviews_run_window_id",
                table: "vet_photo_reviews",
                column: "run_window_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_run_windows_action_id",
                table: "vet_photo_run_windows",
                column: "action_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_run_windows_family_id_bot_db_id_telegram_bot_id_c",
                table: "vet_photo_run_windows",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_run_windows_run_id_ordinal",
                table: "vet_photo_run_windows",
                columns: new[] { "run_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_runs_family_id_bot_db_id_operation_key",
                table: "vet_photo_runs",
                columns: new[] { "family_id", "bot_db_id", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_runs_family_id_bot_db_id_telegram_bot_id_chat_id_",
                table: "vet_photo_runs",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_runs_selection_review_id",
                table: "vet_photo_runs",
                column: "selection_review_id");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_sources_batch_id_item_number",
                table: "vet_photo_sources",
                columns: new[] { "batch_id", "item_number" },
                unique: true,
                filter: "batch_id IS NOT NULL AND item_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_sources_family_id_bot_db_id_state_admitted_at",
                table: "vet_photo_sources",
                columns: new[] { "family_id", "bot_db_id", "state", "admitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_sources_family_id_bot_db_id_telegram_bot_id_chat_",
                table: "vet_photo_sources",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_sources_family_id_bot_db_id_telegram_bot_id_chat_1",
                table: "vet_photo_sources",
                columns: new[] { "family_id", "bot_db_id", "telegram_bot_id", "chat_id", "topic_id", "telegram_message_id", "source_slot" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_sources_proposed_batch_id",
                table: "vet_photo_sources",
                column: "proposed_batch_id");

            migrationBuilder.AddForeignKey(
                name: "fk_vet_photo_attempts_vet_photo_run_windows_run_window_id",
                table: "vet_photo_attempts",
                column: "run_window_id",
                principalTable: "vet_photo_run_windows",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_vet_photo_reviews_vet_photo_run_windows_run_window_id",
                table: "vet_photo_reviews",
                column: "run_window_id",
                principalTable: "vet_photo_run_windows",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_vet_photo_reviews_vet_photo_run_windows_run_window_id",
                table: "vet_photo_reviews");

            migrationBuilder.DropTable(
                name: "vet_photo_candidates");

            migrationBuilder.DropTable(
                name: "vet_photo_reader_leases");

            migrationBuilder.DropTable(
                name: "vet_photo_extractions");

            migrationBuilder.DropTable(
                name: "vet_photo_original_references");

            migrationBuilder.DropTable(
                name: "vet_photo_attempts");

            migrationBuilder.DropTable(
                name: "vet_photo_blobs");

            migrationBuilder.DropTable(
                name: "vet_photo_input_revisions");

            migrationBuilder.DropTable(
                name: "vet_photo_sources");

            migrationBuilder.DropTable(
                name: "vet_photo_run_windows");

            migrationBuilder.DropTable(
                name: "vet_photo_runs");

            migrationBuilder.DropTable(
                name: "vet_photo_reviews");

            migrationBuilder.DropTable(
                name: "vet_photo_batches");
        }
    }
}
