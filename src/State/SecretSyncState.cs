namespace AditiKraft.Aspire.Hosting.SecretSync.State;

internal sealed class SecretSyncState
{
    public int Version { get; set; } = 1;
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
