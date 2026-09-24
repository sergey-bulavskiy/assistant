using Assistant.Application.Messages;
using Assistant.Domain.Messages;

namespace Assistant.Application.Telegram;

public static class ReplyPolicy
{
    public static string? Decide(IncomingMessage? message, StoreResult result, string botUsername, Func<string> versionText)
    {
        if (message is null)
        {
            return null;
        }

        if (result.Outcome is StoreOutcome.AlreadyProcessed or StoreOutcome.Duplicate or StoreOutcome.OffsetOnly)
        {
            return null;
        }

        if (message.Kind == MessageKind.Service)
        {
            return null;
        }

        if (message.IsEdit)
        {
            return null;
        }

        var command = CommandParser.Parse(message.Text, botUsername);
        if (command == "version")
        {
            return versionText();
        }

        if (command == "start" && message.ChatType == "private")
        {
            return "Привет! Я сохраняю сообщения. /version — какая версия запущена.";
        }

        if (command is null && message.Text is not null && message.Text.StartsWith('/'))
        {
            // Looks like a command, but not one we recognize (or addressed to another bot) — stay silent.
            return null;
        }

        if (message.ChatType == "private" && result.Outcome == StoreOutcome.Stored)
        {
            return $"Получил ✅ #{result.MessageDbId}";
        }

        return null;
    }
}
