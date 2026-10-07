using Assistant.Domain.Vet.Photos;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant.IntegrationTests.Vet.Photos;

public sealed class VetPhotoSchemaTests : VetTestBase
{
    private AssistantDbContext FamilyScope(long? family)
    {
        var current = new CurrentFamily(); current.Set(family);
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        return new(options.Options, current);
    }

    private async Task<Guid> GraphAsync()
    {
        await SeedAsync();
        await using var s = Open();
        var profile = await s.Context.VetProfiles.SingleAsync();
        var batch = new VetPhotoBatch { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, ProfileId = profile.Id,
            ProfileRevision = profile.Revision, StarterUserId = 111, CreatedAt = Now, IntakeOpenedAt = Now, UpdatedAt = Now };
        var source = new VetPhotoSource { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, BatchId = batch.Id,
            TelegramMessageId = 1000, SourceAuthorUserId = 111, ChatType = "supergroup",
            SentAt = Now, AdmittedAt = Now, CurrentOrdinal = 1, ItemNumber = 1 };
        var input = new VetPhotoInputRevision { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, SourceId = source.Id,
            Ordinal = 1, UpdateId = 1, FileId = "synthetic-file", Caption = "synthetic caption",
            InputFingerprint = new string('a', 64), ReceivedAt = Now };
        source.CurrentInputRevisionId = input.Id;
        var blob = new VetPhotoBlob { Id = Guid.NewGuid(), FamilyId = FamilyId, ContentHash = new string('b', 64),
            ActualBytes = 3, Content = [1, 2, 3], Format = "png", Width = 1, Height = 1, CreatedAt = Now };
        var reference = new VetPhotoOriginalReference { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, InputRevisionId = input.Id,
            BlobId = blob.Id, ContentHash = blob.ContentHash, ActualBytes = 3, Format = "png", Width = 1, Height = 1, RetainedAt = Now };
        var attempt = new VetPhotoAttempt { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, SourceId = source.Id,
            InputRevisionId = input.Id, ActorUserId = 111, CreatedAt = Now, UpdatedAt = Now };
        var result = new VetPhotoExtraction { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, SourceId = source.Id,
            InputRevisionId = input.Id, AttemptId = attempt.Id, ModelName = "synthetic-model",
            StructuredJson = "{\"synthetic\":true}", CreatedAt = Now };
        var candidate = new VetPhotoCandidate { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, SourceId = source.Id,
            BatchId = batch.Id, InputRevisionId = input.Id, ExtractionResultId = result.Id, UpdatedAt = Now };
        var review = new VetPhotoReview { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, BatchId = batch.Id,
            OperationKey = Guid.NewGuid(), Kind = "save", RequesterUserId = 111, CreatedAt = Now };
        var run = new VetPhotoRun { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, SelectionReviewId = review.Id,
            ActorUserId = 111, OperationKey = Guid.NewGuid(), CreatedAt = Now };
        var window = new VetPhotoRunWindow { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, RunId = run.Id, CreatedAt = Now };
        var reader = new VetPhotoReaderLease { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, BlobId = blob.Id,
            OriginalReferenceId = reference.Id, OriginalReferenceRevision = 1, AttemptId = attempt.Id,
            ClaimToken = Guid.NewGuid(), CreatedAt = Now, ExpiresAt = Now.AddMinutes(5) };
        s.Context.AddRange(batch, source, input, blob, reference, attempt, result, candidate, review, run, window, reader);
        await s.Context.SaveChangesAsync();
        return source.Id;
    }

    [Fact]
    public async Task Every_photo_table_fails_closed_without_matching_family()
    {
        await GraphAsync();
        foreach (var family in new long?[] { null, FamilyId + 100 })
        {
            await using var scoped = FamilyScope(family);
            (await scoped.Set<VetPhotoBatch>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoSource>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoInputRevision>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoBlob>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoCandidate>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoReview>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoRun>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoRunWindow>().CountAsync()).ShouldBe(0);
            (await scoped.Set<VetPhotoReaderLease>().CountAsync()).ShouldBe(0);
        }
        await using var own = FamilyScope(FamilyId);
        (await own.Set<VetPhotoBatch>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoSource>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoInputRevision>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoBlob>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoExtraction>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoCandidate>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoReview>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoAttempt>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoRun>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoRunWindow>().CountAsync()).ShouldBe(1);
        (await own.Set<VetPhotoReaderLease>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Null_topic_transport_key_is_unique_and_other_exact_topic_is_independent()
    {
        await GraphAsync();
        await using (var s = FamilyScope(FamilyId))
        {
            var original = await s.Set<VetPhotoSource>().AsNoTracking().SingleAsync();
            s.Add(new VetPhotoSource { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
                TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = 7,
                TelegramMessageId = original.TelegramMessageId, SourceAuthorUserId = 111,
                CurrentInputRevisionId = Guid.NewGuid(), SentAt = Now, AdmittedAt = Now });
            await s.SaveChangesAsync();
            (await s.Set<VetPhotoSource>().CountAsync()).ShouldBe(2);
        }
        await using (var s = FamilyScope(FamilyId))
        {
            s.Add(new VetPhotoSource { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
                TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, TelegramMessageId = 1000,
                SourceAuthorUserId = 222, CurrentInputRevisionId = Guid.NewGuid(), SentAt = Now, AdmittedAt = Now });
            var error = await Should.ThrowAsync<DbUpdateException>(() => s.SaveChangesAsync());
            error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
        await using var verify = FamilyScope(FamilyId);
        (await verify.Set<VetPhotoSource>().OrderBy(s => s.TopicId).Select(s => s.TopicId).ToListAsync())
            .ShouldBe(new int?[] { 7, null });
    }

    [Fact]
    public async Task Only_one_collecting_batch_exists_per_true_null_topic_scope()
    {
        await GraphAsync();
        await using var s = FamilyScope(FamilyId);
        var original = await s.Set<VetPhotoBatch>().AsNoTracking().SingleAsync();
        s.Add(new VetPhotoBatch { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null, ProfileId = original.ProfileId,
            ProfileRevision = original.ProfileRevision, StarterUserId = 222, CreatedAt = Now, IntakeOpenedAt = Now, UpdatedAt = Now });
        var error = await Should.ThrowAsync<DbUpdateException>(() => s.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        await using var verify = FamilyScope(FamilyId);
        (await verify.Set<VetPhotoBatch>().SingleAsync()).StarterUserId.ShouldBe(111);
    }

    [Fact]
    public async Task Content_hash_uniqueness_is_family_scoped_and_original_bytea_is_exact()
    {
        await GraphAsync();
        await using (var s = FamilyScope(FamilyId))
        {
            s.Add(new VetPhotoBlob { Id = Guid.NewGuid(), FamilyId = FamilyId + 100,
                ContentHash = new string('b', 64), ActualBytes = 2, Content = [4, 5],
                Format = "jpeg", Width = 1, Height = 1, CreatedAt = Now });
            await s.SaveChangesAsync();
            (await s.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(new byte[] { 1, 2, 3 });
        }
        await using (var other = FamilyScope(FamilyId + 100))
            (await other.Set<VetPhotoBlob>().SingleAsync()).Content.ShouldBe(new byte[] { 4, 5 });
        await using (var s = FamilyScope(FamilyId))
        {
            s.Add(new VetPhotoBlob { Id = Guid.NewGuid(), FamilyId = FamilyId,
                ContentHash = new string('b', 64), ActualBytes = 1, Content = [6], Format = "png", CreatedAt = Now });
            var error = await Should.ThrowAsync<DbUpdateException>(() => s.SaveChangesAsync());
            error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
    }

    [Fact]
    public async Task Source_revisions_are_unique_and_caption_boundary_roundtrips_completely()
    {
        var sourceId = await GraphAsync();
        await using (var s = FamilyScope(FamilyId))
        {
            var input = await s.Set<VetPhotoInputRevision>().SingleAsync();
            input.Caption = "synthetic-start-" + new string('x', 4096 - "synthetic-start-".Length);
            await s.SaveChangesAsync();
            await using var verify = FamilyScope(FamilyId);
            (await verify.Set<VetPhotoInputRevision>().SingleAsync()).Caption.ShouldBe(input.Caption);
        }
        await using (var s = FamilyScope(FamilyId))
        {
            s.Add(new VetPhotoInputRevision { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
                TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null,
                SourceId = sourceId, Ordinal = 1, UpdateId = 2, FileId = "other-synthetic-file", ReceivedAt = Now });
            var error = await Should.ThrowAsync<DbUpdateException>(() => s.SaveChangesAsync());
            error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
    }

    [Fact]
    public async Task Missing_input_reference_is_rejected_without_partial_original_row()
    {
        await GraphAsync();
        await using var s = FamilyScope(FamilyId);
        var blob = await s.Set<VetPhotoBlob>().SingleAsync();
        s.Add(new VetPhotoOriginalReference { Id = Guid.NewGuid(), FamilyId = FamilyId, BotDbId = Bot.BotDbId,
            TelegramBotId = Bot.TelegramBotId, ChatId = -100, TopicId = null,
            InputRevisionId = Guid.NewGuid(), BlobId = blob.Id, ContentHash = blob.ContentHash, RetainedAt = Now });
        var error = await Should.ThrowAsync<DbUpdateException>(() => s.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        await using var verify = FamilyScope(FamilyId);
        (await verify.Set<VetPhotoOriginalReference>().CountAsync()).ShouldBe(1);
    }
}
