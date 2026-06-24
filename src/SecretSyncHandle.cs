using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync;

public sealed class SecretSyncHandle
{
    internal SecretSyncHandle(SecretSyncOptions options)
    {
        Options = options;
    }

    public SecretSyncOptions Options { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Resources { get; internal set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

    public string? RemoteETag { get; internal set; }
    public string? RemoteRevision { get; internal set; }
    public bool Pulled { get; internal set; }
    public bool InitializedRemote { get; internal set; }

    internal SecretSyncVault Vault { get; set; } = new();
    internal string? LastRemoteVaultHash { get; set; }

    public IReadOnlyDictionary<string, string?> ResolveForResource(
        string resourceName,
        IEnumerable<string>? resourceNames = null,
        bool includeResourceMatchingName = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (resourceNames is not null)
        {
            foreach (string name in resourceNames)
            {
                MergeResource(resolved, name);
            }
        }

        if (includeResourceMatchingName)
        {
            MergeResource(resolved, resourceName);
        }

        return resolved;
    }

    internal void Load(
        SecretSyncVault vault,
        string? etag,
        string? revision,
        string? lastRemoteVaultHash,
        bool initializedRemote)
    {
        Vault = vault;
        Resources = vault.Resources.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string?>)VaultFlattener.Flatten(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        RemoteETag = etag;
        RemoteRevision = revision;
        LastRemoteVaultHash = lastRemoteVaultHash;
        Pulled = true;
        InitializedRemote = initializedRemote;
    }

    private void MergeResource(IDictionary<string, string?> target, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return;
        }

        if (Resources.TryGetValue(resourceName, out IReadOnlyDictionary<string, string?>? values))
        {
            Merge(target, values);
            return;
        }

        if (Options.MissingResourceBehavior == SecretSyncMissingResourceBehavior.Fail)
        {
            throw new InvalidOperationException($"SecretSync resource '{resourceName}' was requested but was not found in the vault.");
        }

        if (Options.MissingResourceBehavior == SecretSyncMissingResourceBehavior.Warn)
        {
            Console.WriteLine($"[SecretSync] Resource '{resourceName}' was requested but was not found in the vault.");
        }
    }

    private static void Merge(IDictionary<string, string?> target, IReadOnlyDictionary<string, string?> source)
    {
        foreach ((string key, string? value) in source)
        {
            target[key] = value;
        }
    }
}
