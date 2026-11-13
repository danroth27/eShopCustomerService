using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Validation;

namespace eShopCustomerService.Contracts.Domain;

[ValidatableType]
public sealed class CustomerReturnRequest
{
    [Range(1, int.MaxValue)]
    [Display(Name = "Order")]
    public int OrderId { get; set; } = 48021;

    [Required, StringLength(100)]
    [Display(Name = "Your name")]
    public string Name { get; set; } = "";

    [Required, EmailAddress, StringLength(254)]
    [Display(Name = "Email address")]
    public string Email { get; set; } = "";

    [Required, StringLength(2000, MinimumLength = 10)]
    [Display(Name = "How can we help?")]
    public string Message { get; set; } = "";
}

public sealed record CustomerRequestReceipt(string Reference, int OrderId);
