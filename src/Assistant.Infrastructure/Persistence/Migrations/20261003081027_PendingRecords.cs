using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PendingRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    source_message_id = table.Column<long>(type: "bigint", nullable: true),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: false),
                    prompt_message_id = table.Column<int>(type: "integer", nullable: true),
                    requested_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    events = table.Column<string>(type: "jsonb", nullable: false),
                    alerted_rule_keys = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    status = table.Column<string>(type: "text", nullable: false),
                    resolved_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_pending_records_health_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "health_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_records_family_id_source_message_id",
                table: "pending_records",
                columns: new[] { "family_id", "source_message_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_records_profile_id",
                table: "pending_records",
                column: "profile_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_records");
        }
    }
}
