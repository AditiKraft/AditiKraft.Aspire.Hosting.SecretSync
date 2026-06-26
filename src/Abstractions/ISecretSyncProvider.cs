namespace AditiKraft.Aspire.Hosting.SecretSync.Abstractions;

public interface ISecretSyncProvider
{
    public string Name { get; }

    public Task<SecretSyncRemoteObject?> GetAsync(
        SecretSyncProviderContext context,
        CancellationToken cancellationToken);

    public Task<SecretSyncRemoteWriteResult> PutAsync(
        SecretSyncProviderContext context,
        byte[] body,
        SecretSyncWriteCondition condition,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken);
}

public sealed record SecretSyncProviderContext(
    string BucketName,
    string ObjectKey,
    SecretSyncOptions Options);

public sealed record SecretSyncRemoteObject(
    byte[] Body,
    string? ETag,
    string? Revision,
    DateTimeOffset? LastModified,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record SecretSyncWriteCondition(
    string? IfMatchETag,
    bool IfMissing);

public sealed record SecretSyncRemoteWriteResult(
    string? ETag,
    string? Revision,
    DateTimeOffset WrittenAt);
