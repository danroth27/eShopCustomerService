using eShopCustomerService.Contracts.Domain;

namespace eShopCustomerService.Web.Services;

public sealed class ResolutionContextLoader(
    WorkspaceService store, IConfiguration configuration, ILogger<ResolutionContextLoader> logger)
{
    private int loads;

    public async Task<ResolutionContext?> LoadAsync(int orderId, CancellationToken ct = default)
    {
        var load = Interlocked.Increment(ref loads);
        logger.LogInformation("Resolution context load {Load} for order {OrderId}", load, orderId);
        await Task.Delay(configuration.GetValue("Demo:ContextLoadDelayMilliseconds", 450), ct);
        var order = await store.GetCaseAsync(orderId, ct);
        return order is null ? null : new ResolutionContext(
            order, (await store.GetAuditAsync(orderId, ct)).ToArray(), load, DateTimeOffset.UtcNow);
    }
}

public sealed record ResolutionContext(
    ReturnCase Order, AuditEntry[] Audit, int Load, DateTimeOffset LoadedAt);
