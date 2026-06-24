namespace AditiKraft.Aspire.Hosting.SecretSync;

public sealed record SecretSyncConflictContext(
    SecretSyncVault LocalVault,
    SecretSyncVault RemoteVault,
    string? LastSeenETag,
    string? RemoteETag);

public sealed record SecretSyncConflictDecision(SecretSyncConflictAction Action, SecretSyncVault? Vault = null);

public enum SecretSyncConflictAction
{
    Fail,
    UseLocal,
    UseRemote,
    UseMerged
}

public sealed class SecretSyncConflictException : InvalidOperationException
{
    public SecretSyncConflictException(string message)
        : base(message)
    {
    }

    public SecretSyncConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
