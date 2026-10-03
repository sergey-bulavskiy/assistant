namespace Assistant.Evals;

/// <summary>Runs in every dotnet test (CI included): each case's recorded model answer goes through
/// the production parser, validator and safety rules and must give the case's expected result. No
/// model, no network, no key.</summary>
public sealed class ExtractionReplayTests
{
    public static IEnumerable<object[]> RecordedCaseIds() =>
        CaseFiles.LoadAll().Where(c => c.RecordedOutput is not null).Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(RecordedCaseIds))]
    public void Recorded_answer_gives_the_expected_result(string caseId)
    {
        var evalCase = CaseFiles.LoadAll().Single(c => c.Id == caseId);

        var result = ExtractionCheck.Run(evalCase, evalCase.RecordedOutput);

        result.Problems.ShouldBeEmpty(string.Join("; ", result.Problems));
    }
}
