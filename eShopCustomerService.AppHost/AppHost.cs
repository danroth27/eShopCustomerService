var builder = DistributedApplication.CreateBuilder(args);

var customerService = builder.AddProject<Projects.eShopCustomerService_Web>("customerservice")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

var crossOrigin = builder.AddProject<Projects.eShopCustomerService_CrossOrigin>("crossorigin")
    .WithExternalHttpEndpoints()
    .WithEnvironment("CustomerServiceUrl", customerService.GetEndpoint("https"))
    .WaitFor(customerService);

customerService.WithEnvironment("CrossOriginUrl", crossOrigin.GetEndpoint("https"));

builder.Build().Run();
