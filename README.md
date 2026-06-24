# AditiKraft.Aspire.Hosting.SecretSync

Encrypted secret sync for .NET Aspire AppHost and mapped project user-secrets.

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
    "R2": {
      "Endpoint": "https://account-id.r2.cloudflarestorage.com",
      "AccessKeyId": "r2-access-key",
      "SecretAccessKey": "r2-secret-key",
      "Region": "auto"
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
    var r2 = secretSync.GetSection("R2");

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

        options.R2.Endpoint = r2["Endpoint"] ?? "";
        options.R2.AccessKeyId = r2["AccessKeyId"] ?? "";
        options.R2.SecretAccessKey = r2["SecretAccessKey"] ?? "";
        options.R2.Region = r2["Region"] ?? "auto";
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
- If `ObjectKey` is empty, default is `aspire/apphosts/{user-secrets-id}/latest.json`; when `UserSecretsId` is unavailable it falls back to `aspire/apphosts/{project-id}/latest.json`.
- Do not commit encryption keys or R2 credentials.
