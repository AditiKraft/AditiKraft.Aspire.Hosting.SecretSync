using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

internal static class SecretPayloadSerializer
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static byte[] SerializePayload(SecretPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    public static SecretPayload DeserializePayload(byte[] bytes) =>
        JsonSerializer.Deserialize<SecretPayload>(bytes, JsonOptions) ??
        throw new InvalidOperationException("SecretSync encrypted payload could not be deserialized.");

    public static byte[] SerializeEnvelope(SecretEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

    public static SecretEnvelope DeserializeEnvelope(byte[] body) =>
        JsonSerializer.Deserialize<SecretEnvelope>(body, JsonOptions) ??
        throw new InvalidOperationException("SecretSync envelope could not be deserialized.");

    public static string ComputeVaultHash(SecretSyncVault vault)
    {
        // Hash a canonical representation with keys sorted recursively. The vault is
        // repeatedly flattened and unflattened during merges, so the same content can
        // come back with a different key order. Hashing the raw serialization would
        // then change even when nothing changed, causing spurious pushes. Sorting keys
        // makes equal content always produce equal bytes.
        JsonObject canonical = new()
        {
            ["version"] = vault.Version
        };

        JsonObject resources = [];
        foreach (string resourceName in vault.Resources.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            resources[resourceName] = CanonicalizeNode(vault.Resources[resourceName]);
        }

        canonical["resources"] = resources;

        byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString());
        byte[] hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }

    public static string HashText(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Base64UrlEncode(hash);
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject sorted = [];
                foreach (string key in obj.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal))
                {
                    sorted[key] = CanonicalizeNode(obj[key]);
                }

                return sorted;
            case JsonArray array:
                JsonArray ordered = [];
                for (int i = 0; i < array.Count; i++)
                {
                    ordered.Add(CanonicalizeNode(array[i]));
                }

                return ordered;
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }
}
