using System.Security.Cryptography;
using System.Text;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal static class SecretValueHasher
{
    public static string Hash(string? value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes);
    }
}
