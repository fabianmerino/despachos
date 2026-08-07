using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Despachos.Api.Services;

public sealed class OutboxHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public OutboxHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.DespachosDbContext>();

        var count = await db.OutboxConfirmaciones
            .CountAsync(o => o.Estado == "Pendiente", cancellationToken);

        if (count > 10)
            return HealthCheckResult.Degraded($"{count} mensajes pendientes en outbox");
        if (count > 0)
            return HealthCheckResult.Healthy($"{count} mensajes pendientes en outbox");

        return HealthCheckResult.Healthy("Outbox sin pendientes");
    }
}
