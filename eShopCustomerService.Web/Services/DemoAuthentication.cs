using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace eShopCustomerService.Web.Services;

public sealed class DemoAuthentication(ILogger<DemoAuthentication> logger)
{
    public const string SessionClaim = "eshop:session";
    public const string RefundPolicy = "ApproveRefund";
    public const string PermissionControlsPolicy = "ManageRefundPermission";

    private readonly ConcurrentDictionary<string, bool> sessions = new();

    public async Task SignInAsync(HttpContext context)
    {
        RemoveSession(context.User);
        var session = Guid.NewGuid().ToString("N");
        sessions[session] = true;
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(session, refundApproval: true));
    }

    public async Task SignOutAsync(HttpContext context)
    {
        RemoveSession(context.User);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task SetRefundApprovalAsync(HttpContext context, bool allowed)
    {
        var session = context.User.FindFirstValue(SessionClaim)
            ?? throw new InvalidOperationException("A customer-service session is required.");
        sessions[session] = allowed;
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(session, allowed));
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
