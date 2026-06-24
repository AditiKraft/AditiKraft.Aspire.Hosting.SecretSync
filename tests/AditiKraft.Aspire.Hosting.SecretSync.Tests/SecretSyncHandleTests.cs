using System.Text.Json.Nodes;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class SecretSyncHandleTests
{
    [Fact]
    public void ResolveForResource_ReturnsMatchingResourceValues()
    {
        var handle = new SecretSyncHandle(new SecretSyncOptions());
        var vault = new SecretSyncVault
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""
                    {
                      "Stripe": {
                        "ApiKey": "sk_api"
                      },
                      "Jwt": {
                        "SigningKey": "jwt-key"
                      }
                    }
                    """)!.AsObject()
            }
        };

        handle.Load(vault, etag: null, revision: null, lastRemoteVaultHash: null, initializedRemote: false);

        IReadOnlyDictionary<string, string?> resolved = handle.ResolveForResource("api");

        Assert.Equal("sk_api", resolved["Stripe:ApiKey"]);
        Assert.Equal("jwt-key", resolved["Jwt:SigningKey"]);
    }
}
