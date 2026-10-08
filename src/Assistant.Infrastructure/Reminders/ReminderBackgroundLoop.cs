using Assistant.Application.Expectations;
using Assistant.Application.Families;
using Assistant.Application.Messages;
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
            try { await RunPassAsync(bot, client, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Nonurgent pass failed: {ExceptionType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task RunPassAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
    {
        if (bot.FamilyId == null || bot.Role is not ("general" or "health" or "vet")) return;
        IReadOnlyList<NonurgentCandidate> candidates;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(bot.FamilyId);
            var store = scope.ServiceProvider.GetRequiredService<INonurgentDispatchStore>();
            await store.CleanupAsync(bot, ct);
            candidates = await store.SelectAsync(bot, ct);
        }
        var sends = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (sends == 5) break;
            NonurgentDispatch? dispatch;
            using (var scope = scopes.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(bot.FamilyId);
                dispatch = await scope.ServiceProvider.GetRequiredService<INonurgentDispatchStore>()
                    .ClaimAsync(bot, candidate, ct);
            }
            if (dispatch is null) continue;
            sends++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var messageId = await client.SendTextAsync(dispatch.ChatId, dispatch.TopicId,
                    dispatch.Text, null, timeout.Token);
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(bot.FamilyId);
                await scope.ServiceProvider.GetRequiredService<INonurgentDispatchStore>()
                    .CompleteAsync(bot, dispatch, messageId, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Nonurgent delivery outcome unknown: {ExceptionType}", ex.GetType().Name);
            }
        }
    }
}
