using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using eShopCustomerService.Contracts;
using eShopCustomerService.Api.Agentic;
using eShopCustomerService.AgentServer.Agentic;
using eShopCustomerService.Web.Agentic;
using eShopCustomerService.Api.Data;
using eShopCustomerService.Contracts.Domain;
using eShopCustomerService.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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
        using var chat = connection.Create(48024, CreatePrincipal(app));
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
        using var chat = connection.Create(48024, CreatePrincipal(app));
        var threadId = Guid.NewGuid().ToString("N");
        using var ui = new UIAgent(chat, options =>
        {
            options.RegisterUIAction(AIFunctionFactory.Create(
                (string customerReply, bool? accepted = null) => "Reply reviewed",
                "propose_customer_reply"));
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
    public void Reply_review_requires_a_current_preview_and_an_explicit_user_decision()
    {
        using var chat = new FrontendApprovalModel();
        using var ui = new UIAgent<CaseWorkspaceState>(chat, _ => { }, new() { CustomerReply = "Original reply" });
        Assert.Throws<InvalidOperationException>(() =>
            CaseWorkspaceUpdates.ReviewReply(ui.State, "Original reply", null));
        Assert.Throws<InvalidOperationException>(() =>
            CaseWorkspaceUpdates.ReviewReply(ui.State, "Stale reply", true));
        Assert.Equal("Original reply", ui.State.Value.CustomerReply);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("discard")]
    [InlineData("cancel")]
    public async Task Predictive_reply_edits_preview_then_accept_or_restore_without_changing_the_replacement(
        string decision)
    {
        using var factory = new DemoFactory();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var initial = await CreateTools(factory).PrepareResolutionAsync(
            "DOCK-PRO-PLUS", "200 Demo Way", "Original reply awaiting approval.", default);
        var inventory = await store.GetInventoryAsync();
        var audit = await store.GetAuditAsync(48024);
        var model = new ReplyProposalModel();
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "CustomerService",
            ChatHistoryProvider = AgentHosting.CreateAguiHistoryProvider()
        });
        await using var app = await CreateTransportAppAsync(factory, agent);
        using var http = app.GetTestClient();
        var connection = new AgentConnection(new TestClientFactory(http),
            factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>(),
            app.Configuration, NullLogger<AgentConnection>.Instance);
        using var chat = connection.Create(48024, CreatePrincipal(app));
        var threadId = Guid.NewGuid().ToString("N");
        UIAgent<CaseWorkspaceState> ui = null!;
        ui = new UIAgent<CaseWorkspaceState>(chat, options =>
        {
            options.StateMapper = context => CaseWorkspaceUpdates.Map(context, ui.State);
            options.RegisterUIAction(AIFunctionFactory.Create(
                (string customerReply, bool? accepted = null) =>
                    CaseWorkspaceUpdates.ReviewReply(ui.State, customerReply, accepted),
                "propose_customer_reply"));
            options.ChatOptions = new()
            {
                RawRepresentationFactory = _ => new RunAgentInput
                {
                    ThreadId = threadId,
                    State = JsonSerializer.SerializeToElement(ui.State.Value, JsonSerializerOptions.Web)
                }
            };
        }, initial);
        using var ownedUi = ui;
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

        var turn = context.SendMessageAsync("Preview reply edits", timeout.Token);
        await Task.WhenAny(turn, awaitingInput.Task).WaitAsync(timeout.Token);
        Assert.Null(context.Error);
        Assert.Equal(ConversationStatus.AwaitingInput, context.Status);
        Assert.True(ui.State.HasPendingPredictiveState);
        Assert.Equal(initial with { CustomerReply = ReplyProposalModel.ProposedReply }, ui.State.Value);
        var action = Assert.Single(context.Turns.Last().ResponseBlocks.OfType<UIActionBlock>());
        if (decision == "cancel")
        {
            await context.CancelAsync();
        }
        else
        {
            action.Call.Arguments!["accepted"] = decision == "accept";
            await action.InvokeAsync(timeout.Token);
        }
        await turn;
        Assert.Null(context.Error);
        Assert.False(ui.State.HasPendingPredictiveState);
        var expected = decision == "accept" ? ReplyProposalModel.ProposedReply : initial.CustomerReply;
        Assert.Equal(initial with { CustomerReply = expected }, ui.State.Value);
        await context.SendMessageAsync("Check current reply", timeout.Token);
        Assert.Contains(expected, model.LastWorkspace);
        Assert.Equal(inventory, await store.GetInventoryAsync());
        Assert.Equal(audit, await store.GetAuditAsync(48024));
        Assert.Equal("Open", (await store.GetCaseAsync(48024))!.Status);
        var savedDraft = (await store.GetDraftAsync(initial.DraftId!.Value))!;
        Assert.Equal(new ReplacementResolution(initial.ProductCode, initial.DeliveryAddress), savedDraft.Resolution.Value);
    }

    [Fact]
    public async Task Preparing_then_confirming_uses_the_existing_atomic_idempotent_workflow()
    {
        using var factory = new DemoFactory();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
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
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var other = await store.SaveDraftAsync(new(48021, new Resolution(new ReplacementResolution("DOCK-PRO-PLUS", "200 Demo Way"))), "Other", default);
        var otherDraft = Assert.IsType<ResolutionDraft>(other.Value);
        var tools = CreateTools(factory);
        var before = await store.GetInventoryAsync();
        await Assert.ThrowsAsync<CaseToolException>(() =>
            tools.ConfirmResolutionAsync(otherDraft.Id, "DOCK-PRO-PLUS", "200 Demo Way", default));
        var draft = await tools.PrepareResolutionAsync("DOCK-PRO-PLUS", "200 Demo Way", "Draft", default);
        await Assert.ThrowsAsync<CaseToolException>(() =>
            tools.ConfirmResolutionAsync(draft.DraftId!.Value, "DOCK-PRO-PLUS", "Changed address", default));
        Assert.Equal(before, await store.GetInventoryAsync());
        Assert.Equal("Open", (await store.GetCaseAsync(48024))!.Status);
    }

    [Fact]
    public async Task Unavailable_replacement_and_anonymous_agent_tools_fail_explicitly()
    {
        using var factory = new DemoFactory();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var audit = await store.GetAuditAsync(48024);
        await Assert.ThrowsAsync<CaseToolException>(() =>
            CreateTools(factory).PrepareResolutionAsync("DOCK-PRO", "200 Demo Way", "Draft", default));
        Assert.Equal(audit, await store.GetAuditAsync(48024));
        await Assert.ThrowsAsync<CaseToolException>(() =>
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
        return new(factory.Api.Services.GetRequiredService<ReturnStore>(),
            new HttpContextAccessor { HttpContext = context },
            factory.Api.Services.GetRequiredService<IAuthorizationService>(), NullLogger<CaseTools>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Separate_agent_calls_application_apis_and_only_commits_an_approved_draft(bool approved)
    {
        using var factory = new DemoFactory();
        await using var app = await CreateTransportAppAsync(factory, model: new ApplicationToolsModel());
        using var http = app.GetTestClient();
        var connection = new AgentConnection(new TestClientFactory(http),
            factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>(),
            app.Configuration, app.Services.GetRequiredService<ILogger<AgentConnection>>());
        using var chat = connection.Create(48024, CreatePrincipal(app));
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        factory.Server.Dispose();
        var inventory = await store.GetInventoryAsync();
        var state = new CaseWorkspaceState { OrderId = 48024 };
        var threadId = Guid.NewGuid().ToString("N");
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput
            {
                ThreadId = threadId,
                State = JsonSerializer.SerializeToElement(state, JsonSerializerOptions.Web)
            }
        };
        List<ChatMessage> messages = [new(ChatRole.User, "Investigate")];
        var evidence = await ReadTurnAsync().ToChatResponseAsync();
        var payload = JsonSerializer.Deserialize<JsonElement>(Assert.IsType<string>(Assert.Single(evidence.Messages
            .SelectMany(m => m.Contents).OfType<FunctionResultContent>()).Result));
        var block = new CaseContextBlock { Payload = payload };
        Assert.Equal(48024, block.CaseInfo!.Order.OrderId);
        Assert.Equal(12, block.CaseInfo.Inventory.Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);
        messages.AddRange(evidence.Messages);
        messages.Add(new(ChatRole.User, "Prepare"));
        var prepared = await ReadTurnAsync().ToChatResponseAsync();
        messages.AddRange(prepared.Messages);
        Assert.Equal("Draft", state.Status);
        Assert.Equal(ApplicationToolsModel.Address, state.DeliveryAddress);
        Assert.Equal("Reply awaiting approval.", state.CustomerReply);
        Assert.NotNull(await store.GetDraftAsync(state.DraftId!.Value));
        Assert.Equal(inventory, await store.GetInventoryAsync());

        using var web = factory.Api.Server.CreateClient();
        web.BaseAddress = new("https://localhost");
        web.DefaultRequestHeaders.Add("Cookie", http.DefaultRequestHeaders.GetValues("Cookie"));
        using var mismatch = await web.PostAsJsonAsync("/api/agent-tools/returns/48024/confirm",
            new ConfirmResolutionRequest
            {
                DraftId = state.DraftId.Value,
                ProductCode = state.ProductCode,
                DeliveryAddress = "A different address"
            });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal(inventory, await store.GetInventoryAsync());

        messages.Add(new(ChatRole.User, "Confirm"));
        var pending = await ReadTurnAsync().ToChatResponseAsync();
        var approval = Assert.Single(pending.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal(inventory, await store.GetInventoryAsync());
        messages.AddRange(pending.Messages);
        messages.Add(new(ChatRole.User, [approval.CreateResponse(approved)]));
        await ReadTurnAsync().ToChatResponseAsync();
        Assert.Equal(approved ? "Resolved" : "Draft", state.Status);
        Assert.Equal(approved ? "Resolved" : "Open", (await store.GetCaseAsync(48024))!.Status);
        Assert.Equal(approved ? 11 : 12,
            (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);
        Assert.Equal(approved ? 1 : 0,
            (await store.GetAuditAsync(48024)).Count(a => a.Action.Contains("one unit allocated")));
        Assert.Equal("Taylor Brooks", (await store.GetAuditAsync(48024)).First().Actor);

        async IAsyncEnumerable<ChatResponseUpdate> ReadTurnAsync()
        {
            await foreach (var update in chat.GetStreamingResponseAsync(messages, options))
            {
                if (update.RawRepresentation is StateSnapshotEvent snapshot)
                {
                    state = Assert.IsType<CaseWorkspaceState>(
                        snapshot.Snapshot.Deserialize<CaseWorkspaceState>(JsonSerializerOptions.Web));
                }
                yield return update;
            }
        }
    }

    [Fact]
    public async Task Separate_agent_revalidates_the_application_session_before_approval_continuation()
    {
        using var factory = new DemoFactory();
        using var anonymous = factory.Browser();
        using var identity = await anonymous.GetAsync("/api/agent-tools/identity");
        Assert.Equal(HttpStatusCode.Unauthorized, identity.StatusCode);
        var model = new ApplicationToolsModel();
        await using var app = await CreateTransportAppAsync(factory, model: model);
        using var http = app.GetTestClient();
        var connection = new AgentConnection(new TestClientFactory(http),
            factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>(),
            app.Configuration, NullLogger<AgentConnection>.Instance);
        using var chat = connection.Create(48024, CreatePrincipal(app));
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new RunAgentInput { ThreadId = Guid.NewGuid().ToString("N") }
        };
        List<ChatMessage> messages = [new(ChatRole.User, "Confirm")];
        var pending = await chat.GetResponseAsync(messages, options);
        var approval = Assert.Single(pending.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        messages.AddRange(pending.Messages);
        messages.Add(new(ChatRole.User, [approval.CreateResponse(true)]));
        var calls = model.Calls;
        using var web = factory.Server.CreateClient();
        web.BaseAddress = new("https://localhost");
        web.DefaultRequestHeaders.Add("Cookie", http.DefaultRequestHeaders.GetValues("Cookie"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/demo/sign-out")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["confirmation"] = "YES" })
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var signedOut = await web.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => chat.GetResponseAsync(messages, options));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(calls, model.Calls);
        Assert.Equal("Open", (await factory.Api.Services.GetRequiredService<ReturnStore>().GetCaseAsync(48024))!.Status);
    }

    private static ClaimsPrincipal CreatePrincipal(WebApplication app)
    {
        var identity = app.Services.GetRequiredService<AgentIdentityResponse>();
        return new(new ClaimsIdentity(
            [new(ClaimTypes.Name, "Untrusted caller"), new(DemoAuthentication.SessionClaim, identity.SessionId)], "Test"));
    }

    [Fact]
    public async Task Blazor_workspace_uses_the_current_authentication_state_for_application_api_calls()
    {
        using var factory = new DemoFactory();
        using var browser = factory.Browser();
        await DemoFactory.SignInAsync(browser);
        var identity = Assert.IsType<AgentIdentityResponse>(
            await browser.GetFromJsonAsync<AgentIdentityResponse>("/api/agent-tools/identity"));
        var authentication = new WorkspaceAuthentication();
        using var workspace = new WorkspaceService(
            factory.Services.GetRequiredService<CustomerServiceClient>(), authentication);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new(DemoAuthentication.SessionClaim, identity.SessionId)], "Test"));
        authentication.SetUser(user);
        Assert.Equal(48024, (await workspace.GetCaseAsync(48024))!.OrderId);
        authentication.SetUser(new());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => workspace.GetCaseAsync(48024));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        authentication.SetUser(user);
        Assert.Equal(12, (await workspace.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);
    }

    private sealed class WorkspaceAuthentication : AuthenticationStateProvider
    {
        private AuthenticationState state = new(new());
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(state);
        public void SetUser(ClaimsPrincipal user)
        {
            state = new(user);
            NotifyAuthenticationStateChanged(Task.FromResult(state));
        }
    }

    private static async Task<WebApplication> CreateTransportAppAsync(
        DemoFactory factory, AIAgent? agent = null, IChatClient? model = null)
    {
        using var browser = factory.Browser();
        await DemoFactory.SignInAsync(browser);
        var identity = Assert.IsType<AgentIdentityResponse>(
            await browser.GetFromJsonAsync<AgentIdentityResponse>("/api/agent-tools/identity"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["CustomerService:ApiUrl"] = "https://localhost";
        builder.Services.AddSingleton(identity);
        builder.Services.AddAgentService(builder.Configuration);
        builder.Services.AddHttpClient("customerservice")
            .ConfigurePrimaryHttpMessageHandler(() => factory.Api.Server.CreateHandler());
        if (model is not null)
        {
            builder.Services.AddSingleton(model);
        }
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (agent is not null)
        {
            var wrappedAgent = new CaseStateAgent(agent);
            Assert.Equal("CustomerService", wrappedAgent.Name);
            app.MapAGUIServer("/api/agents/returns/{orderId:int}", wrappedAgent)
                .RequireAuthorization("CustomerService");
        }
        else
        {
            app.MapCustomerServiceAgent();
        }
        await app.StartAsync();
        return app;
    }

    private sealed class ApplicationToolsModel : IChatClient
    {
        public const string Address = "200 Demo Way, Seattle, WA 98101";
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Calls++;
            var history = messages.ToList();
            var prompt = history.Last(m => m.Role == ChatRole.User && m.Text is "Investigate" or "Prepare" or "Confirm").Text;
            var (name, id) = prompt switch
            {
                "Investigate" => ("get_case_context", "evidence-1"),
                "Prepare" => ("prepare_resolution", "prepare-1"),
                "Confirm" => ("confirm_resolution", "confirm-1"),
                _ => throw new InvalidOperationException("Unexpected test prompt.")
            };
            if (history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any(result => result.CallId == id))
            {
                yield return new(ChatRole.Assistant, "Operation completed.");
                yield break;
            }
            Dictionary<string, object?> arguments = [];
            if (name != "get_case_context")
            {
                arguments["productCode"] = "DOCK-PRO-PLUS";
                arguments["deliveryAddress"] = Address;
                if (name == "prepare_resolution")
                {
                    arguments["customerReply"] = "Reply awaiting approval.";
                }
                else
                {
                    var draft = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                        .LastOrDefault(result => result.CallId == "prepare-1");
                    arguments["draftId"] = draft is null ? Guid.NewGuid()
                        : JsonSerializer.Deserialize<JsonElement>(
                            Assert.IsType<string>(draft.Result)).GetProperty("draftId").GetGuid();
                }
            }
            yield return new()
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent(id, name, arguments)]
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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

    private sealed class ReplyProposalModel : IChatClient
    {
        public const string ProposedReply = "Hi Taylor! We have a compatible replacement for your workshop, awaiting approval.";
        public string LastWorkspace { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var history = messages.ToList();
            if (history.LastOrDefault(m => m.Text?.Contains("Workspace:\n") == true) is { } workspace)
            {
                LastWorkspace = workspace.Text!;
            }
            var checking = history.Last(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text)).Text == "Check current reply";
            var reviewed = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Any(result => result.CallId == "reply-1");
            yield return checking || reviewed
                ? new(ChatRole.Assistant, "Reply noted. Nothing sent or allocated.")
                : new()
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("reply-1", "propose_customer_reply",
                        new Dictionary<string, object?> { ["customerReply"] = ProposedReply })]
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
