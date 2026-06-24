using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal sealed class ProjectUserSecretsStore(
    SecretSyncOptions options,
    UserSecretsStore userSecretsStore)
{
    public async Task<ProjectUserSecretsReadResult> ReadResourceChangesAsync(
        CancellationToken cancellationToken)
    {
        var resources = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        var edits = new List<SecretSyncLocalEdit>();

        foreach (ProjectUserSecretsSource source in options.ProjectUserSecretsSources)
        {
            string userSecretsId = ProjectUserSecretsResolver.GetRequiredUserSecretsId(source.ProjectPath);
            IReadOnlyDictionary<string, string?> values = await userSecretsStore.ReadAsync(userSecretsId, cancellationToken);
            UserSecretsReadResult result = UserSecretsMaterializer.ReadResource(source.ResourceName, values);

            if (!resources.TryGetValue(source.ResourceName, out Dictionary<string, string?>? resourceValues))
            {
                resourceValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                resources[source.ResourceName] = resourceValues;
            }

            foreach ((string key, string? value) in result.Resource)
            {
                resourceValues[key] = value;
            }

            edits.AddRange(result.Edits);
        }

        return new ProjectUserSecretsReadResult(resources, edits);
    }

    public async Task MergeVaultAsync(SecretSyncVault vault, CancellationToken cancellationToken)
    {
        foreach (ProjectUserSecretsSource source in options.ProjectUserSecretsSources)
        {
            string userSecretsId = ProjectUserSecretsResolver.GetRequiredUserSecretsId(source.ProjectPath);
            Dictionary<string, string?> current = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string? value) in await userSecretsStore.ReadAsync(userSecretsId, cancellationToken))
            {
                current[key] = value;
            }

            Dictionary<string, string?> resourceValues = new(StringComparer.OrdinalIgnoreCase);
            if (vault.Resources.TryGetValue(source.ResourceName, out System.Text.Json.Nodes.JsonObject? resource))
            {
                foreach ((string key, string? value) in VaultFlattener.Flatten(resource))
                {
                    resourceValues[key] = value;
                }
            }

            UserSecretsMaterializer.Materialize(current, resourceValues);
            await userSecretsStore.WriteAsync(userSecretsId, current, cancellationToken);
        }
    }
}

internal sealed record ProjectUserSecretsReadResult(
    IReadOnlyDictionary<string, Dictionary<string, string?>> Resources,
    IReadOnlyList<SecretSyncLocalEdit> Edits);
