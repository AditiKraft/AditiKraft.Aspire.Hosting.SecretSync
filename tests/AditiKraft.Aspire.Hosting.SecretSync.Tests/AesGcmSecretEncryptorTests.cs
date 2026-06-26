using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class AesGcmSecretEncryptorTests
{
    [Fact]
    public void EncryptThenDecrypt_RoundTripsVault()
    {
        SecretSyncOptions options = new()
        {
            EncryptionKey = "unit-test-encryption-key",
            ProjectId = "unit-tests"
        };
        options.KeyDerivation.MemorySizeKiB = 1024;
        options.KeyDerivation.Iterations = 1;
        options.KeyDerivation.DegreeOfParallelism = 1;

        SecretSyncVault vault = new()
        {
            Resources =
            {
                ["apphost"] = JsonNode.Parse("""
                    {
                      "Parameters": {
                        "postgres-password": "dev-password"
                      }
                    }
                    """)!.AsObject(),
                ["api"] = JsonNode.Parse("""
                    {
                      "Stripe": {
                        "ApiKey": "sk_api"
                      }
                    }
                    """)!.AsObject()
            }
        };

        AesGcmSecretEncryptor encryptor = new(options);

        SecretEnvelope envelope = encryptor.Encrypt(vault, parentRevision: null);
        SecretPayload payload = encryptor.Decrypt(encryptor.SerializeEnvelope(envelope));

        Assert.Equal("unit-tests", payload.ProjectId);
        Assert.Equal(
            "dev-password",
            payload.Vault.Resources["apphost"]["Parameters"]!["postgres-password"]!.GetValue<string>());
        Assert.Equal(
            "sk_api",
            payload.Vault.Resources["api"]["Stripe"]!["ApiKey"]!.GetValue<string>());
    }

    [Fact]
    public void Decrypt_RejectsOversizedKdfMemoryBeforeDerivingKey()
    {
        SecretSyncOptions options = new() { EncryptionKey = "unit-test-encryption-key" };
        AesGcmSecretEncryptor encryptor = new(options);

        byte[] body = CreateEnvelopeBytesWithKdfMemory(2_000_000); // ~2 GiB request

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => encryptor.Decrypt(body));

        Assert.Contains("KDF memory size", exception.Message);
    }

    [Fact]
    public void Decrypt_RejectsInvalidKdfKeySize()
    {
        SecretSyncOptions options = new() { EncryptionKey = "unit-test-encryption-key" };
        AesGcmSecretEncryptor encryptor = new(options);

        byte[] body = CreateEnvelopeBytesWithKdfMemory(1024, keySizeBytes: 20);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => encryptor.Decrypt(body));

        Assert.Contains("key size", exception.Message);
    }

    private static byte[] CreateEnvelopeBytesWithKdfMemory(int memorySizeKiB, int keySizeBytes = 32)
    {
        SecretEnvelope envelope = new()
        {
            Kdf = new KdfEnvelope
            {
                Salt = Convert.ToBase64String(new byte[16]),
                MemorySizeKiB = memorySizeKiB,
                Iterations = 1,
                DegreeOfParallelism = 1,
                KeySizeBytes = keySizeBytes
            },
            Nonce = Convert.ToBase64String(new byte[12]),
            Tag = Convert.ToBase64String(new byte[16]),
            Ciphertext = "",
            Revision = "test-revision"
        };

        return SecretPayloadSerializer.SerializeEnvelope(envelope);
    }
}
