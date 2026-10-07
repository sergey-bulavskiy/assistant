namespace Assistant.Application.Vet.Photos;

public sealed record VetPhotoLateCommand(Guid BatchId, int BatchRevision, Guid SourceId,
    Guid CurrentInputId);

public sealed record VetPhotoCommandStep(string Kind, Guid? BatchId = null,
    int? BatchRevision = null, VetPhotoAssumptionChange? Assumption = null,
    VetPhotoHumanProposal? Human = null, VetPhotoCandidateChange? Candidate = null,
    VetPhotoLateCommand? Late = null);

public sealed record VetPhotoCommandPlanRequest(VetDiaryScope Scope, Guid SourceOperationKey,
    long ActorUserId, string RequestJson, IReadOnlyList<VetPhotoCommandStep> Steps);

public sealed record VetPhotoCommandPlan(Guid Id, VetDiaryScope Scope, Guid SourceOperationKey,
    Guid SourceId, Guid SourceRevisionId, long ActorUserId, string RequestJson,
    IReadOnlyList<VetPhotoCommandStep> Steps);

public sealed record VetPhotoCommandPlanResult(VetPhotoWorkflowStatus Status,
    VetPhotoCommandPlan? Plan);

public sealed record VetPhotoCommandStepOutcome(VetPhotoWorkflowStatus Status,
    Guid? BatchId = null, Guid? CandidateId = null, Guid? ExtractionResultId = null);

public interface IVetPhotoCommandReceiptStore
{
    Task<VetPhotoCommandPlanResult> ReadPlanAsync(VetDiaryScope scope, Guid sourceOperationKey,
        long actorUserId, CancellationToken cancellationToken);
    Task<VetPhotoCommandPlanResult> PreparePlanAsync(VetPhotoCommandPlanRequest request,
        CancellationToken cancellationToken);
    Task<VetPhotoCommandStepOutcome> ExecuteStepAsync(VetDiaryScope scope, Guid sourceOperationKey,
        int stepIndex, long actorUserId, CancellationToken cancellationToken);
}
