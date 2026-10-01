using Assistant.Application.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramUpdateType = Telegram.Bot.Types.Enums.UpdateType;

namespace Assistant.Infrastructure.Telegram;

public class TelegramClientAdapter : ITelegramClient
{
    private readonly ITelegramBotClient _client;

    public TelegramClientAdapter(ITelegramBotClient client)
    {
        _client = client;
    }

    public async Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken)
    {
        var me = await _client.GetMe(cancellationToken);
        return new BotIdentity(me.Id, me.Username ?? string.Empty);
    }

    public async Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(
        long offset, int timeoutSeconds, IReadOnlyList<UpdateKind> allowedUpdates, CancellationToken cancellationToken)
    {
        var updates = await _client.GetUpdates(
            offset: checked((int)offset),
            limit: 100,
            timeout: timeoutSeconds,
            allowedUpdates: allowedUpdates.Select(MapAllowedUpdate).ToArray(),
            cancellationToken: cancellationToken);

        return updates.Select(TelegramUpdateMapper.Map).ToArray();
    }

    public async Task<int> SendTextAsync(long chatId, int? topicId, string text, int? replyToMessageId, CancellationToken cancellationToken)
    {
        var sent = await _client.SendMessage(
            chatId: chatId,
            text: text,
            messageThreadId: topicId,
            // AllowSendingWithoutReply: true -- if the triggering message was deleted (or otherwise
            // no longer reply-able) before we answer, Telegram would otherwise reject the whole send
            // with an error instead of just dropping the reply link. We'd rather still deliver the
            // answer, without the visual "reply to" thread, than lose it.
            replyParameters: replyToMessageId is { } id
                ? new ReplyParameters { MessageId = id, AllowSendingWithoutReply = true }
                : null,
            cancellationToken: cancellationToken);
        return sent.Id;
    }

    public Task SendChatActionAsync(long chatId, int? topicId, string action, CancellationToken cancellationToken)
    {
        var chatAction = action switch
        {
            "typing" => ChatAction.Typing,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported chat action.")
        };
        return _client.SendChatAction(chatId: chatId, action: chatAction, messageThreadId: topicId, cancellationToken: cancellationToken);
    }

    public async Task<int> SendTextWithButtonsAsync(
        long chatId, int? topicId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken)
    {
        var markup = new InlineKeyboardMarkup(buttons.Select(b => InlineKeyboardButton.WithCallbackData(b.Label, b.CallbackData)));
        var sent = await _client.SendMessage(
            chatId: chatId, text: text, messageThreadId: topicId, replyMarkup: markup, cancellationToken: cancellationToken);
        return sent.Id;
    }

    public Task EditMessageButtonsAsync(long chatId, int messageId, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken)
    {
        var markup = new InlineKeyboardMarkup(buttons.Select(b => InlineKeyboardButton.WithCallbackData(b.Label, b.CallbackData)));
        return _client.EditMessageReplyMarkup(chatId: chatId, messageId: messageId, replyMarkup: markup, cancellationToken: cancellationToken);
    }

    public Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) =>
        _client.EditMessageText(chatId: chatId, messageId: messageId, text: text, cancellationToken: cancellationToken);

    public Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken cancellationToken) =>
        _client.AnswerCallbackQuery(callbackQueryId: callbackQueryId, text: text, cancellationToken: cancellationToken);

    public Task<string> GetManagedBotTokenAsync(long managedBotUserId, CancellationToken cancellationToken) =>
        _client.GetManagedBotToken(managedBotUserId, cancellationToken);

    private static TelegramUpdateType MapAllowedUpdate(UpdateKind kind) => kind switch
    {
        UpdateKind.Message => TelegramUpdateType.Message,
        UpdateKind.EditedMessage => TelegramUpdateType.EditedMessage,
        UpdateKind.CallbackQuery => TelegramUpdateType.CallbackQuery,
        UpdateKind.MyChatMember => TelegramUpdateType.MyChatMember,
        UpdateKind.ManagedBot => TelegramUpdateType.ManagedBot,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped UpdateKind")
    };
}
