namespace AditiKraft.Aspire.Hosting.SecretSync.Remote;

internal sealed class SecretSyncManifest
{
    public string Format { get; set; } = "aditikraft.secretsync.manifest.v1";
    public int SchemaVersion { get; set; } = 1;
    public string LatestRevision { get; set; } = "";
    public string VaultObjectKey { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string? ParentRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed record SecretSyncRemoteManifest(
    SecretSyncManifest Manifest,
    string? ETag,
    DateTimeOffset? LastModified);
