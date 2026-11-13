using Microsoft.AspNetCore.Components.AI;

namespace eShopCustomerService.Web.Agentic;

[ToolBlock("show_resolution_review")]
public partial class ResolutionReviewBlock : FunctionInvocationContentBlock
{
    [ToolResult]
    public string? Confirmation { get; set; }
}
