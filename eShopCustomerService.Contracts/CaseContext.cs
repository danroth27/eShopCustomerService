using eShopCustomerService.Contracts.Domain;

namespace eShopCustomerService.Contracts;

public sealed record CaseContext(
    ReturnCase Order, IReadOnlyList<InventoryItem> Inventory, IReadOnlyList<AuditEntry> Activity);
