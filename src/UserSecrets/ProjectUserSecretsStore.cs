using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using AditiKraft.Aspire.Hosting.SecretSync.State;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal sealed class ProjectUserSecretsStore(SecretSyncOptions options)
{
    public async Task<ProjectUserSecretsReadResult> ReadResourceChangesAsync(
        SecretSyncState state,
        CancellationToken cancellationToken)
    {
        Dictionary<string, Dictionary<string, JsonNode?>> resources = new(StringComparer.OrdinalIgnoreCase);
        List<SecretSyncLocalEdit> edits = [];
        bool hasMissingBaselineValues = false;

        foreach (ProjectUserSecretsSource source in options.ProjectUserSecretsSources)
        {
            string userSecretsId = ProjectUserSecretsResolver.GetRequiredUserSecretsId(source.ProjectPath);
            IReadOnlyDictionary<string, JsonNode?> values = await UserSecretsStore.ReadAsync(userSecretsId, cancellationToken);
            UserSecretsReadResult result = UserSecretsMaterializer.ReadResource(
                source.ResourceName,
                values,
                state.GetResourceHashes(source.ResourceName));

            if (!resources.TryGetValue(source.ResourceName, out Dictionary<string, JsonNode?>? resourceValues))
            {
                resourceValues = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
                resources[source.ResourceName] = resourceValues;
            }

            foreach ((string key, JsonNode? value) in result.Resource)
            {
                resourceValues[key] = value;
            }

            edits.AddRange(result.Edits);
            hasMissingBaselineValues |= result.HasMissingBaselineValues;
        }

        return new ProjectUserSecretsReadResult(resources, edits, hasMissingBaselineValues);
    }

    public async Task<IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>> ReadAllResourcesAsync(
        CancellationToken cancellationToken)
    {
        Dictionary<string, Dictionary<string, JsonNode?>> resources = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectUserSecretsSource source in options.ProjectUserSecretsSources)
        {
            string userSecretsId = ProjectUserSecretsResolver.GetRequiredUserSecretsId(source.ProjectPath);
            IReadOnlyDictionary<string, JsonNode?> values = await UserSecretsStore.ReadAsync(userSecretsId, cancellationToken);
            Dictionary<string, JsonNode?> resource = UserSecretsMaterializer.ReadResourceValues(values);

            if (!resources.TryGetValue(source.ResourceName, out Dictionary<string, JsonNode?>? resourceValues))
            {
                resourceValues = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
                resources[source.ResourceName] = resourceValues;
            }

            foreach ((string key, JsonNode? value) in resource)
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
            await UserSecretsStore.MaterializeAsync(
                userSecretsId,
                vault.Resources.GetValueOrDefault(source.ResourceName),
                state.GetResourceHashes(source.ResourceName),
                cancellationToken);
        }
    }
}

internal sealed record ProjectUserSecretsReadResult(
    IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> Resources,
    IReadOnlyList<SecretSyncLocalEdit> Edits,
    bool HasMissingBaselineValues);
