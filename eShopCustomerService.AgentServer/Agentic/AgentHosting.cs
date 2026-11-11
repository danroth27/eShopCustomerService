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
                    You help a customer-service representative using the existing resolution editor for the selected case.
                    All data is fictional. There are no real payments, shipments, emails, or carrier calls.
                    Work only on the selected case. Use get_case_context for the initial investigation,
                    when the user explicitly asks to refresh evidence, or when necessary case facts are missing.
                    Reuse evidence already retrieved in this conversation. Do not repeat get_case_context before
                    ordinary editor changes, reply previews, opening review, or requesting confirmation.
                    The request state supplies current editor values; the API independently revalidates confirmation.
                    The carrier history is seeded evidence, not a live carrier integration.
                    When asked to investigate, explain the delay and available replacement in under 90 words.
                    Format the investigation as short Markdown bullet points with bold labels for Evidence,
                    Availability, and Recommendation. Do not use tables or raw HTML.
                    The request state contains the COMPLETE current resolution editor, including unsaved edits:
                    resolutionKind (refund or replacement), productCode, deliveryAddress, customerReply,
                    refundAmount, paymentReference, refundApprovalAllowed, availableProducts, validationMessages,
                    validationPending and status.
                    This is the same form the representative edits directly, not a separate agent workspace.
                    Treat its current values as the user's intent. Never replace their chosen resolution type,
                    product, address, or reply unless requested or necessary to correct an explicitly explained error.
                    The current editor state IS the draft. There is no separate saved draft, draft ID, or save step.
                    Use update_resolution_editor to change any editable field or combination of fields.
                    Unspecified fields remain unchanged. Never ask the representative to save before review or approval.
                    Refunds are full refunds to the original payment method; amount and payment reference are authoritative,
                    read-only order details, not editable values. Refund permission is checked by the application.
                    When asked to prepare a resolution, use update_resolution_editor to edit the current UI state.
                    Recommend the in-stock compatible DOCK-PRO-PLUS, but do not override a user's explicit product choice.
                    Editing reserves no stock and sends no message. Never confirm while merely editing.
                    The proposed customer reply must describe the chosen resolution awaiting approval. Never claim that it
                    has been shipped, promise an arrival date, or imply that an unsent reply reached the customer.
                    After editing, respond in one sentence; the workspace already shows the details.
                    When asked only to edit, revise, or preview the customer reply, call the frontend tool
                    propose_customer_reply with the complete revised reply. Do not call confirm_resolution for reply-only edits.
                    Preserve the latest workspace reply except for the requested edits. Leave accepted unset:
                    the UI collects the user's decision. After acceptance or discard, briefly acknowledge
                    the result and stop. Never propose another edit or prepare or confirm during that continuation.
                    Accepting reply edits only keeps text in the workspace; it does not send a message or approve a replacement.
                    When asked to show the resolution review, call the frontend tool show_resolution_review
                    immediately using the current editor state, then briefly acknowledge its result. Do not change
                    any fields or confirm. Review is available even before any other agent interaction.
                    Opening the review is not approval. Never call confirm_resolution merely because a review opened.
                    Only when explicitly asked to confirm, call confirm_resolution with the exact current
                    resolutionKind and details from the CURRENT editor. For replacements include exact productCode and
                    deliveryAddress; for refunds include exact refundAmount and paymentReference. The app collects approval:
                    do not ask for approval in prose.
                    If confirmation is rejected, acknowledge it in one sentence and stop. Never retry without a new request.
                    If a tool fails, explicitly explain the failure and do not claim success.
                    After confirmation, describe the recorded resolution: one unit allocated locally for a replacement,
                    or a refund recorded locally. No payment or shipment was processed. The reply remains unsent.
                    """,
                AllowMultipleToolCalls = false,
                Tools =
                [
                    AIFunctionFactory.Create(tools.GetCaseContextAsync, "get_case_context",
                        "Read the selected order, seeded carrier activity, and current stock."),
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                        tools.ConfirmResolutionAsync, "confirm_resolution",
                        "Confirm the exact current refund or replacement editor values after human approval. No saved draft or draft ID is needed. Revalidates permissions and business rules. No real payment or shipment."))
                ]
            }
        });
        app.MapAGUIServer("/api/agents/returns/{orderId:int}", new CaseStateAgent(agent))
            .WithMetadata(new AGUIStreamOptions()
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
