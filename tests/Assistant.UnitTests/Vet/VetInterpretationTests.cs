using Assistant.Application.Vet;
using Assistant.Domain.Vet;

namespace Assistant.UnitTests.Vet;

public sealed class VetInterpretationTests
{
    private static readonly DateTimeOffset Sent = DateTimeOffset.Parse("2031-05-12T23:45:00Z");

    [Theory]
    [InlineData("0.125", "0.125")]
    [InlineData("0,3", "0.3")]
    [InlineData("1.25e-1", "0.125")]
    [InlineData("1000", "1000")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void Exact_positive_numbers_preserve_every_digit_without_clinical_grid(string raw, string expected)
    {
        VetInterpretationParser.TryPositiveDecimal(raw, out var value).ShouldBeTrue();
        VetEventText.Number(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.125")]
    [InlineData("NaN")]
    [InlineData("1.2.3")]
    [InlineData("1 000")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("7922816251426433759354395033.51")]
    public void Invalid_or_inexact_numbers_never_become_facts(string raw)
    {
        var candidate = Candidate("insulin", raw);
        var result = VetEventValidation.Validate(candidate, Profile(), Source(), Guid.NewGuid());
        result.State.ShouldBeNull();
        result.Reason.ShouldBe("Укажите точное положительное значение без округления.");
    }

    [Fact]
    public void Parser_keeps_record_and_question_intents_independent_of_reply_intent()
    {
        var parsed = VetInterpretationParser.Parse("""{"needs_reply":true,"events":[{"type":"glucose","intent":"record","value":"6.4","time_evidence":"current"},{"type":"insulin","intent":"question_only","dose":"0.125"},{"type":"insulin","dose":"0.3"}],"unclear":[]}""")!;
        parsed.NeedsReply.ShouldBeTrue();
        parsed.Events.Select(e => e.Intent).ShouldBe(new[] { "record", "question_only", "unsure" });
        parsed.Events.Select(e => e.RawValue).ShouldBe(new[] { "6.4", "0.125", "0.3" });
        parsed.Events.Select(e => e.Ordinal).ShouldBe(new[] { 0, 0, 1 });
    }

    [Theory]
    [InlineData("""{"events":[],"unclear":[]}""")]
    [InlineData("""{"needs_reply":"true","events":[],"unclear":[]}""")]
    public void Missing_or_invalid_reply_intent_is_conservatively_false(string json) =>
        VetInterpretationParser.Parse(json)!.NeedsReply.ShouldBeFalse();

    [Theory]
    [InlineData("""{"needs_reply":true,"events":[],"unexpected":true}""")]
    [InlineData("""{"needs_reply":true,"needs_reply":false,"events":[]}""")]
    [InlineData("""{"needs_reply":true,"events":[{"type":"glucose","value":6.4,"sql":"delete"}]}""")]
    [InlineData("""{"needs_reply":true,"events":[],"operation":{"kind":"correct","event_id":-1}}""")]
    [InlineData("""{"needs_reply":true,"events":[],"operation":{"kind":"correct","event_id":"12"}}""")]
    [InlineData("""{"needs_reply":true,"events":[{"type":"glucose","intent":"record","value":"6.8","event_id":"12"}]}""")]
    public void Nonclosed_or_invalid_schema_is_rejected(string json) =>
        VetInterpretationParser.Parse(json).ShouldBeNull();

    [Fact]
    public void Candidate_overflow_is_rejected_instead_of_truncated()
    {
        var json = "{\"events\":[" + string.Join(",", Enumerable.Repeat("{\"type\":\"glucose\"}", 21)) + "]}";
        VetInterpretationParser.Parse(json).ShouldBeNull();
    }

    [Fact]
    public void Unknown_operation_becomes_clarification_without_executing_it()
    {
        var parsed = VetInterpretationParser.Parse("""{"events":[],"operation":{"kind":"shell"}}""")!;
        parsed.Operation.ShouldBeNull();
        parsed.Unclear.ShouldBe(new[] { "Нужна конкретная поддерживаемая операция." });
    }

    [Fact]
    public void Current_missing_time_is_original_message_time_with_profile_evidence()
    {
        var result = VetEventValidation.Validate(Candidate("insulin", "0.125"), Profile(), Source(), Guid.NewGuid());
        result.State!.OccurredAt.ShouldBe(Sent);
        result.State.LocalTime.ShouldBe("2031-05-12 23:45:00");
        result.State.Value.ShouldBe(0.125m);
        result.State.OccurredAtSource.ShouldBe("message");
        result.State.ValueUnitSource.ShouldBe("profile");
        result.State.Product.ShouldBeNull();
    }

    [Fact]
    public void Explicit_historical_offset_needs_no_profile_defaults_or_recent_date_cutoff()
    {
        var candidate = Candidate("glucose", "6.4") with { Unit = "mmol/L", Date = "2001-03-04", Time = "12:10", Offset = "+02:00", TimeEvidence = "stated" };
        var result = VetEventValidation.Validate(candidate, new(), Source(), Guid.NewGuid());
        result.State!.OccurredAt.ShouldBe(DateTimeOffset.Parse("2001-03-04T10:10:00Z"));
        result.State.TimeZoneSnapshot.ShouldBe("+02:00");
        result.State.ValueUnitSource.ShouldBe("stated");
    }

    [Theory]
    [InlineData(null, null, "historical")]
    [InlineData("2001-03-04", null, "historical")]
    [InlineData(null, "09:00", "unknown")]
    public void Historical_missing_date_or_clock_stays_pending(string? date, string? time, string evidence)
    {
        var result = VetEventValidation.Validate(Candidate("glucose", "6.4") with { Date = date, Time = time, TimeEvidence = evidence },
            Profile(), Source(), Guid.NewGuid());
        result.State.ShouldBeNull();
        result.Reason!.ShouldContain("дату и время");
    }

    [Fact]
    public void Relative_date_uses_source_local_day_across_midnight()
    {
        var profile = Profile(); profile.TimeZone = "Etc/GMT-2";
        var result = VetEventValidation.Validate(Candidate("glucose", "6.4") with { Date = "yesterday", Time = "23:30", TimeEvidence = "relative" },
            profile, Source(), Guid.NewGuid());
        result.State!.OccurredAt.ShouldBe(DateTimeOffset.Parse("2031-05-12T21:30:00Z"));
        result.State.OccurredAtSource.ShouldBe("relative");
    }

    [Theory]
    [InlineData("2024-03-10", "02:30", "Такого местного времени нет")]
    [InlineData("2024-11-03", "01:30", "местное время повторяется")]
    public void Dst_invalid_and_ambiguous_local_times_require_clarification(string date, string time, string reason)
    {
        var profile = Profile(); profile.TimeZone = "America/New_York";
        var result = VetEventValidation.Validate(Candidate("glucose", "6.4") with { Date = date, Time = time, TimeEvidence = "stated" },
            profile, Source(), Guid.NewGuid());
        result.State.ShouldBeNull();
        result.Reason!.ShouldContain(reason);
    }

    [Theory]
    [InlineData("23:55", true)]
    [InlineData("23:56", false)]
    public void Actual_future_tolerance_is_ten_minutes(string clock, bool accepted)
    {
        var result = VetEventValidation.Validate(Candidate("glucose", "6.4") with { Date = "2031-05-12", Time = clock, TimeEvidence = "stated" },
            Profile(), Source(), Guid.NewGuid());
        (result.State is not null).ShouldBe(accepted);
        if (!accepted) result.Reason!.ShouldContain("будущем");
    }

    [Fact]
    public void Unsupported_unit_is_retained_for_pending_clarification()
    {
        var result = VetEventValidation.Validate(Candidate("glucose", "120") with { Unit = "mg/dL" }, Profile(), Source(), Guid.NewGuid());
        result.State.ShouldBeNull();
        result.Candidate.Unit.ShouldBe("mg/dL");
        result.Reason!.ShouldContain("пересчёт");
    }

    [Fact]
    public void Hypothetical_dose_does_not_become_a_record_or_a_pending_fact()
    {
        var result = VetEventValidation.Validate(Candidate("insulin", "0.3") with { Intent = "question_only" }, Profile(), Source(), Guid.NewGuid());
        result.State.ShouldBeNull(); result.Reason.ShouldBeNull();
    }

    [Fact]
    public void Independent_edit_is_protected_and_ambiguous_extraction_cannot_delete_existing_fact()
    {
        var old = new VetEvent { Id = 10, EventType = "glucose", Value = 6.4m, Unit = "mmol/L",
            OccurredAt = Sent, CandidateOrdinal = 0, Revision = 3, LastMutationKind = "manual" };
        var validation = VetEventValidation.Validate(Candidate("glucose", "6.8"), Profile(), Source(), Guid.NewGuid());
        var plan = VetTextPlanner.Plan([validation], [old], true, 222, Sent);
        plan.ClearChanges.ShouldBeEmpty();
        plan.Pending!.Changes.Single().EventId.ShouldBe(10);
        plan.Pending.Changes.Single().ExpectedRevision.ShouldBe(3);
        plan.Pending.Reasons.Single().ShouldContain("меняли отдельно");
        var uncertain = VetTextPlanner.Plan([new(Candidate("glucose", "6.8"), null, "Уточните время.")], [old], true, 222, Sent);
        uncertain.ClearChanges.ShouldBeEmpty();
        uncertain.Pending!.Changes.ShouldBeEmpty();
    }

    private static VetCandidate Candidate(string type, string value) => new(type, "record", value, null, null, null, null, null, "current", 0);
    private static VetProfile Profile() => new() { TimeZone = "UTC", GlucoseUnit = "mmol/L", InsulinUnit = "U" };
    private static VetAdmittedSource Source()
    {
        var id = Guid.NewGuid(); var revision = Guid.NewGuid();
        return new(new() { Id = id, SentAt = Sent, SourceAuthorUserId = 111, SourceMessageDbId = 99, TelegramMessageId = 1000 },
            new() { Id = revision, SourceId = id });
    }
}
