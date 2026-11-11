using System.Security.Claims;
using eShopCustomerService.Contracts;
using eShopCustomerService.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace eShopCustomerService.Api.Agentic;

public static class AgentToolEndpoints
{
    public static void MapAgentToolEndpoints(this WebApplication app)
    {
        var tools = app.MapGroup("/api/agent-tools")
            .RequireAuthorization("CustomerService")
            .ExcludeFromDescription();
        tools.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (CaseToolException error)
            {
                return TypedResults.Problem(statusCode: error.StatusCode,
                    title: "The agent operation could not be completed.", detail: error.Message);
            }
        });
        tools.MapGet("/identity", Results<Ok<AgentIdentityResponse>, UnauthorizedHttpResult> (
            ClaimsPrincipal user, ILogger<CaseTools> logger) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var session = user.FindFirstValue(DemoAuthentication.SessionClaim);
            var name = user.Identity?.Name;
            if (userId is null || session is null || name is null)
            {
                logger.LogWarning("Agent identity request rejected because the representative identity is incomplete");
                return TypedResults.Unauthorized();
            }
            return TypedResults.Ok(new AgentIdentityResponse(userId, name, session, user.IsInRole("RefundApprover")));
        });
        tools.MapGet("/returns/{orderId:int}", async (CaseTools service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetCaseContextAsync(ct)));
        tools.MapPost("/returns/{orderId:int}/confirm", async (
            ConfirmResolutionRequest request, CaseTools service, CancellationToken ct) =>
            TypedResults.Ok(await service.ConfirmResolutionAsync(
                request.ProductCode, request.DeliveryAddress, ct,
                request.ResolutionKind, request.RefundAmount, request.PaymentReference)));
    }
}
