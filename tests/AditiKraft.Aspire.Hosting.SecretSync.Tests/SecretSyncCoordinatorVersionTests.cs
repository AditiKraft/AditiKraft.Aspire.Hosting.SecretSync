using System.Text;
using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;
using AditiKraft.Aspire.Hosting.SecretSync.Lifecycle;
using AditiKraft.Aspire.Hosting.SecretSync.Remote;
using AditiKraft.Aspire.Hosting.SecretSync.State;
using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class SecretSyncCoordinatorVersionTests : IDisposable
{
    private readonly List<string> _directories = [];

    [Fact]
    public async Task PushAsync_WritesNewVersionWhenAspireAppHostSecretsChangedAfterPull()
    {
        SecretSyncOptions options = CreateOptions();
        TrackDirectory(options.StateDirectory);

        string appHostSecretsPath = Path.Combine(options.StateDirectory, "Development.json");
        await File.WriteAllTextAsync(appHostSecretsPath, """{ "Stripe:ApiKey": "sk-first" }""");

        InMemorySecretSyncProvider provider = new();
        AesGcmSecretEncryptor encryptor = new(options);
        UserSecretsStore userSecretsStore = new(options, appHostSecretsPath);
        SecretSyncCoordinator coordinator = CreateCoordinator(options, provider, encryptor, userSecretsStore);
        ConfigurationManager configuration = new();

        await coordinator.PullAsync(configuration, CancellationToken.None);

        SecretSyncManifest firstManifest = provider.ReadManifest(options.S3.ManifestKey);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(appHostSecretsPath));
        }

        await File.WriteAllTextAsync(appHostSecretsPath, """{ "Stripe:ApiKey": "sk-second", "OpenAI:ApiKey": "oa-local" }""");

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
    public async Task PullAndPushAsync_PreserveNativeJsonAndSyncTypeChanges()
    {
        SecretSyncOptions sourceOptions = CreateOptions();
        SecretSyncOptions targetOptions = CreateOptions();
        TrackDirectory(sourceOptions.StateDirectory);
        TrackDirectory(targetOptions.StateDirectory);

        const string initialJson = """
            {
              "State": { "Count": 42, "Enabled": true, "Targets": ["one", 2], "Empty": {}, "Number": "42" },
              "SecretSync": { "Enabled": true }
            }
            """;
        string sourcePath = Path.Combine(sourceOptions.StateDirectory, "Development.json");
        await File.WriteAllTextAsync(sourcePath, initialJson);
        InMemorySecretSyncProvider provider = new();
        UserSecretsStore sourceStore = new(sourceOptions, sourcePath);
        SecretSyncCoordinator source = CreateCoordinator(sourceOptions, provider, new AesGcmSecretEncryptor(sourceOptions), sourceStore);

        await source.PullAsync(new ConfigurationManager(), CancellationToken.None);

        JsonObject local = JsonNode.Parse(await File.ReadAllTextAsync(sourcePath))!.AsObject();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(initialJson), local));
        string firstRevision = provider.ReadManifest(sourceOptions.S3.ManifestKey).LatestRevision;
        JsonNode expectedState = local["State"]!;
        expectedState["Number"] = 42;
        await File.WriteAllTextAsync(sourcePath, local.ToJsonString());

        await source.PushAsync(CancellationToken.None);

        Assert.NotEqual(firstRevision, provider.ReadManifest(sourceOptions.S3.ManifestKey).LatestRevision);
        string targetPath = Path.Combine(targetOptions.StateDirectory, "Development.json");
        UserSecretsStore targetStore = new(targetOptions, targetPath);
        SecretSyncCoordinator target = CreateCoordinator(targetOptions, provider, new AesGcmSecretEncryptor(targetOptions), targetStore);
        ConfigurationManager configuration = new();

        await target.PullAsync(configuration, CancellationToken.None);

        JsonObject targetDocument = JsonNode.Parse(await File.ReadAllTextAsync(targetPath))!.AsObject();
        Assert.True(JsonNode.DeepEquals(expectedState, targetDocument["State"]));
        Assert.False(targetDocument.ContainsKey("SecretSync"));
        Assert.Equal("42", configuration["State:Number"]);
    }

    [Fact]
    public async Task PullAsync_LeavesTheMissingVaultAndBaselineUnwrittenWhenInjectionFails()
    {
        SecretSyncOptions options = CreateOptions();
        TrackDirectory(options.StateDirectory);
        const string initialJson = """{ "Parameters:shared": "local" }""";
        string path = Path.Combine(options.StateDirectory, "Development.json");
        await File.WriteAllTextAsync(path, initialJson);
        InMemorySecretSyncProvider provider = new();
        UserSecretsStore store = new(options, path);
        SecretSyncCoordinator coordinator = CreateCoordinator(options, provider, new AesGcmSecretEncryptor(options), store);
        using ConfigurationRoot configuration = (ConfigurationRoot)new ConfigurationBuilder().Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PullAsync(configuration, CancellationToken.None));

        Assert.Empty(provider.Objects);
        Assert.False(File.Exists(Path.Combine(options.StateDirectory, "state.json")));
        Assert.Equal(initialJson, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PullAsync_PinnedVersionModeFailsWhenLocalUserSecretsHaveUnbaselinedChanges()
    {
        SecretSyncOptions options = CreateOptions();
        options.VersionMode = SecretSyncVersionMode.Pinned;
        options.PinnedRevision = "202606240000000000-revision";
        TrackUserSecretsDirectory(options.UserSecretsId);
        TrackDirectory(options.StateDirectory);

        await UserSecretsStore.WriteAsync(
            options.UserSecretsId,
            new Dictionary<string, JsonNode?>
            {
                ["Stripe:ApiKey"] = "sk-local"
            },
            CancellationToken.None);

        SecretSyncCoordinator coordinator = CreateCoordinator(
            options,
            new InMemorySecretSyncProvider(),
            new AesGcmSecretEncryptor(options),
            new UserSecretsStore(options, PathHelper.GetSecretsPathFromSecretsId(options.UserSecretsId)));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.PullAsync(new ConfigurationManager(), CancellationToken.None));

        Assert.Contains("pinned version mode is read-only", exception.Message);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task PullAsync_PinnedVersionRemovesOnlyUnchangedMigratedSecrets(
        bool legacyValueChanged,
        bool overrideUserSecretsIdAfterBuilderCreation,
        bool useExplicitLegacyId)
    {
        SecretSyncOptions options = CreateOptions();
        TrackUserSecretsDirectory(options.UserSecretsId);
        TrackDirectory(options.StateDirectory);

        const string parameterKey = "Parameters:obsolete";
        Dictionary<string, JsonNode?> migratedSecrets = new()
        {
            [parameterKey] = "from-later-revision"
        };
        await UserSecretsStore.WriteAsync(options.UserSecretsId, migratedSecrets, CancellationToken.None);

        string appHostSecretsPath = Path.Combine(options.StateDirectory, "Development.json");
        DistributedApplicationOptions appHostOptions = new()
        {
            Args =
            [
                "--environment", "Development",
                "--ASPIRE_USER_SECRETS_ID", options.UserSecretsId,
                "--ASPIRE_SECRETS_FILE", appHostSecretsPath
            ],
            DisableDashboard = true
        };
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(appHostOptions);
        if (!useExplicitLegacyId)
        {
            JsonObject legacyDocument = new()
            {
                [parameterKey] = legacyValueChanged ? "local-legacy-edit" : "from-later-revision",
                ["Token"] = "standalone",
                ["Token:Issuer"] = "issuer"
            };
            string legacyPath = PathHelper.GetSecretsPathFromSecretsId(options.UserSecretsId);
            await File.WriteAllTextAsync(legacyPath, legacyDocument.ToJsonString());
            builder = DistributedApplication.CreateBuilder(appHostOptions);
        }

        string? unrelatedSecretsId = null;
        if (overrideUserSecretsIdAfterBuilderCreation)
        {
            unrelatedSecretsId = $"secretsync-test-{Guid.NewGuid():N}";
            TrackUserSecretsDirectory(unrelatedSecretsId);
            await UserSecretsStore.WriteAsync(unrelatedSecretsId, migratedSecrets, CancellationToken.None);
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ASPIRE_USER_SECRETS_ID"] = unrelatedSecretsId });
        }

        await builder.AddSecretSyncAsync(syncOptions =>
        {
            syncOptions.AutoPull = false;
            if (useExplicitLegacyId)
            {
                syncOptions.UserSecretsId = options.UserSecretsId;
            }
        });
        UserSecretsStore userSecretsStore = Assert.IsType<UserSecretsStore>(builder.Services
            .Single(service => service.ServiceType == typeof(UserSecretsStore)).ImplementationInstance);
        Assert.Equal("from-later-revision", builder.Configuration[parameterKey]);
        using MemoryStream configStream = new(Encoding.UTF8.GetBytes("""{ "StreamSetting": "stream-value" }"""));
        builder.Configuration.AddJsonStream(configStream);

        SecretSyncVault currentVault = new();
        currentVault.Resources[options.AppHostResourceName] = VaultFlattener.Unflatten(migratedSecrets);
        SecretSyncStateStore stateStore = new(options);
        await stateStore.SaveVaultBaselineAsync(currentVault, remote: null, CancellationToken.None);

        SecretSyncVault pinnedVault = new();
        pinnedVault.Resources["api"] = VaultFlattener.Unflatten(
            new Dictionary<string, JsonNode?> { ["Data:Key"] = "old-value" });
        pinnedVault.Resources["apphost"] = new JsonObject
        {
            ["Parameters:kept"] = "pinned-value"
        };
        AesGcmSecretEncryptor encryptor = new(options);
        SecretEnvelope pinnedVersion = encryptor.Encrypt(pinnedVault, parentRevision: null);
        InMemorySecretSyncProvider provider = new();
        provider.Objects[$"app/versions/{pinnedVersion.Revision}.vault.json"] = new SecretSyncRemoteObject(
            AesGcmSecretEncryptor.SerializeEnvelope(pinnedVersion),
            ETag: null,
            Revision: pinnedVersion.Revision,
            LastModified: null,
            Metadata: new Dictionary<string, string>());

        options.VersionMode = SecretSyncVersionMode.Pinned;
        options.PinnedRevision = pinnedVersion.Revision;
        SecretSyncCoordinator coordinator = CreateCoordinator(options, provider, encryptor, userSecretsStore);

        await coordinator.PullAsync(builder.Configuration, CancellationToken.None);

        string? expectedValue = legacyValueChanged ? "local-legacy-edit" : null;
        Assert.Equal(expectedValue, builder.Configuration[parameterKey]);
        Assert.Equal("pinned-value", builder.Configuration["Parameters:kept"]);
        Assert.Equal("stream-value", builder.Configuration["StreamSetting"]);
        IReadOnlyDictionary<string, JsonNode?> legacySecrets = await UserSecretsStore.ReadAsync(
            options.UserSecretsId,
            CancellationToken.None);
        Assert.Equal(expectedValue, legacySecrets.GetValueOrDefault(parameterKey)?.GetValue<string>());
        if (!useExplicitLegacyId)
        {
            Assert.Equal("standalone", legacySecrets["Token"]?.GetValue<string>());
            Assert.Equal("issuer", legacySecrets["Token:Issuer"]?.GetValue<string>());
        }
        if (unrelatedSecretsId is not null)
        {
            IReadOnlyDictionary<string, JsonNode?> unrelatedSecrets = await UserSecretsStore.ReadAsync(
                unrelatedSecretsId,
                CancellationToken.None);
            Assert.Equal("from-later-revision", unrelatedSecrets[parameterKey]?.GetValue<string>());
        }
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

    private static SecretSyncCoordinator CreateCoordinator(
        SecretSyncOptions options,
        ISecretSyncProvider provider,
        AesGcmSecretEncryptor encryptor,
        UserSecretsStore userSecretsStore)
    {
        SecretSyncHandle handle = new();
        ProjectUserSecretsStore projectStore = new(options);
        SecretSyncStateStore stateStore = new(options);
        SecretSnapshotBuilder snapshotBuilder = new(options, userSecretsStore, projectStore, stateStore);

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

    private static SecretSyncOptions CreateOptions()
    {
        SecretSyncOptions options = new()
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
            SecretSyncRemoteObject written = new(
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
