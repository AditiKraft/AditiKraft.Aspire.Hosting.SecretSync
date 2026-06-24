using System.Security.Cryptography;
using System.Text;

namespace AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

internal sealed class AesGcmSecretEncryptor(SecretSyncOptions options)
{
    private readonly Argon2idKeyDeriver _keyDeriver = new();

    public SecretEnvelope Encrypt(SecretSyncVault vault, string? parentRevision)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(options.KeyDerivation.SaltSizeBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = _keyDeriver.DeriveKey(options.EncryptionKey, salt, options.KeyDerivation);
        byte[] plaintext = [];

        try
        {
            string revision = CreateRevision();
            var payload = new SecretPayload
            {
                ProjectId = options.ProjectId,
                ContentHash = SecretPayloadSerializer.ComputeVaultHash(vault),
                Vault = vault
            };

            plaintext = SecretPayloadSerializer.SerializePayload(payload);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];

            var envelope = new SecretEnvelope
            {
                Kdf = new KdfEnvelope
                {
                    Salt = Convert.ToBase64String(salt),
                    MemorySizeKiB = options.KeyDerivation.MemorySizeKiB,
                    Iterations = options.KeyDerivation.Iterations,
                    DegreeOfParallelism = options.KeyDerivation.DegreeOfParallelism,
                    KeySizeBytes = options.KeyDerivation.KeySizeBytes
                },
                Nonce = Convert.ToBase64String(nonce),
                Revision = revision,
                ParentRevision = parentRevision,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = CreateUpdatedBy()
            };

            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, CreateAssociatedData(envelope));

            envelope.Ciphertext = Convert.ToBase64String(ciphertext);
            envelope.Tag = Convert.ToBase64String(tag);
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public SecretPayload Decrypt(byte[] body)
    {
        SecretEnvelope envelope = SecretPayloadSerializer.DeserializeEnvelope(body);
        byte[] salt = Convert.FromBase64String(envelope.Kdf.Salt);
        byte[] nonce = Convert.FromBase64String(envelope.Nonce);
        byte[] tag = Convert.FromBase64String(envelope.Tag);
        byte[] ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        byte[] key = _keyDeriver.DeriveKey(options.EncryptionKey, salt, new Argon2idOptions
        {
            MemorySizeKiB = envelope.Kdf.MemorySizeKiB,
            Iterations = envelope.Kdf.Iterations,
            DegreeOfParallelism = envelope.Kdf.DegreeOfParallelism,
            SaltSizeBytes = salt.Length,
            KeySizeBytes = envelope.Kdf.KeySizeBytes
        });
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, CreateAssociatedData(envelope));
            return SecretPayloadSerializer.DeserializePayload(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public byte[] SerializeEnvelope(SecretEnvelope envelope) =>
        SecretPayloadSerializer.SerializeEnvelope(envelope);

    private static byte[] CreateAssociatedData(SecretEnvelope envelope)
    {
        string aad = string.Join('|',
            envelope.Format,
            envelope.Algorithm,
            envelope.Kdf.Name,
            envelope.Kdf.Version,
            envelope.Kdf.MemorySizeKiB,
            envelope.Kdf.Iterations,
            envelope.Kdf.DegreeOfParallelism,
            envelope.Kdf.KeySizeBytes,
            envelope.Revision,
            envelope.ParentRevision ?? "");

        return Encoding.UTF8.GetBytes(aad);
    }

    private static string CreateRevision() =>
        $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssffff}-{Guid.NewGuid():N}";

    private static UpdatedByEnvelope CreateUpdatedBy()
    {
        string machine = Environment.MachineName;
        string user = Environment.UserName;

        return new UpdatedByEnvelope
        {
            MachineHash = SecretPayloadSerializer.HashText(machine),
            UserHash = SecretPayloadSerializer.HashText(user),
            AppHost = AppDomain.CurrentDomain.FriendlyName
        };
    }
}
