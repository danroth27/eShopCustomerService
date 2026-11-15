using System.Text.Json;

namespace eShopCustomerService.Contracts;

public sealed record ReplyReviewDecision(bool Accepted, string Message)
{
    public static ReplyReviewDecision FromResult(object? result) =>
        (result switch
        {
            ReplyReviewDecision decision => decision,
            JsonElement { ValueKind: JsonValueKind.Object } json =>
                json.Deserialize<ReplyReviewDecision>(JsonSerializerOptions.Web),
            JsonElement { ValueKind: JsonValueKind.String } json =>
                JsonSerializer.Deserialize<ReplyReviewDecision>(json.GetString()!, JsonSerializerOptions.Web),
            string json => JsonSerializer.Deserialize<ReplyReviewDecision>(json, JsonSerializerOptions.Web),
            _ => throw new InvalidOperationException("The reply review result is invalid.")
        }) ?? throw new InvalidOperationException("The reply review decision is missing.");
}
