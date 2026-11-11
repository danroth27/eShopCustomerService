using Microsoft.AspNetCore.Components.AI;
using System.Text.Json;

using eShopCustomerService.Contracts;

namespace eShopCustomerService.Web.Agentic;

[ToolBlock("get_case_context")]
public partial class CaseContextBlock : FunctionInvocationContentBlock
{
    [ToolResult]
    public JsonElement? Payload { get; set; }

    public CaseContext? CaseInfo => Payload?.Deserialize<CaseContext>(JsonSerializerOptions.Web);
}
