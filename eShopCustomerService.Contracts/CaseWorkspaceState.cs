using eShopCustomerService.Contracts.Domain;

namespace eShopCustomerService.Contracts;

public sealed record CaseWorkspaceState
{
    public int OrderId { get; init; }
    public string ResolutionKind { get; init; } = "replacement";
    public string ProductCode { get; init; } = "";
    public string DeliveryAddress { get; init; } = "";
    public string CustomerReply { get; init; } = "";
    public decimal RefundAmount { get; init; }
    public string PaymentReference { get; init; } = "";
    public bool RefundApprovalAllowed { get; init; }
    public IReadOnlyList<InventoryItem> AvailableProducts { get; init; } = [];
    public bool ValidationPending { get; init; }
    public string[] ValidationMessages { get; init; } = [];
    public string Status { get; init; } = "Editing";
}
