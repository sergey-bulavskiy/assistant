using System.Globalization;
using Assistant.Infrastructure.Common;
using TL;

namespace Assistant.SmokeTests;

/// <summary>
/// The smoke checklist as steps. Each step drives real Telegram through one test account and asserts
/// on replies/buttons and on rows in the stack's Postgres. Steps share state and must run in order.
/// Text and button labels are the bot's Russian UI strings (see ManagerUpdateHandler / ApprovalService).
/// Unknown-user approval and second-owner promotion need a second real user and are covered by the
/// integration tests (ApprovalServiceTests, ManagerUpdateHandlerCallbackTests) instead.
/// </summary>
public sealed class Scenario(SmokeConfig config, SmokeStack stack, TelegramUser owner)
{
    private InputPeer _manager = null!;
    private InputPeer _roleBot = null!;
    private InputPeer _group = null!;
    private InputPeer _forum = null!;
    private long _managerId;
    private long _roleBotId;
    private long _groupId;

    public async Task ClaimAsync()
    {
        _manager = await owner.ResolveUsernameAsync(config.ManagerUsername);
        _roleBot = await owner.ResolveUsernameAsync(config.RoleBotUsername);
        _managerId = ((InputPeerUser)_manager).user_id;
        _roleBotId = ((InputPeerUser)_roleBot).user_id;
        _group = await owner.FindChatAsync(config.GroupTitle);
        _forum = await owner.FindChatAsync(config.ForumTitle);
        _groupId = ChatId(_group);

        var code = await stack.ReadClaimCodeAsync();
        var mark = owner.Mark();
        await owner.SendAsync(_manager, $"/claim {code}");
        await owner.WaitForAsync(FromDm(_managerId, "Семья создана"), mark, "claim confirmation");

        (await stack.CountAsync("SELECT count(*) FROM families")).ShouldBe(1);
        (await stack.CountAsync(
            "SELECT count(*) FROM family_members WHERE telegram_user_id = @u AND is_owner AND status = 'Approved'",
            ("u", owner.Id))).ShouldBe(1);
    }

    /// <summary>
    /// Fallback for the managed-bot creation confirmation, which needs a human tap in Telegram's UI:
    /// a role bot created once by hand is inserted like /newbot would, then the app restarts to pick it up.
    /// </summary>
    public async Task SeedRoleBotAsync()
    {
        var familyId = Convert.ToInt64(await stack.ScalarAsync("SELECT id FROM families LIMIT 1"), CultureInfo.InvariantCulture);
        var encrypted = new TokenEncryptor(stack.EncryptionKey).Encrypt(config.RoleBotToken);
        await stack.ExecuteAsync(
            "INSERT INTO bots (family_id, telegram_bot_id, username, role, token_encrypted, status, last_update_id, created_at) " +
            "VALUES (@f, @t, @n, 'general', @e, 'Active', 0, now())",
            ("f", familyId),
            ("t", long.Parse(config.RoleBotToken.Split(':')[0], CultureInfo.InvariantCulture)),
            ("n", config.RoleBotUsername),
            ("e", encrypted));
        await stack.RestartAppAsync();
    }

    public async Task PrivateChatAndGroupAsync()
    {
        var dmMarker = Marker("dm");
        var mark = owner.Mark();
        await owner.SendAsync(_roleBot, dmMarker);
        await owner.WaitForAsync(FromDm(_roleBotId, "Получил"), mark, "private ack");
        (await stack.CountAsync("SELECT count(*) FROM messages WHERE text = @t", ("t", dmMarker))).ShouldBe(1);

        await ApprovePlaceAsync(_group, config.GroupTitle);

        var groupMarker = Marker("group");
        mark = owner.Mark();
        await owner.SendAsync(_group, $"@{config.RoleBotUsername} {groupMarker}");
        await EventuallyAsync(
            async () => await stack.CountAsync("SELECT count(*) FROM messages WHERE text LIKE @t", ("t", $"%{groupMarker}%")) == 1,
            "group message stored");
        await owner.AssertNoneAsync(m => m.peer_id.ID == _groupId && m.from_id?.ID == _roleBotId, mark, "role bot reply in group");
    }

    public async Task ForumTopicAsync()
    {
        await ApprovePlaceAsync(_forum, config.ForumTitle);

        var topicId = await owner.CreateForumTopicAsync(_forum, Marker("topic"));
        var mark = owner.Mark();
        await owner.SendAsync(_forum, $"@{config.RoleBotUsername} {Marker("topic-msg")}", topicId);
        var dm = await owner.WaitForAsync(FromDm(_managerId, "Первое сообщение в теме"), mark, "topic approval DM");
        await owner.TapAsync(_manager, dm, "Да");
        await EventuallyAsync(
            async () => await stack.CountAsync("SELECT count(*) FROM places WHERE topic_id = @t AND status = 'Approved'", ("t", topicId)) == 1,
            "topic place approved");
    }

    public async Task SettingsAsync()
    {
        var mark = owner.Mark();
        await owner.SendAsync(_manager, "/settings");
        var botMessage = await owner.WaitForAsync(FromDm(_managerId, $"Бот @{config.RoleBotUsername}"), mark, "settings: bot");
        var groupPlace = await owner.WaitForAsync(FromDm(_managerId, $"Место «{config.GroupTitle}»"), mark, "settings: group place");
        await owner.WaitForAsync(FromDm(_managerId, $"Место «{config.ForumTitle}»"), mark, "settings: forum place");
        await owner.WaitForAsync(FromDm(_managerId, "(владелец)"), mark, "settings: owner member");
        (await stack.CountAsync("SELECT count(*) FROM bots WHERE family_id IS NOT NULL")).ShouldBe(1);

        // Disable: the bot stops storing and replying.
        await owner.TapAsync(_manager, botMessage, "Отключить");
        await EventuallyAsync(async () => await StatusOfRoleBotAsync() == "Disabled", "bot disabled");
        var whileDisabled = Marker("while-disabled");
        mark = owner.Mark();
        await owner.SendAsync(_roleBot, whileDisabled);
        await owner.AssertNoneAsync(FromDm(_roleBotId, "Получил"), mark, "ack while disabled");
        (await stack.CountAsync("SELECT count(*) FROM messages WHERE text = @t", ("t", whileDisabled))).ShouldBe(0);

        // Enable: it resumes and picks up the message Telegram held for it.
        mark = owner.Mark();
        await owner.SendAsync(_manager, "/settings");
        var disabledBot = await owner.WaitForAsync(FromDm(_managerId, "отключен"), mark, "settings: disabled bot");
        await owner.TapAsync(_manager, disabledBot, "Включить");
        await EventuallyAsync(async () => await StatusOfRoleBotAsync() == "Active", "bot enabled");
        await EventuallyAsync(
            async () => await stack.CountAsync("SELECT count(*) FROM messages WHERE text = @t", ("t", whileDisabled)) == 1,
            "message sent while disabled is stored after enabling");

        // Remove a place: a new message from that chat asks for approval again.
        var placesBefore = await stack.CountAsync("SELECT count(*) FROM places");
        await owner.TapAsync(_manager, groupPlace, "Удалить");
        await EventuallyAsync(async () => await stack.CountAsync("SELECT count(*) FROM places") == placesBefore - 1, "place removed");
        mark = owner.Mark();
        await owner.SendAsync(_group, $"@{config.RoleBotUsername} {Marker("after-remove")}");
        await owner.WaitForAsync(FromDm(_managerId, $"добавлен в «{config.GroupTitle}»"), mark, "fresh approval after place removal");
    }

    public async Task RestartSurvivesAsync()
    {
        var before = Marker("before-restart");
        var mark = owner.Mark();
        await owner.SendAsync(_roleBot, before);
        await owner.WaitForAsync(FromDm(_roleBotId, "Получил"), mark, "ack before restart");

        mark = owner.Mark();
        await stack.RestartAppAsync();
        await owner.AssertNoneAsync(FromDm(_roleBotId, "Получил"), mark, "duplicate ack after restart", seconds: 15);
        (await stack.CountAsync("SELECT count(*) FROM messages WHERE text = @t", ("t", before))).ShouldBe(1);

        var after = Marker("after-restart");
        mark = owner.Mark();
        await owner.SendAsync(_roleBot, after);
        await owner.WaitForAsync(FromDm(_roleBotId, "Получил"), mark, "ack after restart");
        (await stack.CountAsync("SELECT count(*) FROM messages WHERE text = @t", ("t", after))).ShouldBe(1);
    }

    private async Task ApprovePlaceAsync(InputPeer chat, string title)
    {
        var mark = owner.Mark();
        await owner.ReAddBotAsync(chat, _roleBot);
        var dm = await owner.WaitForAsync(FromDm(_managerId, $"добавлен в «{title}»"), mark, $"place approval DM for '{title}'");
        await owner.TapAsync(_manager, dm, "Да");
        await EventuallyAsync(
            async () => await stack.CountAsync("SELECT count(*) FROM places WHERE title = @t AND topic_id IS NULL AND status = 'Approved'", ("t", title)) == 1,
            $"place '{title}' approved");
    }

    private async Task<string?> StatusOfRoleBotAsync() =>
        await stack.ScalarAsync("SELECT status FROM bots WHERE family_id IS NOT NULL LIMIT 1") as string;

    private static Func<Message, bool> FromDm(long botId, string textPart) =>
        m => !m.flags.HasFlag(Message.Flags.out_) && m.peer_id is PeerUser pu && pu.user_id == botId && m.message?.Contains(textPart) == true;

    private static bool HasButton(Message m, string label) =>
        m.reply_markup is ReplyInlineMarkup markup && markup.rows.SelectMany(r => r.buttons).Any(b => b.text == label);

    private static long ChatId(InputPeer peer) => peer switch
    {
        InputPeerChat chat => chat.chat_id,
        InputPeerChannel channel => channel.channel_id,
        _ => throw new InvalidOperationException("Expected a group or supergroup peer.")
    };

    private static string Marker(string kind) => $"smoke-{kind}-{Guid.NewGuid():N}";

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string description, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Condition not met within {seconds}s: {description}.");
    }
}
