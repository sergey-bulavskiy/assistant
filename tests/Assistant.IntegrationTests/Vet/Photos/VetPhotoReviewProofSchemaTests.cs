using System.Text.Json;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoReviewProofSchemaTests : VetTestBase
{
    private static Guid SyntheticId(int ordinal) => Guid.Parse($"00000000-0000-4000-8000-{ordinal:D12}");

    private VetPhotoReview Review(long profileId, int profileRevision, string selection) => new()
    {
        Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId, TelegramBotId = Bot.TelegramBotId,
        ChatId = Scope.ChatId, TopicId = Scope.TopicId, ProfileId = profileId, ProfileRevision = profileRevision,
        OperationKey = Guid.NewGuid(), Kind = "reextract_selection", RequesterUserId = 111,
        SelectionJson = selection, CreatedAt = Now
    };

    [Fact]
    public async Task Complete_ten_thousand_reference_selection_survives_old_varchar_boundary_without_truncation()
    {
        await SeedAsync(); await using var s = Open();
        var profile = await s.Context.VetProfiles.SingleAsync();
        var selected = Enumerable.Range(1, 10_000).Select(n => new
        {
            SourceId = SyntheticId(n), InputRevisionId = SyntheticId(n + 10_000),
            OriginalReferenceId = SyntheticId(n + 20_000), ExpectedReferenceRevision = 1,
            ExpectedCurrentInputId = SyntheticId(n + 30_000), ExpectedSourceOrdinal = 1,
            ExpectedCandidateRevision = (int?)1, AttemptKey = SyntheticId(n + 40_000)
        }).ToArray();
        var json = JsonSerializer.Serialize(selected);
        json.Length.ShouldBeGreaterThan(2_097_152);
        var review = Review(profile.Id, profile.Revision, json);
        var run = new VetPhotoRun { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
            ActorUserId = 111, OperationKey = Guid.NewGuid(), SelectionReviewId = review.Id,
            SelectionMode = "all_originals", SelectionJson = json, SelectedCount = 10_000,
            ModelName = "synthetic-model", CreatedAt = Now };
        s.Context.AddRange(review, run); await s.Context.SaveChangesAsync();
        await using var restarted = Open();
        var storedReview = await restarted.Context.Set<VetPhotoReview>().SingleAsync();
        var storedRun = await restarted.Context.Set<VetPhotoRun>().SingleAsync();
        storedReview.SelectionJson.ShouldBe(json); storedRun.SelectionJson.ShouldBe(json);
        storedRun.SelectedCount.ShouldBe(10_000);
        using var parsed = JsonDocument.Parse(storedRun.SelectionJson);
        parsed.RootElement.GetArrayLength().ShouldBe(10_000);
        parsed.RootElement[0].GetProperty("SourceId").GetGuid().ShouldBe(SyntheticId(1));
        parsed.RootElement[9999].GetProperty("SourceId").GetGuid().ShouldBe(SyntheticId(10_000));
        restarted.Context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Batchless_review_preserves_explicit_profile_revision_after_restart()
    {
        await SeedAsync(); await using var s = Open();
        var profile = await s.Context.VetProfiles.SingleAsync();
        var review = Review(profile.Id, profile.Revision, "[]"); review.Kind = "delete_originals_selection";
        s.Context.Add(review); await s.Context.SaveChangesAsync();
        await using var restarted = Open();
        var stored = await restarted.Context.Set<VetPhotoReview>().SingleAsync();
        stored.BatchId.ShouldBeNull(); stored.ProfileId.ShouldBe(profile.Id);
        stored.ProfileRevision.ShouldBe(profile.Revision); stored.Kind.ShouldBe("delete_originals_selection");
    }

    [Fact]
    public async Task Missing_profile_proof_identity_rolls_back_review_and_run_together()
    {
        await SeedAsync(); await using var s = Open();
        var review = Review(9_999_999, 1, "[]");
        var run = new VetPhotoRun { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = Scope.ChatId, TopicId = Scope.TopicId,
            ActorUserId = 111, OperationKey = Guid.NewGuid(), SelectionReviewId = review.Id,
            SelectionMode = "all_originals", ModelName = "synthetic-model", CreatedAt = Now };
        s.Context.AddRange(review, run);
        var error = await Should.ThrowAsync<DbUpdateException>(() => s.Context.SaveChangesAsync());
        ((PostgresException)error.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        await using var restarted = Open();
        (await restarted.Context.Set<VetPhotoReview>().CountAsync()).ShouldBe(0);
        (await restarted.Context.Set<VetPhotoRun>().CountAsync()).ShouldBe(0);
    }
}
