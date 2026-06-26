using Konscious.Security.Cryptography;

namespace AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

internal sealed class Argon2idKeyDeriver
{
    public static byte[] DeriveKey(string encryptionKey, byte[] salt, Argon2idOptions options)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new InvalidOperationException("SecretSync EncryptionKey is required for encrypted remote sync.");
        }

        Argon2id argon2 = new(System.Text.Encoding.UTF8.GetBytes(encryptionKey))
        {
            Salt = salt,
            DegreeOfParallelism = options.DegreeOfParallelism,
            Iterations = options.Iterations,
            MemorySize = options.MemorySizeKiB
        };

        return argon2.GetBytes(options.KeySizeBytes);
    }
}
