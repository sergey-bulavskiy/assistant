using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SafetyAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "safety_alerts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    rule_key = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    threshold = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    threshold_source = table.Column<string>(type: "text", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    topic_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_safety_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_safety_alerts_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_safety_alerts_event_id_rule_key",
                table: "safety_alerts",
                columns: new[] { "event_id", "rule_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "safety_alerts");
        }
    }
}
