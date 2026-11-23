using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal static class SecretValueHasher
{
    public static string Hash(string? value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes);
    }

    public static string HashValue(JsonNode? value) =>
        $"json:{Hash(SecretPayloadSerializer.CanonicalizeNode(value)?.ToJsonString() ?? "null")}";

    public static bool Matches(string hash, JsonNode? value)
    {
        if (hash.StartsWith("json:", StringComparison.Ordinal))
        {
            return string.Equals(hash, HashValue(value), StringComparison.Ordinal);
        }

        // Earlier baselines hashed the string values written by the old materializer.
        return value switch
        {
            null => string.Equals(hash, Hash(null), StringComparison.Ordinal),
            JsonValue scalar when scalar.TryGetValue(out string? text) =>
                string.Equals(hash, Hash(text), StringComparison.Ordinal),
            _ => false
        };
    }
}
