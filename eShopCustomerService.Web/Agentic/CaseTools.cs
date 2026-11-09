using System.Globalization;
using eShopCustomerService.Web.Data;
using eShopCustomerService.Web.Domain;
using Microsoft.AspNetCore.Authorization;

namespace eShopCustomerService.Web.Agentic;

public sealed class CaseTools(
    ReturnStore store, IHttpContextAccessor accessor,
    IAuthorizationService authorization, ILogger<CaseTools> logger)
{
    private HttpContext Request => accessor.HttpContext
        ?? throw new InvalidOperationException("An authenticated agent HTTP request is required.");

    private int OrderId => int.Parse(
        Request.Request.RouteValues["orderId"]?.ToString()
        ?? throw new InvalidOperationException("An order route is required."), CultureInfo.InvariantCulture);

    public async Task<CaseContext> GetCaseContextAsync(CancellationToken cancellationToken)
    {
        await RequireRepresentativeAsync();
        var order = await store.GetCaseAsync(OrderId, cancellationToken)
            ?? throw Reject("The selected order was not found.");
        return new(order, await store.GetInventoryAsync(cancellationToken),
            await store.GetAuditAsync(OrderId, cancellationToken));
    }

    public async Task<CaseWorkspaceState> PrepareResolutionAsync(
        string productCode, string deliveryAddress, string customerReply, CancellationToken cancellationToken)
    {
        await RequireRepresentativeAsync();
        var result = await store.SaveDraftAsync(
            new(OrderId, new Resolution(new ReplacementResolution(productCode, deliveryAddress))),
            Request.User.Identity!.Name!, cancellationToken);
        return result switch
        {
            ResolutionDraft draft => new()
            {
                OrderId = OrderId,
                ProductCode = productCode,
                DeliveryAddress = deliveryAddress,
                CustomerReply = customerReply,
                DraftId = draft.Id,
                Status = "Draft"
            },
            ResolutionFailure failure => throw Reject(failure.Message)
        };
    }

    public async Task<CaseWorkspaceState> ConfirmResolutionAsync(
        Guid draftId, string productCode, string deliveryAddress, CancellationToken cancellationToken)
    {
        await RequireRepresentativeAsync();
        var draft = await store.GetDraftAsync(draftId, cancellationToken)
            ?? throw Reject("The resolution draft was not found.");
        if (draft.OrderId != OrderId
            || draft.Resolution.Value is not ReplacementResolution replacement
            || replacement.ProductCode != productCode
            || replacement.DeliveryAddress != deliveryAddress)
        {
            throw Reject("The approved details do not match this order's saved replacement draft.");
        }

        var result = await store.ConfirmAsync(draftId, Request.User.Identity!.Name!, cancellationToken);
        return result switch
        {
            ReturnCase order => new()
            {
                OrderId = order.OrderId,
                ProductCode = productCode,
                DeliveryAddress = deliveryAddress,
                DraftId = draftId,
                Status = "Resolved"
            },
            ResolutionFailure failure => throw Reject(failure.Message)
        };
    }

    private async Task RequireRepresentativeAsync()
    {
        if (!(await authorization.AuthorizeAsync(Request.User, "CustomerService")).Succeeded)
        {
            throw Reject("A current representative session is required.");
        }
    }

    private InvalidOperationException Reject(string message)
    {
        logger.LogWarning("Agent operation rejected for order {OrderId}: {Reason}", OrderId, message);
        return new(message);
    }
}
