using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;

namespace AditiKraft.Aspire.Hosting.SecretSync.Providers;

/// <summary>
/// Stores encrypted vault objects in a Git repository through the GitHub Contents
/// API. The Contents API hands us optimistic concurrency for free: each file read
/// returns its blob <c>sha</c>, which we round-trip as the ETag. A create-only
/// write omits the sha (GitHub returns 422 if the file already exists) and a
/// compare-and-swap write supplies the prior sha (GitHub returns 409 if it moved).
/// </summary>
public sealed class GitHubSecretSyncProvider : ISecretSyncProvider
{
    private const string DefaultApiBaseUrl = "https://api.github.com";
    private const string ApiVersion = "2022-11-28";
    private const string ProductName = "AditiKraft.Aspire.Hosting.SecretSync";

    // A single shared client avoids socket exhaustion. Authentication is set per
    // request from the active options, never baked into the client.
    private static readonly HttpClient SharedHttpClient = new();

    private readonly HttpClient _httpClient;

    public GitHubSecretSyncProvider()
        : this(SharedHttpClient)
    {
    }

    internal GitHubSecretSyncProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string Name => "GitHub";

    public async Task<SecretSyncRemoteObject?> GetAsync(
        SecretSyncProviderContext context,
        CancellationToken cancellationToken)
    {
        GitHubSecretSyncOptions gh = Validate(context);

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            gh,
            context.ObjectKey,
            query: $"?ref={Uri.EscapeDataString(ResolveBranch(gh))}");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);

        using JsonDocument document = await ReadJsonAsync(response, cancellationToken);
        JsonElement root = document.RootElement;

        return new SecretSyncRemoteObject(
            DecodeContent(root),
            root.TryGetProperty("sha", out JsonElement sha) ? sha.GetString() : null,
            Revision: null,
            LastModified: null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<SecretSyncRemoteWriteResult> PutAsync(
        SecretSyncProviderContext context,
        byte[] body,
        SecretSyncWriteCondition condition,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        GitHubSecretSyncOptions gh = Validate(context);

        // IfMatch -> compare-and-swap against that sha. IfMissing -> create only
        // (omit sha). Unconditional -> resolve the current sha so we update in place.
        string? sha = condition.IfMatchETag;
        if (string.IsNullOrWhiteSpace(sha) && !condition.IfMissing)
        {
            SecretSyncRemoteObject? existing = await GetAsync(context, cancellationToken);
            sha = existing?.ETag;
        }

        Dictionary<string, object?> payload = new()
        {
            ["message"] = CreateCommitMessage(context.ObjectKey, metadata),
            ["content"] = Convert.ToBase64String(body),
            ["branch"] = ResolveBranch(gh)
        };
        if (!string.IsNullOrWhiteSpace(sha))
        {
            payload["sha"] = sha;
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, gh, context.ObjectKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        bool conditional = condition.IfMissing || !string.IsNullOrWhiteSpace(condition.IfMatchETag);
        if (conditional &&
            (response.StatusCode == HttpStatusCode.Conflict ||
             response.StatusCode == HttpStatusCode.UnprocessableEntity))
        {
            throw new SecretSyncConflictException(
                "Remote secret blob changed while this AppHost was running. Pull, merge, then retry.");
        }

        await EnsureSuccessAsync(response, cancellationToken);

        using JsonDocument document = await ReadJsonAsync(response, cancellationToken);
        string? newSha = document.RootElement.TryGetProperty("content", out JsonElement content) &&
            content.TryGetProperty("sha", out JsonElement contentSha)
            ? contentSha.GetString()
            : null;

        return new SecretSyncRemoteWriteResult(
            newSha,
            metadata.TryGetValue("secret-sync-revision", out string? revision) ? revision : null,
            DateTimeOffset.UtcNow);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        GitHubSecretSyncOptions gh,
        string objectKey,
        string? query = null)
    {
        string baseUrl = string.IsNullOrWhiteSpace(gh.ApiBaseUrl)
            ? DefaultApiBaseUrl
            : gh.ApiBaseUrl.TrimEnd('/');
        string url = $"{baseUrl}/repos/{Uri.EscapeDataString(gh.Owner)}/{Uri.EscapeDataString(gh.Repository)}/contents/{EncodePath(objectKey)}{query}";

        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gh.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(ProductName, "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static byte[] DecodeContent(JsonElement root)
    {
        string encoding = root.TryGetProperty("encoding", out JsonElement enc)
            ? enc.GetString() ?? "base64"
            : "base64";
        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SecretSync GitHub provider received unsupported content encoding '{encoding}'. " +
                "The object may exceed the Contents API inline size limit.");
        }

        string content = root.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";

        // GitHub wraps base64 content at 60 characters per line.
        string normalized = content.Replace("\n", "").Replace("\r", "");
        return normalized.Length == 0 ? [] : Convert.FromBase64String(normalized);
    }

    private static string CreateCommitMessage(
        string objectKey,
        IReadOnlyDictionary<string, string> metadata)
    {
        return metadata.TryGetValue("secret-sync-revision", out string? revision) &&
            !string.IsNullOrWhiteSpace(revision)
            ? $"secretsync: {revision} {objectKey}"
            : $"secretsync: update {objectKey}";
    }

    private static string EncodePath(string objectKey) =>
        string.Join('/', objectKey
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    private static string ResolveBranch(GitHubSecretSyncOptions gh) =>
        string.IsNullOrWhiteSpace(gh.Branch) ? "main" : gh.Branch;

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"SecretSync GitHub request failed with {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
    }

    private static GitHubSecretSyncOptions Validate(SecretSyncProviderContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ObjectKey))
        {
            throw new InvalidOperationException("SecretSync GitHub ManifestKey is required.");
        }

        GitHubSecretSyncOptions gh = context.Options.GitHub;
        if (string.IsNullOrWhiteSpace(gh.Owner) ||
            string.IsNullOrWhiteSpace(gh.Repository) ||
            string.IsNullOrWhiteSpace(gh.Token))
        {
            throw new InvalidOperationException("SecretSync GitHub Owner, Repository, and Token are required.");
        }

        return gh;
    }
}
