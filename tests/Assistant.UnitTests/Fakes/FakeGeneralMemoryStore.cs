using Assistant.Application.Memory;

namespace Assistant.UnitTests.Fakes;

public sealed class FakeGeneralMemoryStore : IGeneralMemoryStore
{
    public GeneralMemorySnapshot Snapshot { get; set; } = new([], null);
    public GeneralSummaryFold? Fold { get; set; }
    public IReadOnlyList<GeneralSearchHit> Hits { get; set; } = [];
    public int PrepareCalls { get; private set; }
    public int CommitCalls { get; private set; }
    public int ForgetCalls { get; private set; }
    public GeneralSummaryFold? CommittedFold { get; private set; }
    public Exception? Failure { get; set; }
    public Task<IReadOnlyList<GeneralSearchHit>> SearchAsync(GeneralMemoryScope scope, string query, CancellationToken ct) =>
        Task.FromResult(Hits);
    public Task<GeneralRememberResult> RememberAsync(GeneralMemoryScope scope, long source, string text, CancellationToken ct) =>
        Task.FromResult(new GeneralRememberResult(17, null));
    public Task<bool> ForgetAsync(GeneralMemoryScope scope, long id, CancellationToken ct)
    { ForgetCalls++; return Task.FromResult(true); }
    public Task<GeneralMemorySnapshot> ReadAsync(GeneralMemoryScope scope, CancellationToken ct)
    { if (Failure is not null) throw Failure; return Task.FromResult(Snapshot); }
    public Task<GeneralSummaryFold?> PrepareFoldAsync(GeneralMemoryScope scope, long before, int recent, CancellationToken ct)
    { PrepareCalls++; if (Failure is not null) throw Failure; return Task.FromResult(Fold); }
    public Task<bool> CommitFoldAsync(GeneralMemoryScope scope, GeneralSummaryFold fold, string text, string model, CancellationToken ct)
    { CommitCalls++; CommittedFold = fold; Snapshot = Snapshot with { Summary = new(text, fold.Sources[^1].Id) }; return Task.FromResult(true); }
}
