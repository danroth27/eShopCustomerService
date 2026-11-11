using Microsoft.Extensions.Http.Resilience;

namespace eShopCustomerService.Web.Agentic;

public static class AgentServices
{
    public static void AddCustomerServiceAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<AgentConnection>();
        services.AddHttpClient("agentic", http =>
        {
            if (configuration["Agentic:ServiceUrl"] is { Length: > 0 } url)
            {
                var endpoint = new Uri(url);
                if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidOperationException("The local agent transport requires a loopback HTTPS service URL.");
                }
                http.BaseAddress = endpoint;
            }
            http.Timeout = TimeSpan.FromMinutes(3);
        }).RemoveAllResilienceHandlers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
    }
}
