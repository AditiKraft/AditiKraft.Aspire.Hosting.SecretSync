using AditiKraft.Aspire.Hosting.SecretSync;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var secretSync = builder.Configuration.GetSection("SecretSync");

if (secretSync.GetValue("Enabled", false))
{
    await builder.AddSecretSyncAsync(secretSync, options =>
    {
        options.MapAppHostSecrets("apphost");
        options.MapProjectUserSecrets<Projects.AditiKraft_Aspire_Hosting_SecretSync_ApiService>("api");
        options.MapProjectUserSecrets<Projects.AditiKraft_Aspire_Hosting_SecretSync_Web>("web");
    });
}

var apiService = builder.AddProject<Projects.AditiKraft_Aspire_Hosting_SecretSync_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

var web = builder.AddProject<Projects.AditiKraft_Aspire_Hosting_SecretSync_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
