using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_document_admissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_db_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    chat_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: false),
                    sender_user_id = table.Column<long>(type: "bigint", nullable: true),
                    first_update_id = table.Column<long>(type: "bigint", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    file_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    file_unique_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    caption = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    source_message_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reaction_attempted = table.Column<bool>(type: "boolean", nullable: false),
                    notice_attempted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_document_admissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_document_admissions_bots_bot_db_id",
                        column: x => x.bot_db_id,
                        principalTable: "bots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_health_document_admissions_health_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "health_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_health_document_admissions_messages_source_message_id",
                        column: x => x.source_message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_message_id = table.Column<long>(type: "bigint", nullable: false),
                    telegram_file_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    caption = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    text = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: true),
                    posted_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    text_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    text_failure_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    text_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_documents_health_document_admissions_admission_id",
                        column: x => x.admission_id,
                        principalTable: "health_document_admissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_health_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "health_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_messages_source_message_id",
                        column: x => x.source_message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_admission_id",
                table: "documents",
                column: "admission_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_family_id_profile_id_deleted_at_posted_at_id",
                table: "documents",
                columns: new[] { "family_id", "profile_id", "deleted_at", "posted_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_family_id_profile_id_source_message_id",
                table: "documents",
                columns: new[] { "family_id", "profile_id", "source_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_profile_id",
                table: "documents",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_source_message_id",
                table: "documents",
                column: "source_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_document_admissions_bot_db_id",
                table: "health_document_admissions",
                column: "bot_db_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_document_admissions_family_id_bot_db_id_status_next_",
                table: "health_document_admissions",
                columns: new[] { "family_id", "bot_db_id", "status", "next_attempt_at", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_health_document_admissions_family_id_telegram_bot_id_chat_i",
                table: "health_document_admissions",
                columns: new[] { "family_id", "telegram_bot_id", "chat_id", "telegram_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_health_document_admissions_profile_id",
                table: "health_document_admissions",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_document_admissions_source_message_id",
                table: "health_document_admissions",
                column: "source_message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "health_document_admissions");
        }
    }
}
