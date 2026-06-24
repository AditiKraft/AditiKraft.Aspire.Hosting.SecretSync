using System.Text.Json.Serialization;

namespace AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

internal sealed class SecretPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public SecretSyncVault Vault { get; set; } = new();
    public Dictionary<string, string> DeletedKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class SecretEnvelope
{
    public string Format { get; set; } = "aditikraft.secretsync.v1";
    public string Algorithm { get; set; } = "AES-256-GCM";
    public KdfEnvelope Kdf { get; set; } = new();
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Revision { get; set; } = "";
    public string? ParentRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public UpdatedByEnvelope UpdatedBy { get; set; } = new();

    [JsonIgnore]
    public string MetadataRevision => Revision;
}

internal sealed class KdfEnvelope
{
    public string Name { get; set; } = "Argon2id";
    public int Version { get; set; } = 19;
    public string Salt { get; set; } = "";
    public int MemorySizeKiB { get; set; }
    public int Iterations { get; set; }
    public int DegreeOfParallelism { get; set; }
    public int KeySizeBytes { get; set; }
}

internal sealed class UpdatedByEnvelope
{
    public string MachineHash { get; set; } = "";
    public string UserHash { get; set; } = "";
    public string AppHost { get; set; } = "";
}
