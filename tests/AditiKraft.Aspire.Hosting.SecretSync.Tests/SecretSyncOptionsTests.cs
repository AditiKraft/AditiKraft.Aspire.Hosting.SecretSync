using Microsoft.Extensions.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class SecretSyncOptionsTests
{
    [Fact]
    public void AppHostSample_LeavesObjectKeyBlankForDerivedDefault()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(
                FindRepositoryRoot(),
                "AditiKraft.Aspire.Hosting.SecretSync.AppHost",
                "appsettings.Development.json"))
            .Build();

        Assert.True(string.IsNullOrWhiteSpace(configuration["SecretSync:ObjectKey"]));
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
