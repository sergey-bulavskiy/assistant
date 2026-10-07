using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VetPhotoReviewProofBounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "selection_json",
                table: "vet_photo_runs",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2097152)",
                oldMaxLength: 2097152);

            migrationBuilder.AlterColumn<string>(
                name: "selection_json",
                table: "vet_photo_reviews",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2097152)",
                oldMaxLength: 2097152);

            migrationBuilder.AddColumn<long>(
                name: "profile_id",
                table: "vet_photo_reviews",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "profile_revision",
                table: "vet_photo_reviews",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_vet_photo_reviews_profile_id",
                table: "vet_photo_reviews",
                column: "profile_id");

            migrationBuilder.AddForeignKey(
                name: "fk_vet_photo_reviews_vet_profiles_profile_id",
                table: "vet_photo_reviews",
                column: "profile_id",
                principalTable: "vet_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_vet_photo_reviews_vet_profiles_profile_id",
                table: "vet_photo_reviews");

            migrationBuilder.DropIndex(
                name: "ix_vet_photo_reviews_profile_id",
                table: "vet_photo_reviews");

            migrationBuilder.DropColumn(
                name: "profile_id",
                table: "vet_photo_reviews");

            migrationBuilder.DropColumn(
                name: "profile_revision",
                table: "vet_photo_reviews");

            migrationBuilder.AlterColumn<string>(
                name: "selection_json",
                table: "vet_photo_runs",
                type: "character varying(2097152)",
                maxLength: 2097152,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "selection_json",
                table: "vet_photo_reviews",
                type: "character varying(2097152)",
                maxLength: 2097152,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
