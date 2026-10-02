using Assistant.Application.Common;
using Assistant.Application.Health;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Health;
using Assistant.Infrastructure.Persistence;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assistant.IntegrationTests.Health;

public class HealthProfileStoreTests : IntegrationTestBase
{
    private static readonly string[] SeedKeys =
    {
        "glucose.any", "glucose.fasting", "glucose.after_1h", "glucose.after_2h",
        "blood_pressure.systolic", "blood_pressure.diastolic", "combo.bp_symptoms",
        "symptom.vision_disturbance", "symptom.epigastric_pain",
        "symptom.bleeding", "symptom.seizure"
    };

    // A fresh request-like scope: its own context and CurrentFamily (left unset when familyId is null).
    private (AssistantDbContext Context, HealthProfileStore Store) OpenScope(long? familyId)
    {
        var currentFamily = new CurrentFamily();
        if (familyId is not null)
        {
            currentFamily.Set(familyId);
        }

        var options = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(options, ConnectionString);
        var context = new AssistantDbContext(options.Options, currentFamily);
        return (context, new HealthProfileStore(context, currentFamily, new SystemClock()));
    }

    private async Task<HealthProfileInfo> CreateProfileAsync(long familyId, long botDbId)
    {
        var (context, store) = OpenScope(familyId);
        await using (context)
        {
            return await store.GetOrCreateAsync(familyId, botDbId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetOrCreate_creates_one_profile_with_defaults_and_the_default_rules_once()
    {
        var info = await CreateProfileAsync(1, 10);

        info.StageStartDate.ShouldBeNull();
        info.TimeZone.ShouldBe("UTC");
        info.EmergencyPhone.ShouldBe("103 или 112");
        info.ContextNote.ShouldBeNull();

        var profiles = await Db.HealthProfiles.ToListAsync();
        profiles.Count.ShouldBe(1);
        profiles[0].FamilyId.ShouldBe(1);
        profiles[0].BotId.ShouldBe(10);
        profiles[0].SubjectTag.ShouldBe("health");
        profiles[0].Id.ShouldBe(info.Id);

        var rules = await Db.SafetyRules.ToListAsync();
        rules.Count.ShouldBe(11);
        rules.ShouldAllBe(r => r.FamilyId == 1 && r.ProfileId == info.Id && r.Source == "guideline_default");
        var glucose = rules.Single(r => r.RuleKey == "glucose.any");
        glucose.LowUrgent.ShouldBe(3.0m);
        glucose.LowAlert.ShouldBe(3.9m);
        glucose.TargetHigh.ShouldBeNull();
        glucose.HighAlert.ShouldBe(11.0m);
        glucose.HighUrgent.ShouldBe(13.9m);

        var again = await CreateProfileAsync(1, 10);
        again.Id.ShouldBe(info.Id);
        (await Db.HealthProfiles.CountAsync()).ShouldBe(1);
        (await Db.SafetyRules.CountAsync()).ShouldBe(11);
    }

    [Fact]
    public async Task GetRules_returns_the_rules_in_seed_order()
    {
        var info = await CreateProfileAsync(1, 10);

        var (context, store) = OpenScope(1);
        await using (context)
        {
            var rules = await store.GetRulesAsync(1, info.Id, CancellationToken.None);

            rules.Select(r => r.RuleKey).ToArray().ShouldBe(SeedKeys);
            rules.Single(r => r.RuleKey == "combo.bp_symptoms").WindowHours.ShouldBe(24);
        }
    }

    [Fact]
    public async Task SaveProfile_persists_the_editable_fields_and_who_changed_them()
    {
        var info = await CreateProfileAsync(1, 10);

        var (writeContext, writeStore) = OpenScope(1);
        await using (writeContext)
        {
            await writeStore.SaveProfileAsync(
                1,
                info with { StageStartDate = new DateOnly(2030, 1, 15), TimeZone = "Europe/Berlin", EmergencyPhone = "112", ContextNote = "test note" },
                111,
                CancellationToken.None);
        }

        var reloaded = await CreateProfileAsync(1, 10);
        reloaded.StageStartDate.ShouldBe(new DateOnly(2030, 1, 15));
        reloaded.TimeZone.ShouldBe("Europe/Berlin");
        reloaded.EmergencyPhone.ShouldBe("112");
        reloaded.ContextNote.ShouldBe("test note");
        (await Db.HealthProfiles.SingleAsync()).UpdatedByUserId.ShouldBe(111);
    }

    [Fact]
    public async Task SaveRule_replaces_one_rule_and_leaves_the_others()
    {
        var info = await CreateProfileAsync(1, 10);
        var original = SafetyRuleDefaults.Find("glucose.any")!;

        var (writeContext, writeStore) = OpenScope(1);
        await using (writeContext)
        {
            await writeStore.SaveRuleAsync(1, info.Id, original with { LowAlert = 4.0m, Source = "doctor" }, 111, CancellationToken.None);
        }

        var (readContext, readStore) = OpenScope(1);
        await using (readContext)
        {
            var rules = await readStore.GetRulesAsync(1, info.Id, CancellationToken.None);

            var changed = rules.Single(r => r.RuleKey == "glucose.any");
            changed.LowAlert.ShouldBe(4.0m);
            changed.Source.ShouldBe("doctor");
            changed.LowUrgent.ShouldBe(3.0m);
            changed.HighAlert.ShouldBe(11.0m);
            changed.HighUrgent.ShouldBe(13.9m);

            var untouched = rules.Single(r => r.RuleKey == "glucose.fasting");
            untouched.TargetHigh.ShouldBe(5.1m);
            untouched.Source.ShouldBe("guideline_default");
        }

        (await Db.SafetyRules.SingleAsync(r => r.RuleKey == "glucose.any")).UpdatedByUserId.ShouldBe(111);
    }

    [Fact]
    public async Task Every_method_throws_when_the_family_scope_is_unset_or_another_family()
    {
        var (unsetContext, unsetStore) = OpenScope(null);
        await using (unsetContext)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => unsetStore.GetOrCreateAsync(1, 10, CancellationToken.None));
        }

        var (context, store) = OpenScope(2);
        await using (context)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => store.GetOrCreateAsync(1, 10, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() => store.GetRulesAsync(1, 5, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(
                () => store.SaveProfileAsync(1, new HealthProfileInfo(5, null, "UTC", "112", null), 111, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(
                () => store.SaveRuleAsync(1, 5, SafetyRuleDefaults.All[0], 111, CancellationToken.None));
        }

        (await Db.HealthProfiles.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_family_scope_never_reads_or_writes_another_familys_profile()
    {
        await CreateProfileAsync(1, 10);
        var profileB = await CreateProfileAsync(2, 20);

        var (context, store) = OpenScope(1);
        await using (context)
        {
            (await store.GetRulesAsync(1, profileB.Id, CancellationToken.None)).ShouldBeEmpty();

            var tampered = SafetyRuleDefaults.Find("glucose.any")! with { LowAlert = 4.0m };
            await Should.ThrowAsync<InvalidOperationException>(
                () => store.SaveRuleAsync(1, profileB.Id, tampered, 111, CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(
                () => store.SaveProfileAsync(1, profileB with { ContextNote = "x" }, 111, CancellationToken.None));
        }

        var ruleB = await Db.SafetyRules.SingleAsync(r => r.ProfileId == profileB.Id && r.RuleKey == "glucose.any");
        ruleB.LowAlert.ShouldBe(3.9m);
        (await Db.HealthProfiles.SingleAsync(p => p.Id == profileB.Id)).ContextNote.ShouldBeNull();
    }
}
