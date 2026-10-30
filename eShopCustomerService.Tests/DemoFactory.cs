using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace eShopCustomerService.Tests;

public sealed class DemoFactory(string? databasePath = null, bool deleteOnDispose = true) : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = databasePath
        ?? Path.Combine(Path.GetTempPath(), $"eshop-returns-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Demo:DatabasePath"] = DatabasePath,
                ["Demo:ValidationDelayMilliseconds"] = "0"
            }));
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    public static async Task SignInAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/demo/sign-in")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "/" })
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && deleteOnDispose)
        {
            File.Delete(DatabasePath);
        }
    }
}
