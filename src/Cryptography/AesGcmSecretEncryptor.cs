using System.Security.Cryptography;
using System.Text;

namespace AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

internal sealed class AesGcmSecretEncryptor(SecretSyncOptions options)
{
    // Upper bounds applied to KDF parameters read from an untrusted vault blob.
    // The parameters drive an Argon2 allocation that runs before the ciphertext is
    // authenticated, so an unbounded MemorySizeKiB would let a malformed or hostile
    // blob exhaust memory. These caps are well above any sane real configuration.
    private const int MaxMemorySizeKiB = 1024 * 1024; // 1 GiB
    private const int MaxIterations = 20;
    private const int MaxDegreeOfParallelism = 16;

    private readonly Argon2idKeyDeriver _keyDeriver = new();

    public SecretEnvelope Encrypt(SecretSyncVault vault, string? parentRevision)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(options.KeyDerivation.SaltSizeBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Argon2idKeyDeriver.DeriveKey(options.EncryptionKey, salt, options.KeyDerivation);
        byte[] plaintext = [];

        try
        {
            string revision = CreateRevision();
            SecretPayload payload = new()
            {
                ProjectId = options.ProjectId,
                ContentHash = SecretPayloadSerializer.ComputeVaultHash(vault),
                Vault = vault
            };

            plaintext = SecretPayloadSerializer.SerializePayload(payload);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[16];

            SecretEnvelope envelope = new()
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

            using AesGcm aes = new(key, tag.Length);
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
        ValidateKdf(envelope.Kdf);
        byte[] salt = Convert.FromBase64String(envelope.Kdf.Salt);
        byte[] nonce = Convert.FromBase64String(envelope.Nonce);
        byte[] tag = Convert.FromBase64String(envelope.Tag);
        byte[] ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        byte[] key = Argon2idKeyDeriver.DeriveKey(options.EncryptionKey, salt, new Argon2idOptions
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
            using AesGcm aes = new(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, CreateAssociatedData(envelope));
            return SecretPayloadSerializer.DeserializePayload(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static byte[] SerializeEnvelope(SecretEnvelope envelope) =>
        SecretPayloadSerializer.SerializeEnvelope(envelope);

    private static void ValidateKdf(KdfEnvelope kdf)
    {
        if (kdf.MemorySizeKiB is < 8 or > MaxMemorySizeKiB)
        {
            throw new InvalidOperationException(
                $"SecretSync KDF memory size ({kdf.MemorySizeKiB} KiB) is outside the allowed range of 8 to {MaxMemorySizeKiB} KiB.");
        }

        if (kdf.Iterations is < 1 or > MaxIterations)
        {
            throw new InvalidOperationException(
                $"SecretSync KDF iteration count ({kdf.Iterations}) is outside the allowed range of 1 to {MaxIterations}.");
        }

        if (kdf.DegreeOfParallelism is < 1 or > MaxDegreeOfParallelism)
        {
            throw new InvalidOperationException(
                $"SecretSync KDF degree of parallelism ({kdf.DegreeOfParallelism}) is outside the allowed range of 1 to {MaxDegreeOfParallelism}.");
        }

        if (kdf.KeySizeBytes is not (16 or 24 or 32))
        {
            throw new InvalidOperationException(
                $"SecretSync KDF key size ({kdf.KeySizeBytes} bytes) must be 16, 24, or 32.");
        }
    }

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
