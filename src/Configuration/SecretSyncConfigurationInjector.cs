using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.FileProviders;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal static class SecretSyncConfigurationInjector
{
    private static readonly StringComparer _pathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void AddSyncedSecrets(
        IConfiguration configuration,
        IReadOnlyDictionary<string, string?> secrets,
        SecretSyncPrecedence precedence)
    {
        if (secrets.Count == 0)
        {
            return;
        }

        if (configuration is not IConfigurationManager manager || configuration is not IConfigurationRoot root)
        {
            throw new InvalidOperationException("SecretSync requires a mutable AppHost configuration root.");
        }

        // Adding a source leaves existing providers loaded, including single-use JSON streams.
        manager.Sources.Add(new SyncedSecretsSource(secrets, precedence, manager, root));
    }

    public static string? FindLegacySecretsPath(IConfiguration configuration, string aspirePath)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return null;
        }

        string? previousPath = null;
        foreach (IConfigurationProvider provider in root.Providers)
        {
            string? path = GetProviderFilePath(provider);
            if (PathsEqual(path, aspirePath))
            {
                // Aspire places its Development user-secrets provider immediately before its native file.
                string? id = Path.GetFileName(Path.GetDirectoryName(previousPath));
                return !string.IsNullOrWhiteSpace(id) && PathsEqual(previousPath, PathHelper.GetSecretsPathFromSecretsId(id))
                    ? previousPath
                    : null;
            }

            previousPath = path;
        }

        return null;
    }

    public static bool PathsEqual(string? left, string? right) => left is not null && _pathComparer.Equals(left, right);

    public static void ReloadSecretsFiles(IConfiguration? configuration, IReadOnlyList<string> paths)
    {
        if (configuration is not IConfigurationRoot root || paths.Count == 0)
        {
            return;
        }

        foreach (JsonConfigurationProvider provider in root.Providers.OfType<JsonConfigurationProvider>())
        {
            string? path = GetProviderFilePath(provider);
            if (path is not null && paths.Contains(path, _pathComparer))
            {
                provider.Load();
            }
        }
    }

    private static string? GetProviderFilePath(IConfigurationProvider provider)
    {
        return provider is JsonConfigurationProvider { Source.FileProvider: PhysicalFileProvider fileProvider, Source.Path: string sourcePath }
            ? Path.GetFullPath(sourcePath, fileProvider.Root)
            : null;
    }

    private static int GetDefaultInsertionIndex(IList<IConfigurationSource> sources)
    {
        int firstOverride = sources.Count;
        int firstOverrideAfterJson = sources.Count;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                firstOverrideAfterJson = sources.Count;
            }
            else if (sources[i] is EnvironmentVariablesConfigurationSource or CommandLineConfigurationSource)
            {
                firstOverride = Math.Min(firstOverride, i);
                firstOverrideAfterJson = Math.Min(firstOverrideAfterJson, i);
                if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
                {
                    // The AppHost's unprefixed environment source follows appsettings and secrets.
                    return firstOverrideAfterJson;
                }
            }
        }

        return firstOverride;
    }

    private sealed class SyncedSecretsSource(
        IReadOnlyDictionary<string, string?> secrets,
        SecretSyncPrecedence precedence,
        IConfigurationManager manager,
        IConfigurationRoot root) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new SyncedSecretsProvider(secrets, precedence, manager, root);
    }

    private sealed class SyncedSecretsProvider(
        IReadOnlyDictionary<string, string?> secrets,
        SecretSyncPrecedence precedence,
        IConfigurationManager manager,
        IConfigurationRoot root) : ConfigurationProvider
    {
        public override void Load() => Data = new Dictionary<string, string?>(secrets, StringComparer.OrdinalIgnoreCase);

        public override bool TryGet(string key, out string? value)
        {
            if (!base.TryGet(key, out value))
            {
                return false;
            }

            if (precedence == SecretSyncPrecedence.Highest)
            {
                return true;
            }

            int insertionIndex = precedence == SecretSyncPrecedence.Lowest ? 0 : GetDefaultInsertionIndex(manager.Sources);
            IConfigurationProvider[] providers = [.. root.Providers];
            // Use the current providers because configuration can rebuild them after a source is removed.
            for (int i = Array.IndexOf(providers, this) - 1; i >= 0; i--)
            {
                IConfigurationProvider provider = providers[i];
                if ((i >= insertionIndex || provider is EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider) &&
                    provider.TryGet(key, out string? overrideValue))
                {
                    value = overrideValue;
                    break;
                }
            }

            return true;
        }
    }
}
