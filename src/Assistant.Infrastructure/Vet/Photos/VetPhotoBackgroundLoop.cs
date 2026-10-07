using Assistant.Application.Families;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Application.Vet.Photos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed class VetPhotoExecutionGate : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new Release(gate);
    }
    public void Dispose() => gate.Dispose();
    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); }
    }
}

public sealed class VetPhotoBackgroundLoop(IServiceScopeFactory scopes, VetPhotoExecutionGate gate,
    TimeProvider time, ILogger<VetPhotoBackgroundLoop> logger) : IVetPhotoBackgroundLoop
{
    public async Task RunAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
    {
        if (!BotRoles.IsVet(bot.Role) || bot.FamilyId == null) return;
        while (!ct.IsCancellationRequested)
        {
            try { await RunPassAsync(bot, client, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning("Photo background work deferred: {ExceptionType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), time, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task RunPassAsync(ReceivingBot bot, ITelegramClient client, CancellationToken ct)
    {
        if (!BotRoles.IsVet(bot.Role) || bot.FamilyId is not { } familyId) return;
        IReadOnlyList<VetPhotoWork> due;
        using (var scope = Fresh(familyId))
        {
            var services = scope.ServiceProvider;
            await services.GetRequiredService<IVetPhotoAssistant>().ResumeAsync(bot, client, ct);
            var archive = await services.GetRequiredService<IVetPhotoArchiveDispatchStore>().GetArchiveDueAsync(familyId, bot.BotDbId, 4, ct);
            var vision = archive.Count < 5 ? await services.GetRequiredService<IVetPhotoDispatchStore>().GetRetainedDueAsync(
                familyId, bot.BotDbId, 5 - archive.Count, ct) : [];
            due = archive.Concat(vision).ToArray();
        }
        foreach (var work in due)
        {
            ct.ThrowIfCancellationRequested();
            if (work.Scope.FamilyId != familyId || work.Scope.BotDbId != bot.BotDbId
                || work.Scope.TelegramBotId != bot.TelegramBotId) continue;
            using var execution = await gate.EnterAsync(ct);
            using var scope = Fresh(familyId);
            var services = scope.ServiceProvider;
            var result = await services.GetRequiredService<VetPhotoProcessor>().ProcessAsync(work, client, ct);
            await services.GetRequiredService<IVetPhotoAssistant>().WorkFinishedAsync(bot, client, work, result, ct);
        }
        using (var scope = Fresh(familyId))
            await scope.ServiceProvider.GetRequiredService<IVetPhotoArchiveStore>().ReclaimAsync(familyId, 50, ct);
    }

    private IServiceScope Fresh(long familyId)
    {
        var scope = scopes.CreateScope();
        try { scope.ServiceProvider.GetRequiredService<ICurrentFamily>().Set(familyId); return scope; }
        catch { scope.Dispose(); throw; }
    }
}
