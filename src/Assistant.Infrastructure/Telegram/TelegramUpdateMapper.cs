using System.Text.Json;
using Assistant.Application.Common;
using Assistant.Application.Telegram;
using Assistant.Domain.Messages;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Infrastructure.Telegram;

public static class TelegramUpdateMapper
{
    public static IncomingUpdate Map(Update update)
    {
        var tgMessage = update.Message ?? update.EditedMessage;
        if (tgMessage is null)
        {
            return new IncomingUpdate(update.Id, null);
        }

        var isEdit = update.EditedMessage is not null;
        var kind = MapKind(tgMessage);
        var chatType = tgMessage.Chat.Type switch
        {
            ChatType.Private => "private",
            ChatType.Group => "group",
            ChatType.Supergroup => "supergroup",
            ChatType.Channel => "channel",
            _ => "unknown"
        };

        var topicId = tgMessage.IsTopicMessage ? tgMessage.MessageThreadId : null;
        var text = TextSanitizer.SanitizeText(tgMessage.Text ?? tgMessage.Caption);
        var rawJson = TextSanitizer.SanitizeRawJson(JsonSerializer.Serialize(update, JsonBotAPI.Options));

        var message = new IncomingMessage(
            ChatId: tgMessage.Chat.Id,
            ChatType: chatType,
            TopicId: topicId,
            MessageId: tgMessage.Id,
            UserId: tgMessage.From?.Id,
            Username: tgMessage.From?.Username,
            Text: text,
            Kind: kind,
            IsEdit: isEdit,
            SentAt: DateTime.SpecifyKind(tgMessage.Date, DateTimeKind.Utc),
            EditedAt: tgMessage.EditDate.HasValue ? DateTime.SpecifyKind(tgMessage.EditDate.Value, DateTimeKind.Utc) : null,
            MigrateToChatId: tgMessage.MigrateToChatId,
            RawJson: rawJson);

        return new IncomingUpdate(update.Id, message);
    }

    private static MessageKind MapKind(Message message) => message.Type switch
    {
        MessageType.Text => MessageKind.Text,
        MessageType.Photo => MessageKind.Photo,
        MessageType.Voice => MessageKind.Voice,
        MessageType.Video => MessageKind.Video,
        MessageType.VideoNote => MessageKind.Video,
        MessageType.Audio => MessageKind.Audio,
        MessageType.Document => MessageKind.Document,
        MessageType.Sticker => MessageKind.Sticker,
        MessageType.NewChatMembers => MessageKind.Service,
        MessageType.LeftChatMember => MessageKind.Service,
        MessageType.NewChatTitle => MessageKind.Service,
        MessageType.NewChatPhoto => MessageKind.Service,
        MessageType.DeleteChatPhoto => MessageKind.Service,
        MessageType.PinnedMessage => MessageKind.Service,
        MessageType.GroupChatCreated => MessageKind.Service,
        MessageType.SupergroupChatCreated => MessageKind.Service,
        MessageType.ChannelChatCreated => MessageKind.Service,
        MessageType.MigrateFromChatId => MessageKind.Service,
        MessageType.MigrateToChatId => MessageKind.Service,
        MessageType.MessageAutoDeleteTimerChanged => MessageKind.Service,
        MessageType.ForumTopicCreated => MessageKind.Service,
        MessageType.ForumTopicClosed => MessageKind.Service,
        MessageType.ForumTopicReopened => MessageKind.Service,
        MessageType.ForumTopicEdited => MessageKind.Service,
        MessageType.GeneralForumTopicHidden => MessageKind.Service,
        MessageType.GeneralForumTopicUnhidden => MessageKind.Service,
        MessageType.VideoChatScheduled => MessageKind.Service,
        MessageType.VideoChatStarted => MessageKind.Service,
        MessageType.VideoChatEnded => MessageKind.Service,
        MessageType.VideoChatParticipantsInvited => MessageKind.Service,
        MessageType.WriteAccessAllowed => MessageKind.Service,
        MessageType.UsersShared => MessageKind.Service,
        MessageType.ChatShared => MessageKind.Service,
        MessageType.ProximityAlertTriggered => MessageKind.Service,
        _ => MessageKind.Other
    };
}
