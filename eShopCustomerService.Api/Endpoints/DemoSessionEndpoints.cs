using System.Security.Claims;
using eShopCustomerService.Api.Services;
using eShopCustomerService.Contracts;

namespace eShopCustomerService.Api.Endpoints;

public static class DemoSessionEndpoints
{
    public static void MapDemoSessionEndpoints(this WebApplication app)
    {
        var demo = app.MapGroup("/api/demo").ExcludeFromDescription();
        demo.MapPost("/sessions", (CreateDemoSessionRequest request, ClaimsPrincipal user, DemoAuthentication sessions) =>
            TypedResults.Created("/api/agent-tools/identity", sessions.CreateSession(user)));
        demo.MapDelete("/session", (ClaimsPrincipal user, DemoAuthentication sessions) =>
        {
            sessions.SignOut(user);
            return TypedResults.NoContent();
        }).RequireAuthorization("CustomerService");
        demo.MapPut("/session/permission", (DemoPermissionRequest request, ClaimsPrincipal user, DemoAuthentication sessions) =>
            TypedResults.Ok(sessions.SetRefundApproval(user, request.RefundApproval)))
            .RequireAuthorization(DemoAuthentication.PermissionControlsPolicy);
    }
}
