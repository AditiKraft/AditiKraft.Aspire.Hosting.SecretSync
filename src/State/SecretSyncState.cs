namespace AditiKraft.Aspire.Hosting.SecretSync.State;

internal sealed class SecretSyncState
{
    public int Version { get; set; } = 1;
    public SecretSyncRemoteState Remote { get; set; } = new();

    public Dictionary<string, Dictionary<string, string>> Resources { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> GetResourceHashes(string resourceName)
    {
        if (Resources.TryGetValue(resourceName, out Dictionary<string, string>? hashes))
        {
            return hashes;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class SecretSyncRemoteState
{
    public string Revision { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string ManifestObjectKey { get; set; } = "";
    public string? ManifestETag { get; set; }
    public string VaultObjectKey { get; set; } = "";
    public DateTimeOffset? CheckedAt { get; set; }
}
