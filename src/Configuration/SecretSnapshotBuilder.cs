using AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal sealed class SecretSnapshotBuilder(
    SecretSyncOptions options,
    UserSecretsStore userSecretsStore,
    ProjectUserSecretsStore projectUserSecretsStore)
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
            return new SecretSyncLocalSnapshot(new SecretSyncVault(), []);
        }

        var vault = new SecretSyncVault();
        var edits = new List<SecretSyncLocalEdit>();

        if (options.ReadFromUserSecrets)
        {
            IReadOnlyDictionary<string, string?> secrets = await userSecretsStore.ReadAsync(cancellationToken);
            UserSecretsReadResult appHost = UserSecretsMaterializer.ReadResource(
                options.AppHostResourceName,
                secrets);

            if (appHost.Resource.Count > 0)
            {
                vault.Resources[options.AppHostResourceName] = VaultFlattener.Unflatten(appHost.Resource);
            }

            edits.AddRange(appHost.Edits);
        }

        ProjectUserSecretsReadResult projectSecrets =
            await projectUserSecretsStore.ReadResourceChangesAsync(cancellationToken);
        foreach ((string resourceName, Dictionary<string, string?> values) in projectSecrets.Resources)
        {
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
        return new SecretSyncLocalSnapshot(vault, edits);
    }
}
