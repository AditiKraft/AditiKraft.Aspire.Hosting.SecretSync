using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal static class UserSecretsMaterializer
{
    public static bool IsControlKey(string key) =>
        key.Equals("SecretSync", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("SecretSync:", StringComparison.OrdinalIgnoreCase);

    public static UserSecretsReadResult ReadResource(
        string resourceName,
        IReadOnlyDictionary<string, JsonNode?> values,
        IReadOnlyDictionary<string, string> baselineHashes)
    {
        Dictionary<string, JsonNode?> resource = new(StringComparer.OrdinalIgnoreCase);
        List<SecretSyncLocalEdit> edits = [];
        HashSet<string> currentKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, JsonNode? value) in values)
        {
            if (IsControlKey(key))
            {
                continue;
            }

            currentKeys.Add(key);
            baselineHashes.TryGetValue(key, out string? baselineHash);
            if (baselineHash is not null && SecretValueHasher.Matches(baselineHash, value))
            {
                continue;
            }

            resource[key] = value;
            edits.Add(new SecretSyncLocalEdit(resourceName, key, baselineHash));
        }

        bool hasMissingBaselineValues = baselineHashes.Keys.Any(key => !currentKeys.Contains(key));
        return new UserSecretsReadResult(resource, edits, hasMissingBaselineValues);
    }

    public static Dictionary<string, JsonNode?> ReadResourceValues(IReadOnlyDictionary<string, JsonNode?> values)
    {
        Dictionary<string, JsonNode?> resource = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, JsonNode? value) in values)
        {
            if (!IsControlKey(key))
            {
                resource[key] = value;
            }
        }

        return resource;
    }

    public static void Materialize(
        Dictionary<string, JsonNode?> current,
        IReadOnlyDictionary<string, JsonNode?> resourceValues,
        IReadOnlyDictionary<string, string> baselineHashes)
    {
        foreach ((string key, JsonNode? value) in resourceValues)
        {
            if (!IsControlKey(key))
            {
                if (value is JsonArray)
                {
                    foreach (string indexKey in VaultFlattener.GetArrayIndexKeys(key, current.Keys))
                    {
                        current.Remove(indexKey);
                    }
                }

                current[key] = value;
            }
        }

        foreach ((string key, JsonNode? value) in current.ToArray())
        {
            if (value is JsonArray && !IsControlKey(key) && !resourceValues.ContainsKey(key) &&
                VaultFlattener.GetArrayIndexKeys(key, resourceValues.Keys).Length > 0)
            {
                // A selected legacy revision supplies the entire indexed representation.
                current.Remove(key);
            }
        }

        foreach ((string key, string hash) in baselineHashes)
        {
            if (IsControlKey(key) || resourceValues.ContainsKey(key))
            {
                continue;
            }

            if (current.TryGetValue(key, out JsonNode? currentValue) && SecretValueHasher.Matches(hash, currentValue))
            {
                current.Remove(key);
            }
        }
    }
}

internal sealed record UserSecretsReadResult(
    IReadOnlyDictionary<string, JsonNode?> Resource,
    IReadOnlyList<SecretSyncLocalEdit> Edits,
    bool HasMissingBaselineValues);
