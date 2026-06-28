using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;
using AditiKraft.Aspire.Hosting.SecretSync.Remote;
using AditiKraft.Aspire.Hosting.SecretSync.State;
using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using Microsoft.Extensions.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.Lifecycle;

internal sealed class SecretSyncCoordinator(
    SecretSyncOptions options,
    SecretSyncHandle handle,
    ISecretSyncProvider provider,
    AesGcmSecretEncryptor encryptor,
    SecretSnapshotBuilder snapshotBuilder,
    UserSecretsStore userSecretsStore,
    ProjectUserSecretsStore projectUserSecretsStore,
    SecretSyncStateStore stateStore)
{
    public async Task PullAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!options.AutoPull)
        {
            return;
        }

        SecretSyncState state = await stateStore.ReadAsync(cancellationToken);
        SecretSyncLocalSnapshot localSnapshot = await snapshotBuilder.BuildLocalSnapshotAsync(cancellationToken);

        if (options.PullMode == SecretSyncPullMode.Manual)
        {
            await LoadLocalOnlyAsync(configuration, state.Remote, cancellationToken);
            return;
        }

        if (options.VersionMode == SecretSyncVersionMode.Pinned)
        {
            await PullPinnedAsync(configuration, state, localSnapshot, cancellationToken);
            return;
        }

        if (ShouldSkipRemoteCheck(state, localSnapshot))
        {
            await LoadLocalOnlyAsync(configuration, state.Remote, cancellationToken);
            return;
        }

        SecretSyncRemoteManifest? remoteManifest = await GetManifestAsync(cancellationToken);
        if (remoteManifest is null)
        {
            await InitializeMissingRemoteAsync(configuration, localSnapshot, cancellationToken);
            return;
        }

        SecretSyncRemoteState remoteState = CreateRemoteState(remoteManifest);
        if (CanUseLocalVaultForManifest(state, remoteManifest, localSnapshot))
        {
            await LoadFullLocalAsync(configuration, remoteState, cancellationToken);
            return;
        }

        SecretSyncRemoteObject remoteVault = await GetRequiredVaultAsync(
            remoteManifest.Manifest.VaultObjectKey,
            cancellationToken);
        SecretPayload payload = encryptor.Decrypt(remoteVault.Body);
        SecretSyncVault mergedVault = SecretSyncVaultMerger.MergeRemoteWithLocal(
            payload.Vault,
            localSnapshot.Vault,
            options.ConflictMode,
            localSnapshot.ProjectEdits);

        handle.Load(
            mergedVault,
            remoteManifest.ETag,
            remoteManifest.Manifest.LatestRevision,
            payload.ContentHash,
            initializedRemote: false);
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration,
            GetAppHostSecrets(),
            options.ConfigurationPrecedence);

        remoteState.ContentHash = payload.ContentHash;
        await PersistUserSecretViewsAsync(mergedVault, remoteState, cancellationToken);
    }

    public async Task PushAsync(CancellationToken cancellationToken)
    {
        if (!options.AutoPush ||
            !handle.Pulled ||
            options.PullMode == SecretSyncPullMode.Manual ||
            options.VersionMode == SecretSyncVersionMode.Pinned)
        {
            return;
        }

        SecretSyncLocalSnapshot localSnapshot = await snapshotBuilder.BuildLocalSnapshotAsync(cancellationToken);
        SecretSyncVault localVault = localSnapshot.Vault.IsEmpty
            ? handle.Vault
            : SecretSyncVaultMerger.MergeRemoteWithLocal(
                handle.Vault,
                localSnapshot.Vault,
                SecretSyncConflictMode.PushWins,
                localSnapshot.ProjectEdits);

        string localHash = SecretPayloadSerializer.ComputeVaultHash(localVault);
        if (string.Equals(localHash, handle.LastRemoteVaultHash, StringComparison.Ordinal))
        {
            return;
        }

        SecretSyncRemoteManifest? currentManifest = await GetManifestAsync(cancellationToken);
        if (currentManifest is null)
        {
            await WriteInitialRemoteAsync(localVault, cancellationToken);
            return;
        }

        if (!string.Equals(currentManifest.Manifest.LatestRevision, handle.RemoteRevision, StringComparison.Ordinal))
        {
            throw new SecretSyncConflictException(
                "Remote SecretSync manifest changed since this AppHost pulled. Pull and merge before pushing.");
        }

        SecretEnvelope envelope = encryptor.Encrypt(localVault, handle.RemoteRevision);
        byte[] body = AesGcmSecretEncryptor.SerializeEnvelope(envelope);
        string versionObjectKey = CreateVersionObjectKey(envelope.Revision);
        await PutVersionAsync(versionObjectKey, body, envelope, cancellationToken);

        SecretSyncManifest nextManifest = new()
        {
            LatestRevision = envelope.Revision,
            ParentRevision = envelope.ParentRevision,
            VaultObjectKey = versionObjectKey,
            ContentHash = localHash,
            CreatedAt = currentManifest.Manifest.CreatedAt == default
                ? envelope.CreatedAt
                : currentManifest.Manifest.CreatedAt,
            UpdatedAt = envelope.UpdatedAt
        };

        SecretSyncRemoteWriteResult manifestWrite = await PutManifestAsync(
            nextManifest,
            currentManifest.ETag,
            ifMissing: false,
            cancellationToken);

        handle.Load(
            localVault,
            manifestWrite.ETag,
            envelope.Revision,
            localHash,
            initializedRemote: false);
        await PersistUserSecretViewsAsync(
            localVault,
            CreateRemoteState(nextManifest, manifestWrite.ETag, manifestWrite.WrittenAt),
            cancellationToken);
    }

    public Task EnsurePulledBeforeStartAsync(CancellationToken cancellationToken)
    {
        if (options.AutoPull &&
            options.PullMode != SecretSyncPullMode.Manual &&
            !handle.Pulled)
        {
            throw new InvalidOperationException("SecretSync was configured for AutoPull but did not complete before AppHost start.");
        }

        return Task.CompletedTask;
    }

    private async Task PullPinnedAsync(
        IConfiguration configuration,
        SecretSyncState state,
        SecretSyncLocalSnapshot localSnapshot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PinnedRevision))
        {
            throw new InvalidOperationException("SecretSync PinnedRevision is required when VersionMode is Pinned.");
        }

        if (!localSnapshot.Vault.IsEmpty)
        {
            throw new InvalidOperationException(
                "SecretSync pinned version mode is read-only, but local user-secrets contain changes. Use Latest version mode before editing synced secrets.");
        }

        if (state.Remote.Revision == options.PinnedRevision &&
            !localSnapshot.HasMissingBaselineValues &&
            localSnapshot.Vault.IsEmpty)
        {
            await LoadLocalOnlyAsync(configuration, state.Remote, cancellationToken);
            return;
        }

        string versionObjectKey = CreateVersionObjectKey(options.PinnedRevision);
        SecretSyncRemoteObject remoteVault = await GetRequiredVaultAsync(versionObjectKey, cancellationToken);
        SecretPayload payload = encryptor.Decrypt(remoteVault.Body);

        SecretSyncRemoteState remoteState = new()
        {
            Revision = options.PinnedRevision,
            ContentHash = payload.ContentHash,
            ManifestObjectKey = GetManifestObjectKey(),
            ManifestETag = null,
            VaultObjectKey = versionObjectKey,
            CheckedAt = DateTimeOffset.UtcNow
        };

        handle.Load(
            payload.Vault,
            remoteVault.ETag,
            options.PinnedRevision,
            payload.ContentHash,
            initializedRemote: false);
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration,
            GetAppHostSecrets(),
            options.ConfigurationPrecedence);

        await PersistUserSecretViewsAsync(payload.Vault, remoteState, cancellationToken);
    }

    private async Task InitializeMissingRemoteAsync(
        IConfiguration configuration,
        SecretSyncLocalSnapshot localSnapshot,
        CancellationToken cancellationToken)
    {
        SecretSyncVault localVault = localSnapshot.Vault;
        if (localVault.IsEmpty)
        {
            if (options.FailWhenRemoteMissingAndLocalEmpty)
            {
                throw new InvalidOperationException(
                    "SecretSync remote manifest was not found and no local user secrets were available to initialize it.");
            }

            handle.Load(
                localVault,
                etag: null,
                revision: null,
                lastRemoteVaultHash: null,
                initializedRemote: false);
            return;
        }

        if (!options.InitializeIfMissing)
        {
            handle.Load(
                localVault,
                etag: null,
                revision: null,
                lastRemoteVaultHash: null,
                initializedRemote: false);
            SecretSyncConfigurationInjector.AddSyncedSecrets(
                configuration,
                GetAppHostSecrets(),
                options.ConfigurationPrecedence);
            return;
        }

        await WriteInitialRemoteAsync(localVault, cancellationToken);
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration,
            GetAppHostSecrets(),
            options.ConfigurationPrecedence);
    }

    private async Task WriteInitialRemoteAsync(
        SecretSyncVault localVault,
        CancellationToken cancellationToken)
    {
        SecretEnvelope envelope = encryptor.Encrypt(localVault, parentRevision: null);
        byte[] body = AesGcmSecretEncryptor.SerializeEnvelope(envelope);
        string localHash = SecretPayloadSerializer.ComputeVaultHash(localVault);
        string versionObjectKey = CreateVersionObjectKey(envelope.Revision);

        await PutVersionAsync(versionObjectKey, body, envelope, cancellationToken);

        SecretSyncManifest manifest = new()
        {
            LatestRevision = envelope.Revision,
            ParentRevision = null,
            VaultObjectKey = versionObjectKey,
            ContentHash = localHash,
            CreatedAt = envelope.CreatedAt,
            UpdatedAt = envelope.UpdatedAt
        };
        SecretSyncRemoteWriteResult manifestWrite = await PutManifestAsync(
            manifest,
            ifMatchETag: null,
            ifMissing: true,
            cancellationToken);

        handle.Load(
            localVault,
            manifestWrite.ETag,
            envelope.Revision,
            localHash,
            initializedRemote: true);
        await PersistUserSecretViewsAsync(
            localVault,
            CreateRemoteState(manifest, manifestWrite.ETag, manifestWrite.WrittenAt),
            cancellationToken);
    }

    private bool ShouldSkipRemoteCheck(
        SecretSyncState state,
        SecretSyncLocalSnapshot localSnapshot)
    {
        if (options.PullMode != SecretSyncPullMode.IfStale ||
            string.IsNullOrWhiteSpace(state.Remote.Revision) ||
            state.Remote.CheckedAt is null ||
            localSnapshot.HasMissingBaselineValues ||
            !localSnapshot.Vault.IsEmpty)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - state.Remote.CheckedAt.Value < options.StaleAfter;
    }

    private static bool CanUseLocalVaultForManifest(
        SecretSyncState state,
        SecretSyncRemoteManifest remoteManifest,
        SecretSyncLocalSnapshot localSnapshot)
    {
        return localSnapshot.Vault.IsEmpty &&
            !localSnapshot.HasMissingBaselineValues &&
            string.Equals(
                state.Remote.Revision,
                remoteManifest.Manifest.LatestRevision,
                StringComparison.Ordinal);
    }

    private async Task LoadLocalOnlyAsync(
        IConfiguration configuration,
        SecretSyncRemoteState remoteState,
        CancellationToken cancellationToken)
    {
        SecretSyncVault localVault = await snapshotBuilder.BuildFullLocalVaultAsync(cancellationToken);
        handle.Load(
            localVault,
            remoteState.ManifestETag,
            string.IsNullOrWhiteSpace(remoteState.Revision) ? null : remoteState.Revision,
            string.IsNullOrWhiteSpace(remoteState.ContentHash) ? null : remoteState.ContentHash,
            initializedRemote: false);
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration,
            GetAppHostSecrets(),
            options.ConfigurationPrecedence);
    }

    private async Task LoadFullLocalAsync(
        IConfiguration configuration,
        SecretSyncRemoteState remoteState,
        CancellationToken cancellationToken)
    {
        SecretSyncVault localVault = await snapshotBuilder.BuildFullLocalVaultAsync(cancellationToken);
        handle.Load(
            localVault,
            remoteState.ManifestETag,
            remoteState.Revision,
            remoteState.ContentHash,
            initializedRemote: false);
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration,
            GetAppHostSecrets(),
            options.ConfigurationPrecedence);

        remoteState.CheckedAt = DateTimeOffset.UtcNow;
        await stateStore.SaveVaultBaselineAsync(localVault, remoteState, cancellationToken);
    }

    private async Task PersistUserSecretViewsAsync(
        SecretSyncVault vault,
        SecretSyncRemoteState remoteState,
        CancellationToken cancellationToken)
    {
        SecretSyncState state = await stateStore.ReadAsync(cancellationToken);

        if (options.WriteToUserSecrets)
        {
            await userSecretsStore.MergeVaultAsync(
                vault,
                state.GetResourceHashes(options.AppHostResourceName),
                cancellationToken);
        }

        await projectUserSecretsStore.MergeVaultAsync(vault, state, cancellationToken);
        remoteState.CheckedAt ??= DateTimeOffset.UtcNow;
        await stateStore.SaveVaultBaselineAsync(vault, remoteState, cancellationToken);
    }

    private async Task<SecretSyncRemoteManifest?> GetManifestAsync(CancellationToken cancellationToken)
    {
        SecretSyncRemoteObject? remote = await provider.GetAsync(
            CreateContext(GetManifestObjectKey()),
            cancellationToken);
        if (remote is null)
        {
            return null;
        }

        return new SecretSyncRemoteManifest(
            SecretSyncManifestSerializer.Deserialize(remote.Body),
            remote.ETag,
            remote.LastModified);
    }

    private async Task<SecretSyncRemoteObject> GetRequiredVaultAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        SecretSyncRemoteObject? remote = await provider.GetAsync(
            CreateContext(objectKey),
            cancellationToken) ?? throw new InvalidOperationException($"SecretSync vault object '{objectKey}' was not found.");
        return remote;
    }

    private async Task PutVersionAsync(
        string objectKey,
        byte[] body,
        SecretEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await provider.PutAsync(
            CreateContext(objectKey),
            body,
            new SecretSyncWriteCondition(null, IfMissing: true),
            CreateVersionMetadata(envelope),
            cancellationToken);
    }

    private async Task<SecretSyncRemoteWriteResult> PutManifestAsync(
        SecretSyncManifest manifest,
        string? ifMatchETag,
        bool ifMissing,
        CancellationToken cancellationToken)
    {
        return await provider.PutAsync(
            CreateContext(GetManifestObjectKey()),
            SecretSyncManifestSerializer.Serialize(manifest),
            new SecretSyncWriteCondition(ifMatchETag, ifMissing),
            CreateManifestMetadata(manifest),
            cancellationToken);
    }

    private SecretSyncProviderContext CreateContext(string objectKey) =>
        new(options.ResolveContainer(), objectKey.Trim().TrimStart('/'), options);

    private string GetManifestObjectKey() =>
        options.ResolveManifestKey().Trim().TrimStart('/');

    private string CreateVersionObjectKey(string revision)
    {
        string manifestKey = GetManifestObjectKey();
        int separator = manifestKey.LastIndexOf('/');
        string prefix = separator < 0 ? "" : manifestKey[..(separator + 1)];
        return $"{prefix}versions/{revision}.vault.json";
    }

    private SecretSyncRemoteState CreateRemoteState(SecretSyncRemoteManifest remoteManifest) =>
        CreateRemoteState(
            remoteManifest.Manifest,
            remoteManifest.ETag,
            DateTimeOffset.UtcNow);

    private SecretSyncRemoteState CreateRemoteState(
        SecretSyncManifest manifest,
        string? etag,
        DateTimeOffset checkedAt) =>
        new()
        {
            Revision = manifest.LatestRevision,
            ContentHash = manifest.ContentHash,
            ManifestObjectKey = GetManifestObjectKey(),
            ManifestETag = etag,
            VaultObjectKey = manifest.VaultObjectKey,
            CheckedAt = checkedAt
        };

    private IReadOnlyDictionary<string, string?> GetAppHostSecrets() =>
        handle.Resources.TryGetValue(
            options.AppHostResourceName,
            out IReadOnlyDictionary<string, string?>? values)
            ? values
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> CreateVersionMetadata(SecretEnvelope envelope) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["secret-sync-format"] = envelope.Format,
            ["secret-sync-revision"] = envelope.Revision
        };

    private static IReadOnlyDictionary<string, string> CreateManifestMetadata(SecretSyncManifest manifest) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["secret-sync-format"] = manifest.Format,
            ["secret-sync-revision"] = manifest.LatestRevision
        };
}
