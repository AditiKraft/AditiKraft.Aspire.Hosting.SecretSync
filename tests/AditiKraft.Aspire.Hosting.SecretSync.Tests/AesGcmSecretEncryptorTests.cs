using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class AesGcmSecretEncryptorTests
{
    [Fact]
    public void EncryptThenDecrypt_RoundTripsVault()
    {
        var options = new SecretSyncOptions
        {
            EncryptionKey = "unit-test-encryption-key",
            ProjectId = "unit-tests"
        };
        options.KeyDerivation.MemorySizeKiB = 1024;
        options.KeyDerivation.Iterations = 1;
        options.KeyDerivation.DegreeOfParallelism = 1;

        var vault = new SecretSyncVault
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

        var encryptor = new AesGcmSecretEncryptor(options);

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
}
