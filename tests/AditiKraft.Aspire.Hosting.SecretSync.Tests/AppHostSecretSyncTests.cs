using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class AppHostSecretSyncTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("secretsync-apphost-");

    private string AppHostSecretsPath => Path.Combine(_directory.FullName, "Development.json");

    [Fact]
    public async Task AddSecretSyncAsync_ReadsAspireAppHostSecrets()
    {
        IDistributedApplicationBuilder builder = CreateBuilder();
        Assert.True(builder.UserSecretsManager.TrySetSecret("Parameters:shared", "local-value"));
        Assert.True(builder.UserSecretsManager.TrySetSecret("SecretSync:EncryptionKey", "bootstrap-key"));
        await File.AppendAllTextAsync(AppHostSecretsPath, "\n// Local development settings");

        SecretSyncHandle handle = await builder.AddSecretSyncAsync(options =>
        {
            options.PullMode = SecretSyncPullMode.Manual;
            options.StateDirectory = _directory.FullName;
        });

        Assert.Equal("local-value", handle.Resources["apphost"]["Parameters:shared"]);
        Assert.False(handle.Resources["apphost"].ContainsKey("SecretSync:EncryptionKey"));
    }

    [Theory]
    [InlineData("Staging", "run")]
    [InlineData("Production", "run")]
    [InlineData("Development", "publish")]
    public async Task AddSecretSyncAsync_OnlyRegistersForDevelopmentRuns(string environment, string operation)
    {
        string secretsPath = Path.Combine(_directory.FullName, $"{environment}.json");
        await File.WriteAllTextAsync(secretsPath, """{ "Parameters:shared": "local-only" }""");
        IDistributedApplicationBuilder builder = CreateBuilder(
            "--environment", environment, "--AppHost:Operation", operation, "--ASPIRE_SECRETS_FILE", secretsPath);

        SecretSyncHandle handle = await builder.AddSecretSyncAsync(options =>
        {
            options.PullMode = SecretSyncPullMode.Manual;
            options.StateDirectory = _directory.FullName;
        });

        Assert.False(handle.Pulled);
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(SecretSyncOptions));
    }

    [Theory]
    [InlineData("ASPIRE_")]
    [InlineData("DOTNET_")]
    public async Task DefaultPrecedence_PreservesPrefixedEnvironmentOverrides(string prefix)
    {
        string name = $"review-{Guid.NewGuid():N}";
        string key = $"Parameters:{name}";
        string variable = $"{prefix}Parameters__{name}";
        Environment.SetEnvironmentVariable(variable, "from-environment");

        try
        {
            await File.WriteAllTextAsync(AppHostSecretsPath, $$"""{ "{{key}}": "from-file" }""");
            IDistributedApplicationBuilder builder = CreateBuilder();

            SecretSyncConfigurationInjector.AddSyncedSecrets(
                builder.Configuration,
                new Dictionary<string, string?> { [key] = "from-sync" },
                SecretSyncPrecedence.BelowEnvironmentAndCommandLine);

            Assert.Equal("from-environment", builder.Configuration[key]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task MergeVaultAsync_RetiresAnImportedCopyWhenItsValueRotates()
    {
        const string key = "Parameters:rotated";
        const string firstVersion = """{ "Parameters:rotated": "first" }""";
        string importedCopyPath = Path.Combine(_directory.FullName, "secrets.json");
        await File.WriteAllTextAsync(importedCopyPath, firstVersion);
        await File.WriteAllTextAsync(AppHostSecretsPath, firstVersion);
        UserSecretsStore store = new(new SecretSyncOptions(), AppHostSecretsPath, importedCopyPath);
        SecretSyncVault rotated = new()
        {
            Resources = { ["apphost"] = new JsonObject { [key] = "second" } }
        };

        await store.MergeVaultAsync(rotated, new Dictionary<string, string> { [key] = SecretValueHasher.Hash("first") }, default);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(importedCopyPath))!.AsObject());

        await store.MergeVaultAsync(new SecretSyncVault(), new Dictionary<string, string> { [key] = SecretValueHasher.HashValue(JsonValue.Create("second")) }, default);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(AppHostSecretsPath))!.AsObject());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(importedCopyPath))!.AsObject());
    }

    [Fact]
    public async Task MergeVaultAsync_ReloadsBothPairedFilesAfterARetry()
    {
        const string key = "Parameters:shared";
        const string initialJson = """{ "Parameters:shared": "stale" }""";
        string importedCopyPath = Path.Combine(_directory.FullName, "secrets.json");
        await File.WriteAllTextAsync(importedCopyPath, initialJson);
        await File.WriteAllTextAsync(AppHostSecretsPath, initialJson);
        using ConfigurationManager configuration = new();
        configuration.AddJsonFile(importedCopyPath, optional: false, reloadOnChange: false);
        configuration.AddJsonFile(AppHostSecretsPath, optional: false, reloadOnChange: false);
        UserSecretsStore store = new(new SecretSyncOptions(), AppHostSecretsPath, importedCopyPath);
        Dictionary<string, string> baseline = new() { [key] = SecretValueHasher.Hash("stale") };

        File.Delete(AppHostSecretsPath);
        Directory.CreateDirectory(AppHostSecretsPath);
        Exception? error = await Record.ExceptionAsync(() => store.MergeVaultAsync(new SecretSyncVault(), baseline, default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(importedCopyPath))!.AsObject());
        Directory.Delete(AppHostSecretsPath);

        IReadOnlyList<string> paths = await store.MergeVaultAsync(new SecretSyncVault(), baseline, default);
        SecretSyncConfigurationInjector.ReloadSecretsFiles(configuration, paths);

        Assert.Null(configuration[key]);
    }

    [Theory]
    [InlineData(SecretSyncPrecedence.BelowEnvironmentAndCommandLine)]
    [InlineData(SecretSyncPrecedence.Lowest)]
    public void RemovingAConfigurationSourceDropsItsOverride(SecretSyncPrecedence precedence)
    {
        const string key = "Parameters:shared";
        using ConfigurationManager configuration = new();
        if (precedence == SecretSyncPrecedence.Lowest)
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "from-override" });
        }
        else
        {
            configuration.AddCommandLine(["--Parameters:shared", "from-override"]);
        }

        IConfigurationSource overrideSource = configuration.Sources.Last();
        SecretSyncConfigurationInjector.AddSyncedSecrets(
            configuration, new Dictionary<string, string?> { [key] = "from-sync" }, precedence);
        Assert.Equal("from-override", configuration[key]);

        Assert.True(configuration.Sources.Remove(overrideSource));

        Assert.Equal("from-sync", configuration[key]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncedValuesOverrideTheAspireSecretsFileWithCommandLineStillTakingPrecedence(bool addCustomJsonSource)
    {
        await File.WriteAllTextAsync(AppHostSecretsPath, """{ "Parameters:shared": "from-file" }""");
        IDistributedApplicationBuilder builder = CreateBuilder("--Parameters:overridden", "from-command-line");
        if (addCustomJsonSource)
        {
            builder.Configuration.AddJsonFile(Path.Combine(_directory.FullName, "local.json"), optional: true);
        }

        SecretSyncConfigurationInjector.AddSyncedSecrets(
            builder.Configuration,
            new Dictionary<string, string?>
            {
                ["Parameters:shared"] = "from-sync",
                ["Parameters:overridden"] = "from-sync"
            },
            SecretSyncPrecedence.BelowEnvironmentAndCommandLine);

        Assert.Equal("from-sync", builder.Configuration["Parameters:shared"]);
        Assert.Equal("from-command-line", builder.Configuration["Parameters:overridden"]);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private IDistributedApplicationBuilder CreateBuilder(params string[] args) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--environment", "Development", "--ASPIRE_SECRETS_FILE", AppHostSecretsPath, .. args],
            DisableDashboard = true
        });
}
