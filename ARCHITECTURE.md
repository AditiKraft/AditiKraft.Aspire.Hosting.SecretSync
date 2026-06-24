# AditiKraft.Aspire.Hosting.SecretSync Architecture

`AditiKraft.Aspire.Hosting.SecretSync` is an Aspire hosting package that syncs development secrets across machines during AppHost startup and shutdown.

The current v1 model is intentionally simple:

```text
SecretSync section = bootstrap/control config, never synced as secret data
Everything else in AppHost user-secrets = apphost resource secrets
Everything else in a mapped project user-secrets file = that resource's secrets
R2 stores one small plaintext manifest plus immutable encrypted vault versions
```

There is no `global` concept and no `shared` concept in v1. Each resource owns its own secrets. If two projects need the same value, store it in both resources for now. Resource inheritance can be added later if duplication becomes painful.

## Local Shape

### AppHost User-Secrets

The AppHost user-secrets file contains SecretSync bootstrap values plus AppHost-specific secrets outside the `SecretSync` section:

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
  },
  "Parameters": {
    "postgres-password": "dev-password"
  },
  "Cloudflare": {
    "Turnstile": {
      "SecretKey": "turnstile-secret"
    }
  }
}
```

Everything under `SecretSync` is control-plane config and is not copied into the encrypted vault as application secret data.

Everything outside `SecretSync` becomes:

```json
{
  "resources": {
    "apphost": {
      "Parameters": {
        "postgres-password": "dev-password"
      },
      "Cloudflare": {
        "Turnstile": {
          "SecretKey": "turnstile-secret"
        }
      }
    }
  }
}
```

### API Project User-Secrets

API project user-secrets should contain only API-specific values:

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

This becomes `resources.api`.

### Web Project User-Secrets

Web project user-secrets should contain only Web-specific values:

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

This becomes `resources.web`.

## Remote Object Shape

SecretSync uses provider-independent versioning. `SecretSync:ObjectKey` points at a stable manifest object. The manifest contains no secret values; it only points at the current encrypted vault version.

Example manifest object at `aspire/apphosts/{apphost-user-secrets-id}/latest.json`:

```json
{
  "format": "aditikraft.secretsync.manifest.v1",
  "schemaVersion": 1,
  "latestRevision": "202606240945301234-4e2f...",
  "vaultObjectKey": "aspire/apphosts/491f37e6-e082-477b-9df6-480d2023fe72/versions/202606240945301234-4e2f....vault.json",
  "contentHash": "sha256-content-hash",
  "parentRevision": "202606231712009876-a1b2...",
  "createdAt": "2026-06-23T17:12:00.9876+00:00",
  "updatedAt": "2026-06-24T09:45:30.1234+00:00"
}
```

Each vault version is immutable. Version object keys are generated beside the manifest:

```text
aspire/apphosts/{apphost-user-secrets-id}/versions/{revision}.vault.json
```

After decrypting a version object, the logical payload looks like:

```json
{
  "version": 1,
  "resources": {
    "apphost": {
      "Parameters": {
        "postgres-password": "dev-password"
      },
      "Cloudflare": {
        "Turnstile": {
          "SecretKey": "turnstile-secret"
        }
      }
    },
    "api": {
      "Stripe": {
        "ApiKey": "sk_api"
      },
      "Jwt": {
        "SigningKey": "api-jwt-key"
      }
    },
    "web": {
      "Stripe": {
        "ApiKey": "sk_web"
      },
      "Auth": {
        "Google": {
          "ClientSecret": "web-google-secret"
        }
      }
    }
  }
}
```

The JSON above is never written to disk in plaintext by SecretSync. R2 receives an encrypted envelope containing this vault. Only the manifest metadata is plaintext.

## AppHost API

The normal AppHost usage is:

```csharp
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
        options.PullMode = SecretSyncPullMode.Always;
        options.VersionMode = SecretSyncVersionMode.Latest;
        options.WriteToUserSecrets = true;

        options.MapAppHostSecrets("apphost");
        options.MapProjectUserSecrets<Projects.ApiService>("api");
        options.MapProjectUserSecrets<Projects.Web>("web");

        options.R2.Endpoint = builder.Configuration["SecretSync:R2:Endpoint"] ?? "";
        options.R2.AccessKeyId = builder.Configuration["SecretSync:R2:AccessKeyId"] ?? "";
        options.R2.SecretAccessKey = builder.Configuration["SecretSync:R2:SecretAccessKey"] ?? "";
        options.R2.Region = builder.Configuration["SecretSync:R2:Region"] ?? "auto";
    });
}

var api = builder.AddProject<Projects.ApiService>("api");

builder.AddProject<Projects.Web>("web")
    .WithReference(api);

builder.Build().Run();
```

Use `IfStale` when you want fewer remote checks during normal local development:

```csharp
options.PullMode = SecretSyncPullMode.IfStale;
options.StaleAfter = TimeSpan.FromMinutes(15);
```

Use pinned mode when you need to reproduce an exact synced version:

```csharp
options.VersionMode = SecretSyncVersionMode.Pinned;
options.PinnedRevision = "202606240945301234-4e2f...";
```

Pinned mode is read-only. SecretSync will not push while pinned, and it fails if local synced secrets have unbaselined edits.

Mapped .NET projects do not need `.WithSecretSync(...)`. SecretSync writes each project's own user-secrets file before resources start.

`.WithSecretSync(...)` remains useful for containers and non-.NET resources that cannot load .NET user-secrets:

```csharp
builder.AddContainer("toolbox", "example/toolbox")
    .WithSecretSync(secretSync, resourceNames: ["toolbox"], includeResourceMatchingName: false);
```

## Resource Mapping

| Local source | Synced resource |
|---|---|
| AppHost user-secrets outside `SecretSync` | `resources.apphost` |
| API project user-secrets outside `SecretSync` | `resources.api` |
| Web project user-secrets outside `SecretSync` | `resources.web` |

Keys beginning with `SecretSync:` are reserved for SecretSync bootstrap/control data and are ignored when building resource data.

## New Machine Flow

On a new machine, the developer only needs bootstrap config in the AppHost user-secrets file:

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

Then AppHost startup:

1. Resolves the R2 object key. If `ObjectKey` is empty, it derives a stable key from the AppHost `UserSecretsId`.
2. Reads the manifest unless `PullMode` allows a local-only startup.
3. Pulls the encrypted version object only when the local state is missing, stale, or behind the manifest.
4. Decrypts the version locally.
5. Writes `resources.apphost` into the AppHost user-secrets file outside `SecretSync`.
6. Writes `resources.api` into the API project user-secrets file.
7. Writes `resources.web` into the Web project user-secrets file.
8. Injects `resources.apphost` into AppHost `IConfiguration` before resources start.

The derived object key format is:

```text
aspire/apphosts/{apphost-user-secrets-id}/latest.json
```

Set `ObjectKey` explicitly only when multiple AppHosts should intentionally share one manifest and version history.

## Startup And Shutdown

SecretSync is not a file watcher.

```text
AppHost startup  -> pull, decrypt, merge, hydrate user-secrets
AppHost shutdown -> read local changes, merge, encrypt, push
```

Pull modes:

| Mode | Startup behavior | Shutdown behavior |
|---|---|---|
| `Always` | Checks the manifest every run. If the manifest revision matches local state and there are no local edits or missing baseline values, it skips the encrypted vault download and uses local user-secrets. | Pushes a new version when local values differ from the last pulled content hash. |
| `IfStale` | Skips R2 entirely when the last successful check is within `StaleAfter`, no local edits exist, and no baseline values are missing. Otherwise behaves like `Always`. | Same as `Always`. |
| `Manual` | Never calls R2. Loads only local user-secrets into AppHost configuration. | Does not push. |

Version modes:

| Mode | Behavior |
|---|---|
| `Latest` | Uses the manifest's latest revision and can create a new version on shutdown. |
| `Pinned` | Pulls `versions/{PinnedRevision}.vault.json`, does not push, and fails if local synced secrets have unbaselined edits. |

If the remote object does not exist:

| Local state | Behavior |
|---|---|
| Local AppHost/project secrets exist | Initialize R2 with a first immutable version and a latest manifest |
| No local secrets exist | Fail by default |

Shutdown push writes the encrypted version object first with an "if missing" condition, then advances the manifest with an "if match" condition. If another developer advanced the manifest after this AppHost pulled, push fails with a conflict instead of overwriting their version.

## Conflict Behavior

SecretSync keeps local baseline hashes in an internal state file, not in `secrets.json`.

```text
%LOCALAPPDATA%\AditiKraft\Aspire\SecretSync\{apphost-identity}\{object-key-hash}\state.json
```

The state file stores SHA-256 hashes of the last hydrated values by resource and key. It does not store plaintext secrets. This lets SecretSync distinguish "unchanged value that was pulled from R2" from "developer intentionally edited this value locally" while keeping AppHost/API/Web user-secrets files clean.

Scenarios:

| Scenario | Behavior |
|---|---|
| Add new key in API user-secrets | Added to `resources.api` and pushed as a new vault version |
| Change existing API key after pull | Local edit wins if the manifest still points at the revision this AppHost pulled |
| Another machine advanced the manifest | Push fails; pull and merge before creating another version |
| Same key changed on another machine too | Conflict is raised during pull/merge when the remote value and local edit both changed from baseline |
| Add new key outside `SecretSync` in AppHost user-secrets | Added to `resources.apphost` and pushed as a new vault version |
| Add key inside `SecretSync` | Treated as control config, not synced as secret data |
| Delete a key locally | Delete semantics are not finalized; remote values may be restored |

Example safe edit:

```text
baseline from R2: resources.api.Stripe.ApiKey = sk_old
local edit:       resources.api.Stripe.ApiKey = sk_new
remote still:     resources.api.Stripe.ApiKey = sk_old
result:           sk_new is accepted and pushed
```

Example conflict:

```text
baseline from R2: resources.api.Stripe.ApiKey = sk_old
local edit:       resources.api.Stripe.ApiKey = sk_local_new
remote now:       resources.api.Stripe.ApiKey = sk_remote_new
result:           conflict
```

Version history remains in R2 because previous vault objects are immutable. Rolling back means setting `VersionMode = SecretSyncVersionMode.Pinned` with the revision you want to inspect, then deciding whether to promote that content later through a normal latest-mode pull/push.

## Security Model

R2 never receives plaintext secret values.

Encryption:

```text
AES-256-GCM
Argon2id key derivation
Random salt per envelope
Random nonce per encryption
One encrypted JSON payload per AppHost vault
Plaintext manifest contains only revision/object metadata, never secret values
```

The encryption key must come from a password manager, environment variable, devcontainer secret, or local user-secrets bootstrap config. Do not commit it.

Best practices:

1. Keep `SecretSync:EncryptionKey` out of git.
2. Use a long random passphrase.
3. Use least-privilege R2 credentials for the bucket/object prefix.
4. Rotate R2 access keys if a machine is lost.
5. Do not log secret values or decrypted vault contents.
6. Do not edit the R2 object manually.

## Devcontainers And Remote Hosts

For devcontainers, pass only bootstrap config:

```json
{
  "containerEnv": {
    "SecretSync__Enabled": "true",
    "SecretSync__BucketName": "${localEnv:SECRET_SYNC_BUCKET}",
    "SecretSync__EncryptionKey": "${localEnv:SECRET_SYNC_ENCRYPTION_KEY}",
    "SecretSync__R2__Endpoint": "${localEnv:SECRET_SYNC_R2_ENDPOINT}",
    "SecretSync__R2__AccessKeyId": "${localEnv:SECRET_SYNC_R2_ACCESS_KEY_ID}",
    "SecretSync__R2__SecretAccessKey": "${localEnv:SECRET_SYNC_R2_SECRET_ACCESS_KEY}"
  }
}
```

If the devcontainer has a different filesystem, SecretSync still works because it resolves `.NET user-secrets` paths inside that environment and hydrates them during AppHost startup.

## Migration From Existing User-Secrets

1. Add SecretSync bootstrap config to AppHost user-secrets.
2. Keep AppHost-specific values outside the `SecretSync` section.
3. Keep each project-specific value in that project's own user-secrets file.
4. Map each project in AppHost with `MapProjectUserSecrets`.
5. Run AppHost once. If R2 is empty, it initializes the encrypted vault from local files.
6. On other machines, add only bootstrap config and run AppHost to hydrate secrets.

## Folder Structure

```text
src/
  Abstractions/
    ISecretSyncProvider.cs
  Configuration/
    SecretSnapshotBuilder.cs
    SecretSyncConfigurationInjector.cs
    SecretSyncLocalSnapshot.cs
    SecretSyncVault.cs
    SecretSyncVaultMerger.cs
    SecretValueHasher.cs
    VaultFlattener.cs
  Cryptography/
    AesGcmSecretEncryptor.cs
    Argon2idKeyDeriver.cs
    SecretPayload.cs
    SecretPayloadSerializer.cs
  Lifecycle/
    SecretSyncCoordinator.cs
    SecretSyncShutdownHostedService.cs
  Providers/
    R2SecretSyncProvider.cs
  Remote/
    SecretSyncManifest.cs
    SecretSyncManifestSerializer.cs
  State/
    SecretSyncState.cs
    SecretSyncStateStore.cs
  UserSecrets/
    ProjectUserSecretsResolver.cs
    ProjectUserSecretsStore.cs
    UserSecretsMaterializer.cs
    UserSecretsStore.cs
  SecretSyncExtensions.cs
  SecretSyncOptions.cs
tests/
  AditiKraft.Aspire.Hosting.SecretSync.Tests/
```

## Future Providers

1. GitHub Gist.
2. Amazon S3.
3. Azure Blob Storage.
4. Supabase Storage.
5. Local file provider for tests and demos.

Provider implementations should only store encrypted bytes and metadata. Encryption remains client-side and provider-independent.
