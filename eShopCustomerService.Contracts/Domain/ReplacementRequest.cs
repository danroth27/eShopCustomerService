using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace eShopCustomerService.Contracts.Domain;

[ValidatableType]
public sealed class ReplacementRequest
{
    [Range(1, int.MaxValue)]
    public int OrderId { get; set; }

    [Required, InStock]
    public string ProductCode { get; set; } = "";

    [Required, StringLength(240, MinimumLength = 5)]
    public string DeliveryAddress { get; set; } = "";
}

public sealed class InStockAttribute : AsyncValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        throw new InvalidOperationException("Inventory validation requires the asynchronous validation APIs.");

    protected override async Task<ValidationResult?> IsValidAsync(
        object? value, ValidationContext context, CancellationToken cancellationToken)
    {
        var inventory = context.GetRequiredService<IInventoryService>();
        return value is string code && await inventory.IsAvailableAsync(code, cancellationToken)
            ? ValidationResult.Success
            : new ValidationResult("This model is currently unavailable. Choose an available alternative.");
    }
}

public interface IInventoryService
{
    Task<bool> IsAvailableAsync(string productCode, CancellationToken cancellationToken);
}

public static class CustomerServiceValidation
{
    public static IServiceCollection AddCustomerServiceValidation(this IServiceCollection services) =>
        services.AddValidation();
}
