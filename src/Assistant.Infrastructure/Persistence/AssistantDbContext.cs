using Assistant.Application.Families;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Health;
using Assistant.Domain.Llm;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
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

    public DbSet<ChatMigration> ChatMigrations => Set<ChatMigration>();

    public DbSet<Family> Families => Set<Family>();

    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

    public DbSet<Bot> Bots => Set<Bot>();

    public DbSet<Place> Places => Set<Place>();

    public DbSet<PendingBotCreation> PendingBotCreations => Set<PendingBotCreation>();

    public DbSet<ChatSetting> ChatSettings => Set<ChatSetting>();

    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();

    public DbSet<HealthProfile> HealthProfiles => Set<HealthProfile>();

    public DbSet<SafetyRule> SafetyRules => Set<SafetyRule>();

    public DbSet<HealthEvent> Events => Set<HealthEvent>();

    public DbSet<SafetyAlert> SafetyAlerts => Set<SafetyAlert>();

    // Platform-wide (no FamilyId, no query filter): budgets span every family.
    public DbSet<BudgetNotice> BudgetNotices => Set<BudgetNotice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AssistantDbContext).Assembly);

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
        // Health tables: the same family filter. Their store additionally fails closed when
        // ICurrentFamily is unset or another family (HealthProfileStore).
        modelBuilder.Entity<HealthProfile>().HasQueryFilter(p => _currentFamily.FamilyId == null || p.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<SafetyRule>().HasQueryFilter(r => _currentFamily.FamilyId == null || r.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<HealthEvent>().HasQueryFilter(e => _currentFamily.FamilyId == null || e.FamilyId == _currentFamily.FamilyId);
        modelBuilder.Entity<SafetyAlert>().HasQueryFilter(a => _currentFamily.FamilyId == null || a.FamilyId == _currentFamily.FamilyId);
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
