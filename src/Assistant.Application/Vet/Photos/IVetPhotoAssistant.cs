using Assistant.Application.Messages;
using Assistant.Application.Telegram;

namespace Assistant.Application.Vet.Photos;

public interface IVetPhotoAssistant
{
    Task<VetPhotoAdmission?> AdmitAsync(ReceivingBot bot, IncomingMessage message, long updateId,
        VetAdmittedSource? textAdmission, CancellationToken cancellationToken);
    Task BindAsync(ReceivingBot bot, IncomingMessage message, StoreResult stored,
        VetPhotoAdmission? admission, CancellationToken cancellationToken);
    Task HandleAdmissionAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        StoreResult stored, VetPhotoAdmission? admission, CancellationToken cancellationToken);
    Task<bool> CommandAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        string command, string? args, Guid operationKey, CancellationToken cancellationToken);
    Task<bool> OperationAsync(ReceivingBot bot, ITelegramClient client, IncomingMessage message,
        VetPhotoOperation operation, Guid operationKey, CancellationToken cancellationToken);
    Task<bool> TryNaturalDecisionAsync(ReceivingBot bot, ITelegramClient client,
        IncomingMessage message, VetOperation operation, CancellationToken cancellationToken) => Task.FromResult(false);
    Task<bool> HandleCallbackAsync(ReceivingBot bot, ITelegramClient client,
        CallbackQueryInfo callback, CancellationToken cancellationToken);
    Task<VetInterpretation> FilterCaptionAsync(VetDiaryScope scope, int telegramMessageId,
        VetInterpretation interpretation, CancellationToken cancellationToken);
    Task<string> DescribeAsync(VetDiaryScope scope, long actorUserId, CancellationToken cancellationToken);
    Task ResumeAsync(ReceivingBot bot, ITelegramClient client, CancellationToken cancellationToken);
    Task WorkFinishedAsync(ReceivingBot bot, ITelegramClient client, VetPhotoWork work,
        VetPhotoProcessResult result, CancellationToken cancellationToken);
}

public interface IVetPhotoBackgroundLoop
{
    Task RunAsync(ReceivingBot bot, ITelegramClient client, CancellationToken cancellationToken);
}
