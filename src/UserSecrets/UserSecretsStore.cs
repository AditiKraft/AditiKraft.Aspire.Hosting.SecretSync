using System.Text.Json;
using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace AditiKraft.Aspire.Hosting.SecretSync.UserSecrets;

internal sealed class UserSecretsStore(
    SecretSyncOptions options,
    string? appHostSecretsPath,
    string? importedCopyPath = null)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<IReadOnlyDictionary<string, JsonNode?>> ReadAsync(CancellationToken cancellationToken) =>
        VaultFlattener.FlattenValues(await ReadDocumentAsync(appHostSecretsPath, cancellationToken));

    public static async Task<IReadOnlyDictionary<string, JsonNode?>> ReadAsync(
        string userSecretsId,
        CancellationToken cancellationToken)
    {
        string? path = GetLegacySecretsPath(userSecretsId);
        return VaultFlattener.FlattenValues(await ReadDocumentAsync(path, cancellationToken));
    }

    public static async Task MergeAsync(
        string userSecretsId,
        IReadOnlyDictionary<string, JsonNode?> values,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        string? path = GetLegacySecretsPath(userSecretsId);
        if (path is null)
        {
            return;
        }

        JsonObject document = await ReadDocumentAsync(path, cancellationToken);
        Dictionary<string, JsonNode?> current = VaultFlattener.FlattenValues(document);

        foreach ((string key, JsonNode? value) in values)
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

        await WriteValuesAsync(path, document, current, cancellationToken);
    }

    public static async Task WriteAsync(
        string userSecretsId,
        IReadOnlyDictionary<string, JsonNode?> values,
        CancellationToken cancellationToken)
    {
        string? path = GetLegacySecretsPath(userSecretsId);
        if (path is null)
        {
            return;
        }

        await WriteDocumentAsync(path, VaultFlattener.Unflatten(values), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> MergeVaultAsync(
        SecretSyncVault vault,
        IReadOnlyDictionary<string, string> baselineHashes,
        CancellationToken cancellationToken)
    {
        if (!options.WriteToUserSecrets || appHostSecretsPath is null)
        {
            return [];
        }

        JsonObject document = await ReadDocumentAsync(appHostSecretsPath, cancellationToken);
        Dictionary<string, JsonNode?> previous = VaultFlattener.FlattenValues(document);
        Dictionary<string, JsonNode?> updated = new(previous, StringComparer.OrdinalIgnoreCase);
        JsonObject? resource = vault.Resources.GetValueOrDefault(options.AppHostResourceName);
        UserSecretsMaterializer.Materialize(updated, VaultFlattener.FlattenValues(resource ?? []), baselineHashes);

        // Retire superseded copies before the primary file changes, so a retry can still identify them.
        if (importedCopyPath is not null)
        {
            await RemoveSupersededCopiesAsync(importedCopyPath, previous, updated, baselineHashes, cancellationToken);
        }

        await WriteValuesAsync(appHostSecretsPath, document, updated, cancellationToken);

        return importedCopyPath is null ? [appHostSecretsPath] : [importedCopyPath, appHostSecretsPath];
    }

    public static async Task MaterializeAsync(
        string userSecretsId,
        JsonObject? resource,
        IReadOnlyDictionary<string, string> baselineHashes,
        CancellationToken cancellationToken)
    {
        string? path = GetLegacySecretsPath(userSecretsId);
        if (path is null)
        {
            return;
        }

        JsonObject document = await ReadDocumentAsync(path, cancellationToken);
        Dictionary<string, JsonNode?> current = VaultFlattener.FlattenValues(document);
        UserSecretsMaterializer.Materialize(current, VaultFlattener.FlattenValues(resource ?? []), baselineHashes);
        await WriteValuesAsync(path, document, current, cancellationToken);
    }

    public static string? GetLegacySecretsPath(string userSecretsId)
    {
        if (string.IsNullOrWhiteSpace(userSecretsId))
        {
            return null;
        }

        return PathHelper.GetSecretsPathFromSecretsId(userSecretsId);
    }

    private static async Task RemoveSupersededCopiesAsync(
        string path,
        Dictionary<string, JsonNode?> previous,
        Dictionary<string, JsonNode?> updated,
        IReadOnlyDictionary<string, string> baselineHashes,
        CancellationToken cancellationToken)
    {
        JsonObject document = await ReadDocumentAsync(path, cancellationToken);
        Dictionary<string, JsonNode?> copies = VaultFlattener.FlattenValues(document);
        int originalCount = copies.Count;

        foreach ((string key, JsonNode? copy) in copies.ToArray())
        {
            if (UserSecretsMaterializer.IsControlKey(key) ||
                (updated.TryGetValue(key, out JsonNode? newValue) && JsonNode.DeepEquals(copy, newValue)))
            {
                continue;
            }

            bool matchedPrimary = previous.TryGetValue(key, out JsonNode? oldValue) && JsonNode.DeepEquals(copy, oldValue);
            bool matchedBaseline = baselineHashes.TryGetValue(key, out string? hash) && SecretValueHasher.Matches(hash, copy);
            if (matchedPrimary || matchedBaseline)
            {
                copies.Remove(key);
            }
        }

        if (copies.Count != originalCount)
        {
            await WriteValuesAsync(path, document, copies, cancellationToken);
        }
    }

    private static async Task<JsonObject> ReadDocumentAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        JsonDocumentOptions documentOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };
        await using FileStream stream = File.OpenRead(path);
        JsonNode? node = await JsonNode.ParseAsync(
            stream,
            nodeOptions: new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            documentOptions: documentOptions,
            cancellationToken: cancellationToken);
        return node as JsonObject ?? [];
    }

    private static async Task WriteValuesAsync(
        string path,
        JsonObject document,
        IReadOnlyDictionary<string, JsonNode?> values,
        CancellationToken cancellationToken)
    {
        Dictionary<string, JsonNode?> remaining = new(values, StringComparer.OrdinalIgnoreCase);
        UpdateExistingValues(document, null, remaining);

        if (document.Count == 0 || remaining.Keys.Any(document.ContainsKey))
        {
            document = VaultFlattener.Unflatten(values);
        }
        else
        {
            // Use Aspire's colon-separated keys for values that are new to an existing file.
            foreach ((string key, JsonNode? value) in remaining)
            {
                document[key] = value?.DeepClone();
            }
        }

        await WriteDocumentAsync(path, document, cancellationToken);
    }

    private static void UpdateExistingValues(JsonObject document, string? prefix, Dictionary<string, JsonNode?> remaining)
    {
        foreach ((string key, JsonNode? currentValue) in document.ToArray())
        {
            string fullKey = prefix is null ? key : $"{prefix}:{key}";
            if (currentValue is JsonObject { Count: > 0 } child)
            {
                UpdateExistingValues(child, fullKey, remaining);
                if (child.Count == 0)
                {
                    document.Remove(key);
                }
            }
            else if (!remaining.Remove(fullKey, out JsonNode? newValue))
            {
                document.Remove(key);
            }
            else if (!JsonNode.DeepEquals(currentValue, newValue))
            {
                document[key] = newValue?.DeepClone();
            }
        }
    }

    private static async Task WriteDocumentAsync(string path, JsonObject document, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileStreamOptions streamOptions = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (StreamWriter writer = new(tempPath, streamOptions))
            {
                await writer.WriteAsync(document.ToJsonString(_jsonOptions).AsMemory(), cancellationToken);
            }

            if (OperatingSystem.IsWindows() && File.Exists(path))
            {
                // Replace preserves the existing file's ACL.
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path, overwrite: true);
            }
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
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
}
