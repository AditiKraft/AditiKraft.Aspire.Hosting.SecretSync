using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync;

public sealed class SecretSyncHandle
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Resources { get; internal set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

    public string? RemoteETag { get; internal set; }
    public string? RemoteRevision { get; internal set; }
    public bool Pulled { get; internal set; }
    public bool InitializedRemote { get; internal set; }

    internal SecretSyncVault Vault { get; set; } = new();
    internal string? LastRemoteVaultHash { get; set; }

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
}
