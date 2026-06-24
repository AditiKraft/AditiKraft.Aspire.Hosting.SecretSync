using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class ProjectUserSecretsStoreTests : IDisposable
{
    private readonly List<string> _secretDirectories = [];

    [Fact]
    public async Task MergeVaultAsync_MaterializesOnlyMappedProjectResource()
    {
        string userSecretsId = $"secretsync-test-{Guid.NewGuid():N}";
        string projectPath = await CreateProjectAsync(userSecretsId);
        TrackUserSecretsDirectory(userSecretsId);

        var options = new SecretSyncOptions();
        options.MapProjectUserSecrets("api", projectPath);

        var userSecretsStore = new UserSecretsStore(options);
        var projectStore = new ProjectUserSecretsStore(options, userSecretsStore);

        var vault = new SecretSyncVault
        {
            Resources =
            {
                ["apphost"] = JsonNode.Parse("""{ "Parameters": { "postgres-password": "pg-password" } }""")!.AsObject(),
                ["api"] = JsonNode.Parse("""
                    {
                      "Stripe": {
                        "ApiKey": "sk-api",
                        "Mode": "api"
                      }
                    }
                    """)!.AsObject(),
                ["web"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk-web" } }""")!.AsObject()
            }
        };

        await projectStore.MergeVaultAsync(vault, CancellationToken.None);

        IReadOnlyDictionary<string, string?> values =
            await userSecretsStore.ReadAsync(userSecretsId, CancellationToken.None);

        Assert.Equal("sk-api", values["Stripe:ApiKey"]);
        Assert.Equal("api", values["Stripe:Mode"]);
        Assert.False(values.ContainsKey("Parameters:postgres-password"));
        Assert.DoesNotContain("sk-web", values.Values);
        Assert.Contains(values.Keys, key => key.StartsWith("SecretSync:Materialized:", StringComparison.Ordinal));

        ProjectUserSecretsReadResult read = await projectStore.ReadResourceChangesAsync(CancellationToken.None);

        Assert.True(read.Resources.TryGetValue("api", out Dictionary<string, string?>? apiResource));
        Assert.Empty(apiResource);

        await userSecretsStore.MergeAsync(
            userSecretsId,
            new Dictionary<string, string?> { ["Stripe:ApiKey"] = "sk-api-local-edit" },
            overwriteExisting: true,
            CancellationToken.None);

        read = await projectStore.ReadResourceChangesAsync(CancellationToken.None);

        Assert.Equal("sk-api-local-edit", read.Resources["api"]["Stripe:ApiKey"]);
        Assert.Single(read.Edits);
        Assert.Equal("api", read.Edits[0].ResourceName);
    }

    public void Dispose()
    {
        foreach (string directory in _secretDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private async Task<string> CreateProjectAsync(string userSecretsId)
    {
        string directory = Directory.CreateTempSubdirectory("secretsync-project-").FullName;
        string projectPath = Path.Combine(directory, "Api.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <UserSecretsId>{{userSecretsId}}</UserSecretsId>
              </PropertyGroup>
            </Project>
            """);

        return projectPath;
    }

    private void TrackUserSecretsDirectory(string userSecretsId)
    {
        string path = PathHelper.GetSecretsPathFromSecretsId(userSecretsId);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _secretDirectories.Add(directory);
        }
    }
}
