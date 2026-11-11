using System.ComponentModel.DataAnnotations;

namespace eShopCustomerService.Contracts;

/// <summary>Identifies the representative validated by the customer-service application.</summary>
public sealed record AgentIdentityResponse(string UserId, string Name, string SessionId, bool RefundApproval)
{
    public const string SessionClaim = "eshop:session";
}

/// <summary>Starts a local fictional representative session.</summary>
public sealed record CreateDemoSessionRequest;

/// <summary>Changes refund permission for the current local demo session.</summary>
public sealed record DemoPermissionRequest
{
    public required bool RefundApproval { get; init; }
}

/// <summary>Requests an explicit reset of fictional demo data.</summary>
public sealed record ResetDemoRequest
{
    [Required] public required string Confirmation { get; init; }
}

/// <summary>Confirms a saved draft using the application's authoritative business rules.</summary>
public sealed record ConfirmDraftRequest(Guid DraftId);

/// <summary>Prepares a replacement draft and an unsent customer reply.</summary>
public sealed record PrepareResolutionRequest
{
    [Required] public required string ProductCode { get; init; }
    [Required] public required string DeliveryAddress { get; init; }
    [Required] public required string CustomerReply { get; init; }
}

/// <summary>Confirms the exact replacement details reviewed by the representative.</summary>
public sealed record ConfirmResolutionRequest
{
    public required Guid DraftId { get; init; }
    [Required] public required string ProductCode { get; init; }
    [Required] public required string DeliveryAddress { get; init; }
}
