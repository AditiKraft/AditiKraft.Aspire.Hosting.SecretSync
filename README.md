# AditiKraft.Aspire.Hosting.SecretSync

Encrypted development secret sync for .NET Aspire AppHost and mapped project user-secrets using S3-compatible storage.

[![NuGet](https://img.shields.io/nuget/v/AditiKraft.Aspire.Hosting.SecretSync.svg)](https://www.nuget.org/packages/AditiKraft.Aspire.Hosting.SecretSync)

SecretSync is for teams who want shared development secrets without a separate CLI. Run the Aspire AppHost and it will pull secrets before resources start. Stop the AppHost and it will push local changes back as a new encrypted version.

## Quick Start

Install the package in your AppHost project:

```bash
dotnet add package AditiKraft.Aspire.Hosting.SecretSync
```

Add bootstrap config to the AppHost user-secrets file:

```json
{
  "SecretSync": {
    "Enabled": true,
    "BucketName": "dev-secrets",
    "ObjectKey": "",
    "EncryptionKey": "use-a-password-manager-value",
    "S3": {
      "Endpoint": "https://s3.us-east-1.amazonaws.com",
      "AccessKeyId": "s3-access-key",
      "SecretAccessKey": "s3-secret-key",
      "Region": "us-east-1"
    }
  }
}
```

Wire SecretSync into `AppHost.cs` or `Program.cs`:

```csharp
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

        options.S3.Endpoint = s3["Endpoint"] ?? "";
        options.S3.AccessKeyId = s3["AccessKeyId"] ?? "";
        options.S3.SecretAccessKey = s3["SecretAccessKey"] ?? "";
        options.S3.Region = s3["Region"] ?? "us-east-1";

        options.MapAppHostSecrets("apphost");
        options.MapProjectUserSecrets<Projects.ApiService>("api");
        options.MapProjectUserSecrets<Projects.Web>("web");

        // Optional: reduce S3 checks during repeated local runs.
        // options.PullMode = SecretSyncPullMode.IfStale;
        // options.StaleAfter = TimeSpan.FromMinutes(15);

        // Optional: reproduce one exact synced version in read-only mode.
        // Pinned mode does not push.
        // options.VersionMode = SecretSyncVersionMode.Pinned;
        // options.PinnedRevision = "202606240945301234-abcdef123456";
    });
}

var api = builder.AddProject<Projects.ApiService>("api");

builder.AddProject<Projects.Web>("web")
    .WithReference(api);

builder.Build().Run();
```

That is enough for the normal case. By default SecretSync pulls on startup, writes local user-secrets, and pushes local changes on shutdown.

## Where Secrets Go

The `SecretSync` section is control config only. It is not synced as app secret data.

Everything outside `SecretSync` in the AppHost user-secrets file becomes the AppHost resource:

```json
{
  "SecretSync": {
    "Enabled": true
  },
  "Parameters": {
    "postgres-password": "dev-password"
  }
}
```

API project user-secrets stay in the API project:

```json
{
  "Stripe": {
    "ApiKey": "sk_api"
  },
  "Jwt": {
    "SigningKey": "api-jwt-key"
  }
}
```

Web project user-secrets stay in the Web project:

```json
{
  "Stripe": {
    "ApiKey": "sk_web"
  },
  "Auth": {
    "Google": {
      "ClientSecret": "web-google-secret"
    }
  }
}
```

The same key can have different values in different projects. For example, `Stripe:ApiKey` can be `sk_api` for `api` and `sk_web` for `web`.

Mapped .NET projects do not need any extra `.With...` call. SecretSync writes each mapped project's local `secrets.json` before Aspire starts resources.

## What Happens

First machine:

1. Add SecretSync bootstrap config.
2. Keep existing AppHost/API/Web development secrets in normal user-secrets files.
3. Run the AppHost.
4. If remote storage is empty, SecretSync creates the first encrypted version.

New machine:

1. Add only SecretSync bootstrap config.
2. Run the AppHost.
3. SecretSync pulls, decrypts locally, and creates/populates mapped user-secrets files.

Normal development:

1. Startup pulls and hydrates local user-secrets.
2. You edit AppHost or project user-secrets normally.
3. Shutdown pushes a new encrypted version if values changed.

## Remote Storage

If `ObjectKey` is empty, SecretSync derives a manifest key from the AppHost user-secrets id:

```text
aspire/apphosts/{user-secrets-id}/latest.json
```

The manifest points to immutable encrypted vault versions:

```text
aspire/apphosts/{identity}/latest.json
aspire/apphosts/{identity}/versions/{revision}.vault.json
```

The manifest contains metadata only. Secret values live inside encrypted vault versions.

## S3-Compatible Storage

SecretSync uses the AWS S3 SDK with a configurable endpoint.

Common targets:

- AWS S3
- Cloudflare R2
- MinIO
- Wasabi
- Backblaze B2 S3 API
- DigitalOcean Spaces

For Cloudflare R2, use your account R2 endpoint and set `Region` to `auto`.

SecretSync depends on conditional writes (`If-Match` and `If-None-Match`) so one developer does not overwrite another developer's version.

## Common Options

These defaults are already set:

```csharp
options.AutoPull = true;
options.AutoPush = true;
options.WriteToUserSecrets = true;
options.PullMode = SecretSyncPullMode.Always;
options.VersionMode = SecretSyncVersionMode.Latest;
```

You usually do not need to set them in AppHost code.

Use `IfStale` when you want to avoid checking S3 on every AppHost run:

```csharp
options.PullMode = SecretSyncPullMode.IfStale;
options.StaleAfter = TimeSpan.FromMinutes(15);
```

Use `Pinned` when you want to reproduce one exact version:

```csharp
options.VersionMode = SecretSyncVersionMode.Pinned;
options.PinnedRevision = "202606240945301234-abcdef123456";
```

Pinned mode is read-only. SecretSync will not push while pinned.

Use `AutoPush = false` when you want to pull shared secrets but avoid publishing local edits:

```csharp
options.AutoPush = false;
```

## Conflict Modes

```csharp
options.ConflictMode = SecretSyncConflictMode.Fail;
```

Available modes:

- `Fail`: fail when local and remote both changed the same value.
- `PullWins`: prefer remote values.
- `PushWins`: prefer local values.
- `MergeNonOverlapping`: merge only non-conflicting changes.

## Security Notes

- Do not commit `SecretSync:EncryptionKey`.
- Do not commit S3 access keys.
- Use a long random encryption key from a password manager.
- Use least-privilege S3 credentials scoped to the bucket or object prefix.
- The S3-compatible provider receives encrypted vault bytes, not plaintext secret values.
- Local baseline state stores hashes, not plaintext secrets.

## API

- `AddSecretSyncAsync(...)`
- `MapAppHostSecrets(resourceName)`
- `MapProjectUserSecrets<TProject>(resourceName)`
- `MapProjectUserSecrets(resourceName, projectPath)`
