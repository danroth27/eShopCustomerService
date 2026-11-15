using System.Text.Json;
using AGUI.Abstractions;
using eShopCustomerService.Contracts;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;

namespace eShopCustomerService.Web.Agentic;

public static class CaseWorkspaceUpdates
{
    public static void Map(StateMapperContext context, AgentState<CaseWorkspaceState> state)
    {
        if (context.Update.RawRepresentation is StateSnapshotEvent snapshot
            && snapshot.Snapshot.Deserialize<CaseWorkspaceState>(JsonSerializerOptions.Web) is { } updated)
        {
            context.SetState(updated with
            {
                CustomerReply = updated.Status == "Resolved" ? state.Value.CustomerReply : updated.CustomerReply,
                RefundApprovalAllowed = state.Value.RefundApprovalAllowed,
                AvailableProducts = state.Value.AvailableProducts
            });
        }

        foreach (var call in context.Update.Contents.OfType<FunctionCallContent>()
            .Where(call => call.Name == "propose_customer_reply"))
        {
            if (state.Value.Status == "Resolved")
            {
                throw new InvalidOperationException("This resolution is already confirmed.");
            }
            if (call.Arguments?.TryGetValue("customerReply", out var value) != true
                || value is not JsonElement { ValueKind: JsonValueKind.String } element
                || string.IsNullOrWhiteSpace(element.GetString()))
            {
                throw new InvalidOperationException("The reply proposal must contain the complete customer reply.");
            }
            context.SetPredictiveState(state.Value with { CustomerReply = element.GetString()! });
        }
    }

    public static CaseWorkspaceState Edit(
        CaseWorkspaceState state, string? resolutionKind = null, string? productCode = null,
        string? deliveryAddress = null, string? customerReply = null)
    {
        if (state.Status == "Resolved")
        {
            throw new InvalidOperationException("This resolution is already confirmed.");
        }
        if (resolutionKind is not (null or "refund" or "replacement"))
        {
            throw new ArgumentException("Choose refund or replacement.", nameof(resolutionKind));
        }
        var updated = state with
        {
            ResolutionKind = resolutionKind ?? state.ResolutionKind,
            ProductCode = productCode ?? state.ProductCode,
            DeliveryAddress = deliveryAddress ?? state.DeliveryAddress,
            CustomerReply = customerReply ?? state.CustomerReply
        };
        var resolutionChanged = updated.ResolutionKind != state.ResolutionKind
            || updated.ProductCode != state.ProductCode || updated.DeliveryAddress != state.DeliveryAddress;
        return resolutionChanged
            ? updated with { Status = "Editing", ValidationMessages = [], ValidationPending = false }
            : updated;
    }

    public static ReplyReviewDecision ReviewReply(AgentState<CaseWorkspaceState> state, string customerReply, bool? accepted)
    {
        if (accepted is null)
        {
            throw new InvalidOperationException("The reply edits have not been reviewed.");
        }
        if (!state.HasPendingPredictiveState || state.Value.CustomerReply != customerReply)
        {
            throw new InvalidOperationException("These reply edits are no longer pending.");
        }
        if (accepted.Value)
        {
            state.AcceptPredictiveState();
        }
        else
        {
            state.RejectPredictiveState();
        }
        return new(accepted.Value, accepted.Value
            ? "Reply edits accepted in the workspace. Nothing sent or allocated."
            : "Reply edits discarded; the previous reply is restored. Nothing sent or allocated.");
    }
}
