using System.Text.Json;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Assistant.UnitTests.Infrastructure.Telegram;

public class TelegramUpdateMapperTests
{
    private static Update Parse(string json) =>
        JsonSerializer.Deserialize<Update>(json, JsonBotAPI.Options)
            ?? throw new InvalidOperationException("fixture failed to deserialize");

    [Fact]
    public void Maps_private_text_message()
    {
        var update = Parse("""
        {
          "update_id": 100001,
          "message": {
            "message_id": 501,
            "from": { "id": 111, "is_bot": false, "first_name": "Test", "username": "test_user" },
            "chat": { "id": 111, "type": "private", "first_name": "Test", "username": "test_user" },
            "date": 1735000000,
            "text": "тест 1"
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.UpdateId.ShouldBe(100001);
        result.Message.ShouldNotBeNull();
        result.Message!.ChatId.ShouldBe(111);
        result.Message.ChatType.ShouldBe("private");
        result.Message.MessageId.ShouldBe(501);
        result.Message.UserId.ShouldBe(111);
        result.Message.Username.ShouldBe("test_user");
        result.Message.Text.ShouldBe("тест 1");
        result.Message.Kind.ShouldBe(MessageKind.Text);
        result.Message.IsEdit.ShouldBeFalse();
        result.Message.TopicId.ShouldBeNull();
    }

    [Fact]
    public void Maps_edited_message()
    {
        var update = Parse("""
        {
          "update_id": 100002,
          "edited_message": {
            "message_id": 502,
            "from": { "id": 111, "is_bot": false, "first_name": "Test" },
            "chat": { "id": 111, "type": "private" },
            "date": 1735000000,
            "edit_date": 1735000100,
            "text": "тест 1 (edited)"
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.IsEdit.ShouldBeTrue();
        result.Message.Text.ShouldBe("тест 1 (edited)");
        result.Message.EditedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Maps_photo_with_caption()
    {
        var update = Parse("""
        {
          "update_id": 100006,
          "message": {
            "message_id": 506,
            "from": { "id": 111, "is_bot": false, "first_name": "Test" },
            "chat": { "id": 111, "type": "private" },
            "date": 1735000500,
            "caption": "caption 1",
            "photo": [ { "file_id": "f1", "file_unique_id": "u1", "width": 90, "height": 90 } ]
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.Kind.ShouldBe(MessageKind.Photo);
        result.Message.Text.ShouldBe("caption 1");
    }

    [Fact]
    public void Maps_voice_message()
    {
        var update = Parse("""
        {
          "update_id": 100008,
          "message": {
            "message_id": 508,
            "from": { "id": 111, "is_bot": false, "first_name": "Test" },
            "chat": { "id": 111, "type": "private" },
            "date": 1735000700,
            "voice": { "file_id": "v1", "file_unique_id": "vu1", "duration": 3 }
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.Kind.ShouldBe(MessageKind.Voice);
        result.Message.Text.ShouldBeNull();
    }

    [Fact]
    public void Maps_supergroup_topic_message()
    {
        var update = Parse("""
        {
          "update_id": 100003,
          "message": {
            "message_id": 503,
            "message_thread_id": 4,
            "is_topic_message": true,
            "from": { "id": 222, "is_bot": false, "first_name": "Test2" },
            "chat": { "id": -1009999, "type": "supergroup", "title": "Family" },
            "date": 1735000200,
            "text": "topic message"
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.ChatType.ShouldBe("supergroup");
        result.Message.TopicId.ShouldBe(4);
        result.Message.Kind.ShouldBe(MessageKind.Text);
    }

    [Fact]
    public void Maps_forum_topic_created_as_service_with_no_text()
    {
        var update = Parse("""
        {
          "update_id": 100004,
          "message": {
            "message_id": 504,
            "message_thread_id": 4,
            "is_topic_message": true,
            "from": { "id": 222, "is_bot": false, "first_name": "Test2" },
            "chat": { "id": -1009999, "type": "supergroup", "title": "Family" },
            "date": 1735000300,
            "forum_topic_created": { "name": "General", "icon_color": 123456 }
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.Kind.ShouldBe(MessageKind.Service);
        result.Message.Text.ShouldBeNull();
    }

    [Fact]
    public void Maps_group_to_supergroup_migration_as_service_with_migrate_to_chat_id()
    {
        var update = Parse("""
        {
          "update_id": 100005,
          "message": {
            "message_id": 505,
            "from": { "id": 222, "is_bot": false, "first_name": "Test2" },
            "chat": { "id": -1008888, "type": "group", "title": "OldGroup" },
            "date": 1735000400,
            "migrate_to_chat_id": -1009999
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.Kind.ShouldBe(MessageKind.Service);
        result.Message.MigrateToChatId.ShouldBe(-1009999);
        result.Message.ChatId.ShouldBe(-1008888);
    }

    [Fact]
    public void Maps_anonymous_admin_message_using_the_group_anonymous_bot_pseudo_user()
    {
        var update = Parse("""
        {
          "update_id": 100007,
          "message": {
            "message_id": 507,
            "from": { "id": 1087968824, "is_bot": true, "first_name": "Group", "username": "GroupAnonymousBot" },
            "sender_chat": { "id": -1008888, "type": "group", "title": "OldGroup" },
            "chat": { "id": -1008888, "type": "group", "title": "OldGroup" },
            "date": 1735000600,
            "text": "anon message"
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.UserId.ShouldBe(1087968824);
        result.Message.Username.ShouldBe("GroupAnonymousBot");
        result.Message.Kind.ShouldBe(MessageKind.Text);
    }

    [Fact]
    public void Strips_nul_characters_from_text_and_raw_json()
    {
        var update = Parse("""
        {
          "update_id": 100009,
          "message": {
            "message_id": 509,
            "from": { "id": 111, "is_bot": false, "first_name": "Test" },
            "chat": { "id": 111, "type": "private" },
            "date": 1735000800,
            "text": "before\u0000after"
          }
        }
        """);

        var result = TelegramUpdateMapper.Map(update);

        result.Message.ShouldNotBeNull();
        result.Message!.Text.ShouldBe("beforeafter");
        result.Message.Text!.ShouldNotContain('\0');
        result.Message.RawJson.ShouldNotContain("\\u0000");
    }

    [Fact]
    public void Returns_null_message_for_updates_with_no_message_payload()
    {
        var update = Parse("""{ "update_id": 100010 }""");

        var result = TelegramUpdateMapper.Map(update);

        result.UpdateId.ShouldBe(100010);
        result.Message.ShouldBeNull();
    }
}
