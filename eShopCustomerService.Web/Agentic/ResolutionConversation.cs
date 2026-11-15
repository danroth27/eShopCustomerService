using System.Text;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;

namespace eShopCustomerService.Web.Agentic;

public sealed class ResolutionConversation : IConversationThread
{
    private readonly List<JsonElement> pending = [];
    private bool abandoned;

    public string ThreadId { get; set; } = Guid.NewGuid().ToString("N");
    public byte[] Transcript { get; set; } = [];
    public bool Interrupted { get; set; }
    [JsonIgnore] public bool Restoring { get; private set; }
    [JsonIgnore] public bool RecoveredInterruption { get; private set; }
    [JsonIgnore] public bool IsStateful => false;
    [JsonIgnore] public string? ConversationId => null;

    public void AppendUserMessage(ChatMessage message) =>
        AppendUpdate(new() { Role = message.Role, Contents = [.. message.Contents] });

    public void AppendUpdate(ChatResponseUpdate update)
    {
        // Rich nodes are reconstructed from Markdown; raw protocol events must not replay state.
        var stored = new ChatResponseUpdate
        {
            Role = update.Role,
            MessageId = update.MessageId,
            Contents = update.Contents.Where(content => content is not RichTextContent).ToList()
        };
        pending.Add(JsonSerializer.SerializeToElement(stored, JsonSerializerOptions.Web));
    }

    public void CompleteTurn()
    {
        // UIAgent completes intermediate tool/approval rounds too. Commit only at conversation Idle.
    }

    public void CompleteExchange()
    {
        if (abandoned)
        {
            pending.Clear();
            return;
        }
        var updates = ReadUpdates();
        updates.AddRange(pending);
        using var buffer = new MemoryStream();
        using (var compression = new BrotliStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            JsonSerializer.Serialize(compression, updates, JsonSerializerOptions.Web);
        }
        Transcript = buffer.ToArray();
        pending.Clear();
        Interrupted = false;
        RecoveredInterruption = false;
    }

    public void BeginExchange()
    {
        abandoned = false;
        pending.Clear();
        Interrupted = true;
    }

    public void InterruptExchange()
    {
        pending.Clear();
        abandoned = true;
        Interrupted = true;
    }

    public void BeginRecovery()
    {
        if (Interrupted)
        {
            RecoveredInterruption = true;
            ThreadId = Guid.NewGuid().ToString("N");
        }
    }

    public IReadOnlyList<ChatResponseUpdate> GetUpdates()
    {
        var textByMessage = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var restored = new List<ChatResponseUpdate>();
        foreach (var item in ReadUpdates())
        {
            var update = item.Deserialize<ChatResponseUpdate>(JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException("The saved conversation contains an invalid update.");
            foreach (var result in update.Contents.OfType<FunctionResultContent>())
            {
                if (result.Result is JsonElement { ValueKind: JsonValueKind.String } text)
                {
                    result.Result = text.GetString();
                }
            }
            if (update.Role == ChatRole.Assistant && update.MessageId is { Length: > 0 } id)
            {
                foreach (var chunk in update.Contents.OfType<TextContent>())
                {
                    if (!textByMessage.TryGetValue(id, out var text))
                    {
                        text = new();
                        textByMessage.Add(id, text);
                    }
                    text.Append(chunk.Text);
                }
                if (textByMessage.TryGetValue(id, out var markdown))
                {
                    update.Contents.Insert(0, new RichTextContent(markdown.ToString(), MarkdownChatClient.Parse(markdown.ToString())));
                }
            }
            restored.Add(update);
        }
        return restored;
    }

    private List<JsonElement> ReadUpdates()
    {
        if (Transcript.Length == 0)
        {
            return [];
        }
        using var buffer = new MemoryStream(Transcript);
        using var decompression = new BrotliStream(buffer, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<JsonElement>>(decompression, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The saved conversation transcript is invalid.");
    }

    public async Task RestoreAsync(AgentContext context)
    {
        Restoring = true;
        try
        {
            await context.RestoreAsync();
            var decisions = GetUpdates().SelectMany(update => update.Contents)
                .OfType<ToolApprovalResponseContent>().ToDictionary(response => response.RequestId);
            foreach (var block in context.Turns.SelectMany(turn => turn.ResponseBlocks).OfType<FunctionApprovalBlock>())
            {
                if (!decisions.TryGetValue(block.ApprovalRequest.RequestId, out var decision))
                {
                    throw new InvalidOperationException("The saved conversation contains an approval without a completed decision.");
                }
                // Restore display status only; no continuation is sent by AgentContext.RestoreAsync.
                if (decision.Approved)
                {
                    block.Approve();
                }
                else
                {
                    block.Reject(decision.Reason);
                }
            }
        }
        finally
        {
            Restoring = false;
        }
    }
}
