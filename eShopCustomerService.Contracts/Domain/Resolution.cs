using System.Text.Json.Serialization;

namespace eShopCustomerService.Contracts.Domain;

[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
public union Resolution(RefundResolution, ReplacementResolution);

public sealed record RefundResolution(decimal Amount, string PaymentReference);

public sealed record ReplacementResolution(string ProductCode, string DeliveryAddress);

public sealed record SaveResolutionRequest(int OrderId, Resolution Resolution);

public sealed record ResolutionDraft(
    Guid Id, int OrderId, Resolution Resolution, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt);

public sealed record ReturnCase(
    int OrderId, string Customer, string Company, string Email, string Product,
    string ProductCode, decimal PaidAmount, string DeliveryAddress, string Reason,
    string Status, Resolution? Resolution);

public sealed record InventoryItem(string ProductCode, string Name, string Description, int Available);

public sealed record AuditEntry(long Id, int OrderId, string Actor, string Action, DateTimeOffset Timestamp);

public union ResolutionResult<T>(T, ResolutionFailure);

public sealed record ResolutionFailure(
    string Message, ResolutionFailureKind Kind = ResolutionFailureKind.Conflict);

public enum ResolutionFailureKind
{
    Invalid,
    NotFound,
    Conflict
}
