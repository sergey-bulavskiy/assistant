using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthConsultationProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allergies",
                table: "health_profiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "conditions",
                table: "health_profiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "doctor_contacts",
                table: "health_profiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "doctor_plan",
                table: "health_profiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "medications",
                table: "health_profiles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allergies",
                table: "health_profiles");

            migrationBuilder.DropColumn(
                name: "conditions",
                table: "health_profiles");

            migrationBuilder.DropColumn(
                name: "doctor_contacts",
                table: "health_profiles");

            migrationBuilder.DropColumn(
                name: "doctor_plan",
                table: "health_profiles");

            migrationBuilder.DropColumn(
                name: "medications",
                table: "health_profiles");
        }
    }
}
