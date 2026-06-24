using Aspire.Hosting;

namespace AditiKraft.Aspire.Hosting.SecretSync;

public sealed class SecretSyncOptions
{
    public string BucketName { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string EncryptionKey { get; set; } = "";
    public bool AutoPush { get; set; } = true;
    public bool AutoPull { get; set; } = true;
    public SecretSyncPullMode PullMode { get; set; } = SecretSyncPullMode.Always;
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(15);
    public SecretSyncVersionMode VersionMode { get; set; } = SecretSyncVersionMode.Latest;
    public string PinnedRevision { get; set; } = "";

    public S3SecretSyncOptions S3 { get; } = new();

    public bool WriteToUserSecrets { get; set; }
    public bool ReadFromUserSecrets { get; set; } = true;
    public bool InitializeIfMissing { get; set; } = true;
    public bool FailWhenRemoteMissingAndLocalEmpty { get; set; } = true;

    public string UserSecretsId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string AppHostResourceName { get; set; } = "apphost";
    public string StateDirectory { get; set; } = "";
    public IList<ProjectUserSecretsSource> ProjectUserSecretsSources { get; } = [];

    public SecretSyncConflictMode ConflictMode { get; set; } = SecretSyncConflictMode.Fail;
    public SecretSyncPrecedence ConfigurationPrecedence { get; set; } =
        SecretSyncPrecedence.BelowEnvironmentAndCommandLine;

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public Argon2idOptions KeyDerivation { get; } = new();

    public SecretSyncOptions MapAppHostSecrets(string resourceName = "apphost")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        AppHostResourceName = resourceName;
        return this;
    }

    public SecretSyncOptions MapProjectUserSecrets<TProject>(string resourceName)
        where TProject : IProjectMetadata, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var metadata = new TProject();
        ProjectUserSecretsSources.Add(new ProjectUserSecretsSource(resourceName, metadata.ProjectPath));
        return this;
    }

    public SecretSyncOptions MapProjectUserSecrets(string resourceName, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        ProjectUserSecretsSources.Add(new ProjectUserSecretsSource(resourceName, projectPath));
        return this;
    }
}

public sealed record ProjectUserSecretsSource(string ResourceName, string ProjectPath);

public enum SecretSyncConflictMode
{
    Fail,
    PullWins,
    PushWins,
    MergeNonOverlapping
}

public enum SecretSyncPrecedence
{
    Highest,
    BelowEnvironmentAndCommandLine,
    Lowest
}

public enum SecretSyncPullMode
{
    Always,
    IfStale,
    Manual
}

public enum SecretSyncVersionMode
{
    Latest,
    Pinned
}

public sealed class S3SecretSyncOptions
{
    public string Endpoint { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
    public bool DisablePayloadSigning { get; set; } = true;
    public bool DisableDefaultChecksumValidation { get; set; } = true;
}

public sealed class Argon2idOptions
{
    public int MemorySizeKiB { get; set; } = 64 * 1024;
    public int Iterations { get; set; } = 3;
    public int DegreeOfParallelism { get; set; } = 2;
    public int SaltSizeBytes { get; set; } = 16;
    public int KeySizeBytes { get; set; } = 32;
}
