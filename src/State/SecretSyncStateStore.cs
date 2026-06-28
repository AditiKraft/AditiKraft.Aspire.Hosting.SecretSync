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
        SecretSyncState state = new();
        if (remote is not null)
        {
            state.Remote = remote;
        }

        foreach ((string resourceName, System.Text.Json.Nodes.JsonObject resource) in vault.Resources)
        {
            Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
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
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(state, _jsonOptions),
                cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
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
        string hash = SecretValueHasher.Hash(options.ResolveRemoteIdentity()).ToLowerInvariant();
        return hash[..Math.Min(hash.Length, 16)];
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp file; the original write failure is rethrown by the caller.
        }
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

        SecretSyncState normalized = new()
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
