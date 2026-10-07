namespace Assistant.Application.Vet.Photos;

public interface IVetPhotoArchiveDispatchStore
{
    Task<IReadOnlyList<VetPhotoWork>> GetArchiveDueAsync(long familyId, long botDbId,
        int limit, CancellationToken cancellationToken);
}
