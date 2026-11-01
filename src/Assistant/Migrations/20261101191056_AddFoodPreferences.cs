using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "food_preferences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<long>(type: "bigint", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    TopicId = table.Column<long>(type: "bigint", nullable: true),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_food_preferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_food_preferences_BotId_ChatId_MessageId",
                table: "food_preferences",
                columns: new[] { "BotId", "ChatId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_food_preferences_SubjectId_BotId_ChatId_TopicId_Id",
                table: "food_preferences",
                columns: new[] { "SubjectId", "BotId", "ChatId", "TopicId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "food_preferences");
        }
    }
}
