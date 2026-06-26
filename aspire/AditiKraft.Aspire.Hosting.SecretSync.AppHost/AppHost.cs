using AditiKraft.Aspire.Hosting.SecretSync;
using Microsoft.Extensions.Configuration;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);
IConfigurationSection secretSync = builder.Configuration.GetSection("SecretSync");

if (secretSync.GetValue("Enabled", false))
{
    await builder.AddSecretSyncAsync(secretSync, options =>
    {
        options.MapAppHostSecrets("apphost");
        options.MapProjectUserSecrets<Projects.AditiKraft_Aspire_Hosting_SecretSync_ApiService>("api");
        options.MapProjectUserSecrets<Projects.AditiKraft_Aspire_Hosting_SecretSync_Web>("web");
    });
}

IResourceBuilder<ProjectResource> apiService = builder.AddProject<Projects.AditiKraft_Aspire_Hosting_SecretSync_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

IResourceBuilder<ProjectResource> web = builder.AddProject<Projects.AditiKraft_Aspire_Hosting_SecretSync_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
