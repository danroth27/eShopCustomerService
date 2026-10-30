using eShopCustomerService.Web.Data;

namespace eShopCustomerService.Web.Domain;

public sealed class InventoryService(ReturnStore store, IConfiguration configuration)
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
