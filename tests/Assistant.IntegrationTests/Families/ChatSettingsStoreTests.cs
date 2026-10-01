using Assistant.Application.Common;
using Assistant.Infrastructure.Families;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Families;

public class ChatSettingsStoreTests : IntegrationTestBase
{
    private const long FamilyId = 1;
    private const long BotId = 999;
    private const long ChatId = -100;

    private ChatSettingsStore CreateStore() => new(Db, new SystemClock());

    [Fact]
    public async Task Missing_row_reads_as_auto_with_no_cutoff()
    {
        var settings = await CreateStore().GetAsync(FamilyId, BotId, ChatId, null, CancellationToken.None);

        settings.PreferredModel.ShouldBeNull();
        settings.ContextStartMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task Preferred_model_round_trips_and_null_clears_it()
    {
        var store = CreateStore();

        await store.SetPreferredModelAsync(FamilyId, BotId, ChatId, null, "test-model", CancellationToken.None);
        (await store.GetAsync(FamilyId, BotId, ChatId, null, CancellationToken.None)).PreferredModel.ShouldBe("test-model");

        await store.SetPreferredModelAsync(FamilyId, BotId, ChatId, null, null, CancellationToken.None);
        (await store.GetAsync(FamilyId, BotId, ChatId, null, CancellationToken.None)).PreferredModel.ShouldBeNull();

        (await Db.ChatSettings.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Context_start_message_id_round_trips()
    {
        var store = CreateStore();

        await store.SetContextStartMessageIdAsync(FamilyId, BotId, ChatId, null, 1234, CancellationToken.None);

        (await store.GetAsync(FamilyId, BotId, ChatId, null, CancellationToken.None)).ContextStartMessageId.ShouldBe(1234);
    }

    [Fact]
    public async Task Setting_one_field_keeps_the_other()
    {
        var store = CreateStore();

        await store.SetContextStartMessageIdAsync(FamilyId, BotId, ChatId, null, 1234, CancellationToken.None);
        await store.SetPreferredModelAsync(FamilyId, BotId, ChatId, null, "test-model", CancellationToken.None);
        await store.SetContextStartMessageIdAsync(FamilyId, BotId, ChatId, null, 5678, CancellationToken.None);

        var settings = await store.GetAsync(FamilyId, BotId, ChatId, null, CancellationToken.None);
        settings.PreferredModel.ShouldBe("test-model");
        settings.ContextStartMessageId.ShouldBe(5678);
    }

    [Fact]
    public async Task Chat_wide_and_topic_settings_and_other_bots_are_separate_rows()
    {
        var store = CreateStore();

        await store.SetPreferredModelAsync(FamilyId, BotId, ChatId, null, "chat-wide", CancellationToken.None);
        await store.SetPreferredModelAsync(FamilyId, BotId, ChatId, 7, "topic-seven", CancellationToken.None);
        await store.SetPreferredModelAsync(FamilyId, BotId, ChatId, 8, "topic-eight", CancellationToken.None);
        await store.SetPreferredModelAsync(FamilyId, 998, ChatId, null, "other-bot", CancellationToken.None);

        (await store.GetAsync(FamilyId, BotId, ChatId, null, CancellationToken.None)).PreferredModel.ShouldBe("chat-wide");
        (await store.GetAsync(FamilyId, BotId, ChatId, 7, CancellationToken.None)).PreferredModel.ShouldBe("topic-seven");
        (await store.GetAsync(FamilyId, BotId, ChatId, 8, CancellationToken.None)).PreferredModel.ShouldBe("topic-eight");
        (await store.GetAsync(FamilyId, 998, ChatId, null, CancellationToken.None)).PreferredModel.ShouldBe("other-bot");
        (await Db.ChatSettings.IgnoreQueryFilters().CountAsync()).ShouldBe(4);
    }
}
