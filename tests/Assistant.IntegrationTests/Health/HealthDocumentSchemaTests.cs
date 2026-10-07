using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Messages;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assistant.IntegrationTests.Health;

public sealed class HealthDocumentSchemaTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2030, 4, 10, 10, 0, 0, TimeSpan.Zero);
    private async Task<Guid> SeedAsync()
    {
        Db.Families.Add(new() { Id = 42, Name = "synthetic family", CreatedAt = Now });
        Db.Bots.Add(new() { Id = 10, FamilyId = 42, TelegramBotId = 999, Username = "test_health_bot", Role = "health", Status = BotStatus.Active, CreatedAt = Now });
        Db.HealthProfiles.Add(new() { Id = 1, FamilyId = 42, BotId = 10, CreatedAt = Now, UpdatedAt = Now });
        Db.Messages.Add(new() { Id = 22, FamilyId = 42, BotId = 999, ChatId = -100, TopicId = 7, TelegramMessageId = 33,
            UserId = 111, ChatType = "supergroup", Kind = MessageKind.Document, SentAt = Now, CreatedAt = Now, Raw = "{}" });
        await Db.SaveChangesAsync();
        var admission = NewAdmission();
        Db.HealthDocumentAdmissions.Add(admission);
        await Db.SaveChangesAsync();
        return admission.Id;
    }
    private static HealthDocumentAdmission NewAdmission(int messageId = 33) => new()
    {
        Id = Guid.NewGuid(), FamilyId = 42, ProfileId = 1, BotDbId = 10, TelegramBotId = 999,
        ChatId = -100, TopicId = 7, ChatType = "supergroup", TelegramMessageId = messageId, SenderUserId = 111,
        FirstUpdateId = 100, SentAt = Now, FileId = "synthetic-file", CreatedAt = Now, UpdatedAt = Now
    };
    private static HealthDocument NewDocument(Guid admission) => new()
    {
        FamilyId = 42, ProfileId = 1, AdmissionId = admission, SourceMessageId = 22, TelegramFileId = "synthetic-file",
        PostedByUserId = 111, PostedAt = Now, CreatedAt = Now, UpdatedAt = Now
    };
    private AssistantDbContext Scope(long? family)
    {
        var current = new CurrentFamily();
        current.Set(family);
        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        return new(options.Options, current);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("profile")]
    [InlineData("admission")]
    public async Task Document_requires_existing_bound_source_profile_and_admission(string missing)
    {
        var admission = await SeedAsync();
        var document = NewDocument(admission);
        if (missing == "source") document.SourceMessageId = 0;
        else if (missing == "profile") document.ProfileId = 222;
        else document.AdmissionId = Guid.NewGuid();
        Db.HealthDocuments.Add(document);
        var error = await Should.ThrowAsync<DbUpdateException>(() => Db.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        await using var correct = Scope(42);
        (await correct.HealthDocuments.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Transport_admission_key_and_document_source_are_independently_unique()
    {
        var admission = await SeedAsync();
        await using (var duplicateScope = Scope(42))
        {
            duplicateScope.HealthDocumentAdmissions.Add(NewAdmission());
            var error = await Should.ThrowAsync<DbUpdateException>(() => duplicateScope.SaveChangesAsync());
            error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
        Db.HealthDocuments.Add(NewDocument(admission));
        var otherAdmission = NewAdmission(34);
        Db.HealthDocumentAdmissions.Add(otherAdmission);
        await Db.SaveChangesAsync();
        await using (var duplicateScope = Scope(42))
        {
            duplicateScope.HealthDocuments.Add(NewDocument(otherAdmission.Id));
            var error = await Should.ThrowAsync<DbUpdateException>(() => duplicateScope.SaveChangesAsync());
            error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
        await using var correct = Scope(42);
        (await correct.HealthDocuments.CountAsync()).ShouldBe(1);
        (await correct.HealthDocumentAdmissions.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Unbound_admission_is_valid_but_direct_reads_fail_closed_without_matching_family()
    {
        var admission = await SeedAsync();
        Db.HealthDocuments.Add(NewDocument(admission));
        await Db.SaveChangesAsync();
        foreach (var family in new long?[] { null, 222 })
        {
            await using var scope = Scope(family);
            (await scope.HealthDocuments.CountAsync()).ShouldBe(0);
            (await scope.HealthDocumentAdmissions.CountAsync()).ShouldBe(0);
        }
        await using var correct = Scope(42);
        (await correct.HealthDocumentAdmissions.SingleAsync()).SourceMessageId.ShouldBeNull();
        (await correct.HealthDocuments.SingleAsync()).SourceMessageId.ShouldBe(22);
    }

    [Fact]
    public async Task Bounded_text_metadata_and_original_posted_time_survive_database_roundtrip()
    {
        var admission = await SeedAsync();
        var document = NewDocument(admission);
        document.Text = "synthetic-start-" + new string('x', 200000 - "synthetic-start-".Length);
        document.FileName = new string('n', 255);
        document.Caption = new string('c', 4096);
        document.TextStatus = "read";
        document.TextTruncated = true;
        document.CreatedAt = Now.AddDays(1);
        Db.HealthDocuments.Add(document);
        await Db.SaveChangesAsync();
        await using var correct = Scope(42);
        var retained = await correct.HealthDocuments.SingleAsync();
        retained.Text.ShouldBe(document.Text);
        retained.FileName.ShouldBe(document.FileName);
        retained.Caption.ShouldBe(document.Caption);
        retained.TextStatus.ShouldBe("read");
        retained.TextTruncated.ShouldBeTrue();
        retained.PostedAt.ShouldBe(Now);
        retained.CreatedAt.ShouldBe(Now.AddDays(1));
    }
}
