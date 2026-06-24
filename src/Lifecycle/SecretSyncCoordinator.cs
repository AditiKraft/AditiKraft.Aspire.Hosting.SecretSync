using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;
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
    ProjectUserSecretsStore projectUserSecretsStore)
{
    public async Task PullAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!options.AutoPull)
        {
            return;
        }

        SecretSyncProviderContext context = CreateContext();
        SecretSyncRemoteObject? remote = await provider.GetAsync(context, cancellationToken);

        if (remote is null)
        {
            await InitializeMissingRemoteAsync(configuration, context, cancellationToken);
            return;
        }

        SecretPayload payload = encryptor.Decrypt(remote.Body);
        string remoteVaultHash = payload.ContentHash;
        SecretSyncLocalSnapshot localSnapshot = await snapshotBuilder.BuildLocalSnapshotAsync(cancellationToken);
        SecretSyncVault mergedVault = SecretSyncVaultMerger.MergeRemoteWithLocal(
            payload.Vault,
            localSnapshot.Vault,
            options.ConflictMode,
            localSnapshot.ProjectEdits);
        handle.Load(
            mergedVault,
            remote.ETag,
            remote.Revision,
            remoteVaultHash,
            initializedRemote: false);
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration,
            GetAppHostSecrets(),
            options.ConfigurationPrecedence);

        await PersistUserSecretViewsAsync(mergedVault, cancellationToken);
    }

    public async Task PushAsync(CancellationToken cancellationToken)
    {
        if (!options.AutoPush || !handle.Pulled)
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

        SecretSyncProviderContext context = CreateContext();
        SecretEnvelope envelope = encryptor.Encrypt(localVault, handle.RemoteRevision);
        SecretSyncRemoteWriteResult write = await provider.PutAsync(
            context,
            encryptor.SerializeEnvelope(envelope),
            new SecretSyncWriteCondition(handle.RemoteETag, IfMissing: false),
            CreateMetadata(envelope),
            cancellationToken);

        handle.Load(
            localVault,
            write.ETag,
            envelope.Revision,
            SecretPayloadSerializer.ComputeVaultHash(localVault),
            initializedRemote: false);
        await PersistUserSecretViewsAsync(localVault, cancellationToken);
    }

    public Task EnsurePulledBeforeStartAsync(CancellationToken cancellationToken)
    {
        if (options.AutoPull && !handle.Pulled)
        {
            throw new InvalidOperationException("SecretSync was configured for AutoPull but did not complete before AppHost start.");
        }

        return Task.CompletedTask;
    }

    private async Task InitializeMissingRemoteAsync(
        IConfiguration configuration,
        SecretSyncProviderContext context,
        CancellationToken cancellationToken)
    {
        SecretSyncLocalSnapshot localSnapshot = await snapshotBuilder.BuildLocalSnapshotAsync(cancellationToken);
        SecretSyncVault localVault = localSnapshot.Vault;
        if (localVault.IsEmpty)
        {
            if (options.FailWhenRemoteMissingAndLocalEmpty)
            {
                throw new InvalidOperationException(
                    "SecretSync remote blob was not found and no local user secrets were available to initialize it.");
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
            await PersistUserSecretViewsAsync(localVault, cancellationToken);
            return;
        }

        SecretEnvelope envelope = encryptor.Encrypt(localVault, parentRevision: null);
        SecretSyncRemoteWriteResult write = await provider.PutAsync(
            context,
            encryptor.SerializeEnvelope(envelope),
            new SecretSyncWriteCondition(null, IfMissing: true),
            CreateMetadata(envelope),
            cancellationToken);

        handle.Load(
            localVault,
            write.ETag,
            envelope.Revision,
            SecretPayloadSerializer.ComputeVaultHash(localVault),
            initializedRemote: true);
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration,
            GetAppHostSecrets(),
            options.ConfigurationPrecedence);
        await PersistUserSecretViewsAsync(localVault, cancellationToken);
    }

    private async Task PersistUserSecretViewsAsync(
        SecretSyncVault vault,
        CancellationToken cancellationToken)
    {
        if (options.WriteToUserSecrets)
        {
            await userSecretsStore.MergeVaultAsync(
                vault,
                cancellationToken);
        }

        await projectUserSecretsStore.MergeVaultAsync(vault, cancellationToken);
    }

    private SecretSyncProviderContext CreateContext() =>
        new(options.BucketName, options.ObjectKey.Trim().TrimStart('/'), options);

    private IReadOnlyDictionary<string, string?> GetAppHostSecrets() =>
        handle.Resources.TryGetValue(
            options.AppHostResourceName,
            out IReadOnlyDictionary<string, string?>? values)
            ? values
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> CreateMetadata(SecretEnvelope envelope) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["secret-sync-format"] = envelope.Format,
            ["secret-sync-revision"] = envelope.Revision
        };
}
