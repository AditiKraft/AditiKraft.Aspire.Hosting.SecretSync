using AditiKraft.Aspire.Hosting.SecretSync;
using Aspire.Hosting.Dotnet;
using Microsoft.Extensions.Configuration;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);
string apiProjectPath = Path.GetFullPath(
    "../../sample/AditiKraft.Aspire.Hosting.SecretSync.ApiService/AditiKraft.Aspire.Hosting.SecretSync.ApiService.csproj",
    builder.AppHostDirectory);
string webProjectPath = Path.GetFullPath(
    "../../sample/AditiKraft.Aspire.Hosting.SecretSync.Web/AditiKraft.Aspire.Hosting.SecretSync.Web.csproj",
    builder.AppHostDirectory);
IConfigurationSection secretSync = builder.Configuration.GetSection("SecretSync");

if (secretSync.GetValue("Enabled", false))
{
    await builder.AddSecretSyncAsync(secretSync, options =>
    {
        options.MapAppHostSecrets("apphost");
        options.MapProjectUserSecrets("api", apiProjectPath);
        options.MapProjectUserSecrets("web", webProjectPath);
    });
}

IResourceBuilder<DotnetProjectResource> apiService = builder.AddDotnetProject("apiservice", apiProjectPath)
    .WithHttpHealthCheck("/health");

IResourceBuilder<DotnetProjectResource> web = builder.AddDotnetProject("webfrontend", webProjectPath)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
