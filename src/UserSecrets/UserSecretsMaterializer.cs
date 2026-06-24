using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal static class UserSecretsMaterializer
{
    private const string MaterializedPrefix = "SecretSync:Materialized:";
    private const string MaterializedHashSuffix = ":Sha256";

    public static bool IsControlKey(string key) =>
        key.StartsWith("SecretSync:", StringComparison.OrdinalIgnoreCase);

    public static UserSecretsReadResult ReadResource(
        string resourceName,
        IReadOnlyDictionary<string, string?> values)
    {
        var resource = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var edits = new List<SecretSyncLocalEdit>();
        Dictionary<string, string> materializedHashes = ReadMaterializedHashes(values);

        foreach ((string key, string? value) in values)
        {
            if (IsControlKey(key))
            {
                continue;
            }

            if (materializedHashes.TryGetValue(key, out string? materializedHash) &&
                string.Equals(materializedHash, SecretValueHasher.Hash(value), StringComparison.Ordinal))
            {
                continue;
            }

            resource[key] = value;
            edits.Add(new SecretSyncLocalEdit(resourceName, key, value, materializedHash));
        }

        return new UserSecretsReadResult(resource, edits);
    }

    public static void Materialize(
        Dictionary<string, string?> current,
        IReadOnlyDictionary<string, string?> resourceValues)
    {
        Dictionary<string, string> materializedHashes = ReadMaterializedHashes(current);
        var resourceKeys = new HashSet<string>(resourceValues.Keys, StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string? value) in resourceValues)
        {
            current[key] = value;
            current[GetMaterializedHashKey(key)] = SecretValueHasher.Hash(value);
        }

        foreach ((string key, string hash) in materializedHashes)
        {
            if (resourceKeys.Contains(key))
            {
                continue;
            }

            string metadataKey = GetMaterializedHashKey(key);
            if (current.TryGetValue(key, out string? currentValue) &&
                string.Equals(hash, SecretValueHasher.Hash(currentValue), StringComparison.Ordinal))
            {
                current.Remove(key);
            }

            current.Remove(metadataKey);
        }
    }

    private static Dictionary<string, string> ReadMaterializedHashes(
        IReadOnlyDictionary<string, string?> values)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string metadataKey, string? hash) in values)
        {
            if (string.IsNullOrWhiteSpace(hash) ||
                !TryReadMaterializedHashKey(metadataKey, out string? key))
            {
                continue;
            }

            hashes[key] = hash;
        }

        return hashes;
    }

    private static string GetMaterializedHashKey(string key) =>
        $"{MaterializedPrefix}{Base64UrlEncode(key)}{MaterializedHashSuffix}";

    private static bool TryReadMaterializedHashKey(string metadataKey, out string key)
    {
        if (!metadataKey.StartsWith(MaterializedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !metadataKey.EndsWith(MaterializedHashSuffix, StringComparison.OrdinalIgnoreCase))
        {
            key = "";
            return false;
        }

        int start = MaterializedPrefix.Length;
        int length = metadataKey.Length - MaterializedPrefix.Length - MaterializedHashSuffix.Length;
        if (length <= 0)
        {
            key = "";
            return false;
        }

        return TryBase64UrlDecode(metadataKey.Substring(start, length), out key);
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out string decoded)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');

        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return true;
        }
        catch (FormatException)
        {
            decoded = "";
            return false;
        }
    }
}

internal sealed record UserSecretsReadResult(
    IReadOnlyDictionary<string, string?> Resource,
    IReadOnlyList<SecretSyncLocalEdit> Edits);
