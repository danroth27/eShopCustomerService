using AGUI.Client;
using AGUI.Abstractions;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace eShopCustomerService.Web.Agentic;

public sealed class AgentConnection(
    IHttpClientFactory clients,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions, IConfiguration configuration,
    ILogger<AgentConnection> logger)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Agentic:ServiceUrl"]);

    public IChatClient Create(int orderId, ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("Enter the representative workspace before using the agent.");
        }

        var options = cookieOptions.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(user,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var http = clients.CreateClient("agentic");
        // The agent forwards this server-side cookie to the application API, which owns
        // session validation. No cookie keys or session database are shared with the agent.
        http.DefaultRequestHeaders.Add("Cookie",
            $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        return new MarkdownChatClient(new ServiceAgentChatClient(
            new AGUIChatClient(new AGUIChatClientOptions(http, $"/api/agents/returns/{orderId}")), http, logger));
    }

    private sealed class ServiceAgentChatClient(IChatClient inner, HttpClient http, ILogger logger) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                if (update.RawRepresentation is RunErrorEvent error)
                {
                    logger.LogError("The service agent failed ({Code}): {Message}", error.Code, error.Message);
                    throw new InvalidOperationException(
                        "The service agent could not complete this request. Check the order's saved state before retrying.");
                }
                yield return update;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                http.Dispose();
            }
        }
    }
}
