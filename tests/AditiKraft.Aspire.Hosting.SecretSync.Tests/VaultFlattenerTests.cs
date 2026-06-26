using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class VaultFlattenerTests
{
    [Fact]
    public void Flatten_UsesConfigurationPathsForNestedJson()
    {
        JsonObject json = JsonNode.Parse("""
            {
              "Stripe": {
                "ApiKey": "sk_api"
              },
              "Auth": {
                "Google": {
                  "ClientSecret": "secret"
                }
              }
            }
            """)!.AsObject();

        Dictionary<string, string?> values = VaultFlattener.Flatten(json);

        Assert.Equal("sk_api", values["Stripe:ApiKey"]);
        Assert.Equal("secret", values["Auth:Google:ClientSecret"]);
    }

    [Fact]
    public void Unflatten_RestoresNestedJson()
    {
        Dictionary<string, string?> values = new()
        {
            ["Stripe:ApiKey"] = "sk_api",
            ["Auth:Google:ClientSecret"] = "secret"
        };

        JsonObject root = VaultFlattener.Unflatten(values);

        Assert.Equal("sk_api", root["Stripe"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("secret", root["Auth"]!["Google"]!["ClientSecret"]!.GetValue<string>());
    }
}
