using System.Net;
using System.Text;
using System.Text.Json;
using AditiKraft.Aspire.Hosting.SecretSync.Abstractions;
using AditiKraft.Aspire.Hosting.SecretSync.Providers;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class GitHubSecretSyncProviderTests
{
    [Fact]
    public async Task GetAsync_ReturnsNullWhenFileMissing()
    {
        StubHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        GitHubSecretSyncProvider provider = new(new HttpClient(handler));

        SecretSyncRemoteObject? result = await provider.GetAsync(
            CreateContext("aspire/apphosts/app/latest.json"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_DecodesBase64ContentAndMapsShaToETag()
    {
        byte[] expected = Encoding.UTF8.GetBytes("encrypted-vault-bytes");
        string base64 = Convert.ToBase64String(expected);
        // GitHub wraps base64 across lines; make sure we tolerate the newlines.
        string wrapped = base64.Insert(Math.Min(4, base64.Length), "\n");

        StubHandler handler = new(_ =>
        {
            string json = JsonSerializer.Serialize(new
            {
                content = wrapped,
                encoding = "base64",
                sha = "blob-sha-123"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });
        GitHubSecretSyncProvider provider = new(new HttpClient(handler));

        SecretSyncRemoteObject? result = await provider.GetAsync(
            CreateContext("aspire/apphosts/app/latest.json"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Body);
        Assert.Equal("blob-sha-123", result.ETag);
    }

    [Fact]
    public async Task GetAsync_RequestsBranchRefAndAuthHeader()
    {
        HttpRequestMessage? captured = null;
        StubHandler handler = new(request =>
        {
            captured = request;
            string json = JsonSerializer.Serialize(new { content = "", encoding = "base64", sha = "s" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        GitHubSecretSyncProvider provider = new(new HttpClient(handler));

        await provider.GetAsync(
            CreateContext("aspire/apphosts/app/latest.json"),
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("/repos/acme/dev-secrets/contents/", captured!.RequestUri!.ToString());
        Assert.Contains("ref=main", captured.RequestUri!.Query);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("github_pat_token", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task PutAsync_CreateOnlyOmitsShaAndReturnsNewSha()
    {
        string? requestBody = null;
        StubHandler handler = new(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            string json = JsonSerializer.Serialize(new { content = new { sha = "new-sha" } });
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(json) };
        });
        GitHubSecretSyncProvider provider = new(new HttpClient(handler));

        SecretSyncRemoteWriteResult result = await provider.PutAsync(
            CreateContext("aspire/apphosts/app/latest.json"),
            Encoding.UTF8.GetBytes("body"),
            new SecretSyncWriteCondition(IfMatchETag: null, IfMissing: true),
            new Dictionary<string, string> { ["secret-sync-revision"] = "rev-1" },
            CancellationToken.None);

        Assert.Equal("new-sha", result.ETag);
        Assert.Equal("rev-1", result.Revision);
        Assert.NotNull(requestBody);
        using JsonDocument doc = JsonDocument.Parse(requestBody!);
        Assert.False(doc.RootElement.TryGetProperty("sha", out _));
    }

    [Fact]
    public async Task PutAsync_CompareAndSwapSendsPriorSha()
    {
        string? requestBody = null;
        StubHandler handler = new(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            string json = JsonSerializer.Serialize(new { content = new { sha = "new-sha" } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        GitHubSecretSyncProvider provider = new(new HttpClient(handler));

        await provider.PutAsync(
            CreateContext("aspire/apphosts/app/latest.json"),
            Encoding.UTF8.GetBytes("body"),
            new SecretSyncWriteCondition(IfMatchETag: "old-sha", IfMissing: false),
            new Dictionary<string, string>(),
            CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(requestBody!);
        Assert.Equal("old-sha", doc.RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task PutAsync_CreateOnlyConflictMapsToConflictException()
    {
        StubHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("{\"message\":\"sha wasn't supplied\"}")
            });
        GitHubSecretSyncProvider provider = new(new HttpClient(handler));

        await Assert.ThrowsAsync<SecretSyncConflictException>(() => provider.PutAsync(
            CreateContext("aspire/apphosts/app/latest.json"),
            Encoding.UTF8.GetBytes("body"),
            new SecretSyncWriteCondition(IfMatchETag: null, IfMissing: true),
            new Dictionary<string, string>(),
            CancellationToken.None));
    }

    [Fact]
    public async Task PutAsync_StaleShaConflictMapsToConflictException()
    {
        StubHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"message\":\"is at ... but expected ...\"}")
            });
        GitHubSecretSyncProvider provider = new(new HttpClient(handler));

        await Assert.ThrowsAsync<SecretSyncConflictException>(() => provider.PutAsync(
            CreateContext("aspire/apphosts/app/latest.json"),
            Encoding.UTF8.GetBytes("body"),
            new SecretSyncWriteCondition(IfMatchETag: "old-sha", IfMissing: false),
            new Dictionary<string, string>(),
            CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_ThrowsWhenRequiredOptionsMissing()
    {
        SecretSyncOptions options = new();
        // Owner/Repository/Token left blank.
        GitHubSecretSyncProvider provider = new(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(
            new SecretSyncProviderContext("dev-secrets", "aspire/apphosts/app/latest.json", options),
            CancellationToken.None));
    }

    private static SecretSyncProviderContext CreateContext(string objectKey)
    {
        SecretSyncOptions options = new()
        {
            Provider = SecretSyncProviderType.GitHub
        };
        options.GitHub.Owner = "acme";
        options.GitHub.Repository = "dev-secrets";
        options.GitHub.Token = "github_pat_token";
        options.GitHub.Branch = "main";

        return new SecretSyncProviderContext(options.GitHub.Repository, objectKey, options);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this(request => Task.FromResult(responder(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
