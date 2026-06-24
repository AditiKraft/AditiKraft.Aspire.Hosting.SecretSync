using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal static class UserSecretsMaterializer
{
    public static bool IsControlKey(string key) =>
        key.StartsWith("SecretSync:", StringComparison.OrdinalIgnoreCase);

    public static UserSecretsReadResult ReadResource(
        string resourceName,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string> baselineHashes)
    {
        var resource = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var edits = new List<SecretSyncLocalEdit>();

        foreach ((string key, string? value) in values)
        {
            if (IsControlKey(key))
            {
                continue;
            }

            baselineHashes.TryGetValue(key, out string? baselineHash);
            if (baselineHash is not null &&
                string.Equals(baselineHash, SecretValueHasher.Hash(value), StringComparison.Ordinal))
            {
                continue;
            }

            resource[key] = value;
            edits.Add(new SecretSyncLocalEdit(resourceName, key, value, baselineHash));
        }

        return new UserSecretsReadResult(resource, edits);
    }

    public static void Materialize(
        Dictionary<string, string?> current,
        IReadOnlyDictionary<string, string?> resourceValues,
        IReadOnlyDictionary<string, string> baselineHashes)
    {
        var resourceKeys = new HashSet<string>(resourceValues.Keys, StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string? value) in resourceValues)
        {
            current[key] = value;
        }

        foreach ((string key, string hash) in baselineHashes)
        {
            if (resourceKeys.Contains(key))
            {
                continue;
            }

            if (current.TryGetValue(key, out string? currentValue) &&
                string.Equals(hash, SecretValueHasher.Hash(currentValue), StringComparison.Ordinal))
            {
                current.Remove(key);
            }
        }
    }
}

internal sealed record UserSecretsReadResult(
    IReadOnlyDictionary<string, string?> Resource,
    IReadOnlyList<SecretSyncLocalEdit> Edits);
