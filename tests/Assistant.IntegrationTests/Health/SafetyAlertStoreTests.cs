using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Domain.Health;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Health;

public class SafetyAlertStoreTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2030, 2, 7, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private (AssistantDbContext Context, SafetyAlertStore Store) OpenScope(long? familyId)
    {
        var currentFamily = new CurrentFamily();
        if (familyId is not null)
        {
            currentFamily.Set(familyId);
        }

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        var context = new AssistantDbContext(options.Options, currentFamily);
        return (context, new SafetyAlertStore(context, currentFamily, new FixedClock()));
    }

    private async Task<long> AddProfileAsync(long familyId, long botId)
    {
        var profile = new HealthProfile { FamilyId = familyId, BotId = botId, CreatedAt = Now, UpdatedAt = Now };
        Db.HealthProfiles.Add(profile);
        await Db.SaveChangesAsync();
        return profile.Id;
    }

    private static HealthEvent NewEventRow(long familyId, long profileId, bool deleted = false) => new()
    {
        FamilyId = familyId,
        ProfileId = profileId,
        Type = "glucose",
        SubjectTag = "health",
        OccurredAt = Now,
        OccurredAtSource = "message",
        Payload = "{\"value\":2.5,\"context\":\"other\"}",
        BotId = 1001,
        ChatId = -100,
        CreatedAt = Now,
        UpdatedAt = Now,
        DeletedAt = deleted ? Now : null,
        DeleteReason = deleted ? "del" : null
    };

    private async Task<long> AddEventAsync(long familyId, long profileId, bool deleted = false)
    {
        var row = NewEventRow(familyId, profileId, deleted);
        Db.Events.Add(row);
        await Db.SaveChangesAsync();
        return row.Id;
    }

    private static NewSafetyAlert Alert(long eventId, string ruleKey = "glucose.any", decimal? threshold = 3.0m, int? topicId = 7) =>
        new(eventId, ruleKey, "urgent", threshold, "guideline_default", -100, topicId);

    private async Task<bool> ClaimAsync(long scopeFamilyId, long familyId, NewSafetyAlert alert)
    {
        var (context, store) = OpenScope(scopeFamilyId);
        await using (context)
        {
            return await store.TryClaimAsync(familyId, alert, CancellationToken.None);
        }
    }

    [Fact]
    public async Task First_claim_inserts_the_row()
    {
        var profile = await AddProfileAsync(1, 10);
        var e = await AddEventAsync(1, profile);

        (await ClaimAsync(1, 1, Alert(e))).ShouldBeTrue();

        var row = await Db.SafetyAlerts.AsNoTracking().SingleAsync();
        row.FamilyId.ShouldBe(1);
        row.EventId.ShouldBe(e);
        row.RuleKey.ShouldBe("glucose.any");
        row.Level.ShouldBe("urgent");
        row.Threshold.ShouldBe(3.00m);
        row.ThresholdSource.ShouldBe("guideline_default");
        row.ChatId.ShouldBe(-100);
        row.TopicId.ShouldBe(7);
        row.CreatedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Second_claim_returns_false_and_the_context_stays_usable()
    {
        var profile = await AddProfileAsync(1, 10);
        var e = await AddEventAsync(1, profile);

        var (context, store) = OpenScope(1);
        await using (context)
        {
            (await store.TryClaimAsync(1, Alert(e), CancellationToken.None)).ShouldBeTrue();
            (await store.TryClaimAsync(1, Alert(e), CancellationToken.None)).ShouldBeFalse();

            context.ChangeTracker.Entries<SafetyAlert>().ShouldBeEmpty();
            context.Events.Add(NewEventRow(1, profile));
            await context.SaveChangesAsync();
        }

        (await Db.SafetyAlerts.CountAsync()).ShouldBe(1);
        (await Db.Events.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_claim_from_another_scope_is_refused_too()
    {
        var profile = await AddProfileAsync(1, 10);
        var e = await AddEventAsync(1, profile);

        (await ClaimAsync(1, 1, Alert(e))).ShouldBeTrue();
        (await ClaimAsync(1, 1, Alert(e))).ShouldBeFalse();

        (await Db.SafetyAlerts.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_claims_for_one_event_and_rule_have_exactly_one_winner()
    {
        var profile = await AddProfileAsync(1, 10);
        var e = await AddEventAsync(1, profile);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ClaimAsync(1, 1, Alert(e)))));

        results.Count(r => r).ShouldBe(1);
        (await Db.SafetyAlerts.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Another_rule_for_the_same_event_is_a_separate_alert()
    {
        var profile = await AddProfileAsync(1, 10);
        var e = await AddEventAsync(1, profile);

        (await ClaimAsync(1, 1, Alert(e, "glucose.any"))).ShouldBeTrue();
        (await ClaimAsync(1, 1, Alert(e, "combo.bp_symptoms", threshold: null))).ShouldBeTrue();

        (await Db.SafetyAlerts.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Null_threshold_and_topic_are_stored_as_null()
    {
        var profile = await AddProfileAsync(1, 10);
        var e = await AddEventAsync(1, profile);

        (await ClaimAsync(1, 1, new NewSafetyAlert(e, "combo.bp_symptoms", "urgent", null, "doctor", -100, null))).ShouldBeTrue();

        var row = await Db.SafetyAlerts.AsNoTracking().SingleAsync();
        row.Threshold.ShouldBeNull();
        row.TopicId.ShouldBeNull();
        row.ThresholdSource.ShouldBe("doctor");
    }

    [Fact]
    public async Task Another_familys_or_a_deleted_event_is_never_claimed()
    {
        var profileA = await AddProfileAsync(1, 10);
        var profileB = await AddProfileAsync(2, 20);
        var b = await AddEventAsync(2, profileB);
        var d = await AddEventAsync(1, profileA, deleted: true);

        (await ClaimAsync(1, 1, Alert(b))).ShouldBeFalse();
        (await ClaimAsync(1, 1, Alert(d))).ShouldBeFalse();

        (await Db.SafetyAlerts.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2L)]
    public async Task Throws_when_the_family_scope_is_unset_or_another_family(long? scopeFamilyId)
    {
        var profile = await AddProfileAsync(1, 10);
        var e = await AddEventAsync(1, profile);

        var (context, store) = OpenScope(scopeFamilyId);
        await using (context)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => store.TryClaimAsync(1, Alert(e), CancellationToken.None));
        }

        (await Db.SafetyAlerts.CountAsync()).ShouldBe(0);
    }
}
