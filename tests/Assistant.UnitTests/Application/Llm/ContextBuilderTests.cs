using Assistant.Application.Llm;
using Assistant.Application.Messages;
using Assistant.Domain.Messages;

namespace Assistant.UnitTests.Application.Llm;

public class ContextBuilderTests
{
    private static ContextMessage History(MessageDirection direction, string? username, string text) =>
        new(direction, username, text, DateTimeOffset.UtcNow);

    [Fact]
    public void Private_chat_messages_carry_no_author_and_no_text_prefix()
    {
        var history = new[] { History(MessageDirection.In, "alice", "earlier message") };

        var built = ContextBuilder.Build(history, "current message", currentUsername: "alice", isGroup: false, maxInputChars: 1000);

        built.Select(m => m.Text).ShouldBe(new[] { "earlier message", "current message" });
        built.ShouldAllBe(m => m.Author == null);
    }

    [Fact]
    public void Group_user_messages_carry_the_authors_username_the_bots_own_replies_do_not()
    {
        // Spec §8.4: authorship reaches the CLI as the <msg author="..."> attribute, not a text
        // prefix baked into the message content -- ContextBuilder sets LlmMessage.Author, it never
        // rewrites Text.
        var history = new[]
        {
            History(MessageDirection.In, "alice", "hi"),
            History(MessageDirection.Out, null, "hello alice")
        };

        var built = ContextBuilder.Build(history, "another message", currentUsername: "bob", isGroup: true, maxInputChars: 1000);

        built.Select(m => m.Text).ShouldBe(new[] { "hi", "hello alice", "another message" });
        built.Select(m => m.Author).ShouldBe(new[] { "alice", null, "bob" });
        built.Select(m => m.Role).ShouldBe(new[] { LlmMessageRole.User, LlmMessageRole.Assistant, LlmMessageRole.User });
    }

    [Fact]
    public void A_group_user_with_no_username_has_a_null_author_the_CLI_client_supplies_the_user_fallback()
    {
        var built = ContextBuilder.Build(Array.Empty<ContextMessage>(), "hi", currentUsername: null, isGroup: true, maxInputChars: 1000);

        built.Single().Text.ShouldBe("hi");
        built.Single().Author.ShouldBeNull(); // ClaudeCliChatClient.SanitizeAuthor falls back to "user" for a null author on a User-role message
    }

    [Fact]
    public void Oldest_history_is_dropped_first_to_fit_the_char_budget_current_message_always_kept()
    {
        var history = new[]
        {
            History(MessageDirection.In, null, new string('a', 50)),
            History(MessageDirection.In, null, new string('b', 50))
        };

        // Budget 110: keeping all three (150 chars) doesn't fit, but dropping only the oldest ('a')
        // leaves b+c = 100 chars, which does -- so only 'a' is dropped. (The plan's own draft used
        // maxInputChars: 90, but b+c=100 > 90 still wouldn't fit there, which would force dropping
        // 'b' too; this value actually exercises "drop oldest, keep the rest".)
        var built = ContextBuilder.Build(history, new string('c', 50), currentUsername: null, isGroup: false, maxInputChars: 110);

        built.Select(m => m.Text).ShouldBe(new[] { new string('b', 50), new string('c', 50) });
    }

    [Fact]
    public void A_current_message_that_alone_exceeds_the_budget_is_truncated_not_dropped()
    {
        var built = ContextBuilder.Build(Array.Empty<ContextMessage>(), new string('x', 200), currentUsername: null, isGroup: false, maxInputChars: 100);

        built.Single().Text.Length.ShouldBe(100);
    }

    [Fact]
    public void Empty_history_still_returns_the_current_message()
    {
        var built = ContextBuilder.Build(Array.Empty<ContextMessage>(), "hi", currentUsername: null, isGroup: false, maxInputChars: 1000);

        built.Single().Text.ShouldBe("hi");
    }

    [Fact]
    public void Exact_limit_boundary_keeps_everything()
    {
        var history = new[] { History(MessageDirection.In, null, new string('a', 50)) };

        var built = ContextBuilder.Build(history, new string('b', 50), currentUsername: null, isGroup: false, maxInputChars: 100);

        built.Select(m => m.Text).ShouldBe(new[] { new string('a', 50), new string('b', 50) });
    }

    [Fact]
    public void One_char_over_the_limit_drops_the_oldest_message()
    {
        var history = new[] { History(MessageDirection.In, null, new string('a', 50)) };

        var built = ContextBuilder.Build(history, new string('b', 51), currentUsername: null, isGroup: false, maxInputChars: 100);

        built.Select(m => m.Text).ShouldBe(new[] { new string('b', 51) });
    }
}
