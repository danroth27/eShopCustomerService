using System.Security.Claims;
using eShopCustomerService.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace eShopCustomerService.Web.Services;

public sealed class DemoAuthentication(
    ReturnStore store, IOptionsMonitor<CookieAuthenticationOptions> options, ILogger<DemoAuthentication> logger)
{
    public const string SessionClaim = "eshop:session";
    public const string RefundPolicy = "ApproveRefund";
    public const string PermissionControlsPolicy = "ManageRefundPermission";

    public async Task SignInAsync(HttpContext context)
    {
        await RemoveSessionAsync(context);
        var session = Guid.NewGuid().ToString("N");
        var properties = NewTicketProperties();
        await store.CreateDemoPermissionAsync(
            session, properties.IssuedUtc!.Value, properties.ExpiresUtc!.Value, context.RequestAborted);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(session, refundApproval: true), properties);
    }

    public async Task SignOutAsync(HttpContext context)
    {
        await RemoveSessionAsync(context);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task SetRefundApprovalAsync(HttpContext context, bool allowed)
    {
        var session = context.User.FindFirstValue(SessionClaim)
            ?? throw new InvalidOperationException("A customer-service session is required.");
        var properties = NewTicketProperties();
        if (!await store.SetDemoRefundApprovalAsync(
            session, allowed, properties.IssuedUtc!.Value, properties.ExpiresUtc!.Value, context.RequestAborted))
        {
            logger.LogWarning("Cannot change permissions for a missing or expired customer-service session");
            throw new BadHttpRequestException("The demo session has expired. Sign in again.", StatusCodes.Status401Unauthorized);
        }
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(session, allowed), properties);
    }

    public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        var lifetime = context.Properties is { IssuedUtc: { } issued, ExpiresUtc: { } expires }
            ? expires - issued : context.Options.ExpireTimeSpan;
        var allowed = context.Principal?.FindFirstValue(SessionClaim) is { } sessionId
            ? await store.RefreshDemoPermissionAsync(
                sessionId, now, now + lifetime, context.HttpContext.RequestAborted)
            : null;
        if (allowed is null)
        {
            logger.LogWarning("Customer-service session is no longer available; sign in again");
            context.RejectPrincipal();
            return;
        }

        context.ReplacePrincipal(CreatePrincipal(context.Principal!.FindFirstValue(SessionClaim)!, allowed.Value));
        context.ShouldRenew = true;
    }

    private async Task RemoveSessionAsync(HttpContext context)
    {
        if (context.User.FindFirstValue(SessionClaim) is { } session)
        {
            await store.RemoveDemoPermissionAsync(session, context.RequestAborted);
        }
    }

    private AuthenticationProperties NewTicketProperties()
    {
        var cookieOptions = options.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var now = (cookieOptions.TimeProvider ?? TimeProvider.System).GetUtcNow();
        return new() { IssuedUtc = now, ExpiresUtc = now + cookieOptions.ExpireTimeSpan };
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
