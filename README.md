# AditiKraft.Aspire.Hosting.SecretSync

Encrypted secret sync for .NET Aspire AppHost and mapped project user-secrets using S3-compatible storage.

[![NuGet](https://img.shields.io/nuget/v/AditiKraft.Aspire.Hosting.SecretSync.svg)](https://www.nuget.org/packages/AditiKraft.Aspire.Hosting.SecretSync)

NuGet: https://www.nuget.org/packages/AditiKraft.Aspire.Hosting.SecretSync

## Install

```bash
dotnet add package AditiKraft.Aspire.Hosting.SecretSync
```

## Minimal setup

1. Add bootstrap config to AppHost user-secrets:

```json
{
  "SecretSync": {
    "Enabled": true,
    "BucketName": "dev-secrets",
    "ObjectKey": "",
    "EncryptionKey": "from-password-manager",
    "S3": {
      "Endpoint": "https://s3.us-east-1.amazonaws.com",
      "AccessKeyId": "s3-access-key",
      "SecretAccessKey": "s3-secret-key",
      "Region": "us-east-1"
    }
  }
}
```

2. Wire it in AppHost:

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
        options.PullMode = SecretSyncPullMode.Always;
        options.VersionMode = SecretSyncVersionMode.Latest;
        options.WriteToUserSecrets = true;

        options.MapAppHostSecrets("apphost");
        options.MapProjectUserSecrets<Projects.ApiService>("api");
        options.MapProjectUserSecrets<Projects.Web>("web");

        options.S3.Endpoint = s3["Endpoint"] ?? "";
        options.S3.AccessKeyId = s3["AccessKeyId"] ?? "";
        options.S3.SecretAccessKey = s3["SecretAccessKey"] ?? "";
        options.S3.Region = s3["Region"] ?? "us-east-1";
    });
}
```

## Modes

`PullMode`:

- `Always`: checks remote manifest and pulls latest data on startup.
- `IfStale`: skips remote check when local baseline is still fresh (`StaleAfter` window).
- `Manual`: does not pull or push remote data automatically.

`VersionMode`:

- `Latest`: tracks the latest manifest revision.
- `Pinned`: loads only `PinnedRevision` (read-only mode for synced values).

`ConflictMode`:

- `Fail`
- `PullWins`
- `PushWins`
- `MergeNonOverlapping`

## API

- `AddSecretSync(...)` / `AddSecretSyncAsync(...)`
- `MapAppHostSecrets(resourceName)`
- `MapProjectUserSecrets<TProject>(resourceName)`
- `MapProjectUserSecrets(resourceName, projectPath)`

## Notes

- `SecretSync` is control config only and is not synced as app secret data.
- The `S3` section works with AWS S3 and S3-compatible providers such as Cloudflare R2, MinIO, Wasabi, Backblaze B2, and DigitalOcean Spaces when the provider supports conditional object writes.
- If `ObjectKey` is empty, default is `aspire/apphosts/{user-secrets-id}/latest.json`; when `UserSecretsId` is unavailable it falls back to `aspire/apphosts/{project-id}/latest.json`.
- For Cloudflare R2, set `Endpoint` to your account R2 endpoint and `Region` to `auto`.
- Do not commit encryption keys or S3-compatible storage credentials.
