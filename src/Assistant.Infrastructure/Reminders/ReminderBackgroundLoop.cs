using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Application.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Reminders;

public sealed class ReminderBackgroundLoop(IServiceScopeFactory scopes, ILogger<ReminderBackgroundLoop> logger)
{
    public async Task RunAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
    {
        if (bot.FamilyId == null || bot.Role is not ("general" or "health" or "vet")) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IReminderAssistant>().TickAsync(bot, client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Reminder background pass failed: {ExceptionType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
