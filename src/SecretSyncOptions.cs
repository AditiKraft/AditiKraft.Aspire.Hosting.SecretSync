using Aspire.Hosting;

namespace AditiKraft.Aspire.Hosting.SecretSync;

public sealed class SecretSyncOptions
{
    public string EncryptionKey { get; set; } = "";
    public bool AutoPush { get; set; } = true;
    public bool AutoPull { get; set; } = true;
    public SecretSyncPullMode PullMode { get; set; } = SecretSyncPullMode.Always;
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(15);
    public SecretSyncVersionMode VersionMode { get; set; } = SecretSyncVersionMode.Latest;
    public string PinnedRevision { get; set; } = "";

    public SecretSyncProviderType Provider { get; set; } = SecretSyncProviderType.S3;

    public S3SecretSyncOptions S3 { get; } = new();

    public GitHubSecretSyncOptions GitHub { get; } = new();

    public bool WriteToUserSecrets { get; set; } = true;
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

        TProject metadata = new();
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

    // Provider-neutral accessors so the coordinator does not hard-code S3. Each
    // backend stores its manifest path on its own options block, but the layout
    // (latest.json plus a sibling versions/ folder) is identical across providers.
    internal string ResolveManifestKey() => Provider switch
    {
        SecretSyncProviderType.GitHub => GitHub.ManifestKey,
        _ => S3.ManifestKey
    };

    internal void ApplyDefaultManifestKey(string value)
    {
        switch (Provider)
        {
            case SecretSyncProviderType.GitHub:
                GitHub.ManifestKey = value;
                break;
            default:
                S3.ManifestKey = value;
                break;
        }
    }

    // The container the object lives in. For S3 this is the bucket; for GitHub the
    // provider reads Owner/Repository directly, so this value is informational only.
    internal string ResolveContainer() => Provider switch
    {
        SecretSyncProviderType.GitHub => GitHub.Repository,
        _ => S3.BucketName
    };

    // Distinguishes the local state file per remote target so two different remotes
    // never share one state.json. The S3 form is kept byte-for-byte identical to the
    // original so existing S3 state files remain valid after upgrading. GitHub adds
    // owner/repository/branch/manifest because its S3 fields are blank and would
    // otherwise all collide on the same hash.
    internal string ResolveRemoteIdentity() => Provider switch
    {
        SecretSyncProviderType.GitHub =>
            $"GitHub|{GitHub.Owner}|{GitHub.Repository}|{(string.IsNullOrWhiteSpace(GitHub.Branch) ? "main" : GitHub.Branch)}|{GitHub.ManifestKey}",
        _ => $"{S3.BucketName}|{S3.ManifestKey}"
    };
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

public enum SecretSyncProviderType
{
    S3,
    GitHub
}

public sealed class S3SecretSyncOptions
{
    public string BucketName { get; set; } = "";
    public string ManifestKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
    public bool DisablePayloadSigning { get; set; } = true;
    public bool DisableDefaultChecksumValidation { get; set; } = true;
}

public sealed class GitHubSecretSyncOptions
{
    public string Owner { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string Token { get; set; } = "";

    // Path to latest.json inside the repo. Leave empty to derive
    // aspire/apphosts/{user-secrets-id}/latest.json, mirroring S3.ManifestKey.
    public string ManifestKey { get; set; } = "";

    // Override only for GitHub Enterprise Server, e.g.
    // https://github.yourcompany.com/api/v3. Empty uses https://api.github.com.
    public string ApiBaseUrl { get; set; } = "";
}

public sealed class Argon2idOptions
{
    public int MemorySizeKiB { get; set; } = 64 * 1024;
    public int Iterations { get; set; } = 3;
    public int DegreeOfParallelism { get; set; } = 2;
    public int SaltSizeBytes { get; set; } = 16;
    public int KeySizeBytes { get; set; } = 32;
}
