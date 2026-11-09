using System.Security.Claims;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using eShopCustomerService.Web.Agentic;
using eShopCustomerService.Web.Data;
using eShopCustomerService.Web.Domain;
using eShopCustomerService.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace eShopCustomerService.Tests;

public sealed class AgenticTests
{
    [Fact]
    public async Task Markdown_stream_renders_lists_and_emphasis_without_changing_text_tools_or_state()
    {
        var snapshot = new StateSnapshotEvent
        {
            Snapshot = JsonSerializer.SerializeToElement(new CaseWorkspaceState { OrderId = 48024 })
        };
        var tool = new FunctionCallContent("evidence-1", "get_case_context", new Dictionary<string, object?>());
        using var chat = new MarkdownChatClient(new MarkdownModel(snapshot, tool));
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chat.GetStreamingResponseAsync([new(ChatRole.User, "Investigate")]))
        {
            updates.Add(update);
        }
        var rich = updates[1].Contents.OfType<RichTextContent>().Single();
        Assert.Equal("- **Evidence:** Delayed\n- **Next:** Review", rich.Text);
        var list = Assert.IsType<ListNode>(Assert.Single(rich.Nodes));
        var paragraph = Assert.IsType<ParagraphNode>(Assert.IsType<ListItemNode>(list.Children[0]).Children[0]);
        Assert.IsType<StrongNode>(paragraph.Children[0]);
        Assert.Equal("idence:** Delayed\n- **Next:** Review", Assert.Single(updates[1].Contents.OfType<TextContent>()).Text);
        Assert.Same(tool, Assert.Single(updates[2].Contents));
        Assert.Same(snapshot, updates[2].RawRepresentation);
        Assert.Equal("A separate answer", Assert.Single(updates[3].Contents.OfType<RichTextContent>()).Text);
        var html = Assert.IsType<TextNode>(Assert.Single(MarkdownChatClient.Parse("<script>alert('test')</script>")));
        Assert.Contains("<script>", html.Text);
    }

    [Fact]
    public async Task Configured_agent_workspace_prerenders_its_controls_and_shared_plan()
    {
        using var baseline = new DemoFactory();
        using var factory = baseline.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agentic:FoundryEndpoint"] = "https://example.test/",
                    ["Agentic:ServiceUrl"] = "https://localhost"
                }));
            builder.ConfigureServices((context, services) => services.AddCustomerServiceAgent(context.Configuration));
        });
        using var browser = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        await DemoFactory.SignInAsync(browser);
        using var response = await browser.GetAsync("/agentic/48024");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Prepare replacement", html);
        Assert.Contains("Show review", html);
        Assert.Contains("Delivery address", html);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Agui_approval_continuation_preserves_the_decision_and_surfaces_run_errors(
        bool approved, bool failAfterConfirmation)
    {
        using var factory = new DemoFactory();
        var allocations = 0;
        var function = AIFunctionFactory.Create(() =>
        {
            allocations++;
            return "Committed locally";
        }, "confirm_resolution");
        var model = new ApprovalModel(failAfterConfirmation);
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "CustomerService",
            ChatHistoryProvider = AgentHosting.CreateAguiHistoryProvider(),
            ChatOptions = new() { Tools = [new ApprovalRequiredAIFunction(function)] }
        });
        await using var app = await CreateTransportAppAsync(factory, agent);
        using var http = app.GetTestClient();
        var connection = new AgentConnection(new TestClientFactory(http),
            factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>(),
            app.Configuration, NullLogger<AgentConnection>.Instance);
        using var chat = connection.Create(48024, new(new ClaimsIdentity(
            [new(DemoAuthentication.SessionClaim, "test-session")], "Test")));
        var threadId = Guid.NewGuid().ToString("N");
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                ThreadId = threadId,
                State = JsonSerializer.SerializeToElement(new CaseWorkspaceState { OrderId = 48024 })
            }
        };
        List<ChatMessage> messages = [new(ChatRole.User, "Confirm the saved draft")];
        var pending = await chat.GetResponseAsync(messages, options);
        var approval = Assert.Single(pending.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal(0, allocations);
        messages.AddRange(pending.Messages);
        messages.Add(new(ChatRole.User, [approval.CreateResponse(approved)]));
        if (failAfterConfirmation)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.GetResponseAsync(messages, options));
            Assert.Contains("Check the order's saved state", error.Message);
        }
        else
        {
            var completed = await chat.GetResponseAsync(messages, options);
            Assert.Contains(approved ? "Committed locally" : "Not committed", completed.Text);
        }
        Assert.Equal(approved ? 1 : 0, allocations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Frontend_tool_execution_returns_to_the_agent_without_authorizing_backend_confirmation(
        bool reviewFirst, bool approved)
    {
        using var factory = new DemoFactory();
        var allocations = 0;
        var frontendCalls = 0;
        var model = new FrontendApprovalModel();
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "CustomerService",
            ChatHistoryProvider = AgentHosting.CreateAguiHistoryProvider(),
            ChatOptions = new()
            {
                Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() =>
                {
                    allocations++;
                    return "Committed locally";
                }, "confirm_resolution"))]
            }
        });
        await using var app = await CreateTransportAppAsync(factory, agent);
        using var http = app.GetTestClient();
        var connection = new AgentConnection(new TestClientFactory(http),
            factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>(),
            app.Configuration, NullLogger<AgentConnection>.Instance);
        using var chat = connection.Create(48024, new(new ClaimsIdentity(
            [new(DemoAuthentication.SessionClaim, "test-session")], "Test")));
        var threadId = Guid.NewGuid().ToString("N");
        using var ui = new UIAgent(chat, options =>
        {
            options.RegisterUIAction(AIFunctionFactory.Create(() =>
            {
                frontendCalls++;
                return "Review opened";
            }, "show_resolution_review"));
            options.ChatOptions = new()
            {
                RawRepresentationFactory = _ => new RunAgentInput { ThreadId = threadId }
            };
        });
        using var context = new AgentContext(ui);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var awaitingInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = context.RegisterOnStatusChanged(status =>
        {
            if (status == ConversationStatus.AwaitingInput)
            {
                awaitingInput.TrySetResult();
            }
        });

        if (reviewFirst)
        {
            var reviewTurn = context.SendMessageAsync("Show review", timeout.Token);
            await Task.WhenAny(reviewTurn, awaitingInput.Task).WaitAsync(timeout.Token);
            Assert.Equal(ConversationStatus.AwaitingInput, context.Status);
            var action = Assert.Single(context.Turns.Last().ResponseBlocks.OfType<UIActionBlock>());
            Assert.Equal(0, frontendCalls);
            Assert.Equal(0, allocations);
            await action.InvokeAsync(timeout.Token);
            await action.InvokeAsync(timeout.Token);
            await reviewTurn;
            Assert.Equal(ConversationStatus.Idle, context.Status);
            Assert.Equal(1, frontendCalls);
            Assert.True(model.SawFrontendResult);
            Assert.Equal(0, allocations);
            awaitingInput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var confirmationTurn = context.SendMessageAsync("Confirm", timeout.Token);
        await Task.WhenAny(confirmationTurn, awaitingInput.Task).WaitAsync(timeout.Token);
        Assert.Null(context.Error);
        if (!reviewFirst)
        {
            // AGUI.Server 1.0.1 suppresses backend approval interrupts with unused client tools (#2393).
            await confirmationTurn;
            Assert.Equal(ConversationStatus.Idle, context.Status);
            Assert.Empty(context.Turns.Last().ResponseBlocks.OfType<FunctionApprovalBlock>());
            Assert.Equal(0, allocations);
            Assert.Equal(0, frontendCalls);
            return;
        }
        Assert.Equal(ConversationStatus.AwaitingInput, context.Status);
        var approval = Assert.Single(context.Turns.Last().ResponseBlocks.OfType<FunctionApprovalBlock>());
        Assert.Equal(0, allocations);
        if (approved)
        {
            approval.Approve();
        }
        else
        {
            approval.Reject();
        }
        await confirmationTurn;
        Assert.Null(context.Error);
        Assert.Equal(ConversationStatus.Idle, context.Status);
        Assert.Equal(approved ? 1 : 0, allocations);
        Assert.Equal(reviewFirst ? 1 : 0, frontendCalls);
    }

    [Fact]
    public async Task Preparing_then_confirming_uses_the_existing_atomic_idempotent_workflow()
    {
        using var factory = new DemoFactory();
        var store = factory.Services.GetRequiredService<ReturnStore>();
        var tools = CreateTools(factory);
        var stock = (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available;
        var draft = await tools.PrepareResolutionAsync("DOCK-PRO-PLUS", "200 Demo Way", "Your replacement is ready for review.", default);
        Assert.Equal("Draft", draft.Status);
        Assert.Equal("Open", (await store.GetCaseAsync(48024))!.Status);
        Assert.Equal(stock, (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);

        var confirmed = await tools.ConfirmResolutionAsync(draft.DraftId!.Value, draft.ProductCode, draft.DeliveryAddress, default);
        Assert.Equal("Resolved", confirmed.Status);
        await tools.ConfirmResolutionAsync(draft.DraftId.Value, draft.ProductCode, draft.DeliveryAddress, default);
        Assert.Equal(stock - 1, (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);
        var committed = Assert.IsType<ReplacementResolution>((await store.GetCaseAsync(48024))!.Resolution!.Value.Value);
        Assert.Equal("200 Demo Way", committed.DeliveryAddress);
        Assert.Single(await store.GetAuditAsync(48024), a => a.Action.Contains("one unit allocated"));
        Assert.Equal("Test representative", (await store.GetAuditAsync(48024)).First().Actor);
    }

    [Fact]
    public async Task Generated_tool_payload_projects_web_json_into_complete_case_evidence()
    {
        using var factory = new DemoFactory();
        var evidence = await CreateTools(factory).GetCaseContextAsync(default);
        var block = new CaseContextBlock
        {
            Payload = JsonSerializer.SerializeToElement(evidence, JsonSerializerOptions.Web)
        };
        Assert.Equal(48024, block.CaseInfo!.Order.OrderId);
        Assert.Equal(12, block.CaseInfo.Inventory.Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);
        Assert.Contains(block.CaseInfo.Activity, a => a.Action.Contains("Seattle hub"));
    }

    [Fact]
    public async Task Confirmation_rejects_cross_order_and_changed_approval_details_without_allocating()
    {
        using var factory = new DemoFactory();
        var store = factory.Services.GetRequiredService<ReturnStore>();
        var other = await store.SaveDraftAsync(new(48021, new Resolution(new ReplacementResolution("DOCK-PRO-PLUS", "200 Demo Way"))), "Other", default);
        var otherDraft = Assert.IsType<ResolutionDraft>(other.Value);
        var tools = CreateTools(factory);
        var before = await store.GetInventoryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.ConfirmResolutionAsync(otherDraft.Id, "DOCK-PRO-PLUS", "200 Demo Way", default));
        var draft = await tools.PrepareResolutionAsync("DOCK-PRO-PLUS", "200 Demo Way", "Draft", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.ConfirmResolutionAsync(draft.DraftId!.Value, "DOCK-PRO-PLUS", "Changed address", default));
        Assert.Equal(before, await store.GetInventoryAsync());
        Assert.Equal("Open", (await store.GetCaseAsync(48024))!.Status);
    }

    [Fact]
    public async Task Unavailable_replacement_and_anonymous_agent_tools_fail_explicitly()
    {
        using var factory = new DemoFactory();
        var store = factory.Services.GetRequiredService<ReturnStore>();
        var audit = await store.GetAuditAsync(48024);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateTools(factory).PrepareResolutionAsync("DOCK-PRO", "200 Demo Way", "Draft", default));
        Assert.Equal(audit, await store.GetAuditAsync(48024));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateTools(factory, authenticated: false).GetCaseContextAsync(default));
    }

    private static CaseTools CreateTools(DemoFactory factory, bool authenticated = true)
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["orderId"] = 48024;
        if (authenticated)
        {
            context.User = new(new ClaimsIdentity(
                [new(ClaimTypes.Name, "Test representative"), new(ClaimTypes.Role, "Representative")], "Test"));
        }
        return new(factory.Services.GetRequiredService<ReturnStore>(),
            new HttpContextAccessor { HttpContext = context },
            factory.Services.GetRequiredService<IAuthorizationService>(), NullLogger<CaseTools>.Instance);
    }

    private static async Task<WebApplication> CreateTransportAppAsync(DemoFactory factory, AIAgent agent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Agentic:FoundryEndpoint"] = "https://example.test/";
        builder.Services.AddSingleton(factory.Services.GetRequiredService<ReturnStore>());
        builder.Services.AddAuthorization();
        builder.Services.AddCustomerServiceAgent(builder.Configuration);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new(new ClaimsIdentity(
                [new(DemoAuthentication.SessionClaim, "test-session")], "Test"));
            return next(context);
        });
        var wrappedAgent = new CaseStateAgent(agent);
        Assert.Equal("CustomerService", wrappedAgent.Name);
        app.MapAGUIServer("/api/agents/returns/{orderId:int}", wrappedAgent);
        await app.StartAsync();
        return app;
    }

    private sealed class FrontendApprovalModel : IChatClient
    {
        public bool SawFrontendResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var history = messages.ToList();
            var reviewing = history.Last(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text)).Text == "Show review";
            var callId = reviewing ? "review-1" : "confirm-1";
            var result = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .LastOrDefault(r => r.CallId == callId);
            if (reviewing && result is not null)
            {
                SawFrontendResult = result.Result?.ToString()?.Contains("Review opened") == true;
            }
            yield return result is not null
                ? new(ChatRole.Assistant, reviewing ? "Review ready" : "Decision received")
                : new()
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent(callId,
                        reviewing ? "show_resolution_review" : "confirm_resolution", new Dictionary<string, object?>())]
                };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MarkdownModel(StateSnapshotEvent snapshot, FunctionCallContent tool) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new(ChatRole.Assistant, "- **Ev") { MessageId = "answer-1" };
            yield return new(ChatRole.Assistant, "idence:** Delayed\n- **Next:** Review") { MessageId = "answer-1" };
            yield return new() { Role = ChatRole.Assistant, Contents = [tool], RawRepresentation = snapshot };
            yield return new(ChatRole.Assistant, "A separate answer") { MessageId = "answer-2" };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ApprovalModel(bool failAfterConfirmation) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (result is not null && failAfterConfirmation)
            {
                throw new InvalidOperationException("Test inference failed after the tool completed.");
            }
            yield return result is not null
                ? new(ChatRole.Assistant, result.Result?.ToString() == "Committed locally" ? "Committed locally" : "Not committed")
                : new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("confirm-1", "confirm_resolution", new Dictionary<string, object?>())] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestClientFactory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }
}
