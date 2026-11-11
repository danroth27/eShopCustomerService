using eShopCustomerService.Api.Data;
using eShopCustomerService.Contracts.Domain;

namespace eShopCustomerService.Api.Services;

public sealed class InventoryService(ReturnStore store, IConfiguration configuration) : IInventoryService
{
    public async Task<bool> IsAvailableAsync(string productCode, CancellationToken cancellationToken)
    {
        var delay = configuration.GetValue("Demo:ValidationDelayMilliseconds", 650);
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken);
        }

        var products = await store.GetInventoryAsync(cancellationToken);
        return products.Any(p => p.ProductCode == productCode && p.Available > 0);
    }
}
