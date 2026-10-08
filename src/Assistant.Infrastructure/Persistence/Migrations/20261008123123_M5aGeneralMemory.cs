using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5aGeneralMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE INDEX ix_messages_memory_russian ON messages USING gin(to_tsvector('russian', text)) WHERE kind = 'Text' AND direction = 'In' AND text IS NOT NULL;");
            migrationBuilder.Sql("CREATE INDEX ix_messages_memory_simple ON messages USING gin(to_tsvector('simple', text)) WHERE kind = 'Text' AND direction = 'In' AND text IS NOT NULL;");
            migrationBuilder.CreateTable(
                name: "general_memory_facts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    source_message_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    tag = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_by_user_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_general_memory_facts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "general_memory_states",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    reset_cutoff = table.Column<long>(type: "bigint", nullable: false),
                    through_message_id = table.Column<long>(type: "bigint", nullable: false),
                    source_version = table.Column<long>(type: "bigint", nullable: false),
                    summary_text = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_general_memory_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_general_memory_facts_family_id_bot_id_chat_id_topic_id",
                table: "general_memory_facts",
                columns: new[] { "family_id", "bot_id", "chat_id", "topic_id" });

            migrationBuilder.CreateIndex(
                name: "ix_general_memory_facts_family_id_bot_id_source_message_id",
                table: "general_memory_facts",
                columns: new[] { "family_id", "bot_id", "source_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_general_memory_states_family_id_bot_id_chat_id_topic_id",
                table: "general_memory_states",
                columns: new[] { "family_id", "bot_id", "chat_id", "topic_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_messages_memory_russian;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_messages_memory_simple;");
            migrationBuilder.DropTable(
                name: "general_memory_facts");

            migrationBuilder.DropTable(
                name: "general_memory_states");
        }
    }
}
