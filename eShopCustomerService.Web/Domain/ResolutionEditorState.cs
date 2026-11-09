namespace eShopCustomerService.Web.Domain;

public sealed class ResolutionEditorState
{
    public string Kind { get; set; } = "replacement";
    public ReplacementRequest Replacement { get; set; } = new();
    public bool Preview { get; set; }
}
