using Assistant.Application.Common;
using Assistant.Infrastructure.Persistence;

namespace Assistant.Host;

public static class HealthEndpoint
{
    public static async Task<IResult> HandleAsync(AssistantDbContext db, PollingHealth pollingHealth, IClock clock, CancellationToken cancellationToken)
    {
        var dbHealthy = await db.Database.CanConnectAsync(cancellationToken);
        var pollingHealthy = pollingHealth.IsHealthy(clock.UtcNow);

        if (dbHealthy && pollingHealthy)
        {
            return Results.Ok(new { status = "ok", database = "connected", polling = "healthy" });
        }

        return Results.Json(
            new { status = "unavailable", database = dbHealthy ? "connected" : "unreachable", polling = pollingHealthy ? "healthy" : "stale" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
