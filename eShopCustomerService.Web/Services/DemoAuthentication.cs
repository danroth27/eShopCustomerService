using System.Security.Claims;
using eShopCustomerService.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace eShopCustomerService.Web.Services;

public sealed class DemoAuthentication(
    CustomerServiceClient api, IOptionsMonitor<CookieAuthenticationOptions> options, ILogger<DemoAuthentication> logger)
{
    public const string SessionClaim = AgentIdentityResponse.SessionClaim;
    public const string RefundPolicy = "ApproveRefund";
    public const string PermissionControlsPolicy = "ManageRefundPermission";

    public async Task SignInAsync(HttpContext context)
    {
        var identity = await api.CreateSessionAsync(context.User, context.RequestAborted);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(identity), NewTicketProperties());
    }

    public async Task SignOutAsync(HttpContext context)
    {
        await api.SignOutAsync(context.User, context.RequestAborted);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task SetRefundApprovalAsync(HttpContext context, bool allowed)
    {
        var identity = await api.SetRefundApprovalAsync(context.User, allowed, context.RequestAborted);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(identity), NewTicketProperties());
    }

    public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var identity = await api.GetIdentityAsync(context.Principal!, context.HttpContext.RequestAborted);
        if (identity is null)
        {
            logger.LogWarning("Customer-service session is no longer available; sign in again");
            context.RejectPrincipal();
            return;
        }
        context.ReplacePrincipal(CreatePrincipal(identity));
        context.ShouldRenew = true;
    }

    private AuthenticationProperties NewTicketProperties()
    {
        var cookieOptions = options.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var now = (cookieOptions.TimeProvider ?? TimeProvider.System).GetUtcNow();
        return new() { IssuedUtc = now, ExpiresUtc = now + cookieOptions.ExpireTimeSpan };
    }

    private static ClaimsPrincipal CreatePrincipal(AgentIdentityResponse identity)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, identity.UserId),
            new(ClaimTypes.Name, identity.Name),
            new(ClaimTypes.Role, "Representative"),
            new(SessionClaim, identity.SessionId)
        ];
        if (identity.RefundApproval)
        {
            claims.Add(new(ClaimTypes.Role, "RefundApprover"));
        }
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
