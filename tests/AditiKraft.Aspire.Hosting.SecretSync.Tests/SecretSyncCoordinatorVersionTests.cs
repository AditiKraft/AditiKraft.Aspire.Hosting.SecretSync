using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;
using AditiKraft.Aspire.Hosting.SecretSync.Lifecycle;
using AditiKraft.Aspire.Hosting.SecretSync.Remote;
using AditiKraft.Aspire.Hosting.SecretSync.State;
using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class SecretSyncCoordinatorVersionTests : IDisposable
{
    private readonly List<string> _directories = [];

    [Fact]
    public async Task PushAsync_WritesNewVersionWhenLocalUserSecretsChangedAfterPull()
    {
        SecretSyncOptions options = CreateOptions();
        TrackUserSecretsDirectory(options.UserSecretsId);
        TrackDirectory(options.StateDirectory);

        var userSecretsStore = new UserSecretsStore(options);
        await userSecretsStore.WriteAsync(
            options.UserSecretsId,
            new Dictionary<string, string?>
            {
                ["Stripe:ApiKey"] = "sk-first"
            },
            CancellationToken.None);

        var provider = new InMemorySecretSyncProvider();
        var encryptor = new AesGcmSecretEncryptor(options);
        var coordinator = CreateCoordinator(options, provider, encryptor);
        var configuration = new ConfigurationManager();

        await coordinator.PullAsync(configuration, CancellationToken.None);

        SecretSyncManifest firstManifest = provider.ReadManifest(options.S3.ManifestKey);

        await userSecretsStore.MergeAsync(
            options.UserSecretsId,
            new Dictionary<string, string?>
            {
                ["Stripe:ApiKey"] = "sk-second",
                ["OpenAI:ApiKey"] = "oa-local"
            },
            overwriteExisting: true,
            CancellationToken.None);

        await coordinator.PushAsync(CancellationToken.None);

        SecretSyncManifest latestManifest = provider.ReadManifest(options.S3.ManifestKey);
        SecretSyncVault latestVault = encryptor.Decrypt(provider.Objects[latestManifest.VaultObjectKey].Body).Vault;
        Dictionary<string, string?> latestAppHost = VaultFlattener.Flatten(latestVault.Resources["apphost"]);

        Assert.NotEqual(firstManifest.LatestRevision, latestManifest.LatestRevision);
        Assert.Equal(firstManifest.LatestRevision, latestManifest.ParentRevision);
        Assert.Equal("sk-second", latestAppHost["Stripe:ApiKey"]);
        Assert.Equal("oa-local", latestAppHost["OpenAI:ApiKey"]);
        Assert.Equal(2, provider.Objects.Keys.Count(key => key.StartsWith("app/versions/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PullAsync_PinnedVersionModeFailsWhenLocalUserSecretsHaveUnbaselinedChanges()
    {
        SecretSyncOptions options = CreateOptions();
        options.VersionMode = SecretSyncVersionMode.Pinned;
        options.PinnedRevision = "202606240000000000-revision";
        TrackUserSecretsDirectory(options.UserSecretsId);
        TrackDirectory(options.StateDirectory);

        var userSecretsStore = new UserSecretsStore(options);
        await userSecretsStore.WriteAsync(
            options.UserSecretsId,
            new Dictionary<string, string?>
            {
                ["Stripe:ApiKey"] = "sk-local"
            },
            CancellationToken.None);

        var coordinator = CreateCoordinator(
            options,
            new InMemorySecretSyncProvider(),
            new AesGcmSecretEncryptor(options));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.PullAsync(new ConfigurationManager(), CancellationToken.None));

        Assert.Contains("pinned version mode is read-only", exception.Message);
    }

    public void Dispose()
    {
        foreach (string directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private SecretSyncCoordinator CreateCoordinator(
        SecretSyncOptions options,
        ISecretSyncProvider provider,
        AesGcmSecretEncryptor encryptor)
    {
        var handle = new SecretSyncHandle();
        var userSecretsStore = new UserSecretsStore(options);
        var projectStore = new ProjectUserSecretsStore(options, userSecretsStore);
        var stateStore = new SecretSyncStateStore(options);
        var snapshotBuilder = new SecretSnapshotBuilder(options, userSecretsStore, projectStore, stateStore);

        return new SecretSyncCoordinator(
            options,
            handle,
            provider,
            encryptor,
            snapshotBuilder,
            userSecretsStore,
            projectStore,
            stateStore);
    }

    private SecretSyncOptions CreateOptions()
    {
        var options = new SecretSyncOptions
        {
            EncryptionKey = "unit-test-encryption-key",
            UserSecretsId = $"secretsync-test-{Guid.NewGuid():N}",
            StateDirectory = Directory.CreateTempSubdirectory("secretsync-state-").FullName,
            WriteToUserSecrets = true
        };

        options.S3.BucketName = "unit-test-bucket";
        options.S3.ManifestKey = "app/latest.json";
        options.KeyDerivation.MemorySizeKiB = 1024;
        options.KeyDerivation.Iterations = 1;
        options.KeyDerivation.DegreeOfParallelism = 1;
        return options;
    }

    private void TrackUserSecretsDirectory(string userSecretsId)
    {
        string path = PathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            TrackDirectory(directory);
        }
    }

    private void TrackDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _directories.Add(directory);
        }
    }

    private sealed class InMemorySecretSyncProvider : ISecretSyncProvider
    {
        private int _etag;

        public string Name => "memory";

        public Dictionary<string, SecretSyncRemoteObject> Objects { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<SecretSyncRemoteObject?> GetAsync(
            SecretSyncProviderContext context,
            CancellationToken cancellationToken)
        {
            Objects.TryGetValue(context.ObjectKey, out SecretSyncRemoteObject? remote);
            return Task.FromResult(remote);
        }

        public Task<SecretSyncRemoteWriteResult> PutAsync(
            SecretSyncProviderContext context,
            byte[] body,
            SecretSyncWriteCondition condition,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (condition.IfMissing && Objects.ContainsKey(context.ObjectKey))
            {
                throw new InvalidOperationException($"Object '{context.ObjectKey}' already exists.");
            }

            if (condition.IfMatchETag is not null &&
                (!Objects.TryGetValue(context.ObjectKey, out SecretSyncRemoteObject? current) ||
                !string.Equals(current.ETag, condition.IfMatchETag, StringComparison.Ordinal)))
            {
                throw new SecretSyncConflictException("ETag mismatch.");
            }

            string etag = $"etag-{++_etag}";
            var written = new SecretSyncRemoteObject(
                body,
                etag,
                metadata.TryGetValue("secret-sync-revision", out string? revision) ? revision : null,
                DateTimeOffset.UtcNow,
                new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase));
            Objects[context.ObjectKey] = written;
            return Task.FromResult(new SecretSyncRemoteWriteResult(etag, written.Revision, written.LastModified!.Value));
        }

        public SecretSyncManifest ReadManifest(string objectKey) =>
            SecretSyncManifestSerializer.Deserialize(Objects[objectKey].Body);
    }
}
