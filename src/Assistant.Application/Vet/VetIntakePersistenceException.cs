namespace Assistant.Application.Vet;

/// <summary>A failed pre-offset Vet admission/storage boundary must be retried, never poison-skipped.</summary>
public sealed class VetIntakePersistenceException : Exception
{
    public VetIntakePersistenceException() : base("Vet intake persistence failed.") { }

    public static bool IsRetryable(Exception exception) =>
        exception is System.Data.Common.DbException { IsTransient: true } or IOException or TimeoutException
        || exception.InnerException is { } inner && IsRetryable(inner);
}
