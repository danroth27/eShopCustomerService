using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using eShopCustomerService.Api.Data;
using eShopCustomerService.Contracts.Domain;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace eShopCustomerService.Tests;

public sealed class ReturnsTests
{
    [Theory]
    [InlineData("/returns/48021")]
    [InlineData("/service-queue")]
    public async Task Interactive_pages_enable_auto_pause_without_a_query_parameter(string path)
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var configuration = Regex.Match(html, "<!--Blazor-Configuration:([^>]+)-->");
        Assert.True(configuration.Success, "The page must emit its app-wide browser configuration.");
        using var options = JsonDocument.Parse(Convert.FromBase64String(configuration.Groups[1].Value));
        var server = options.RootElement.GetProperty("server");
        Assert.True(server.GetProperty("autoPauseEnabled").GetBoolean());
        Assert.Equal(8000, server.GetProperty("autoPauseHiddenDelayMilliseconds").GetInt32());
        Assert.DoesNotContain("Preview without saving", html);
    }

    [Fact]
    public async Task Operations_requires_a_representative_session()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var page = await client.GetAsync("/operations");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/sign-in", page.Headers.Location!.AbsolutePath);
        using var post = await client.PostAsync("/operations", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "stop-service",
            ["ConfirmShutdown"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/sign-in", post.Headers.Location!.AbsolutePath);
        Assert.False(factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operations_only_stops_the_service_after_explicit_confirmation(bool confirmed)
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        using var page = await client.GetAsync("/operations");
        page.EnsureSuccessStatusCode();
        Assert.DoesNotContain("\"type\":\"server\"", await page.Content.ReadAsStringAsync());
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/operations")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "stop-service",
                ["ConfirmShutdown"] = confirmed ? "true" : "false"
            })
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(confirmed ? "Stopping this service." : "Confirm that you want to stop this service.",
            await response.Content.ReadAsStringAsync());
        if (confirmed)
        {
            // Response.OnCompleted can request shutdown after the client receives the response body.
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(confirmed, lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task Operations_rejects_cross_origin_shutdown_forms()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/operations")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "stop-service",
                ["ConfirmShutdown"] = "true"
            })
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", "https://example.invalid");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData("""{"amount":149,"paymentReference":"PAY-48021"}""", typeof(RefundResolution))]
    [InlineData("""{"productCode":"DOCK-PRO-PLUS","deliveryAddress":"120 Market Street"}""", typeof(ReplacementResolution))]
    public void Union_round_trips_as_the_active_plain_json_case(string json, Type expectedCase)
    {
        var resolution = JsonSerializer.Deserialize<Resolution>(json, JsonSerializerOptions.Web);
        Assert.IsType(expectedCase, resolution.Value);
        var roundTrip = JsonSerializer.Serialize(resolution, JsonSerializerOptions.Web);
        Assert.DoesNotContain("$type", roundTrip);
        Assert.Equal(resolution.Value, JsonSerializer.Deserialize<Resolution>(roundTrip, JsonSerializerOptions.Web).Value);
    }

    [Fact]
    public async Task Return_workflow_requires_a_representative_session()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var response = await client.GetAsync("/api/returns");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var draft = await client.PostAsJsonAsync("/api/drafts", new SaveResolutionRequest(48021,
            new Resolution(new ReplacementResolution("DOCK-PRO-PLUS", "120 Market Street"))));
        Assert.Equal(HttpStatusCode.Unauthorized, draft.StatusCode);
        using var confirmation = await client.PostAsync("/returns/confirm", Form(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Redirect, confirmation.StatusCode);
        Assert.Equal("/sign-in", confirmation.Headers.Location!.AbsolutePath);
        using var queue = await client.GetAsync("/service-queue");
        Assert.Equal(HttpStatusCode.Redirect, queue.StatusCode);
        Assert.Equal("/sign-in", queue.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Viewing_the_simulated_queue_does_not_change_persisted_returns()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var cases = await store.GetCasesAsync();
        var inventory = await store.GetInventoryAsync();
        var audit = await store.GetAuditAsync(48021);
        using var response = await client.GetAsync("/service-queue");
        response.EnsureSuccessStatusCode();
        Assert.Equal(cases, await store.GetCasesAsync());
        Assert.Equal(inventory, await store.GetInventoryAsync());
        Assert.Equal(audit, await store.GetAuditAsync(48021));
    }

    [Theory]
    [InlineData("/api/examples/refund", typeof(RefundResolution))]
    [InlineData("/api/examples/replacement", typeof(ReplacementResolution))]
    public async Task Union_examples_work_without_a_representative_session(string path, Type expectedCase)
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolution = await response.Content.ReadFromJsonAsync<Resolution>();
        Assert.IsType(expectedCase, resolution.Value);
    }

    [Fact]
    public async Task Anonymous_validation_awaits_the_shared_inventory_rule_without_mutating_state()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var initialReturn = await store.GetCaseAsync(48021);
        var initialInventory = await store.GetInventoryAsync();
        var initialAudit = await store.GetAuditAsync(48021);
        using var unavailable = await client.PostAsJsonAsync("/api/replacements/validate", new ReplacementRequest
        {
            OrderId = 48021,
            ProductCode = "DOCK-PRO",
            DeliveryAddress = "120 Market Street"
        });
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
        using var errors = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.Contains("unavailable", errors.RootElement.GetProperty("errors").GetProperty("ProductCode")[0].GetString());
        using var available = await client.PostAsJsonAsync("/api/replacements/validate", new ReplacementRequest
        {
            OrderId = 48021,
            ProductCode = "DOCK-PRO-PLUS",
            DeliveryAddress = "120 Market Street"
        });
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        Assert.Equal(initialReturn, await store.GetCaseAsync(48021));
        Assert.Equal(initialInventory, await store.GetInventoryAsync());
        Assert.Equal(initialAudit, await store.GetAuditAsync(48021));
    }

    [Fact]
    public async Task Openapi_describes_both_union_cases_and_defaults_to_32()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("3.2.0", document.RootElement.GetProperty("openapi").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var union = schemas.EnumerateObject().Single(s => s.Name == "Resolution").Value;
        var cases = union.GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal(2, cases.Length);
        Assert.Contains(cases, c => c.GetProperty("$ref").GetString()!.EndsWith("/RefundResolution", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.GetProperty("$ref").GetString()!.EndsWith("/ReplacementResolution", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"orderId":48021,"resolution":null}""")]
    [InlineData("""{"orderId":48021,"resolution":{"amount":1,"paymentReference":"PAY-48021"}}""")]
    [InlineData("""{"orderId":48021,"resolution":{"productCode":"DOCK-PRO-PLUS","deliveryAddress":""}}""")]
    [InlineData("""{"orderId":48021,"resolution":{"unrecognized":"value"}}""")]
    [InlineData("""{"orderId":48021,"resolution":{"amount":149,"paymentReference":"PAY-48021","productCode":"DOCK-PRO-PLUS","deliveryAddress":"120 Market Street"}}""")]
    public async Task Invalid_draft_contracts_never_mutate_the_order(string json)
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        using var response = await client.PostAsync("/api/drafts", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        Assert.Equal("Open", (await store.GetCaseAsync(48021))!.Status);
        Assert.Equal(3, (await store.GetAuditAsync(48021)).Count);
    }

    [Theory]
    [InlineData(99999, "DOCK-PRO-PLUS", "120 Market Street", HttpStatusCode.NotFound, "The order was not found.")]
    [InlineData(48021, "DOCK-PRO-PLUS", "no", HttpStatusCode.BadRequest, "Provide a delivery address between 5 and 240 characters.")]
    [InlineData(48021, "DOCK-PRO", "120 Market Street", HttpStatusCode.Conflict, "This replacement is unavailable. Choose an available alternative.")]
    public async Task Rejected_drafts_return_problem_details_without_mutating_state(
        int orderId, string productCode, string address, HttpStatusCode status, string message)
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var cases = await store.GetCasesAsync();
        var inventory = await store.GetInventoryAsync();
        var audit = await store.GetAuditAsync(48021);
        using var response = await client.PostAsJsonAsync("/api/drafts", new SaveResolutionRequest(orderId,
            new Resolution(new ReplacementResolution(productCode, address))));
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("The operation could not be completed.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(cases, await store.GetCasesAsync());
        Assert.Equal(inventory, await store.GetInventoryAsync());
        Assert.Equal(audit, await store.GetAuditAsync(48021));
    }

    [Fact]
    public async Task Resolved_returns_reject_new_drafts_without_mutating_state()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        var draft = await CreateDraftAsync(client);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        Assert.IsType<ReturnCase>((await store.ConfirmAsync(draft.Id, "Test representative")).Value);
        var audit = await store.GetAuditAsync(48021);
        var inventory = await store.GetInventoryAsync();
        var result = await store.SaveDraftAsync(new(48021,
            new Resolution(new ReplacementResolution("DOCK-PRO-PLUS", "120 Market Street"))), "Test representative");
        var failure = Assert.IsType<ResolutionFailure>(result.Value);
        Assert.Equal(ResolutionFailureKind.Conflict, failure.Kind);
        Assert.Equal("This return has already been resolved.", failure.Message);
        Assert.Equal(audit, await store.GetAuditAsync(48021));
        Assert.Equal(inventory, await store.GetInventoryAsync());
    }

    [Fact]
    public async Task Missing_drafts_return_a_not_found_failure()
    {
        using var factory = new DemoFactory();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var result = await store.ConfirmAsync(Guid.NewGuid(), "Test representative");
        var failure = Assert.IsType<ResolutionFailure>(result.Value);
        Assert.Equal(ResolutionFailureKind.NotFound, failure.Kind);
        Assert.Equal("The resolution draft was not found.", failure.Message);
    }

    [Fact]
    public async Task Authenticated_cross_origin_form_is_rejected_without_state_change()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        var draft = await CreateDraftAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/returns/confirm") { Content = Form(draft.Id) };
        request.Headers.Add("Sec-Fetch-Site", "same-site");
        request.Headers.Add("Origin", "https://localhost:7444");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        Assert.Null((await store.GetDraftAsync(draft.Id))!.ConfirmedAt);
        Assert.Equal("Open", (await store.GetCaseAsync(48021))!.Status);
        Assert.Equal(12, (await store.GetInventoryAsync()).Single(p => p.ProductCode == "DOCK-PRO-PLUS").Available);
        Assert.Equal(4, (await store.GetAuditAsync(48021)).Count);
    }

    [Fact]
    public async Task Same_origin_confirmation_is_atomic_and_repeated_posts_are_idempotent()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        var draft = await CreateDraftAsync(client);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/returns/confirm") { Content = Form(draft.Id) };
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/returns/48021?completed=true", response.Headers.Location!.OriginalString);
        }
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        Assert.Equal("Resolved", (await store.GetCaseAsync(48021))!.Status);
        Assert.Equal(11, (await store.GetInventoryAsync()).Single(p => p.ProductCode == "DOCK-PRO-PLUS").Available);
        var confirmations = (await store.GetAuditAsync(48021)).Where(e => e.Action.StartsWith("Replacement confirmed", StringComparison.Ordinal)).ToArray();
        Assert.Single(confirmations);
        Assert.Equal("Taylor Brooks", confirmations[0].Actor);
    }

    [Fact]
    public async Task Inventory_change_after_validation_rolls_back_confirmation()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        var draft = await CreateDraftAsync(client);
        await SetStockAsync(factory.DatabasePath, 0);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var result = await store.ConfirmAsync(draft.Id, "Test representative");
        var failure = Assert.IsType<ResolutionFailure>(result.Value);
        Assert.Equal(ResolutionFailureKind.Conflict, failure.Kind);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/returns/confirm") { Content = Form(draft.Id) };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/review/{draft.Id}?error={Uri.EscapeDataString(failure.Message)}", response.Headers.Location!.OriginalString);
        Assert.Equal("Open", (await store.GetCaseAsync(48021))!.Status);
        Assert.Null((await store.GetDraftAsync(draft.Id))!.ConfirmedAt);
        Assert.Equal(4, (await store.GetAuditAsync(48021)).Count);
    }

    [Fact]
    public async Task Competing_returns_cannot_allocate_the_last_unit_twice()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        await DemoFactory.SignInAsync(client);
        var first = await CreateDraftAsync(client);
        var second = await CreateDraftAsync(client, 48023);
        await SetStockAsync(factory.DatabasePath, 1);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var results = await Task.WhenAll(
            store.ConfirmAsync(first.Id, "Test representative"),
            store.ConfirmAsync(second.Id, "Test representative"));
        Assert.Single(results, result => result.Value is ReturnCase);
        Assert.Equal(ResolutionFailureKind.Conflict,
            Assert.IsType<ResolutionFailure>(Assert.Single(results, result => result.Value is ResolutionFailure).Value).Kind);
        Assert.Equal(0, (await store.GetInventoryAsync()).Single(p => p.ProductCode == "DOCK-PRO-PLUS").Available);
        Assert.Single(await store.GetCasesAsync(), c => c.Status == "Resolved");
    }

    [Fact]
    public async Task Confirmed_resolution_survives_host_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eshop-persistence-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new DemoFactory(path, deleteOnDispose: false))
            {
                using var client = first.Browser();
                await DemoFactory.SignInAsync(client);
                var store = first.Api.Services.GetRequiredService<ReturnStore>();
                var result = await store.SaveDraftAsync(new(48022, new Resolution(new RefundResolution(149m, "PAY-48022"))), "Test representative");
                var draft = Assert.IsType<ResolutionDraft>(result.Value);
                Assert.IsType<ReturnCase>((await store.ConfirmAsync(draft.Id, "Test representative")).Value);
            }
            using (var restarted = new DemoFactory(path, deleteOnDispose: false))
            {
                using var client = restarted.Browser();
                var item = await restarted.Api.Services.GetRequiredService<ReturnStore>().GetCaseAsync(48022);
                Assert.Equal("Resolved", item!.Status);
                Assert.IsType<RefundResolution>(item.Resolution!.Value.Value);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reset_requires_authentication_and_a_trusted_form_origin()
    {
        using var factory = new DemoFactory();
        using var client = factory.Browser();
        using var anonymous = await client.PostAsync("/demo/reset", new FormUrlEncodedContent(new Dictionary<string, string> { ["confirmation"] = "RESET" }));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        await DemoFactory.SignInAsync(client);
        var draft = await CreateDraftAsync(client);
        using var untrusted = new HttpRequestMessage(HttpMethod.Post, "/demo/reset")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["confirmation"] = "RESET" })
        };
        untrusted.Headers.Add("Sec-Fetch-Site", "same-site");
        using var rejected = await client.SendAsync(untrusted);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        Assert.NotNull(await store.GetDraftAsync(draft.Id));
        using var trusted = new HttpRequestMessage(HttpMethod.Post, "/demo/reset")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["confirmation"] = "RESET" })
        };
        trusted.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var reset = await client.SendAsync(trusted);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Null(await store.GetDraftAsync(draft.Id));
        Assert.Equal(3, (await store.GetAuditAsync(48021)).Count);
    }

    private static async Task<ResolutionDraft> CreateDraftAsync(HttpClient client, int orderId = 48021)
    {
        using var response = await client.PostAsJsonAsync("/api/drafts", new SaveResolutionRequest(orderId,
            new Resolution(new ReplacementResolution("DOCK-PRO-PLUS", "120 Market Street, Seattle"))));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResolutionDraft>())!;
    }

    private static FormUrlEncodedContent Form(Guid id) =>
        new(new Dictionary<string, string> { ["draftId"] = id.ToString() });

    private static async Task SetStockAsync(string path, int available)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "UPDATE Inventory SET Available = $available WHERE ProductCode = 'DOCK-PRO-PLUS'";
        command.Parameters.AddWithValue("$available", available);
        await command.ExecuteNonQueryAsync();
    }
}
