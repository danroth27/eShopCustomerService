using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using eShopCustomerService.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace eShopCustomerService.Tests;

public sealed class DemoFactory : WebApplicationFactory<Program>
{
    private readonly bool deleteOnDispose;
    public string DatabasePath { get; }
    public ApiFactory Api { get; }

    public DemoFactory(string? databasePath = null, bool deleteOnDispose = true)
    {
        DatabasePath = databasePath ?? Path.Combine(Path.GetTempPath(), $"eshop-returns-{Guid.NewGuid():N}.db");
        Api = new(DatabasePath);
        this.deleteOnDispose = deleteOnDispose;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CustomerService:ApiUrl"] = "https://localhost"
            }));
        builder.ConfigureServices(services => services.AddHttpClient("application-api")
            .ConfigurePrimaryHttpMessageHandler(() => Api.Server.CreateHandler()));
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
        if (disposing)
        {
            Api.Dispose();
            if (deleteOnDispose)
            {
                File.Delete(DatabasePath);
            }
        }
    }
}

public sealed class ApiFactory(string databasePath) : WebApplicationFactory<ApiProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Demo:DatabasePath"] = databasePath,
                ["Demo:ValidationDelayMilliseconds"] = "0"
            }));
    }
}
