using System.Reflection;
using System.Text;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;
using AditiKraft.Aspire.Hosting.SecretSync.Lifecycle;
using AditiKraft.Aspire.Hosting.SecretSync.Providers;
using AditiKraft.Aspire.Hosting.SecretSync.State;
using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AditiKraft.Aspire.Hosting.SecretSync;

public static class SecretSyncExtensions
{
    public static SecretSyncHandle AddSecretSync(
        this IDistributedApplicationBuilder builder,
        Action<SecretSyncOptions> configure)
    {
        return AddSecretSyncAsync(builder, configure).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public static async Task<SecretSyncHandle> AddSecretSyncAsync(
        this IDistributedApplicationBuilder builder,
        Action<SecretSyncOptions> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SecretSyncOptions();
        configure(options);
        ResolveAppHostDefaults(builder, options);

        var handle = new SecretSyncHandle();
        ISecretSyncProvider provider = new S3SecretSyncProvider();
        var encryptor = new AesGcmSecretEncryptor(options);
        var userSecretsStore = new UserSecretsStore(options);
        var projectUserSecretsStore = new ProjectUserSecretsStore(options, userSecretsStore);
        var stateStore = new SecretSyncStateStore(options);
        var snapshotBuilder = new SecretSnapshotBuilder(options, userSecretsStore, projectUserSecretsStore, stateStore);
        var coordinator = new SecretSyncCoordinator(
            options,
            handle,
            provider,
            encryptor,
            snapshotBuilder,
            userSecretsStore,
            projectUserSecretsStore,
            stateStore);

        await coordinator.PullAsync(builder.Configuration, cancellationToken);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(handle);
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton(encryptor);
        builder.Services.AddSingleton(userSecretsStore);
        builder.Services.AddSingleton(projectUserSecretsStore);
        builder.Services.AddSingleton(stateStore);
        builder.Services.AddSingleton(snapshotBuilder);
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddHostedService<SecretSyncShutdownHostedService>();
        builder.Services.Configure<HostOptions>(hostOptions =>
        {
            if (hostOptions.ShutdownTimeout < options.ShutdownTimeout)
            {
                hostOptions.ShutdownTimeout = options.ShutdownTimeout;
            }
        });

        builder.OnBeforeStart(async (@event, ct) =>
        {
            var registeredCoordinator = @event.Services.GetRequiredService<SecretSyncCoordinator>();
            await registeredCoordinator.EnsurePulledBeforeStartAsync(ct);
        });

        return handle;
    }

    private static void ResolveAppHostDefaults(IDistributedApplicationBuilder builder, SecretSyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            options.ProjectId = builder.Environment.ApplicationName;
        }

        if (string.IsNullOrWhiteSpace(options.UserSecretsId))
        {
            Assembly? assembly = builder.AppHostAssembly;
            string? userSecretsId = assembly?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
            if (!string.IsNullOrWhiteSpace(userSecretsId))
            {
                options.UserSecretsId = userSecretsId;
            }
        }

        if (string.IsNullOrWhiteSpace(options.S3.ManifestKey))
        {
            options.S3.ManifestKey = CreateDefaultManifestKey(options);
        }
    }

    private static string CreateDefaultManifestKey(SecretSyncOptions options)
    {
        string identity = !string.IsNullOrWhiteSpace(options.UserSecretsId)
            ? options.UserSecretsId
            : options.ProjectId;

        return $"aspire/apphosts/{NormalizeObjectKeySegment(identity)}/latest.json";
    }

    private static string NormalizeObjectKeySegment(string value)
    {
        // Use a fixed allowlist rather than Path.GetInvalidFileNameChars(), whose
        // contents differ between Windows and Linux. An OS-dependent set would derive
        // different manifest keys for the same identity across platforms, so teammates
        // on different operating systems could point at different remote objects.
        string trimmed = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(trimmed.Length);

        foreach (char c in trimmed)
        {
            bool isAllowed = c is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_' or '.';
            builder.Append(isAllowed ? c : '-');
        }

        string normalized = builder.ToString();
        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }
}
