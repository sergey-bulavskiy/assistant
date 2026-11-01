using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assistant.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedRecipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_recipes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<long>(type: "bigint", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    TopicId = table.Column<long>(type: "bigint", nullable: true),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    IngredientsJson = table.Column<string>(type: "jsonb", nullable: false),
                    StepsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_recipes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_saved_recipes_BotId_ChatId_MessageId",
                table: "saved_recipes",
                columns: new[] { "BotId", "ChatId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_saved_recipes_SubjectId_BotId_ChatId_TopicId_Id",
                table: "saved_recipes",
                columns: new[] { "SubjectId", "BotId", "ChatId", "TopicId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_recipes");
        }
    }
}
