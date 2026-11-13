using System.Security.Claims;
using eShopCustomerService.Api.Services;
using eShopCustomerService.Contracts;

namespace eShopCustomerService.Api.Endpoints;

public static class DemoSessionEndpoints
{
    public static void MapDemoSessionEndpoints(this WebApplication app)
    {
        var demo = app.MapGroup("/api/demo").ExcludeFromDescription();
        demo.MapPost("/sessions", async (
            CreateDemoSessionRequest request, ClaimsPrincipal user, DemoAuthentication sessions, CancellationToken ct) =>
            TypedResults.Created("/api/agent-tools/identity", await sessions.CreateSessionAsync(user, ct)));
        demo.MapDelete("/session", async (ClaimsPrincipal user, DemoAuthentication sessions, CancellationToken ct) =>
        {
            await sessions.SignOutAsync(user, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization("CustomerService");
        demo.MapPut("/session/permission", async (
            DemoPermissionRequest request, ClaimsPrincipal user, DemoAuthentication sessions, CancellationToken ct) =>
            TypedResults.Ok(await sessions.SetRefundApprovalAsync(user, request.RefundApproval, ct)))
            .RequireAuthorization(DemoAuthentication.PermissionControlsPolicy);
    }
}
