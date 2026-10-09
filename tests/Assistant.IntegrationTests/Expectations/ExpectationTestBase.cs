using Assistant.Application.Common;
using Assistant.Application.Expectations;
using Assistant.Application.Messages;
using Assistant.Application.Reminders;
using Assistant.Domain.Bots;
using Assistant.Domain.Expectations;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Places;
using Assistant.Domain.Reminders;
using Assistant.Domain.Vet;
using Assistant.Infrastructure.Expectations;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Reminders;
using Assistant.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Assistant.IntegrationTests.Expectations;

public abstract class ExpectationTestBase : IntegrationTestBase
{
    protected static readonly DateTimeOffset Initial = DateTimeOffset.Parse("2032-02-09T12:00:00Z");
    protected static readonly ReminderScope HealthScope = new(11, 22, 999, "health", -100, 7, "supergroup", 111);
    protected static readonly ReminderScope VetScope = new(11, 23, 998, "vet", -100, 7, "supergroup", 111);
    protected static readonly ReminderScope OtherHealthScope = new(11, 24, 997, "health", -100, 7, "supergroup", 111);
    protected static readonly ReceivingBot HealthBot = new(22, 999, "synthetic_health_bot", 11, "health");
    protected static readonly ReceivingBot OtherHealthBot = new(24, 997, "synthetic_second_health_bot", 11, "health");
    protected readonly MutableClock Clock = new();
    private int _source = 100;

    protected int NextSource() => Interlocked.Increment(ref _source);

    protected async Task SeedAsync()
    {
        Db.Families.AddRange(new Family { Id = 11, Name = "Synthetic family", CreatedAt = Initial },
            new Family { Id = 12, Name = "Second synthetic family", CreatedAt = Initial });
        Db.Bots.AddRange(new Bot { Id = 22, FamilyId = 11, TelegramBotId = 999, Username = "synthetic_health_bot",
                Role = "health", Status = BotStatus.Active, CreatedAt = Initial },
            new Bot { Id = 23, FamilyId = 11, TelegramBotId = 998, Username = "synthetic_vet_bot",
                Role = "vet", Status = BotStatus.Active, CreatedAt = Initial },
            new Bot { Id = 24, FamilyId = 11, TelegramBotId = 997, Username = "synthetic_second_health_bot",
                Role = "health", Status = BotStatus.Active, CreatedAt = Initial },
            new Bot { Id = 25, FamilyId = 12, TelegramBotId = 996, Username = "synthetic_other_family_bot",
                Role = "health", Status = BotStatus.Active, CreatedAt = Initial });
        foreach (var family in new long[] { 11, 12 })
        foreach (var actor in new long[] { 111, 222 })
            Db.FamilyMembers.Add(new FamilyMember { FamilyId = family, TelegramUserId = actor,
                IsOwner = actor == 222, Status = FamilyMemberStatus.Approved, CreatedAt = Initial, UpdatedAt = Initial });
        foreach (var bot in new long[] { 22, 23, 24, 25 })
        foreach (var topic in new int?[] { null, 7, 8 })
            Db.Places.Add(new Place { BotId = bot, ChatId = -100, TopicId = topic,
                Title = "Synthetic place", Status = PlaceStatus.Approved, CreatedAt = Initial });
        Db.Places.Add(new Place { BotId = 22, ChatId = -101, TopicId = 7,
            Title = "Synthetic second chat", Status = PlaceStatus.Approved, CreatedAt = Initial });
        Db.HealthProfiles.AddRange(new HealthProfile { Id = 333, FamilyId = 11, BotId = 22,
                SubjectTag = "synthetic_subject", CreatedAt = Initial, UpdatedAt = Initial },
            new HealthProfile { Id = 334, FamilyId = 11, BotId = 24,
                SubjectTag = "synthetic_second_subject", CreatedAt = Initial, UpdatedAt = Initial },
            new HealthProfile { Id = 335, FamilyId = 12, BotId = 25,
                SubjectTag = "synthetic_other_subject", CreatedAt = Initial, UpdatedAt = Initial });
        Db.VetProfiles.Add(new VetProfile { Id = 444, FamilyId = 11, BotDbId = 23,
            Name = "Synthetic animal", TimeZone = "UTC", GlucoseUnit = "mmol/L", InsulinUnit = "U", UpdatedAt = Initial });
        foreach (var family in new long[] { 11, 12 })
        foreach (var actor in new long[] { 111, 222 })
            Db.Add(new ReminderPreference { FamilyId = family, ActorUserId = actor, OffsetMinutes = 180,
                QuietStartMinute = 1320, QuietEndMinute = 480 });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    protected Session Open(long? familyId = 11, params IInterceptor[] interceptors) =>
        new(ConnectionString, familyId, Clock, interceptors);

    protected async Task<ExpectationIntake> CreateAsync(ReminderScope? scope = null, string type = "glucose",
        int deadline = 540, int grace = 30)
    {
        await using var session = Open((scope ?? HealthScope).FamilyId);
        return await session.Store.ExecuteAsync(scope ?? HealthScope, NextSource(),
            new("create", EventType: type, DeadlineMinute: deadline, GraceMinutes: grace), default);
    }

    protected async Task<ExpectationPreview> DeliverAsync(ExpectationIntake intake, int message = 700)
    {
        intake.Kind.ShouldBe("preview");
        await using var session = Open(intake.Scope.FamilyId);
        var preview = (await session.Store.BeginPreviewAsync(intake.Scope, intake.DraftId!.Value, default)).ShouldNotBeNull();
        await session.Store.BindPreviewAsync(intake.Scope, preview.DraftId, message, default);
        return preview;
    }

    protected async Task<ExpectationPreview> ActiveAsync(ReminderScope? scope = null, string type = "glucose",
        int deadline = 540, int grace = 30)
    {
        var intake = await CreateAsync(scope, type, deadline, grace);
        var preview = await DeliverAsync(intake);
        await using var session = Open(intake.Scope.FamilyId);
        (await session.Store.ResolveAsync(intake.Scope, preview.DraftId, 700, true, default)).ShouldBe("saved");
        return preview;
    }

    protected async Task<Expectation> RowAsync(Guid id)
    {
        await using var read = Open();
        return await read.Db.Set<Expectation>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    protected async Task<ExpectationDraft> DraftRowAsync(Guid id)
    {
        await using var read = Open();
        return await read.Db.Set<ExpectationDraft>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    protected sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Initial;
    }

    protected sealed class Session : IAsyncDisposable
    {
        public AssistantDbContext Db { get; }
        public CurrentFamily Family { get; } = new();
        public ExpectationStore Store { get; }
        public ReminderStore Reminders { get; }
        public NonurgentDispatchStore Dispatch { get; }

        public Session(string connection, long? family, IClock clock, IInterceptor[] interceptors)
        {
            Family.Set(family);
            var options = new DbContextOptionsBuilder<AssistantDbContext>();
            AssistantDbContext.Configure(options, connection);
            options.AddInterceptors(interceptors);
            Db = new(options.Options, Family);
            Store = new(Db, Family, clock);
            Reminders = new(Db, Family, clock);
            Dispatch = new(Db, Family, clock);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
