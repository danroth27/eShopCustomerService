using System.ClientModel.Primitives;
using System.Security.Claims;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Azure.Identity;
using eShopCustomerService.Contracts;
using eShopCustomerService.AgentServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI;
using Microsoft.Extensions.Http.Resilience;

namespace eShopCustomerService.AgentServer.Agentic;

public static class AgentHosting
{
    public static void AddAgentService(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<ICustomerServiceClient, CustomerServiceClient>();
        services.AddHttpClient("customerservice", http =>
        {
            var endpoint = new Uri(configuration["CustomerService:ApiUrl"]
                ?? throw new InvalidOperationException("The customer-service application URL is required."));
            if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("The local application API requires a loopback HTTPS URL.");
            }
            http.BaseAddress = endpoint;
        }).RemoveAllResilienceHandlers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });

        services.AddSingleton<IChatClient>(services =>
        {
            var endpoint = configuration["Foundry:Endpoint"]
                ?? throw new InvalidOperationException("Configure the Foundry endpoint through the AppHost's user secrets.");
            var client = new OpenAIClient(
                new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"),
                new OpenAIClientOptions { Endpoint = new Uri(new Uri(endpoint), "openai/v1/") });
            return client.GetChatClient(configuration["Foundry:Model"] ?? "gpt-5-mini")
                .AsIChatClient()
                .AsBuilder()
                .UseLogging(services.GetRequiredService<ILoggerFactory>())
                .Build();
        });
        services.AddAuthentication("Representative")
            .AddScheme<AuthenticationSchemeOptions, RepresentativeAuthenticationHandler>("Representative", _ => { });
        services.AddAuthorization(options => options.AddPolicy("CustomerService", policy =>
            policy.RequireAuthenticatedUser().RequireRole("Representative")));
        services.AddKeyedSingleton<AgentSessionStore>("CustomerService", new InMemoryAgentSessionStore());
        services.AddSingleton<AgentIsolationKeyProvider, CaseAgentIsolation>();
        services.AddAGUIServer();
    }

    public static void MapCustomerServiceAgent(this WebApplication app)
    {
        var tools = app.Services.GetRequiredService<ICustomerServiceClient>();
        var agent = app.Services.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
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
                    When asked only to edit, revise, or preview the customer reply, call the frontend tool
                    propose_customer_reply with the complete revised reply. Do not call prepare_resolution
                    or confirm_resolution for reply-only edits. A saved replacement draft must exist first.
                    Preserve the latest workspace reply except for the requested edits. Leave accepted unset:
                    the UI collects the user's decision. After acceptance or discard, briefly acknowledge
                    the result and stop. Never propose another edit or prepare or confirm during that continuation.
                    Accepting reply edits only keeps text in the workspace; it does not send a message or approve a replacement.
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
                .MapResult("prepare_resolution", MapWorkspaceStateSnapshot)
                .MapResult("confirm_resolution", MapWorkspaceStateSnapshot))
            .RequireAuthorization("CustomerService")
            .ExcludeFromDescription();
    }

    private static IEnumerable<BaseEvent> MapWorkspaceStateSnapshot(FunctionResultContent result) =>
        // Rejection and tool errors remain tool results, not replacement workspace state.
        result.Result is JsonElement { ValueKind: JsonValueKind.Object } state
            && state.TryGetProperty("orderId", out _) && state.TryGetProperty("status", out _)
            ? [new StateSnapshotEvent { Snapshot = state }]
            : [];

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
            var session = context?.User.FindFirstValue(AgentIdentityResponse.SessionClaim);
            return ValueTask.FromResult<string?>(
                context?.User.Identity?.IsAuthenticated == true && session is not null
                    && context.Request.RouteValues["orderId"] is { } orderId
                    ? $"{session}:{orderId}"
                    : null);
        }
    }
}
