using System.Reflection;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;
using AditiKraft.Aspire.Hosting.SecretSync.Lifecycle;
using AditiKraft.Aspire.Hosting.SecretSync.Providers;
using AditiKraft.Aspire.Hosting.SecretSync.State;
using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
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

        var handle = new SecretSyncHandle(options);
        ISecretSyncProvider provider = CreateProvider(options);
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

    public static IResourceBuilder<T> WithSecretSync<T>(
        this IResourceBuilder<T> builder,
        SecretSyncHandle secrets,
        IEnumerable<string>? resourceNames = null,
        bool includeResourceMatchingName = true)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(secrets);

        IReadOnlyDictionary<string, string?> values = secrets.ResolveForResource(
            builder.Resource.Name,
            resourceNames,
            includeResourceMatchingName);

        foreach ((string key, string? value) in values)
        {
            if (value is not null)
            {
                builder.WithEnvironment(ToEnvironmentName(key), value);
            }
        }

        return builder;
    }

    public static IResourceBuilder<T> WithSecretSyncValue<T>(
        this IResourceBuilder<T> builder,
        SecretSyncHandle secrets,
        string configurationPath,
        string? environmentVariableName = null,
        string? resourceName = null)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        string? value = null;
        string resolvedResourceName = string.IsNullOrWhiteSpace(resourceName)
            ? builder.Resource.Name
            : resourceName;
        if (secrets.Resources.TryGetValue(resolvedResourceName, out IReadOnlyDictionary<string, string?>? resourceValues))
        {
            resourceValues.TryGetValue(configurationPath, out value);
        }

        if (value is null)
        {
            if (secrets.Options.MissingResourceBehavior == SecretSyncMissingResourceBehavior.Fail)
            {
                throw new InvalidOperationException($"SecretSync value '{configurationPath}' was not found for resource '{resolvedResourceName}'.");
            }

            return builder;
        }

        builder.WithEnvironment(environmentVariableName ?? ToEnvironmentName(configurationPath), value);
        return builder;
    }

    private static string ToEnvironmentName(string configurationPath) =>
        configurationPath.Replace(":", "__", StringComparison.Ordinal);

    private static ISecretSyncProvider CreateProvider(SecretSyncOptions options)
    {
        return options.Provider switch
        {
            SecretSyncProvider.CloudflareR2 => new R2SecretSyncProvider(),
            _ => throw new NotSupportedException($"SecretSync provider '{options.Provider}' is not supported yet.")
        };
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

        if (string.IsNullOrWhiteSpace(options.ObjectKey))
        {
            options.ObjectKey = CreateDefaultObjectKey(options);
        }
    }

    private static string CreateDefaultObjectKey(SecretSyncOptions options)
    {
        string identity = !string.IsNullOrWhiteSpace(options.UserSecretsId)
            ? options.UserSecretsId
            : options.ProjectId;

        return $"aspire/apphosts/{NormalizeObjectKeySegment(identity)}/latest.json";
    }

    private static string NormalizeObjectKeySegment(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        char[] invalidChars = Path.GetInvalidFileNameChars();

        foreach (char invalidChar in invalidChars)
        {
            normalized = normalized.Replace(invalidChar, '-');
        }

        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }
}
