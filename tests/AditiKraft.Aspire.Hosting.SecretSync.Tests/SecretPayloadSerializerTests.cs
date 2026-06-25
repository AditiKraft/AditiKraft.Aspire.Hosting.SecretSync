using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Cryptography;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class SecretPayloadSerializerTests
{
    [Fact]
    public void ComputeVaultHash_IsStableAcrossKeyOrder()
    {
        var first = new SecretSyncVault
        {
            Resources =
            {
                ["apphost"] = JsonNode.Parse("""
                    { "A": "1", "B": "2", "Nested": { "X": "9", "Y": "8" } }
                    """)!.AsObject(),
                ["api"] = JsonNode.Parse("""{ "Stripe": "sk" }""")!.AsObject()
            }
        };

        var second = new SecretSyncVault
        {
            Resources =
            {
                // Same content, different insertion order at every level.
                ["api"] = JsonNode.Parse("""{ "Stripe": "sk" }""")!.AsObject(),
                ["apphost"] = JsonNode.Parse("""
                    { "Nested": { "Y": "8", "X": "9" }, "B": "2", "A": "1" }
                    """)!.AsObject()
            }
        };

        Assert.Equal(
            SecretPayloadSerializer.ComputeVaultHash(first),
            SecretPayloadSerializer.ComputeVaultHash(second));
    }

    [Fact]
    public void ComputeVaultHash_ChangesWhenAValueChanges()
    {
        var first = new SecretSyncVault
        {
            Resources =
            {
                ["apphost"] = JsonNode.Parse("""{ "A": "1" }""")!.AsObject()
            }
        };

        var second = new SecretSyncVault
        {
            Resources =
            {
                ["apphost"] = JsonNode.Parse("""{ "A": "2" }""")!.AsObject()
            }
        };

        Assert.NotEqual(
            SecretPayloadSerializer.ComputeVaultHash(first),
            SecretPayloadSerializer.ComputeVaultHash(second));
    }

    [Fact]
    public void ComputeVaultHash_PreservesArrayOrder()
    {
        var first = new SecretSyncVault
        {
            Resources =
            {
                ["apphost"] = JsonNode.Parse("""{ "List": [ "a", "b" ] }""")!.AsObject()
            }
        };

        var second = new SecretSyncVault
        {
            Resources =
            {
                ["apphost"] = JsonNode.Parse("""{ "List": [ "b", "a" ] }""")!.AsObject()
            }
        };

        Assert.NotEqual(
            SecretPayloadSerializer.ComputeVaultHash(first),
            SecretPayloadSerializer.ComputeVaultHash(second));
    }
}
