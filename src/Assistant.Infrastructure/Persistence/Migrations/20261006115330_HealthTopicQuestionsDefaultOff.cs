using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthTopicQuestionsDefaultOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Health ignored this flag before this release. Clear old unused values once so
            // deployment does not silently enable passive answers for existing places.
            migrationBuilder.Sql("""
                UPDATE places AS p
                SET reply_to_all = false
                FROM bots AS b
                WHERE p.bot_id = b.id
                  AND lower(regexp_replace(b.role, '^[[:space:]]+|[[:space:]]+$', '', 'g')) = 'health'
                  AND p.reply_to_all = true;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Previous unused values cannot be reconstructed.
        }
    }
}
