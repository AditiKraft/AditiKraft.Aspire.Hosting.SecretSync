using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(vault, JsonOptions);
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
}
