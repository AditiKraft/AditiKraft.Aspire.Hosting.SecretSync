using System.Text.Json.Nodes;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal static class SecretSyncVaultMerger
{
    public static SecretSyncVault MergeRemoteWithLocal(
        SecretSyncVault remote,
        SecretSyncVault local,
        SecretSyncConflictMode conflictMode,
        IReadOnlyList<SecretSyncLocalEdit>? localEdits = null)
    {
        if (local.IsEmpty)
        {
            return remote;
        }

        if (remote.IsEmpty)
        {
            return local;
        }

        SecretSyncVault merged = new()
        {
            Version = remote.Version
        };
        Dictionary<string, SecretSyncLocalEdit> localEditIndex = CreateLocalEditIndex(localEdits);

        foreach ((string resourceName, JsonObject remoteResource) in remote.Resources)
        {
            merged.Resources[resourceName] = remoteResource.DeepClone().AsObject();
        }

        foreach ((string resourceName, JsonObject localResource) in local.Resources)
        {
            if (merged.Resources.TryGetValue(resourceName, out JsonObject? remoteResource))
            {
                merged.Resources[resourceName] = MergeObjects(
                    $"resource '{resourceName}'",
                    remoteResource,
                    localResource,
                    conflictMode,
                    resourceName,
                    localEditIndex);
            }
            else
            {
                merged.Resources[resourceName] = localResource.DeepClone().AsObject();
            }
        }

        return merged;
    }

    private static JsonObject MergeObjects(
        string area,
        JsonObject remote,
        JsonObject local,
        SecretSyncConflictMode conflictMode,
        string resourceName,
        IReadOnlyDictionary<string, SecretSyncLocalEdit> localEditIndex)
    {
        Dictionary<string, string?> remoteValues = VaultFlattener.Flatten(remote);
        Dictionary<string, string?> localValues = VaultFlattener.Flatten(local);
        Dictionary<string, string?> merged = new(remoteValues, StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string? localValue) in localValues)
        {
            if (!remoteValues.TryGetValue(key, out string? remoteValue))
            {
                merged[key] = localValue;
                continue;
            }

            if (string.Equals(remoteValue, localValue, StringComparison.Ordinal))
            {
                merged[key] = localValue;
                continue;
            }

            if (IsSafeLocalEdit(resourceName, key, remoteValue, localEditIndex))
            {
                merged[key] = localValue;
                continue;
            }

            merged[key] = conflictMode switch
            {
                SecretSyncConflictMode.PullWins => remoteValue,
                SecretSyncConflictMode.PushWins => localValue,
                _ => throw new SecretSyncConflictException(
                                        $"SecretSync conflict in {area} for key '{key}'. Local and remote values differ. Resolve the local secrets.json file or choose PullWins/PushWins explicitly."),
            };
        }

        return VaultFlattener.Unflatten(merged);
    }

    private static Dictionary<string, SecretSyncLocalEdit> CreateLocalEditIndex(
        IReadOnlyList<SecretSyncLocalEdit>? localEdits)
    {
        Dictionary<string, SecretSyncLocalEdit> index = new(StringComparer.OrdinalIgnoreCase);
        if (localEdits is null)
        {
            return index;
        }

        foreach (SecretSyncLocalEdit edit in localEdits)
        {
            index[CreateLocalEditIndexKey(edit.ResourceName, edit.Key)] = edit;
        }

        return index;
    }

    private static bool IsSafeLocalEdit(
        string resourceName,
        string key,
        string? remoteValue,
        IReadOnlyDictionary<string, SecretSyncLocalEdit> localEditIndex)
    {
        if (!localEditIndex.TryGetValue(CreateLocalEditIndexKey(resourceName, key), out SecretSyncLocalEdit? edit) ||
            string.IsNullOrWhiteSpace(edit.BaselineHash))
        {
            return false;
        }

        return string.Equals(
            edit.BaselineHash,
            SecretValueHasher.Hash(remoteValue),
            StringComparison.Ordinal);
    }

    private static string CreateLocalEditIndexKey(string resourceName, string key) =>
        $"{resourceName}\0{key}";
}
