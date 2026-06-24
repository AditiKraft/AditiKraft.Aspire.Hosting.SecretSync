using System.Text.Json;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

namespace AditiKraft.Aspire.Hosting.SecretSync.Remote;

internal static class SecretSyncManifestSerializer
{
    public static byte[] Serialize(SecretSyncManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, SecretPayloadSerializer.JsonOptions);

    public static SecretSyncManifest Deserialize(byte[] body) =>
        JsonSerializer.Deserialize<SecretSyncManifest>(body, SecretPayloadSerializer.JsonOptions) ??
        throw new InvalidOperationException("SecretSync manifest could not be deserialized.");
}
