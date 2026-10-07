using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Vet.Photos;

public interface IVetPhotoApplicationOperations
{
    Task<bool> CommandAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        string command, string? args, Guid operationKey, CancellationToken cancellationToken);
    Task<bool> OperationAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetPhotoOperation operation, Guid operationKey, CancellationToken cancellationToken);
    Task<bool> TryNaturalDecisionAsync(ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, VetOperation operation, CancellationToken cancellationToken) => Task.FromResult(false);
    Task<bool> HandleCallbackAsync(ReceivingBot bot, ITelegramClient client,
        CallbackQueryInfo callback, CancellationToken cancellationToken);
    Task ResumeRunsAsync(ReceivingBot bot, ITelegramClient client, CancellationToken cancellationToken);
    Task WorkFinishedAsync(ReceivingBot bot, ITelegramClient client, VetPhotoWork work,
        VetPhotoProcessResult result, CancellationToken cancellationToken);
}

public interface IVetPhotoBindingStore
{
    Task<bool> BindStoredMessageAsync(VetDiaryScope scope, Guid sourceId,
        long actorUserId, CancellationToken cancellationToken);
}
