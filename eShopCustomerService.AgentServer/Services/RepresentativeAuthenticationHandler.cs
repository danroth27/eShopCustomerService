using System.Security.Claims;
using System.Text.Encodings.Web;
using eShopCustomerService.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace eShopCustomerService.AgentServer.Services;

public sealed class RepresentativeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    ICustomerServiceClient customerService) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrEmpty(Request.Headers.Cookie))
        {
            return AuthenticateResult.NoResult();
        }
        var identity = await customerService.GetIdentityAsync(Context.RequestAborted);
        if (identity is null || string.IsNullOrWhiteSpace(identity.UserId)
            || string.IsNullOrWhiteSpace(identity.Name) || string.IsNullOrWhiteSpace(identity.SessionId))
        {
            Logger.LogWarning("Agent request rejected because the representative session is no longer valid");
            return AuthenticateResult.Fail("A current representative session is required.");
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new(ClaimTypes.NameIdentifier, identity.UserId),
            new(ClaimTypes.Name, identity.Name),
            new(ClaimTypes.Role, "Representative"),
            new(AgentIdentityResponse.SessionClaim, identity.SessionId)
        ], Scheme.Name));
        return AuthenticateResult.Success(new(principal, Scheme.Name));
    }
}
