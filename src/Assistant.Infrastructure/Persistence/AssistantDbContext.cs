using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Messages;
using Assistant.Domain.Places;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Persistence;

public class AssistantDbContext : DbContext
{
    public AssistantDbContext(DbContextOptions<AssistantDbContext> options) : base(options) { }

    public DbSet<StoredMessage> Messages => Set<StoredMessage>();

    public DbSet<ChatMigration> ChatMigrations => Set<ChatMigration>();

    public DbSet<Family> Families => Set<Family>();

    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

    public DbSet<Bot> Bots => Set<Bot>();

    public DbSet<Place> Places => Set<Place>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AssistantDbContext).Assembly);
    }

    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AssistantDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention();
    }
}
