using Assistant.Application.Common;
using Assistant.Application.Diagnostics;
using Assistant.Application.Health;
using Assistant.Application.Llm;
using Assistant.Application.Manager;
using Assistant.Application.Messages;
using Assistant.Application.Telegram;
using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Infrastructure.Families;
using Assistant.Infrastructure.Llm;
using Assistant.Infrastructure.Persistence;
using Assistant.Infrastructure.Roles;
using Assistant.Infrastructure.Telegram;
using Assistant.Infrastructure.Vet;
using Assistant.IntegrationTests.Host;
using Assistant.IntegrationTests.Infrastructure;
using Assistant.IntegrationTests.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.IntegrationTests.Vet;

public abstract class VetTestBase : IntegrationTestBase
{
    protected static readonly DateTimeOffset Now = DateTimeOffset.Parse("2031-05-12T12:00:00Z");
    protected readonly VetClock Clock = new();
    protected long FamilyId;
    protected ReceivingBot Bot = null!;
    protected VetDiaryScope Scope => new(FamilyId, Bot.BotDbId, Bot.TelegramBotId, -100, 7);

    protected async Task SeedAsync()
    {
        var family = new Family { Name = "synthetic family", CreatedAt = Now };
        Db.Add(family); await Db.SaveChangesAsync(); FamilyId = family.Id;
        Db.AddRange(new FamilyMember { FamilyId = FamilyId, TelegramUserId = 111, DisplayName = "synthetic owner",
                IsOwner = true, Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now },
            new FamilyMember { FamilyId = FamilyId, TelegramUserId = 222, DisplayName = "synthetic member",
                Status = FamilyMemberStatus.Approved, CreatedAt = Now, UpdatedAt = Now });
        var row = new Bot { FamilyId = FamilyId, TelegramBotId = 1001, Username = "synthetic_vet_bot",
            Role = "vet", Status = BotStatus.Active, CreatedAt = Now };
        Db.Add(row); await Db.SaveChangesAsync();
        Bot = new(row.Id, row.TelegramBotId, row.Username, FamilyId, row.Role);
        Db.AddRange(new Place { BotId = row.Id, ChatId = -100, TopicId = 7, Title = "synthetic topic",
                Status = PlaceStatus.Approved, ReplyToAll = true, CreatedAt = Now },
            new Place { BotId = row.Id, ChatId = -100, TopicId = 8, Title = "synthetic topic",
                Status = PlaceStatus.Approved, ReplyToAll = false, CreatedAt = Now });
        await Db.SaveChangesAsync();
        await using var session = Open();
        var p = await session.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, CancellationToken.None);
        await session.Profiles.UpdateAsync(FamilyId, Bot.BotDbId, 111, p.Revision,
            [new("TimeZone", "UTC"), new("GlucoseUnit", "mmol/L"), new("InsulinUnit", "U")], CancellationToken.None);
    }

    protected VetTestSession Open(long? familyId = null, IInterceptor? interceptor = null,
        VetRuntimeOptions? runtimeOptions = null, ITraceSession? trace = null, ILogger<VetAssistant>? logger = null,
        IVetPhotoAssistant? photos = null)
        => new(ConnectionString, familyId ?? FamilyId, Bot, Clock, interceptor, runtimeOptions, trace, logger, photos);
    protected VetTestSession Unscoped() => new(ConnectionString, null, Bot, Clock);
    protected static IncomingMessage Text(string text, int id = 1000, long actor = 111, int topic = 7) =>
        new(-100, "supergroup", "synthetic topic", topic, id, actor, "synthetic_user", text,
            MessageKind.Text, false, Now, null, null, "{}", null, null);

    protected async Task<(VetAdmittedSource Source, VetEventState State, VetProfile Profile)> EvidenceAsync(
        VetTestSession s, int id = 1000, long update = 1, string type = "glucose", string value = "6.4",
        IncomingMessage? message = null)
    {
        message ??= Text("synthetic actual report", id);
        var scope = VetDiaryScope.From(Bot, message);
        var admitted = await s.Diary.AdmitAsync(scope, message, update, CancellationToken.None);
        var stored = await s.Messages.StoreAsync(Bot.TelegramBotId, update, message, CancellationToken.None);
        await s.Diary.LinkMessageAsync(scope, admitted.Source.Id, stored.MessageDbId!.Value, CancellationToken.None);
        await s.Diary.SetProcessingAsync(scope, admitted.Revision.Id, "admitted", "dispatching", null, CancellationToken.None);
        await s.Diary.SaveResultAsync(scope, admitted.Revision.Id, "{\"needs_reply\":false,\"events\":[]}",
            "synthetic-model", null, CancellationToken.None);
        admitted = (await s.Diary.GetSourceAsync(scope, admitted.Source.Id, CancellationToken.None))!;
        var result = (await s.Diary.GetResultAsync(scope, admitted.Revision.Id, CancellationToken.None))!;
        var profile = await s.Profiles.GetOrCreateAsync(FamilyId, Bot.BotDbId, CancellationToken.None);
        var state = VetEventValidation.Validate(new(type, "record", value, null, null, null, null, null,
            "current", 0), profile, admitted, result.Id).State!;
        return (admitted, state, profile);
    }

    protected static VetDiaryMutation Save(VetDiaryScope scope, VetAdmittedSource source, VetProfile profile,
        params VetEventState[] states) => new(scope, source.Revision.OperationKey, source.Source.SourceAuthorUserId,
            "save", profile.Id, states.Select(state => new VetEventChange(null, null, state)).ToArray(),
            source.Source.Id, source.Revision.Id);

    protected sealed class VetClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    protected sealed class VetTestSession : IAsyncDisposable, IDisposable
    {
        public AssistantDbContext Context { get; }
        public CurrentFamily Current { get; } = new();
        public VetDiaryStore Diary { get; }
        public VetProfileStore Profiles { get; }
        public MessageStore Messages { get; }
        public VetAssistant Assistant { get; }
        public UpdateHandler Handler { get; }
        public FakeTelegramClient Telegram { get; } = new();
        public ScriptedChatClient Chat { get; } = new();
        public VetTestSession(string connectionString, long? familyId, ReceivingBot bot, IClock clock,
            IInterceptor? interceptor = null, VetRuntimeOptions? runtimeOptions = null,
            ITraceSession? trace = null, ILogger<VetAssistant>? logger = null, IVetPhotoAssistant? photos = null)
        {
            Current.Set(familyId);
            var options = new DbContextOptionsBuilder<AssistantDbContext>();
            AssistantDbContext.Configure(options, connectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            Context = new(options.Options, Current);
            var ownership = new FamilyOwnership(Context);
            Diary = new(Context, Current, clock);
            Profiles = new(Context, Current, ownership, clock);
            Messages = new(Context, clock, NullLogger<MessageStore>.Instance);
            var config = new LlmConfig
            {
                Models = [new("codex-cli", "synthetic-model")], FastModels = [],
                CallsPerMinute = 1000, CallsPerDay = 10000, MaxContextMessages = 20, MaxInputChars = 32000,
                MaxOutputTokens = 2048, CallTimeoutSeconds = 5, MaxConcurrentCalls = 2, ModelCooldownMinutes = 1,
                Prices = new Dictionary<string, ModelPrice> { ["synthetic-model"] = new(0, 0) }, Budget = null
            };
            var gateway = new LlmGateway(config, new ModelCatalog(config), new ModelAvailability(clock),
                new ChatClientProvider(new Dictionary<string, IChatClient> { ["codex-cli"] = Chat }),
                Context, clock, new ConcurrentCallGate(2), new BudgetGuard(config, Context, clock),
                new NoopBudget(), NullLogger<LlmGateway>.Instance);
            var botOptions = Options.Create(new BotOptions { ManagerToken = "test-manager-token",
                TokenEncryptionKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=" });
            var approvals = new ApprovalService(Context, new ClientFactory(Telegram), botOptions, clock);
            var build = new BuildInfo("abcdef1", null, Now);
            Assistant = new(Profiles, Diary, approvals, ownership, Messages, gateway,
                new RolePrompts(typeof(RolePrompts).Assembly), clock, build, runtimeOptions ?? new(true, 20, 32000),
                logger ?? NullLogger<VetAssistant>.Instance, trace, photos);
            Handler = new(Messages, approvals, Current, new NoopManager(), new NoopGeneral(), new NoopHealth(),
                botOptions, build, clock, NullLogger<UpdateHandler>.Instance, trace, vetAssistant: Assistant);
        }
        public ValueTask DisposeAsync() => Context.DisposeAsync();
        public void Dispose() => Context.Dispose();
        private sealed class ClientFactory(FakeTelegramClient client) : ITelegramClientFactory
        { public ITelegramClient Create(string token) => client; }
        private sealed class NoopBudget : IBudgetNoticeDispatcher { public Task Dispatch() => Task.CompletedTask; }
        private sealed class NoopManager : IManagerUpdateHandler
        { public Task HandleAsync(ReceivingBot b, ITelegramClient c, IncomingUpdate u, CancellationToken ct) => Task.CompletedTask; }
        private sealed class NoopGeneral : IGeneralAssistant
        { public Task HandleAsync(ReceivingBot b, ITelegramClient c, IncomingMessage m, StoreResult s, CancellationToken ct, bool replyToAll = false) => Task.CompletedTask; }
        private sealed class NoopHealth : IHealthAssistant
        {
            public Task HandleAsync(ReceivingBot b, ITelegramClient c, IncomingMessage m, StoreResult s, CancellationToken ct, bool replyToAll = false) => Task.CompletedTask;
            public Task HandleCallbackAsync(ReceivingBot b, ITelegramClient c, CallbackQueryInfo q, CancellationToken ct) => Task.CompletedTask;
        }
    }
}
