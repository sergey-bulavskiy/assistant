using Assistant.Domain.Messages;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Persistence;

public class AssistantDbContext : DbContext
{
    public AssistantDbContext(DbContextOptions<AssistantDbContext> options) : base(options) { }

    public DbSet<StoredMessage> Messages => Set<StoredMessage>();

    public DbSet<BotState> BotStates => Set<BotState>();

    public DbSet<ChatMigration> ChatMigrations => Set<ChatMigration>();

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
