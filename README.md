# AditiKraft.Aspire.Hosting.SecretSync

Dev-time encrypted secret synchronization for .NET Aspire AppHost.

SecretSync hydrates AppHost and mapped project user-secrets during Aspire startup, then pushes local edits back to Cloudflare R2 on shutdown. It is designed for teams that want `dotnet user-secrets sync` behavior without a separate CLI.

## Features

- Aspire AppHost extension API: `builder.AddSecretSync(...)`
- AppHost secrets loaded into `IConfiguration` before resources start
- Project-specific user-secrets hydration through `MapProjectUserSecrets`
- Cloudflare R2 backend with client-side AES-256-GCM encryption
- Argon2id key derivation from a developer-held encryption key
- Provider-independent versioning: stable manifest plus immutable encrypted vault versions
- Pull modes: `Always`, `IfStale`, `Manual`
- Version modes: `Latest`, `Pinned`
- Local baseline hashes stored outside `secrets.json`; no plaintext state file

## Quick Start

Add bootstrap config to the AppHost user-secrets file:

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

Then wire it into AppHost:

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

Optional settings you can enable in that block:

```csharp
options.ConflictMode = SecretSyncConflictMode.PushWins;
options.PullMode = SecretSyncPullMode.IfStale;
options.StaleAfter = TimeSpan.FromMinutes(15);
options.VersionMode = SecretSyncVersionMode.Pinned;
options.PinnedRevision = "202606240945301234-...";
```

In the AppHost user-secrets file, `SecretSync` is control config only. Every other key belongs to the AppHost resource and is synced to R2.

When `SecretSync:ObjectKey` is empty, the default is:

```text
aspire/apphosts/{apphost-user-secrets-id}/latest.json
```

Example AppHost user-secrets:

```json
{
  "SecretSync": { "Enabled": true, "...": "..." },
  "Parameters": { "postgres-password": "dev-password" },
  "Cloudflare": { "Turnstile": { "SecretKey": "turnstile-secret" } }
}
```

## Local Secret Model

`SecretSync` is control config and is never synced as app secret data.

Everything else in the AppHost user-secrets file becomes `resources.apphost`. Everything else in a mapped project's user-secrets file becomes that mapped resource.

## Versioning

`ObjectKey` points to a manifest such as:

```text
aspire/apphosts/{apphost-user-secrets-id}/latest.json
```

Encrypted versions are stored beside it:

```text
aspire/apphosts/{apphost-user-secrets-id}/versions/{revision}.vault.json
```

Use `SecretSyncPullMode.IfStale` to avoid checking R2 on every local run when local state is fresh. Use `SecretSyncVersionMode.Pinned` with `PinnedRevision` to reproduce a specific version in read-only mode.

## Documentation

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full architecture, conflict behavior, devcontainer guidance, migration path, and provider roadmap.
