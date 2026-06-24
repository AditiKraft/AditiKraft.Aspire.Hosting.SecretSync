using AditiKraft.Aspire.Hosting.SecretSync;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var secretSync = builder.Configuration.GetSection("SecretSync");

if (secretSync.GetValue("Enabled", false))
{
    var r2 = secretSync.GetSection("R2");

    await builder.AddSecretSyncAsync(options =>
    {
        options.BucketName = secretSync["BucketName"] ?? "";
        options.ObjectKey = secretSync["ObjectKey"] ?? "";
        options.EncryptionKey = secretSync["EncryptionKey"] ?? "";

        options.AutoPull = true;
        options.AutoPush = true;
        options.PullMode = SecretSyncPullMode.Always;
        options.VersionMode = SecretSyncVersionMode.Latest;
        options.WriteToUserSecrets = true;

        options.MapAppHostSecrets("apphost");

        options.R2.Endpoint = r2["Endpoint"] ?? "";
        options.R2.AccessKeyId = r2["AccessKeyId"] ?? "";
        options.R2.SecretAccessKey = r2["SecretAccessKey"] ?? "";
        options.R2.Region = r2["Region"] ?? "auto";

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
