using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class ProjectUserSecretsResolverTests
{
    [Fact]
    public void GetRequiredUserSecretsId_ReadsProjectFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string projectPath = Path.Combine(directory, "Sample.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <UserSecretsId>sample-secret-id</UserSecretsId>
              </PropertyGroup>
            </Project>
            """);

        string id = ProjectUserSecretsResolver.GetRequiredUserSecretsId(projectPath);

        Assert.Equal("sample-secret-id", id);
    }
}
