using System.Runtime.CompilerServices;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;

namespace eShopCustomerService.Web.Agentic;

public sealed class MarkdownChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var textByMessage = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (update.Role == ChatRole.Assistant && update.MessageId is { Length: > 0 } id
                && update.Contents.OfType<TextContent>().Any())
            {
                if (!textByMessage.TryGetValue(id, out var text))
                {
                    text = new();
                    textByMessage.Add(id, text);
                }
                foreach (var chunk in update.Contents.OfType<TextContent>())
                {
                    text.Append(chunk.Text);
                }
                var markdown = text.ToString();
                // Preserve TextContent for the remote transcript; rich nodes only control rendering.
                update.Contents.Insert(0, new RichTextContent(markdown, Parse(markdown)));
            }
            yield return update;
        }
    }

    public static IReadOnlyList<RichTextNode> Parse(string markdown) =>
        Markdown.Parse(markdown).Select(block => Map(block, markdown)).ToList();

    private static RichTextNode Map(MarkdownObject source, string markdown)
    {
        RichTextNode node = source switch
        {
            ParagraphBlock => new ParagraphNode(),
            HeadingBlock heading => new HeadingNode(heading.Level),
            ListBlock list => new ListNode(list.IsOrdered,
                list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : null),
            ListItemBlock => new ListItemNode(),
            LiteralInline literal => new TextNode(literal.Content.ToString()),
            EmphasisInline { DelimiterCount: 2 } => new StrongNode(),
            EmphasisInline => new EmphasisNode(),
            CodeInline code => new InlineCodeNode(code.Content),
            LineBreakInline => new LineBreakNode(),
            CodeBlock code => new CodeBlockNode(code.Lines.ToString(),
                code is FencedCodeBlock fenced ? fenced.Info : null),
            _ => new TextNode(markdown.Substring(source.Span.Start, source.Span.Length))
        };
        if (node is TextNode)
        {
            return node;
        }
        if (source is ContainerBlock blocks)
        {
            foreach (var child in blocks)
            {
                node.AddChild(Map(child, markdown));
            }
        }
        else if (source is LeafBlock { Inline: { } inline })
        {
            foreach (var child in inline)
            {
                node.AddChild(Map(child, markdown));
            }
        }
        else if (source is EmphasisInline emphasis)
        {
            foreach (var child in emphasis)
            {
                node.AddChild(Map(child, markdown));
            }
        }
        return node;
    }
}
