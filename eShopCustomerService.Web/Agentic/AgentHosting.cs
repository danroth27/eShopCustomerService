using System.ClientModel.Primitives;
using System.Security.Claims;
using AGUI.Server;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Microsoft.Extensions.Http.Resilience;
using eShopCustomerService.Web.Services;

namespace eShopCustomerService.Web.Agentic;

public static class AgentHosting
{
    public static void AddCustomerServiceAgent(this IServiceCollection services, IConfiguration configuration)
    {
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

        if (string.IsNullOrWhiteSpace(configuration["Agentic:FoundryEndpoint"]))
        {
            return;
        }
        services.AddHttpContextAccessor();
        services.AddSingleton<CaseTools>();
        services.AddKeyedSingleton<AgentSessionStore>("CustomerService", new InMemoryAgentSessionStore());
        services.AddSingleton<AgentIsolationKeyProvider, CaseAgentIsolation>();
        services.AddAGUIServer();
    }

    public static void MapCustomerServiceAgent(this WebApplication app)
    {
        if (app.Configuration["Agentic:FoundryEndpoint"] is not { Length: > 0 } endpoint)
        {
            return;
        }
        var model = app.Configuration["Agentic:Model"] ?? "gpt-5-mini";
        var client = new OpenAIClient(
            new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"),
            new OpenAIClientOptions { Endpoint = new Uri(new Uri(endpoint), "openai/v1/") });
        var tools = app.Services.GetRequiredService<CaseTools>();
        var agent = client.GetChatClient(model).AsAIAgent(new ChatClientAgentOptions
        {
            Name = "CustomerService",
            ChatHistoryProvider = CreateAguiHistoryProvider(),
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You help a customer-service representative resolve the selected delayed-delivery case.
                    All data is fictional. There are no real payments, shipments, emails, or carrier calls.
                    Work only on the selected case. Always use get_case_context before investigating or preparing a resolution.
                    The carrier history is seeded evidence, not a live carrier integration.
                    When asked to investigate, explain the delay and available replacement in under 90 words.
                    Format the investigation as short Markdown bullet points with bold labels for Evidence,
                    Availability, and Recommendation. Do not use tables or raw HTML.
                    When asked to prepare or update a replacement, call prepare_resolution with the complete replacement
                    product code, full delivery address, and a concise proposed customer reply.
                    Prefer the in-stock compatible DOCK-PRO-PLUS. Preserve the latest delivery address in the workspace.
                    A draft reserves no stock and sends no message. Never confirm while merely preparing a draft.
                    The proposed customer reply must describe a replacement awaiting approval. Never claim that it
                    has been shipped, promise an arrival date, or imply that an unsent reply reached the customer.
                    After preparing, respond in one sentence; the workspace already shows the details.
                    When asked to show the resolution review, call the frontend tool show_resolution_review
                    for the existing draft, then briefly acknowledge its result. Do not prepare a new draft or confirm.
                    If no draft exists yet, explain that one must be prepared first.
                    Opening the review is not approval. Never call confirm_resolution merely because a review opened.
                    Only when explicitly asked to confirm, call confirm_resolution with the saved draft ID and exact
                    product code and delivery address. The app collects approval: do not ask for approval in prose.
                    If confirmation is rejected, acknowledge it in one sentence and stop. Never retry without a new request.
                    If a tool fails, explicitly explain the failure and do not claim success.
                    After confirmation, state that one unit was allocated locally, not shipped. The reply remains unsent.
                    """,
                AllowMultipleToolCalls = false,
                Tools =
                [
                    AIFunctionFactory.Create(tools.GetCaseContextAsync, "get_case_context",
                        "Read the selected order, seeded carrier activity, and current stock."),
                    AIFunctionFactory.Create(tools.PrepareResolutionAsync, "prepare_resolution",
                        "Save a complete replacement draft and synchronize the workspace. Does not allocate stock."),
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                        tools.ConfirmResolutionAsync, "confirm_resolution",
                        "Confirm the saved replacement after human approval. Allocates one unit locally, not a real shipment."))
                ]
            }
        });
        app.MapAGUIServer("/api/agents/returns/{orderId:int}", new CaseStateAgent(agent))
            .WithMetadata(new AGUIStreamOptions()
                .MapResultAsStateSnapshot("prepare_resolution")
                .MapResultAsStateSnapshot("confirm_resolution"))
            .RequireAuthorization("CustomerService")
            .ExcludeFromDescription();
    }

    public static ChatHistoryProvider CreateAguiHistoryProvider() =>
        // AG-UI supplies full history. Keep approval state, not a duplicate server-side transcript.
        new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            StorageInputRequestMessageFilter = _ => [],
            StorageInputResponseMessageFilter = _ => []
        });

    private sealed class CaseAgentIsolation(IHttpContextAccessor accessor) : AgentIsolationKeyProvider
    {
        public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default)
        {
            var context = accessor.HttpContext;
            var session = context?.User.FindFirstValue(DemoAuthentication.SessionClaim);
            return ValueTask.FromResult<string?>(
                context?.User.Identity?.IsAuthenticated == true && session is not null
                    && context.Request.RouteValues["orderId"] is { } orderId
                    ? $"{session}:{orderId}"
                    : null);
        }
    }
}
