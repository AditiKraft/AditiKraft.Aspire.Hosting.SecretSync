using Microsoft.Extensions.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class SecretSyncOptionsTests
{
    [Fact]
    public void AppHostSample_LeavesManifestKeyBlankForDerivedDefault()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(
                FindRepositoryRoot(),
                "aspire",
                "AditiKraft.Aspire.Hosting.SecretSync.AppHost",
                "appsettings.Development.json"))
            .Build();

        Assert.True(string.IsNullOrWhiteSpace(configuration["SecretSync:S3:ManifestKey"]));
    }

    [Fact]
    public void Defaults_EnableNormalStartupAndShutdownSync()
    {
        SecretSyncOptions options = new();

        Assert.True(options.AutoPull);
        Assert.True(options.AutoPush);
        Assert.True(options.WriteToUserSecrets);
        Assert.Equal(SecretSyncPullMode.Always, options.PullMode);
        Assert.Equal(SecretSyncVersionMode.Latest, options.VersionMode);
    }

    [Fact]
    public void Bind_PopulatesEncryptionKeyAndNestedS3Options()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EncryptionKey"] = "from-config",
                ["S3:BucketName"] = "dev-secrets",
                ["S3:Endpoint"] = "https://s3.example.com",
                ["S3:AccessKeyId"] = "access-key",
                ["S3:SecretAccessKey"] = "secret-key",
                ["S3:Region"] = "auto"
            })
            .Build();

        SecretSyncOptions options = new();
        configuration.Bind(options);

        Assert.Equal("from-config", options.EncryptionKey);
        Assert.Equal("dev-secrets", options.S3.BucketName);
        Assert.Equal("https://s3.example.com", options.S3.Endpoint);
        Assert.Equal("access-key", options.S3.AccessKeyId);
        Assert.Equal("secret-key", options.S3.SecretAccessKey);
        Assert.Equal("auto", options.S3.Region);
    }

    [Fact]
    public void Bind_LeavesDefaultsWhenSectionOmitsValues()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["S3:BucketName"] = "dev-secrets"
            })
            .Build();

        SecretSyncOptions options = new();
        configuration.Bind(options);

        Assert.Equal("dev-secrets", options.S3.BucketName);
        Assert.Equal("us-east-1", options.S3.Region);
        Assert.True(options.AutoPull);
        Assert.True(options.AutoPush);
    }

    [Fact]
    public void ResolveRemoteIdentity_S3FormStaysUnchangedForBackwardCompatibility()
    {
        SecretSyncOptions options = new();
        options.S3.BucketName = "dev-secrets";
        options.S3.ManifestKey = "aspire/apphosts/app/latest.json";

        Assert.Equal("dev-secrets|aspire/apphosts/app/latest.json", options.ResolveRemoteIdentity());
    }

    [Fact]
    public void ResolveRemoteIdentity_DistinguishesGitHubRemotesThatWouldShareBlankS3Fields()
    {
        SecretSyncOptions first = CreateGitHubOptions("acme", "dev-secrets", "main");
        SecretSyncOptions second = CreateGitHubOptions("acme", "dev-secrets", "release");
        SecretSyncOptions third = CreateGitHubOptions("other-org", "dev-secrets", "main");

        // The original hash input was "S3.BucketName|S3.ManifestKey", which is "|" for
        // every GitHub remote. The provider-aware identity must keep them distinct.
        Assert.NotEqual("|", first.ResolveRemoteIdentity());
        Assert.NotEqual(first.ResolveRemoteIdentity(), second.ResolveRemoteIdentity());
        Assert.NotEqual(first.ResolveRemoteIdentity(), third.ResolveRemoteIdentity());
    }

    [Fact]
    public void ResolveRemoteIdentity_GitHubTreatsBlankBranchAsMain()
    {
        SecretSyncOptions blankBranch = CreateGitHubOptions("acme", "dev-secrets", "");
        SecretSyncOptions mainBranch = CreateGitHubOptions("acme", "dev-secrets", "main");

        Assert.Equal(mainBranch.ResolveRemoteIdentity(), blankBranch.ResolveRemoteIdentity());
    }

    private static SecretSyncOptions CreateGitHubOptions(string owner, string repository, string branch)
    {
        SecretSyncOptions options = new()
        {
            Provider = SecretSyncProviderType.GitHub
        };
        options.GitHub.Owner = owner;
        options.GitHub.Repository = repository;
        options.GitHub.Branch = branch;
        return options;
    }

    private static string FindRepositoryRoot()
    {
        string directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "AditiKraft.Aspire.Hosting.SecretSync.slnx")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? "";
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
