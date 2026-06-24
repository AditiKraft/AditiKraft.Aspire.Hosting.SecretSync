using AditiKraft.Aspire.Hosting.SecretSync;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var secretSync = builder.Configuration.GetSection("SecretSync");

if (secretSync.GetValue("Enabled", false))
{
    var s3 = secretSync.GetSection("S3");

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

        options.S3.Endpoint = s3["Endpoint"] ?? "";
        options.S3.AccessKeyId = s3["AccessKeyId"] ?? "";
        options.S3.SecretAccessKey = s3["SecretAccessKey"] ?? "";
        options.S3.Region = s3["Region"] ?? "us-east-1";

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
