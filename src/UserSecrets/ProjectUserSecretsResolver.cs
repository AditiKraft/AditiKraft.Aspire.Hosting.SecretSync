using System.Xml.Linq;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal static class ProjectUserSecretsResolver
{
    public static string GetRequiredUserSecretsId(string projectPath)
    {
        if (!File.Exists(projectPath))
        {
            throw new InvalidOperationException($"SecretSync project user-secrets source '{projectPath}' does not exist.");
        }

        XDocument document = XDocument.Load(projectPath);
        string? id = document
            .Descendants("UserSecretsId")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                $"Project '{projectPath}' does not have a UserSecretsId. Run 'dotnet user-secrets init --project \"{projectPath}\"' or add <UserSecretsId> to the project file.");
        }

        return id;
    }
}
