using System.Text.RegularExpressions;
using Assistant.Application.Telegram;

namespace Assistant.Application.Messages;

/// <summary>Whether a message is addressed to a role bot (the M3a rule, shared by the General and
/// health assistants): a private chat, an @username mention of this bot, or a genuine reply to one of
/// this bot's messages. Pure. A place's reply_to_all setting is not part of this rule: the General
/// and Health assistants add the exact approved place's flag on top.</summary>
public static class Addressing
{
    public static bool IsAddressed(ReceivingBot bot, IncomingMessage message, string text)
    {
        if (message.ChatType == "private")
        {
            return true;
        }

        if (MentionsBot(text, bot.Username))
        {
            return true;
        }

        // Ordinary forum-topic messages carry reply_to_message == the topic root; that is not a reply
        // to the bot, even when the bot happens to have sent the root.
        var isReplyToTopicRoot = message.TopicId is { } topicId && message.ReplyToMessageId == topicId;
        return message.ReplyToUserId == bot.TelegramBotId && !isReplyToTopicRoot;
    }

    // @username followed by end of text or a non-word character (@bot does not match @bot2), and no
    // word character or '@' right before the '@', so an email-like "me@test_bot" is not a mention.
    public static bool MentionsBot(string text, string botUsername) =>
        Regex.IsMatch(text, $@"(?<![\w@])@{Regex.Escape(botUsername)}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
