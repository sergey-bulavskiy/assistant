using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_profiles",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    subject_tag = table.Column<string>(type: "text", nullable: false, defaultValue: "health"),
                    stage_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    time_zone = table.Column<string>(type: "text", nullable: false, defaultValue: "UTC"),
                    emergency_phone = table.Column<string>(type: "text", nullable: false, defaultValue: "103 или 112"),
                    context_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "safety_rules",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    family_id = table.Column<long>(type: "bigint", nullable: false),
                    profile_id = table.Column<long>(type: "bigint", nullable: false),
                    rule_key = table.Column<string>(type: "text", nullable: false),
                    low_urgent = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    low_alert = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    target_high = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    high_alert = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    high_urgent = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    symptom_level = table.Column<string>(type: "text", nullable: true),
                    window_hours = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    updated_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_safety_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_safety_rules_health_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "health_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_health_profiles_bot_id",
                table: "health_profiles",
                column: "bot_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_safety_rules_profile_id_rule_key",
                table: "safety_rules",
                columns: new[] { "profile_id", "rule_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "safety_rules");

            migrationBuilder.DropTable(
                name: "health_profiles");
        }
    }
}
