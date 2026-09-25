using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Assistant.Infrastructure.Persistence;

public class AssistantDbContextFactory : IDesignTimeDbContextFactory<AssistantDbContext>
{
    public AssistantDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AssistantDbContext>();
        AssistantDbContext.Configure(builder, "Host=localhost;Database=assistant_design;Username=postgres;Password=postgres");
        return new AssistantDbContext(builder.Options);
    }
}
