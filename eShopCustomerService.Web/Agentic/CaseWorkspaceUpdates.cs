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
                CustomerReply = updated.Status == "Resolved" ? state.Value.CustomerReply : updated.CustomerReply
            });
        }

        foreach (var call in context.Update.Contents.OfType<FunctionCallContent>()
            .Where(call => call.Name == "propose_customer_reply"))
        {
            if (state.Value is not { Status: "Draft", DraftId: not null })
            {
                throw new InvalidOperationException("Prepare a replacement draft before proposing reply edits.");
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

    public static string ReviewReply(AgentState<CaseWorkspaceState> state, string customerReply, bool? accepted)
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
        return accepted.Value
            ? "Reply edits accepted in the workspace. Nothing sent or allocated."
            : "Reply edits discarded; the previous reply is restored. Nothing sent or allocated.";
    }
}
