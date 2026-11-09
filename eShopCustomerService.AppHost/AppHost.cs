var builder = DistributedApplication.CreateBuilder(args);

var customerService = builder.AddProject<Projects.eShopCustomerService_Web>("customerservice")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

var crossOrigin = builder.AddProject<Projects.eShopCustomerService_CrossOrigin>("crossorigin")
    .WithExternalHttpEndpoints()
    .WithEnvironment("CustomerServiceUrl", customerService.GetEndpoint("https"))
    .WaitFor(customerService);

customerService.WithEnvironment("CrossOriginUrl", crossOrigin.GetEndpoint("https"));

if (builder.Configuration["Parameters:agentic-foundry-endpoint"] is { Length: > 0 } endpoint)
{
    var foundry = builder.AddExternalService("foundry", new Uri(endpoint));
    customerService
        .WithEnvironment("Agentic:FoundryEndpoint", foundry)
        .WithEnvironment("Agentic:Model",
            builder.Configuration["Parameters:agentic-foundry-model"] ?? "gpt-5-mini")
        .WithEnvironment("Demo:CookieName", "eShop.CustomerService.Agentic")
        .WithEnvironment("Agentic:ServiceUrl", customerService.GetEndpoint("https"));
}

builder.Build().Run();
