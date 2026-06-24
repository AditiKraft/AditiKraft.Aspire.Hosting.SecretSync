using System.Text.Json;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.State;

internal sealed class SecretSyncStateStore(SecretSyncOptions options)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<SecretSyncState> ReadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new SecretSyncState();
        }

        await using FileStream stream = File.OpenRead(path);
        SecretSyncState? state = await JsonSerializer.DeserializeAsync<SecretSyncState>(
            stream,
            _jsonOptions,
            cancellationToken);

        return Normalize(state);
    }

    public async Task SaveVaultBaselineAsync(
        SecretSyncVault vault,
        SecretSyncRemoteState? remote,
        CancellationToken cancellationToken)
    {
        var state = new SecretSyncState();
        if (remote is not null)
        {
            state.Remote = remote;
        }

        foreach ((string resourceName, System.Text.Json.Nodes.JsonObject resource) in vault.Resources)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string? value) in VaultFlattener.Flatten(resource))
            {
                hashes[key] = SecretValueHasher.Hash(value);
            }

            state.Resources[resourceName] = hashes;
        }

        string path = GetStatePath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            tempPath,
            JsonSerializer.Serialize(state, _jsonOptions),
            cancellationToken);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
    }

    private string GetStatePath()
    {
        string directory = !string.IsNullOrWhiteSpace(options.StateDirectory)
            ? options.StateDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AditiKraft",
                "Aspire",
                "SecretSync",
                NormalizePathSegment(GetIdentity()),
                GetObjectKeyHash());

        return Path.Combine(directory, "state.json");
    }

    private string GetIdentity()
    {
        if (!string.IsNullOrWhiteSpace(options.UserSecretsId))
        {
            return options.UserSecretsId;
        }

        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            return options.ProjectId;
        }

        return "default";
    }

    private string GetObjectKeyHash()
    {
        string key = $"{options.BucketName}|{options.ObjectKey}";
        string hash = SecretValueHasher.Hash(key).ToLowerInvariant();
        return hash[..Math.Min(hash.Length, 16)];
    }

    private static string NormalizePathSegment(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        char[] invalidChars = Path.GetInvalidFileNameChars();

        foreach (char invalidChar in invalidChars)
        {
            normalized = normalized.Replace(invalidChar, '-');
        }

        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized;
    }

    private static SecretSyncState Normalize(SecretSyncState? state)
    {
        if (state is null)
        {
            return new SecretSyncState();
        }

        var normalized = new SecretSyncState
        {
            Version = state.Version,
            Remote = state.Remote ?? new SecretSyncRemoteState()
        };

        foreach ((string resourceName, Dictionary<string, string> hashes) in state.Resources)
        {
            normalized.Resources[resourceName] =
                new Dictionary<string, string>(hashes, StringComparer.OrdinalIgnoreCase);
        }

        return normalized;
    }
}
