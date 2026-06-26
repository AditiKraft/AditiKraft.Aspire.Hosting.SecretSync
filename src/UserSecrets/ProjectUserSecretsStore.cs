using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.State;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal sealed class ProjectUserSecretsStore(
    SecretSyncOptions options,
    UserSecretsStore userSecretsStore)
{
    public async Task<ProjectUserSecretsReadResult> ReadResourceChangesAsync(
        SecretSyncState state,
        CancellationToken cancellationToken)
    {
        Dictionary<string, Dictionary<string, string?>> resources = new(StringComparer.OrdinalIgnoreCase);
        List<SecretSyncLocalEdit> edits = [];
        bool hasMissingBaselineValues = false;

        foreach (ProjectUserSecretsSource source in options.ProjectUserSecretsSources)
        {
            string userSecretsId = ProjectUserSecretsResolver.GetRequiredUserSecretsId(source.ProjectPath);
            IReadOnlyDictionary<string, string?> values = await UserSecretsStore.ReadAsync(userSecretsId, cancellationToken);
            UserSecretsReadResult result = UserSecretsMaterializer.ReadResource(
                source.ResourceName,
                values,
                state.GetResourceHashes(source.ResourceName));

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
            hasMissingBaselineValues |= result.HasMissingBaselineValues;
        }

        return new ProjectUserSecretsReadResult(resources, edits, hasMissingBaselineValues);
    }

    public async Task<IReadOnlyDictionary<string, Dictionary<string, string?>>> ReadAllResourcesAsync(
        CancellationToken cancellationToken)
    {
        Dictionary<string, Dictionary<string, string?>> resources = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectUserSecretsSource source in options.ProjectUserSecretsSources)
        {
            string userSecretsId = ProjectUserSecretsResolver.GetRequiredUserSecretsId(source.ProjectPath);
            IReadOnlyDictionary<string, string?> values = await UserSecretsStore.ReadAsync(userSecretsId, cancellationToken);
            Dictionary<string, string?> resource = UserSecretsMaterializer.ReadResourceValues(values);

            if (!resources.TryGetValue(source.ResourceName, out Dictionary<string, string?>? resourceValues))
            {
                resourceValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                resources[source.ResourceName] = resourceValues;
            }

            foreach ((string key, string? value) in resource)
            {
                resourceValues[key] = value;
            }
        }

        return resources;
    }

    public async Task MergeVaultAsync(
        SecretSyncVault vault,
        SecretSyncState state,
        CancellationToken cancellationToken)
    {
        foreach (ProjectUserSecretsSource source in options.ProjectUserSecretsSources)
        {
            string userSecretsId = ProjectUserSecretsResolver.GetRequiredUserSecretsId(source.ProjectPath);
            Dictionary<string, string?> current = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string? value) in await UserSecretsStore.ReadAsync(userSecretsId, cancellationToken))
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

            UserSecretsMaterializer.Materialize(
                current,
                resourceValues,
                state.GetResourceHashes(source.ResourceName));
            await UserSecretsStore.WriteAsync(userSecretsId, current, cancellationToken);
        }
    }
}

internal sealed record ProjectUserSecretsReadResult(
    IReadOnlyDictionary<string, Dictionary<string, string?>> Resources,
    IReadOnlyList<SecretSyncLocalEdit> Edits,
    bool HasMissingBaselineValues);
