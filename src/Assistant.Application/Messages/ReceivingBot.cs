namespace Assistant.Application.Messages;

/// <summary>Identifies which bot received an update and, if it's a role bot, which family it
/// belongs to. FamilyId is null exactly when this is the manager bot.</summary>
public record ReceivingBot(long BotDbId, long TelegramBotId, string Username, long? FamilyId, string Role);
