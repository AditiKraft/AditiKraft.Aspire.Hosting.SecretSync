namespace AditiKraft.Aspire.Hosting.SecretSync;

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
