using eShopCustomerService.Web.Domain;

namespace eShopCustomerService.Web.Agentic;

public sealed record CaseWorkspaceState
{
    public int OrderId { get; init; }
    public string ProductCode { get; init; } = "DOCK-PRO-PLUS";
    public string DeliveryAddress { get; init; } = "";
    public string CustomerReply { get; init; } = "";
    public Guid? DraftId { get; init; }
    public string Status { get; init; } = "Investigating";
}

public sealed record CaseContext(
    ReturnCase Order, IReadOnlyList<InventoryItem> Inventory, IReadOnlyList<AuditEntry> Activity);
