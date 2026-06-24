using System.Text.Json;
using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal sealed class UserSecretsStore(SecretSyncOptions options)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyDictionary<string, string?>> ReadAsync(CancellationToken cancellationToken)
    {
        string? path = TryGetSecretsPath(options.UserSecretsId);
        return await ReadPathAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string?>> ReadAsync(
        string userSecretsId,
        CancellationToken cancellationToken)
    {
        string? path = TryGetSecretsPath(userSecretsId);
        return await ReadPathAsync(path, cancellationToken);
    }

    public async Task MergeAsync(
        string userSecretsId,
        IReadOnlyDictionary<string, string?> values,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        string? path = TryGetSecretsPath(userSecretsId);
        if (path is null)
        {
            return;
        }

        Dictionary<string, string?> current = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in await ReadPathAsync(path, cancellationToken))
        {
            current[key] = value;
        }

        foreach ((string key, string? value) in values)
        {
            if (overwriteExisting)
            {
                current[key] = value;
            }
            else
            {
                current.TryAdd(key, value);
            }
        }

        await WritePathAsync(path, current, cancellationToken);
    }

    public async Task WriteAsync(
        string userSecretsId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken)
    {
        string? path = TryGetSecretsPath(userSecretsId);
        if (path is null)
        {
            return;
        }

        await WritePathAsync(path, values, cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, string?>> ReadPathAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        await using FileStream stream = File.OpenRead(path);
        JsonNode? node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        if (node is not JsonObject root)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        return VaultFlattener.Flatten(root);
    }

    public async Task MergeVaultAsync(
        SecretSyncVault vault,
        CancellationToken cancellationToken)
    {
        if (!options.WriteToUserSecrets)
        {
            return;
        }

        string? path = TryGetSecretsPath(options.UserSecretsId);
        if (path is null)
        {
            return;
        }

        Dictionary<string, string?> current = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in await ReadAsync(cancellationToken))
        {
            current[key] = value;
        }

        Dictionary<string, string?> appHostValues = new(StringComparer.OrdinalIgnoreCase);
        if (vault.Resources.TryGetValue(options.AppHostResourceName, out JsonObject? appHostResource))
        {
            foreach ((string key, string? value) in VaultFlattener.Flatten(appHostResource))
            {
                appHostValues[key] = value;
            }
        }

        UserSecretsMaterializer.Materialize(current, appHostValues);
        await WritePathAsync(path, current, cancellationToken);
    }

    private static async Task WritePathAsync(
        string path,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken)
    {
        JsonObject root = VaultFlattener.Unflatten(values);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, root.ToJsonString(_jsonOptions), cancellationToken);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
    }

    private static string? TryGetSecretsPath(string userSecretsId)
    {
        if (string.IsNullOrWhiteSpace(userSecretsId))
        {
            return null;
        }

        return PathHelper.GetSecretsPathFromSecretsId(userSecretsId);
    }
}
