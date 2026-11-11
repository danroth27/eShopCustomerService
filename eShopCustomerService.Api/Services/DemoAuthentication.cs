using System.Collections.Concurrent;
using System.Security.Claims;
using eShopCustomerService.Contracts;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace eShopCustomerService.Api.Services;

public sealed class DemoAuthentication(ILogger<DemoAuthentication> logger)
{
    public const string SessionClaim = AgentIdentityResponse.SessionClaim;
    public const string RefundPolicy = "ApproveRefund";
    public const string PermissionControlsPolicy = "ManageRefundPermission";

    private readonly ConcurrentDictionary<string, bool> sessions = new();

    public AgentIdentityResponse CreateSession(ClaimsPrincipal user)
    {
        RemoveSession(user);
        var session = Guid.NewGuid().ToString("N");
        sessions[session] = true;
        return new("demo-representative", "Taylor Brooks", session, RefundApproval: true);
    }

    public void SignOut(ClaimsPrincipal user)
    {
        RemoveSession(user);
    }

    public AgentIdentityResponse SetRefundApproval(ClaimsPrincipal user, bool allowed)
    {
        var session = user.FindFirstValue(SessionClaim)
            ?? throw new InvalidOperationException("A customer-service session is required.");
        sessions[session] = allowed;
        return new("demo-representative", "Taylor Brooks", session, allowed);
    }

    public Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.FindFirstValue(SessionClaim) is not { } session
            || !sessions.TryGetValue(session, out var allowed))
        {
            logger.LogWarning("Customer-service session is no longer available; sign in again");
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        context.ReplacePrincipal(CreatePrincipal(session, allowed));
        context.ShouldRenew = true;
        return Task.CompletedTask;
    }

    private void RemoveSession(ClaimsPrincipal user)
    {
        if (user.FindFirstValue(SessionClaim) is { } session)
        {
            sessions.TryRemove(session, out _);
        }
    }

    private static ClaimsPrincipal CreatePrincipal(string session, bool refundApproval)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "demo-representative"),
            new(ClaimTypes.Name, "Taylor Brooks"),
            new(ClaimTypes.Role, "Representative"),
            new(SessionClaim, session)
        ];
        if (refundApproval)
        {
            claims.Add(new(ClaimTypes.Role, "RefundApprover"));
        }
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
