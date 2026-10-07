using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Domain.Vet.Photos;

namespace Assistant.Application.Vet.Photos;

public interface IVetPhotoRunApplication
{
    Task BeginAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetPhotoOperation operation, Guid operationKey, CancellationToken cancellationToken);
    Task ContinueAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        Guid runId, CancellationToken cancellationToken);
    Task ShowAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        Guid runId, CancellationToken cancellationToken);
    Task SelectResultAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        Guid runId, Guid windowId, Guid sourceId, Guid inputRevisionId, Guid extractionResultId,
        bool restore, CancellationToken cancellationToken);
    Task CancelAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        Guid runId, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(VetDiaryScope scope, VetPhotoReview review, long actorUserId,
        int? callbackPromptMessageId, ITelegramClient client, int? replyToMessageId,
        CancellationToken cancellationToken);
    Task ResumeAsync(ReceivingBot bot, ITelegramClient client, CancellationToken cancellationToken);
    Task WorkFinishedAsync(ReceivingBot bot, ITelegramClient client, VetPhotoWork work,
        VetPhotoProcessResult result, CancellationToken cancellationToken);
}

public interface IVetPhotoReversalApplication
{
    Task StageAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetPhotoOperation operation, Guid operationKey, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(VetDiaryScope scope, VetPhotoReview review, long actorUserId,
        int? callbackPromptMessageId, ITelegramClient client, int? replyToMessageId,
        CancellationToken cancellationToken);
}
