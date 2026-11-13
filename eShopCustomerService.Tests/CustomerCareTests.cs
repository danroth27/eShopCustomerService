using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using eShopCustomerService.Api.Data;
using eShopCustomerService.Contracts.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace eShopCustomerService.Tests;

public sealed class CustomerCareTests
{
    [Theory]
    [InlineData(48022, "alex@example.com")]
    [InlineData(48021, "not-an-email")]
    public async Task Customer_request_api_rejects_other_orders_and_invalid_contact_details(int orderId, string email)
    {
        using var factory = new DemoFactory(DatabasePath());
        using var client = factory.Api.CreateClient();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var before = await store.GetCasesAsync();
        using var response = await client.PostAsJsonAsync("/api/customer-requests", new CustomerReturnRequest
        {
            OrderId = orderId,
            Name = "Alex Morgan",
            Email = email,
            Message = "Please arrange a compatible replacement."
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await store.GetCasesAsync());
        Assert.Equal(0, await RequestCountAsync(factory.DatabasePath));
    }

    [Fact]
    public async Task Customer_request_persists_on_the_same_case_and_explicit_reset_clears_it()
    {
        var path = DatabasePath();
        CustomerRequestReceipt receipt;
        using (var factory = new DemoFactory(path, deleteOnDispose: false))
        {
            var store = factory.Api.Services.GetRequiredService<ReturnStore>();
            var inventory = await store.GetInventoryAsync();
            var otherOrder = await store.GetCaseAsync(48022);
            var result = await store.SubmitCustomerRequestAsync(ValidRequest());
            receipt = Assert.IsType<CustomerRequestReceipt>(result.Value);
            var order = (await store.GetCaseAsync(48021))!;
            Assert.Equal(ValidRequest().Message, order.Reason);
            Assert.Equal("alex.care@example.com", order.Email);
            Assert.Equal("Open", order.Status);
            Assert.Null(order.Resolution);
            Assert.Equal(otherOrder, await store.GetCaseAsync(48022));
            Assert.Equal(inventory, await store.GetInventoryAsync());
            Assert.Contains(await store.GetAuditAsync(48021),
                entry => entry.Actor == "Alex Morgan" && entry.Action.Contains(receipt.Reference));
        }

        using var reopened = new DemoFactory(path);
        var reopenedStore = reopened.Api.Services.GetRequiredService<ReturnStore>();
        Assert.Equal(ValidRequest().Message, (await reopenedStore.GetCaseAsync(receipt.OrderId))!.Reason);
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT Message FROM CustomerRequests WHERE Reference = $reference AND OrderId = 48021";
            command.Parameters.AddWithValue("$reference", receipt.Reference);
            Assert.Equal(ValidRequest().Message, await command.ExecuteScalarAsync());
        }

        await reopenedStore.ResetAsync();
        Assert.Equal(0, await RequestCountAsync(path));
        Assert.Equal("alex@example.com", (await reopenedStore.GetCaseAsync(48021))!.Email);
    }

    [Theory]
    [InlineData("", "alex@example.com", "Displays keep disconnecting.", 48021, ResolutionFailureKind.Invalid)]
    [InlineData("Alex Morgan", "not-an-email", "Displays keep disconnecting.", 48021, ResolutionFailureKind.Invalid)]
    [InlineData("Alex Morgan", "alex@example.com", "          ", 48021, ResolutionFailureKind.Invalid)]
    [InlineData("Alex Morgan", "alex@example.com", "Displays keep disconnecting.", 99999, ResolutionFailureKind.NotFound)]
    public async Task Invalid_or_missing_order_requests_do_not_mutate_any_case(
        string name, string email, string message, int orderId, ResolutionFailureKind expected)
    {
        using var factory = new DemoFactory(DatabasePath());
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var cases = await store.GetCasesAsync();
        var audit = await store.GetAuditAsync(48021);
        var result = await store.SubmitCustomerRequestAsync(new()
        {
            Name = name,
            Email = email,
            Message = message,
            OrderId = orderId
        });
        Assert.Equal(expected, Assert.IsType<ResolutionFailure>(result.Value).Kind);
        Assert.Equal(cases, await store.GetCasesAsync());
        Assert.Equal(audit, await store.GetAuditAsync(48021));
        Assert.Equal(0, await RequestCountAsync(factory.DatabasePath));
    }

    [Fact]
    public async Task Customer_submission_cannot_reopen_a_resolved_order()
    {
        using var factory = new DemoFactory(DatabasePath());
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var draftResult = await store.SaveDraftAsync(new(48021, new Resolution(new RefundResolution(149, "PAY-48021"))), "Representative");
        var draft = Assert.IsType<ResolutionDraft>(draftResult.Value);
        var resolved = Assert.IsType<ReturnCase>((await store.ConfirmAsync(draft.Id, "Representative")).Value);
        var audit = await store.GetAuditAsync(48021);
        var result = await store.SubmitCustomerRequestAsync(ValidRequest());
        Assert.Equal(ResolutionFailureKind.Conflict, Assert.IsType<ResolutionFailure>(result.Value).Kind);
        Assert.Equal(resolved, await store.GetCaseAsync(48021));
        Assert.Equal(audit, await store.GetAuditAsync(48021));
        Assert.Equal(0, await RequestCountAsync(factory.DatabasePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("48021")]
    [InlineData("48022")]
    public async Task Anonymous_static_care_journey_saves_the_request_and_consumes_thank_you_once(string? postedOrderId)
    {
        using var factory = new DemoFactory(DatabasePath());
        using var client = factory.Browser();
        foreach (var path in new[]
        {
            "/care", "/care/help", "/care/help?page=2&sort=Article&direction=Descending",
            "/care/help/display-connections", "/care/help/replacement-options", "/care/policy",
            "/care/request", "/care/received"
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("CUSTOMER CARE", html);
            Assert.DoesNotContain("Service representative", html);
            Assert.DoesNotContain("\"type\":\"server\"", html);
        }

        using var help = await client.GetAsync("/care/help");
        var helpHtml = await help.Content.ReadAsStringAsync();
        var articlePaths = Regex.Matches(helpHtml, "href=\"(/care/help/[^\"]+)\"")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).Distinct().ToArray();
        Assert.Equal(3, articlePaths.Length);
        foreach (var path in articlePaths)
        {
            using var article = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, article.StatusCode);
        }

        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var untouchedOrder = await store.GetCaseAsync(48022);
        var fields = new Dictionary<string, string>
        {
            ["_handler"] = "care-request",
            ["Input.Name"] = "Alex Morgan",
            ["Input.Email"] = "alex.care@example.com",
            ["Input.Message"] = ValidRequest().Message
        };
        if (postedOrderId is not null)
        {
            fields["Input.OrderId"] = postedOrderId;
        }
        using var post = new HttpRequestMessage(HttpMethod.Post, "/care/request")
        {
            Content = new FormUrlEncodedContent(fields)
        };
        post.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var submitted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Redirect, submitted.StatusCode);
        Assert.Equal("/care/received", new Uri(client.BaseAddress!, submitted.Headers.Location!).AbsolutePath);
        Assert.Equal(untouchedOrder, await store.GetCaseAsync(48022));
        Assert.Equal(ValidRequest().Message, (await store.GetCaseAsync(48021))!.Reason);
        Assert.Equal(1, await RequestCountAsync(factory.DatabasePath));

        using var first = await client.GetAsync("/care/received");
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Contains("data-testid=\"flash-message\"", firstHtml);
        var referenceElement = Regex.Match(firstHtml,
            """<strong\b[^>]*\bdata-testid="request-reference"[^>]*>(.*?)</strong>""", RegexOptions.Singleline);
        Assert.True(referenceElement.Success, "The confirmation must render its session request reference.");
        var reference = WebUtility.HtmlDecode(Regex.Replace(referenceElement.Groups[1].Value, "<[^>]+>", "")).Trim();
        Assert.StartsWith("CARE-", reference);
        using var second = await client.GetAsync("/care/received");
        var secondHtml = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-testid=\"flash-message\"", secondHtml);
        Assert.Contains(reference, secondHtml);
        using var anotherBrowser = factory.Browser();
        using var privateReceipt = await anotherBrowser.GetAsync("/care/received");
        Assert.DoesNotContain(reference, await privateReceipt.Content.ReadAsStringAsync());
    }

    private static CustomerReturnRequest ValidRequest() => new()
    {
        Name = "Alex Morgan",
        Email = "alex.care@example.com",
        Message = "Both external displays disconnect after a few minutes. I would prefer a compatible replacement."
    };

    private static string DatabasePath() =>
        Path.Combine(Directory.GetCurrentDirectory(), $"customer-care-{Guid.NewGuid():N}.db");

    private static async Task<long> RequestCountAsync(string path)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM CustomerRequests";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
