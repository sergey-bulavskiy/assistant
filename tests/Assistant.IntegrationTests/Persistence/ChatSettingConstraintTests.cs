using Assistant.Domain.Llm;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Persistence;

public class ChatSettingConstraintTests : IntegrationTestBase
{
    [Fact]
    public async Task Two_chat_wide_settings_for_the_same_bot_and_chat_collide_on_null_topic_id()
    {
        // Postgres' default unique index treats every NULL as distinct; chat_settings must use
        // NULLS NOT DISTINCT (spec §4: "Unique (bot, chat, topic), nulls not distinct") so a second
        // chat-wide (TopicId == null) row for the same (bot, chat) is rejected, not silently
        // duplicated.
        Db.ChatSettings.Add(new ChatSetting { FamilyId = 1, BotId = 1, ChatId = 100, TopicId = null, UpdatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        Db.ChatSettings.Add(new ChatSetting { FamilyId = 1, BotId = 1, ChatId = 100, TopicId = null, UpdatedAt = DateTimeOffset.UtcNow });

        await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync());
    }
}
