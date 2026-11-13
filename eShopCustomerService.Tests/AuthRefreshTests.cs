using System.Net;
using System.Net.Http.Json;
using eShopCustomerService.Api.Data;
using eShopCustomerService.Contracts.Domain;
using eShopCustomerService.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace eShopCustomerService.Tests;

public sealed class AuthRefreshTests
{
    [Fact]
    public async Task Signing_in_enables_refund_permission_controls_without_shortening_the_cookie_lifetime()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var signedIn = await PostFormAsync(client, "/demo/sign-in", "returnUrl", "/");
        var ticket = ReadTicket(factory, signedIn);
        Assert.True(ticket.Principal.IsInRole("RefundApprover"));
        Assert.NotNull(ticket.Principal.FindFirst(DemoAuthentication.SessionClaim));
        Assert.True(ticket.Properties.ExpiresUtc - ticket.Properties.IssuedUtc > TimeSpan.FromDays(1));
        using var controls = await client.GetAsync("/auth-refresh");
        Assert.Equal(HttpStatusCode.OK, controls.StatusCode);
        Assert.Contains("Remove refund approval", await controls.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Permission_changes_apply_to_old_cookies_and_protect_both_refund_write_paths()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var signedIn = await PostFormAsync(client, "/demo/sign-in", "returnUrl", "/");
        var oldCookie = ReadCookie(signedIn);
        var request = new SaveResolutionRequest(48021, new Resolution(new RefundResolution(149m, "PAY-48021")));
        using var prepared = await client.PostAsJsonAsync("/api/drafts", request);
        Assert.Equal(HttpStatusCode.Created, prepared.StatusCode);
        var draft = (await prepared.Content.ReadFromJsonAsync<ResolutionDraft>())!;

        using var removed = await PostFormAsync(client, "/demo/auth-refresh/permission", "refundApproval", "false");
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        using var oldClient = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        oldClient.DefaultRequestHeaders.Add("Cookie", $"{oldCookie.Name}={oldCookie.Value}");
        using var rejectedDraft = await oldClient.PostAsJsonAsync("/api/drafts", request);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedDraft.StatusCode);
        using var rejectedConfirmation = await PostFormAsync(oldClient, "/returns/confirm", "draftId", draft.Id.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, rejectedConfirmation.StatusCode);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        Assert.Null((await store.GetDraftAsync(draft.Id))!.ConfirmedAt);
        Assert.Equal("Open", (await store.GetCaseAsync(48021))!.Status);
        Assert.Equal(4, (await store.GetAuditAsync(48021)).Count);

        using var replacement = await oldClient.PostAsJsonAsync("/api/drafts", new SaveResolutionRequest(48021,
            new Resolution(new ReplacementResolution("DOCK-PRO-PLUS", "120 Market Street"))));
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        using var restored = await PostFormAsync(client, "/demo/auth-refresh/permission", "refundApproval", "true");
        Assert.Equal(HttpStatusCode.Redirect, restored.StatusCode);
        using var allowed = await oldClient.PostAsJsonAsync("/api/drafts", request);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Permission_controls_require_authentication_and_reject_cross_origin_changes()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var anonymous = await PostFormAsync(client, "/demo/auth-refresh/permission", "refundApproval", "false");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/sign-in", anonymous.Headers.Location!.AbsolutePath);
        await DemoFactory.SignInAsync(client);
        using var untrusted = new HttpRequestMessage(HttpMethod.Post, "/demo/auth-refresh/permission")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["refundApproval"] = "false" })
        };
        untrusted.Headers.Add("Sec-Fetch-Site", "same-site");
        untrusted.Headers.Add("Origin", "https://localhost:7444");
        using var rejected = await client.SendAsync(untrusted);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var stillAllowed = await client.PostAsJsonAsync("/api/drafts",
            new SaveResolutionRequest(48021, new Resolution(new RefundResolution(149m, "PAY-48021"))));
        Assert.Equal(HttpStatusCode.Created, stillAllowed.StatusCode);
    }

    [Fact]
    public async Task Signing_out_invalidates_the_permission_session_even_for_an_old_cookie()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var signedIn = await PostFormAsync(client, "/demo/sign-in", "returnUrl", "/");
        var cookie = ReadCookie(signedIn);
        using var signedOut = await PostFormAsync(client, "/demo/sign-out", "confirmation", "SIGNOUT");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        using var oldClient = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        oldClient.DefaultRequestHeaders.Add("Cookie", $"{cookie.Name}={cookie.Value}");
        using var stale = await oldClient.GetAsync("/api/returns");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_replacement_preserves_current_permissions_and_sign_out_revocation(bool refundApproval)
    {
        var database = Path.Combine(Path.GetTempPath(), $"eshop-auth-restart-{Guid.NewGuid():N}.db");
        SetCookieHeaderValue cookie;
        try
        {
            using (var original = new DemoFactory(database, deleteOnDispose: false))
            using (var client = original.Browser())
            {
                using var signedIn = await PostFormAsync(client, "/demo/sign-in", "returnUrl", "/");
                cookie = ReadCookie(signedIn);
                if (!refundApproval)
                {
                    using var removed = await PostFormAsync(client, "/demo/auth-refresh/permission", "refundApproval", "false");
                    Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
                }
            }
            using (var replacement = new DemoFactory(database, deleteOnDispose: false))
            using (var client = replacement.CreateClient(new()
            {
                BaseAddress = new("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = false
            }))
            {
                client.DefaultRequestHeaders.Add("Cookie", $"{cookie.Name}={cookie.Value}");
                using var returns = await client.GetAsync("/api/returns");
                Assert.Equal(HttpStatusCode.OK, returns.StatusCode);
                using var refund = await client.PostAsJsonAsync("/api/drafts",
                    new SaveResolutionRequest(48021, new Resolution(new RefundResolution(149m, "PAY-48021"))));
                Assert.Equal(refundApproval ? HttpStatusCode.Created : HttpStatusCode.Forbidden, refund.StatusCode);
                using var signedOut = await PostFormAsync(client, "/demo/sign-out", "confirmation", "SIGNOUT");
                Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
            }
            using var restartedAgain = new DemoFactory(database);
            using var stale = restartedAgain.CreateClient(new()
            {
                BaseAddress = new("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = false
            });
            stale.DefaultRequestHeaders.Add("Cookie", $"{cookie.Name}={cookie.Value}");
            using var rejected = await stale.GetAsync("/api/returns");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        finally
        {
            File.Delete(database);
        }
    }

    [Fact]
    public async Task A_valid_cookie_does_not_revive_an_expired_permission_record()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var signedIn = await PostFormAsync(client, "/demo/sign-in", "returnUrl", "/");
        var session = ReadTicket(factory, signedIn).Principal.FindFirst(DemoAuthentication.SessionClaim)!.Value;
        await using var db = new SqliteConnection($"Data Source={factory.DatabasePath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "UPDATE DemoPermissions SET ExpiresAtUtc = 0 WHERE SessionId = $session";
        command.Parameters.AddWithValue("$session", session);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        using var rejected = await client.GetAsync("/api/returns");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        command.CommandText = "SELECT ExpiresAtUtc FROM DemoPermissions WHERE SessionId = $session";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private static AuthenticationTicket ReadTicket(DemoFactory factory, HttpResponseMessage response)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return options.TicketDataFormat.Unprotect(ReadCookie(response).Value.ToString())!;
    }

    private static SetCookieHeaderValue ReadCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Select(value => SetCookieHeaderValue.Parse(value))
            .Single(cookie => cookie.Name == "eShop.CustomerService.Demo");

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string name, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { [name] = value })
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return await client.SendAsync(request);
    }
}
