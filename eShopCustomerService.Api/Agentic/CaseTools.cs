using System.Globalization;
using eShopCustomerService.Contracts;
using eShopCustomerService.Api.Data;
using eShopCustomerService.Contracts.Domain;
using Microsoft.AspNetCore.Authorization;
using eShopCustomerService.Api.Services;

namespace eShopCustomerService.Api.Agentic;

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
            ?? throw Reject("The selected order was not found.", StatusCodes.Status404NotFound);
        return new(order, await store.GetInventoryAsync(cancellationToken),
            await store.GetAuditAsync(OrderId, cancellationToken));
    }

    public async Task<CaseWorkspaceState> ConfirmResolutionAsync(
        string productCode, string deliveryAddress, CancellationToken cancellationToken,
        string resolutionKind = "replacement", decimal? refundAmount = null, string? paymentReference = null)
    {
        await RequireRepresentativeAsync();
        var order = await store.GetCaseAsync(OrderId, cancellationToken)
            ?? throw Reject("The selected order was not found.", StatusCodes.Status404NotFound);
        Resolution resolution;
        switch (resolutionKind)
        {
            case "replacement":
                resolution = new(new ReplacementResolution(productCode, deliveryAddress));
                break;
            case "refund":
                await RequireRefundApprovalAsync();
                if (refundAmount != order.PaidAmount || paymentReference != $"PAY-{OrderId}")
                {
                    throw Reject("A full refund must match the original payment and paid amount.", StatusCodes.Status400BadRequest);
                }
                resolution = new(new RefundResolution(order.PaidAmount, paymentReference));
                break;
            default:
                throw Reject("Choose refund or replacement.", StatusCodes.Status400BadRequest);
        }
        var result = await store.ConfirmResolutionAsync(
            new(OrderId, resolution),
            Request.User.Identity!.Name!, cancellationToken);
        return result switch
        {
            ReturnCase confirmedOrder => new()
            {
                OrderId = confirmedOrder.OrderId,
                ResolutionKind = resolutionKind,
                ProductCode = productCode,
                DeliveryAddress = deliveryAddress,
                RefundAmount = confirmedOrder.PaidAmount,
                PaymentReference = $"PAY-{confirmedOrder.OrderId}",
                RefundApprovalAllowed = (await authorization.AuthorizeAsync(Request.User, DemoAuthentication.RefundPolicy)).Succeeded,
                AvailableProducts = await store.GetInventoryAsync(cancellationToken),
                Status = "Resolved"
            },
            ResolutionFailure failure => throw Reject(failure)
        };
    }

    private async Task RequireRefundApprovalAsync()
    {
        if (!(await authorization.AuthorizeAsync(Request.User, DemoAuthentication.RefundPolicy)).Succeeded)
        {
            throw Reject("Refund approval is required.", StatusCodes.Status403Forbidden);
        }
    }

    private async Task RequireRepresentativeAsync()
    {
        if (!(await authorization.AuthorizeAsync(Request.User, "CustomerService")).Succeeded)
        {
            throw Reject("A current representative session is required.", StatusCodes.Status403Forbidden);
        }
    }

    private CaseToolException Reject(ResolutionFailure failure) => Reject(failure.Message, failure.Kind switch
    {
        ResolutionFailureKind.Invalid => StatusCodes.Status400BadRequest,
        ResolutionFailureKind.NotFound => StatusCodes.Status404NotFound,
        ResolutionFailureKind.Conflict => StatusCodes.Status409Conflict,
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    });

    private CaseToolException Reject(string message, int statusCode = StatusCodes.Status409Conflict)
    {
        logger.LogWarning("Agent operation rejected for order {OrderId}: {Reason}", OrderId, message);
        return new(message, statusCode);
    }
}
