using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M3cLlmCallChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "chat_id",
                table: "llm_calls",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "topic_id",
                table: "llm_calls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "trigger_message_id",
                table: "llm_calls",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "chat_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "topic_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "trigger_message_id",
                table: "llm_calls");
        }
    }
}
