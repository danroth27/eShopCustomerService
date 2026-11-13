using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Microsoft.Data.Sqlite;

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

    [Theory]
    [InlineData("/returns/48024")]
    [InlineData("/agentic/48024")]
    public async Task Configured_resolution_editor_includes_manual_controls_and_the_agent(string route)
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
        using var response = await browser.GetAsync(route);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Save current resolution", html);
        Assert.DoesNotContain("Saved resolution draft", html);
        Assert.Contains("Show review", html);
        Assert.Contains("Delivery address", html);
        Assert.Contains("Replacement model", html);
        Assert.Contains("Refund", html);
        Assert.Contains("customer-reply", html);
        Assert.Contains("Resolve this return", html);
        Assert.Contains("Work the case together", html);
        var configuration = Regex.Match(html, "<!--Blazor-Configuration:([^>]+)-->");
        Assert.True(configuration.Success);
        using var options = JsonDocument.Parse(Convert.FromBase64String(configuration.Groups[1].Value));
        Assert.False(options.RootElement.GetProperty("server").GetProperty("autoPauseEnabled").GetBoolean());
        Assert.Contains("class=\"agent-chat-controls\" disabled", html);
        Assert.DoesNotContain("Replacement plan", html);
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
        if (!approved)
        {
            Assert.Equal(ChatToolMode.None, model.ContinuationToolMode);
        }
        else
        {
            Assert.NotEqual(ChatToolMode.None, model.ContinuationToolMode);
        }
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
            options.ChatOptions = new()
            {
                Tools = [AIFunctionFactory.Create(() =>
                {
                    frontendCalls++;
                    return "Review opened";
                }, "show_resolution_review")],
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
            await context.SendMessageAsync("Show review", timeout.Token);
            Assert.Null(context.Error);
            Assert.Equal(ConversationStatus.Idle, context.Status);
            Assert.DoesNotContain(context.Turns.Last().ResponseBlocks, block => block is UIActionBlock);
            Assert.False(awaitingInput.Task.IsCompleted);
            var review = Assert.Single(context.Turns.Last().ResponseBlocks.OfType<FunctionInvocationContentBlock>());
            Assert.True(review.HasResult);
            Assert.Equal("show_resolution_review", review.Call!.Name);
            Assert.Equal("Review opened", review.Result!.Result?.ToString());
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
    [InlineData("refund", null, null)]
    [InlineData(null, "DOCK-PRO", null)]
    [InlineData(null, null, "A new address")]
    public void Editing_resolution_details_clears_validation_but_preserves_other_editor_values(
        string? kind, string? product, string? address)
    {
        var state = new CaseWorkspaceState
        {
            OrderId = 48024,
            ProductCode = "DOCK-PRO-PLUS",
            DeliveryAddress = "Original address",
            CustomerReply = "My manually drafted response",
            RefundAmount = 149m,
            PaymentReference = "PAY-48024",
            ValidationMessages = ["Previous validation"],
            ValidationPending = true
        };
        var edited = CaseWorkspaceUpdates.Edit(state, kind, product, address);
        Assert.Empty(edited.ValidationMessages);
        Assert.False(edited.ValidationPending);
        Assert.Equal("Editing", edited.Status);
        Assert.Equal(kind ?? state.ResolutionKind, edited.ResolutionKind);
        Assert.Equal(product ?? state.ProductCode, edited.ProductCode);
        Assert.Equal(address ?? state.DeliveryAddress, edited.DeliveryAddress);
        Assert.Equal(state.CustomerReply, edited.CustomerReply);
        Assert.Equal(state.RefundAmount, edited.RefundAmount);
        Assert.Equal(state.PaymentReference, edited.PaymentReference);
    }

    [Fact]
    public void Reply_edits_preserve_the_editor_values_and_confirmed_resolutions_cannot_be_edited()
    {
        var state = new CaseWorkspaceState { CustomerReply = "Old reply" };
        var edited = CaseWorkspaceUpdates.Edit(state, customerReply: "");
        Assert.Equal("", edited.CustomerReply);
        Assert.Equal(state with { CustomerReply = "" }, edited);
        Assert.Throws<ArgumentException>(() => CaseWorkspaceUpdates.Edit(state, resolutionKind: "unknown"));
        Assert.Throws<InvalidOperationException>(() =>
            CaseWorkspaceUpdates.Edit(state with { Status = "Resolved" }, deliveryAddress: "Changed"));
    }

    [Fact]
    public async Task Frontend_editor_tool_and_manual_edits_share_the_complete_state_sent_to_the_remote_agent()
    {
        using var factory = new DemoFactory();
        var model = new EditorModel();
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
        UIAgent<CaseWorkspaceState> ui = null!;
        var threadId = Guid.NewGuid().ToString("N");
        using var agentUi = ui = new UIAgent<CaseWorkspaceState>(chat, options =>
        {
            options.ChatOptions = new()
            {
                Tools = [AIFunctionFactory.Create(() => "Review", "show_resolution_review"),
                AIFunctionFactory.Create((string resolutionKind, string productCode,
                    string deliveryAddress, string customerReply) =>
                {
                    ui.State.Value = CaseWorkspaceUpdates.Edit(ui.State.Value,
                        resolutionKind, productCode, deliveryAddress, customerReply);
                    return ui.State.Value;
                }, "update_resolution_editor")],
                RawRepresentationFactory = _ => new RunAgentInput
                {
                    ThreadId = threadId,
                    State = JsonSerializer.SerializeToElement(ui.State.Value, JsonSerializerOptions.Web)
                }
            };
        }, new()
        {
            OrderId = 48024,
            ResolutionKind = "refund",
            ProductCode = "DOCK-PRO",
            DeliveryAddress = "Manual address",
            CustomerReply = "Manually drafted reply",
            RefundAmount = 149m,
            PaymentReference = "PAY-48024",
            RefundApprovalAllowed = true,
            AvailableProducts = await factory.Api.Services.GetRequiredService<ReturnStore>().GetInventoryAsync(),
            Status = "Editing"
        });
        using var context = new AgentContext(ui);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await context.SendMessageAsync("Edit all", timeout.Token);
        Assert.Null(context.Error);
        Assert.Contains("update_resolution_editor", model.ToolNames);
        Assert.Contains("show_resolution_review", model.ToolNames);
        Assert.Equal("replacement", ui.State.Value.ResolutionKind);
        Assert.Equal("DOCK-PRO-PLUS", ui.State.Value.ProductCode);
        Assert.Equal("Agent updated address", ui.State.Value.DeliveryAddress);
        Assert.Equal("Agent updated reply", ui.State.Value.CustomerReply);
        ui.State.Value = CaseWorkspaceUpdates.Edit(ui.State.Value, customerReply: "My final manual edit");
        await context.SendMessageAsync("Inspect", timeout.Token);
        Assert.Null(context.Error);
        Assert.Contains("\"resolutionKind\":\"refund\"", model.Workspaces[0]);
        Assert.Contains("\"customerReply\":\"Manually drafted reply\"", model.Workspaces[0]);
        Assert.Contains("\"refundAmount\":149", model.Workspaces[0]);
        Assert.Contains("\"paymentReference\":\"PAY-48024\"", model.Workspaces[0]);
        Assert.Contains("\"refundApprovalAllowed\":true", model.Workspaces[0]);
        Assert.Contains("\"availableProducts\":[{", model.Workspaces[0]);
        Assert.Contains("\"resolutionKind\":\"replacement\"", model.Workspaces[^1]);
        Assert.Contains("\"productCode\":\"DOCK-PRO-PLUS\"", model.Workspaces[^1]);
        Assert.Contains("\"deliveryAddress\":\"Agent updated address\"", model.Workspaces[^1]);
        Assert.Contains("\"customerReply\":\"My final manual edit\"", model.Workspaces[^1]);
    }

    [Fact]
    public async Task Refund_agent_tools_use_authoritative_amounts_and_recheck_permission_before_confirmation()
    {
        using var factory = new DemoFactory();
        using var browser = factory.Browser();
        await DemoFactory.SignInAsync(browser);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var inventory = await store.GetInventoryAsync();
        var order = (await store.GetCaseAsync(48024))!;
        var confirmation = new ConfirmResolutionRequest
        {
            ResolutionKind = "refund",
            ProductCode = "",
            DeliveryAddress = "",
            RefundAmount = order.PaidAmount,
            PaymentReference = "PAY-48024"
        };
        using var mismatch = await browser.PostAsJsonAsync("/api/agent-tools/returns/48024/confirm",
            confirmation with { RefundAmount = 1m });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        using var revoke = new HttpRequestMessage(HttpMethod.Post, "/demo/auth-refresh/permission")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["refundApproval"] = "false" })
        };
        revoke.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var revoked = await browser.SendAsync(revoke);
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        using var forbiddenConfirm = await browser.PostAsJsonAsync("/api/agent-tools/returns/48024/confirm", confirmation);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenConfirm.StatusCode);
        await AssertNoSavedDraftsAsync(factory);
        Assert.Equal(inventory, await store.GetInventoryAsync());
        Assert.Equal("Open", (await store.GetCaseAsync(48024))!.Status);
    }

    [Fact]
    public async Task Approved_refund_is_recorded_once_without_allocating_replacement_stock()
    {
        using var factory = new DemoFactory();
        using var browser = factory.Browser();
        await DemoFactory.SignInAsync(browser);
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var inventory = await store.GetInventoryAsync();
        var request = new ConfirmResolutionRequest
        {
            ResolutionKind = "refund",
            ProductCode = "",
            DeliveryAddress = "",
            RefundAmount = 149m,
            PaymentReference = "PAY-48024"
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var confirmed = await browser.PostAsJsonAsync("/api/agent-tools/returns/48024/confirm", request);
            confirmed.EnsureSuccessStatusCode();
            var completed = (await confirmed.Content.ReadFromJsonAsync<CaseWorkspaceState>())!;
            Assert.Equal("Resolved", completed.Status);
            Assert.True(completed.RefundApprovalAllowed);
            Assert.Equal(inventory, completed.AvailableProducts);
        }
        Assert.Equal(inventory, await store.GetInventoryAsync());
        Assert.IsType<RefundResolution>((await store.GetCaseAsync(48024))!.Resolution!.Value.Value);
        Assert.Single(await store.GetAuditAsync(48024), entry => entry.Action.Contains("refund confirmed"));
        await AssertNoSavedDraftsAsync(factory);
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
        var initial = new CaseWorkspaceState
        {
            OrderId = 48024,
            ProductCode = "DOCK-PRO-PLUS",
            DeliveryAddress = "200 Demo Way",
            CustomerReply = "Original reply awaiting approval."
        };
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
        await AssertNoSavedDraftsAsync(factory);
    }

    [Fact]
    public async Task Confirming_current_editor_values_is_atomic_and_idempotent_without_saved_drafts()
    {
        using var factory = new DemoFactory();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var tools = CreateTools(factory);
        var stock = (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available;
        Assert.Equal("Open", (await store.GetCaseAsync(48024))!.Status);
        Assert.Equal(stock, (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);

        var confirmed = await tools.ConfirmResolutionAsync("DOCK-PRO-PLUS", "200 Demo Way", default);
        Assert.Equal("Resolved", confirmed.Status);
        await tools.ConfirmResolutionAsync("DOCK-PRO-PLUS", "200 Demo Way", default);
        Assert.Equal(stock - 1, (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);
        var committed = Assert.IsType<ReplacementResolution>((await store.GetCaseAsync(48024))!.Resolution!.Value.Value);
        Assert.Equal("200 Demo Way", committed.DeliveryAddress);
        Assert.Single(await store.GetAuditAsync(48024), a => a.Action.Contains("one unit allocated"));
        Assert.Equal("Test representative", (await store.GetAuditAsync(48024)).First().Actor);
        await AssertNoSavedDraftsAsync(factory);
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
    public async Task Confirmation_is_case_bound_and_cannot_replace_a_previously_confirmed_resolution()
    {
        using var factory = new DemoFactory();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var tools = CreateTools(factory);
        await tools.ConfirmResolutionAsync("DOCK-PRO-PLUS", "200 Demo Way", default);
        var after = await store.GetInventoryAsync();
        await Assert.ThrowsAsync<CaseToolException>(() =>
            tools.ConfirmResolutionAsync("DOCK-PRO-PLUS", "Changed address", default));
        Assert.Equal(after, await store.GetInventoryAsync());
        Assert.Equal("Open", (await store.GetCaseAsync(48021))!.Status);
        Assert.Equal("200 Demo Way", Assert.IsType<ReplacementResolution>(
            (await store.GetCaseAsync(48024))!.Resolution!.Value.Value).DeliveryAddress);
    }

    [Fact]
    public async Task Unavailable_replacement_and_anonymous_agent_tools_fail_explicitly()
    {
        using var factory = new DemoFactory();
        var store = factory.Api.Services.GetRequiredService<ReturnStore>();
        var audit = await store.GetAuditAsync(48024);
        await Assert.ThrowsAsync<CaseToolException>(() =>
            CreateTools(factory).ConfirmResolutionAsync("DOCK-PRO", "200 Demo Way", default));
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

    private static async Task AssertNoSavedDraftsAsync(DemoFactory factory)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = factory.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Drafts";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Separate_agent_calls_application_apis_and_only_commits_approved_editor_values(bool approved)
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
        var audit = await store.GetAuditAsync(48024);
        var state = new CaseWorkspaceState
        {
            OrderId = 48024,
            DeliveryAddress = ApplicationToolsModel.Address,
            CustomerReply = "Reply awaiting approval."
        };
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
        Assert.Equal(inventory, await store.GetInventoryAsync());

        messages.Add(new(ChatRole.User, "Confirm"));
        var pending = await ReadTurnAsync().ToChatResponseAsync();
        var approval = Assert.Single(pending.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal(inventory, await store.GetInventoryAsync());
        messages.AddRange(pending.Messages);
        messages.Add(new(ChatRole.User, [approval.CreateResponse(approved)]));
        await ReadTurnAsync().ToChatResponseAsync();
        Assert.Equal(approved ? "Resolved" : "Editing", state.Status);
        Assert.Equal(approved ? "Resolved" : "Open", (await store.GetCaseAsync(48024))!.Status);
        Assert.Equal(approved ? 11 : 12,
            (await store.GetInventoryAsync()).Single(i => i.ProductCode == "DOCK-PRO-PLUS").Available);
        Assert.Equal(approved ? 1 : 0,
            (await store.GetAuditAsync(48024)).Count(a => a.Action.Contains("one unit allocated")));
        if (approved)
        {
            Assert.Equal("Taylor Brooks", (await store.GetAuditAsync(48024)).First().Actor);
        }
        else
        {
            Assert.Equal(audit, await store.GetAuditAsync(48024));
        }
        await AssertNoSavedDraftsAsync(factory);

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

    private sealed class EditorModel : IChatClient
    {
        public List<string> Workspaces { get; } = [];
        public HashSet<string> ToolNames { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var history = messages.ToList();
            var workspace = history.LastOrDefault(m => m.Text.StartsWith("The current workspace draft is JSON data"));
            if (workspace is not null)
            {
                Workspaces.Add(workspace.Text);
            }
            foreach (var tool in options?.Tools ?? [])
            {
                ToolNames.Add(tool.Name);
            }
            if (history.Any(m => m.Role == ChatRole.User && m.Text == "Inspect")
                || history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any(r => r.CallId == "editor-1"))
            {
                yield return new(ChatRole.Assistant, "Editor state received.");
            }
            else
            {
                yield return new()
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("editor-1", "update_resolution_editor",
                        new Dictionary<string, object?>
                        {
                            ["resolutionKind"] = "replacement", ["productCode"] = "DOCK-PRO-PLUS",
                            ["deliveryAddress"] = "Agent updated address", ["customerReply"] = "Agent updated reply"
                        })]
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
            var prompt = history.Last(m => m.Role == ChatRole.User && m.Text is "Investigate" or "Confirm").Text;
            var (name, id) = prompt switch
            {
                "Investigate" => ("get_case_context", "evidence-1"),
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
        public ChatToolMode? ContinuationToolMode { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (result is not null)
            {
                ContinuationToolMode = options?.ToolMode;
            }
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
