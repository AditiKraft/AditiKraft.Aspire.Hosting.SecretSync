using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;
using AditiKraft.Aspire.Hosting.SecretSync.State;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal sealed class SecretSnapshotBuilder(
    SecretSyncOptions options,
    UserSecretsStore userSecretsStore,
    ProjectUserSecretsStore projectUserSecretsStore,
    SecretSyncStateStore stateStore)
{
    public async Task<SecretSyncVault> BuildLocalVaultAsync(CancellationToken cancellationToken)
    {
        SecretSyncLocalSnapshot snapshot = await BuildLocalSnapshotAsync(cancellationToken);
        return snapshot.Vault;
    }

    public async Task<SecretSyncLocalSnapshot> BuildLocalSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!options.ReadFromUserSecrets && options.ProjectUserSecretsSources.Count == 0)
        {
            return new SecretSyncLocalSnapshot(new SecretSyncVault(), [], HasMissingBaselineValues: false);
        }

        var vault = new SecretSyncVault();
        var edits = new List<SecretSyncLocalEdit>();
        bool hasMissingBaselineValues = false;
        SecretSyncState state = await stateStore.ReadAsync(cancellationToken);

        if (options.ReadFromUserSecrets)
        {
            IReadOnlyDictionary<string, string?> secrets = await userSecretsStore.ReadAsync(cancellationToken);
            UserSecretsReadResult appHost = UserSecretsMaterializer.ReadResource(
                options.AppHostResourceName,
                secrets,
                state.GetResourceHashes(options.AppHostResourceName));

            if (appHost.Resource.Count > 0)
            {
                vault.Resources[options.AppHostResourceName] = VaultFlattener.Unflatten(appHost.Resource);
            }

            edits.AddRange(appHost.Edits);
            hasMissingBaselineValues |= appHost.HasMissingBaselineValues;
        }

        ProjectUserSecretsReadResult projectSecrets =
            await projectUserSecretsStore.ReadResourceChangesAsync(state, cancellationToken);
        foreach ((string resourceName, Dictionary<string, string?> values) in projectSecrets.Resources)
        {
            if (values.Count == 0)
            {
                continue;
            }

            if (vault.Resources.TryGetValue(resourceName, out System.Text.Json.Nodes.JsonObject? existing))
            {
                Dictionary<string, string?> merged = VaultFlattener.Flatten(existing);
                foreach ((string key, string? value) in values)
                {
                    merged[key] = value;
                }

                vault.Resources[resourceName] = VaultFlattener.Unflatten(merged);
            }
            else
            {
                vault.Resources[resourceName] = VaultFlattener.Unflatten(values);
            }
        }

        edits.AddRange(projectSecrets.Edits);
        hasMissingBaselineValues |= projectSecrets.HasMissingBaselineValues;
        return new SecretSyncLocalSnapshot(vault, edits, hasMissingBaselineValues);
    }

    public async Task<SecretSyncVault> BuildFullLocalVaultAsync(CancellationToken cancellationToken)
    {
        var vault = new SecretSyncVault();

        if (options.ReadFromUserSecrets)
        {
            IReadOnlyDictionary<string, string?> secrets = await userSecretsStore.ReadAsync(cancellationToken);
            Dictionary<string, string?> appHost = UserSecretsMaterializer.ReadResourceValues(secrets);
            if (appHost.Count > 0)
            {
                vault.Resources[options.AppHostResourceName] = VaultFlattener.Unflatten(appHost);
            }
        }

        IReadOnlyDictionary<string, Dictionary<string, string?>> projectResources =
            await projectUserSecretsStore.ReadAllResourcesAsync(cancellationToken);
        foreach ((string resourceName, Dictionary<string, string?> values) in projectResources)
        {
            if (values.Count > 0)
            {
                vault.Resources[resourceName] = VaultFlattener.Unflatten(values);
            }
        }

        return vault;
    }
}
