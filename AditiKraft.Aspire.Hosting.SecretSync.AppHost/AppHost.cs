using AditiKraft.Aspire.Hosting.SecretSync;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

if (builder.Configuration.GetValue("SecretSync:Enabled", false))
{
    await builder.AddSecretSyncAsync(options =>
    {
        options.Provider = SecretSyncProvider.CloudflareR2;
        options.BucketName = builder.Configuration["SecretSync:BucketName"] ?? "";
        options.ObjectKey = builder.Configuration["SecretSync:ObjectKey"] ?? "";
        options.EncryptionKey = builder.Configuration["SecretSync:EncryptionKey"] ?? "";
        options.AutoPull = true;
        options.AutoPush = true;
        options.WriteToUserSecrets = true;
        options.MapAppHostSecrets("apphost");
        // options.ConflictMode = SecretSyncConflictMode.PushWins;

        // In the AppHost user-secrets file, SecretSync is control config only.
        // Every other key belongs to the AppHost resource and is synced to R2.
        //
        // Example:
        // {
        //   "SecretSync": { "Enabled": true, "...": "..." },
        //   "Parameters": { "postgres-password": "dev-password" },
        //   "Cloudflare": { "Turnstile": { "SecretKey": "turnstile-secret" } }
        // }

        options.R2.Endpoint = builder.Configuration["SecretSync:R2:Endpoint"] ?? "";
        options.R2.AccessKeyId = builder.Configuration["SecretSync:R2:AccessKeyId"] ?? "";
        options.R2.SecretAccessKey = builder.Configuration["SecretSync:R2:SecretAccessKey"] ?? "";
        options.R2.Region = builder.Configuration["SecretSync:R2:Region"] ?? "auto";

        options.MapProjectUserSecrets<Projects.AditiKraft_Aspire_Hosting_SecretSync_ApiService>("api");
        options.MapProjectUserSecrets<Projects.AditiKraft_Aspire_Hosting_SecretSync_Web>("web");
    });
}

// Example: if you add Parameters:postgres-password outside the SecretSync section,
// builder.AddParameter("postgres-password", secret: true) can read it here.
//
// The mapped .NET projects do not need .WithSecretSync(...). SecretSync writes each
// project's own resource secrets into that project's local secrets.json before startup.

var apiService = builder.AddProject<Projects.AditiKraft_Aspire_Hosting_SecretSync_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

var web = builder.AddProject<Projects.AditiKraft_Aspire_Hosting_SecretSync_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
