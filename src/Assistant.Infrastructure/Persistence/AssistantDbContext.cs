using Assistant.Application.Families;
using Assistant.Domain.Bots;
using Assistant.Domain.Diagnostics;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Llm;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Assistant.Domain.Vet;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Persistence;

public class AssistantDbContext : DbContext
{
    // Never null: a null ICurrentFamily argument (design-time tooling, test helpers that construct
    // this type directly with one argument, IgnoreQueryFilters() call sites) is normalized to
    // UnscopedCurrentFamily below, whose FamilyId is always null, so the query filters can
    // dereference _currentFamily.FamilyId directly. EF Core's query-filter parameter extraction
    // evaluates captured member accesses like `_currentFamily.FamilyId` eagerly while building the
    // parameterized query — a `_currentFamily == null || _currentFamily.FamilyId == null || ...`
    // guard throws NullReferenceException whenever _currentFamily is actually null, because C#'s
    // runtime OrElse short-circuit does not apply to how EF evaluates the expression tree for
    // parameterization. Keeping the field itself non-null avoids that trap entirely.
    private readonly ICurrentFamily _currentFamily;

    public AssistantDbContext(DbContextOptions<AssistantDbContext> options, ICurrentFamily? currentFamily = null)
        : base(options)
    {
        _currentFamily = currentFamily ?? UnscopedCurrentFamily.Instance;
    }

    public DbSet<StoredMessage> Messages => Set<StoredMessage>();

    public DbSet<Assistant.Domain.Memory.GeneralMemoryFact> GeneralMemoryFacts => Set<Assistant.Domain.Memory.GeneralMemoryFact>();
    public DbSet<Assistant.Domain.Memory.GeneralMemoryState> GeneralMemoryStates => Set<Assistant.Domain.Memory.GeneralMemoryState>();

    public DbSet<ChatMigration> ChatMigrations => Set<ChatMigration>();

    public DbSet<Family> Families => Set<Family>();

    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

    public DbSet<Bot> Bots => Set<Bot>();

    public DbSet<Place> Places => Set<Place>();

    public DbSet<PendingBotCreation> PendingBotCreations => Set<PendingBotCreation>();

    public DbSet<ChatSetting> ChatSettings => Set<ChatSetting>();

    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();

    public DbSet<DebugTrace> DebugTraces => Set<DebugTrace>();

    public DbSet<DebugTraceEvent> DebugTraceEvents => Set<DebugTraceEvent>();

    public DbSet<DebugTraceCoverage> DebugTraceCoverage => Set<DebugTraceCoverage>();

    public DbSet<HealthProfile> HealthProfiles => Set<HealthProfile>();

    public DbSet<SafetyRule> SafetyRules => Set<SafetyRule>();

    public DbSet<HealthEvent> Events => Set<HealthEvent>();

    public DbSet<SafetyAlert> SafetyAlerts => Set<SafetyAlert>();

    public DbSet<PendingRecord> PendingRecords => Set<PendingRecord>();
    public DbSet<HealthDocument> HealthDocuments => Set<HealthDocument>();
    public DbSet<HealthDocumentAdmission> HealthDocumentAdmissions => Set<HealthDocumentAdmission>();

    public DbSet<VetProfile> VetProfiles => Set<VetProfile>();
    public DbSet<VetEvent> VetEvents => Set<VetEvent>();
    public DbSet<VetTextSource> VetTextSources => Set<VetTextSource>();
    public DbSet<VetTextSourceRevision> VetTextSourceRevisions => Set<VetTextSourceRevision>();
    public DbSet<VetExtractionResult> VetExtractionResults => Set<VetExtractionResult>();
    public DbSet<VetPendingDecision> VetPendingDecisions => Set<VetPendingDecision>();
    public DbSet<VetDiaryAction> VetDiaryActions => Set<VetDiaryAction>();
    public DbSet<VetDiaryActionChange> VetDiaryActionChanges => Set<VetDiaryActionChange>();

    public DbSet<VetPhotoBatch> VetPhotoBatches => Set<VetPhotoBatch>();
    public DbSet<VetPhotoSource> VetPhotoSources => Set<VetPhotoSource>();
    public DbSet<VetPhotoInputRevision> VetPhotoInputRevisions => Set<VetPhotoInputRevision>();
    public DbSet<VetPhotoOriginalReference> VetPhotoOriginalReferences => Set<VetPhotoOriginalReference>();
    public DbSet<VetPhotoBlob> VetPhotoBlobs => Set<VetPhotoBlob>();
    public DbSet<VetPhotoExtraction> VetPhotoExtractions => Set<VetPhotoExtraction>();
    public DbSet<VetPhotoCandidate> VetPhotoCandidates => Set<VetPhotoCandidate>();
    public DbSet<VetPhotoReview> VetPhotoReviews => Set<VetPhotoReview>();
    public DbSet<VetPhotoAttempt> VetPhotoAttempts => Set<VetPhotoAttempt>();
    public DbSet<VetPhotoRun> VetPhotoRuns => Set<VetPhotoRun>();
    public DbSet<VetPhotoRunWindow> VetPhotoRunWindows => Set<VetPhotoRunWindow>();
    public DbSet<VetPhotoReaderLease> VetPhotoReaderLeases => Set<VetPhotoReaderLease>();

    // Platform-wide (no FamilyId, no query filter): budgets span every family.
    public DbSet<BudgetNotice> BudgetNotices => Set<BudgetNotice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AssistantDbContext).Assembly);
        modelBuilder.Entity<Assistant.Domain.Expectations.Expectation>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Expectations.ExpectationVersion>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Expectations.ExpectationDraft>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Expectations.ExpectationOccurrence>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Expectations.ExpectationAttempt>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Expectations.ExpectationReceipt>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Reminders.Reminder>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Reminders.ReminderPreference>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Reminders.ReminderAttempt>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Reminders.ReminderSettingsReceipt>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Memory.GeneralMemoryFact>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Assistant.Domain.Memory.GeneralMemoryState>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);

        // Family isolation (spec §4.1): a null CurrentFamily.FamilyId disables the filter entirely
        // (used by the manager bot's own scope and by design-time/migration tooling, where
        // ICurrentFamily is not resolved at all). A concrete FamilyId scopes every read to that
        // family. Bot rows with FamilyId == null (the manager bot itself) are always visible, since
        // the manager is not itself family-scoped.
        modelBuilder.Entity<FamilyMember>().HasQueryFilter(m => _currentFamily.FamilyId == null || m.FamilyId == _currentFamily.FamilyId);
        // The inner (b.FamilyId == null || b.FamilyId == _currentFamily.FamilyId) duplicates Bot's own
        // query filter, which EF Core already applies inside this Bots.Any(...) subquery. It's redundant,
        // not load-bearing on its own — kept only for explicitness/defense-in-depth.
        modelBuilder.Entity<Place>().HasQueryFilter(p => _currentFamily.FamilyId == null || Bots.Any(b => b.Id == p.BotId && (b.FamilyId == null || b.FamilyId == _currentFamily.FamilyId)));
        modelBuilder.Entity<StoredMessage>().HasQueryFilter(m => _currentFamily.FamilyId == null || m.FamilyId == null || m.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<Bot>().HasQueryFilter(b => _currentFamily.FamilyId == null || b.FamilyId == null || b.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<ChatSetting>().HasQueryFilter(c => _currentFamily.FamilyId == null || c.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<LlmCall>().HasQueryFilter(c => _currentFamily.FamilyId == null || c.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<DebugTrace>().HasQueryFilter(t => _currentFamily.FamilyId == null || t.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<DebugTraceEvent>().HasQueryFilter(e => _currentFamily.FamilyId == null || e.FamilyId == _currentFamily.FamilyId);
        // Health tables: the same family filter. Their store additionally fails closed when
        // ICurrentFamily is unset or another family (HealthProfileStore).
        modelBuilder.Entity<HealthProfile>().HasQueryFilter(p => _currentFamily.FamilyId == null || p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<SafetyRule>().HasQueryFilter(r => _currentFamily.FamilyId == null || r.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<HealthEvent>().HasQueryFilter(e => _currentFamily.FamilyId == null || e.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<SafetyAlert>().HasQueryFilter(a => _currentFamily.FamilyId == null || a.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<PendingRecord>().HasQueryFilter(p => _currentFamily.FamilyId == null || p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<HealthDocument>().HasQueryFilter(d => _currentFamily.FamilyId != null && d.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<HealthDocumentAdmission>().HasQueryFilter(d => _currentFamily.FamilyId != null && d.FamilyId == _currentFamily.FamilyId);
        // Vet's direct reads and store methods both fail closed without an active family.
        modelBuilder.Entity<VetProfile>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetEvent>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetTextSource>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetTextSourceRevision>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetExtractionResult>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPendingDecision>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetDiaryAction>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetDiaryActionChange>().HasQueryFilter(p => _currentFamily.FamilyId != null && p.FamilyId == _currentFamily.FamilyId);

        modelBuilder.Entity<VetPhotoBatch>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoSource>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoInputRevision>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoOriginalReference>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoBlob>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoExtraction>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoCandidate>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoReview>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoAttempt>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoRun>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoRunWindow>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<VetPhotoReaderLease>().HasQueryFilter(x => _currentFamily.FamilyId != null && x.FamilyId == _currentFamily.FamilyId);
    }

    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AssistantDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention();
    }

    private sealed class UnscopedCurrentFamily : ICurrentFamily
    {
        public static readonly UnscopedCurrentFamily Instance = new();

        public long? FamilyId => null;

        public void Set(long? familyId) =>
            throw new InvalidOperationException("This is the fallback used when no ICurrentFamily was supplied to AssistantDbContext; it cannot be scoped.");
    }
}
