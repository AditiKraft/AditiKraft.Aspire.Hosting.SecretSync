using System.Text.Json.Nodes;
using AditiKraft.Aspire.Hosting.SecretSync.Configuration;

namespace AditiKraft.Aspire.Hosting.SecretSync.Tests;

public sealed class SecretSyncVaultMergerTests
{
    [Fact]
    public void MergeRemoteWithLocal_FillsMissingAndPreservesLocalAdditions()
    {
        SecretSyncVault remote = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk_remote" } }""")!.AsObject()
            }
        };
        SecretSyncVault local = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "OpenAI": { "ApiKey": "sk_local" } }""")!.AsObject()
            }
        };

        SecretSyncVault merged = SecretSyncVaultMerger.MergeRemoteWithLocal(
            remote,
            local,
            SecretSyncConflictMode.Fail);

        Assert.Equal("sk_remote", merged.Resources["api"]["Stripe"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("sk_local", merged.Resources["api"]["OpenAI"]!["ApiKey"]!.GetValue<string>());
    }

    [Fact]
    public void MergeRemoteWithLocal_FailsWhenSameKeyDiffersByDefault()
    {
        SecretSyncVault remote = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk_remote" } }""")!.AsObject()
            }
        };
        SecretSyncVault local = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk_local" } }""")!.AsObject()
            }
        };

        Assert.Throws<SecretSyncConflictException>(() =>
            SecretSyncVaultMerger.MergeRemoteWithLocal(remote, local, SecretSyncConflictMode.Fail));
    }

    [Fact]
    public void MergeRemoteWithLocal_AllowsLocalEditWhenRemoteStillMatchesBaseline()
    {
        SecretSyncVault remote = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk_old" } }""")!.AsObject()
            }
        };
        SecretSyncVault local = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk_new" } }""")!.AsObject()
            }
        };

        SecretSyncVault merged = SecretSyncVaultMerger.MergeRemoteWithLocal(
            remote,
            local,
            SecretSyncConflictMode.Fail,
            [new SecretSyncLocalEdit("api", "Stripe:ApiKey", SecretValueHasher.Hash("sk_old"))]);

        Assert.Equal("sk_new", merged.Resources["api"]["Stripe"]!["ApiKey"]!.GetValue<string>());
    }

    [Fact]
    public void MergeRemoteWithLocal_FailsLocalEditWhenRemoteChangedSinceMaterialization()
    {
        SecretSyncVault remote = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk_remote_new" } }""")!.AsObject()
            }
        };
        SecretSyncVault local = new()
        {
            Resources =
            {
                ["api"] = JsonNode.Parse("""{ "Stripe": { "ApiKey": "sk_local_new" } }""")!.AsObject()
            }
        };

        Assert.Throws<SecretSyncConflictException>(() =>
            SecretSyncVaultMerger.MergeRemoteWithLocal(
                remote,
                local,
                SecretSyncConflictMode.Fail,
                [new SecretSyncLocalEdit("api", "Stripe:ApiKey", SecretValueHasher.Hash("sk_old"))]));
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("99", false)]
    public void MergeRemoteWithLocal_ReconcilesNativeArraysWithLegacyIndexedValues(string legacyValue, bool equivalent)
    {
        SecretSyncVault remote = new()
        {
            Resources = { ["apphost"] = new JsonObject { ["Numbers:0"] = legacyValue } }
        };
        SecretSyncVault local = new()
        {
            Resources = { ["apphost"] = new JsonObject { ["Numbers"] = new JsonArray(42) } }
        };

        if (!equivalent)
        {
            Assert.Throws<SecretSyncConflictException>(() =>
                SecretSyncVaultMerger.MergeRemoteWithLocal(remote, local, SecretSyncConflictMode.Fail));
            return;
        }

        SecretSyncVault merged = SecretSyncVaultMerger.MergeRemoteWithLocal(remote, local, SecretSyncConflictMode.Fail);

        Assert.Equal(42, merged.Resources["apphost"]["Numbers"]![0]!.GetValue<int>());
        Assert.Single(merged.Resources["apphost"]);
    }

    [Theory]
    [InlineData(SecretSyncConflictMode.PullWins, "42", false)]
    [InlineData(SecretSyncConflictMode.PullWins, "99", false)]
    [InlineData(SecretSyncConflictMode.PushWins, "42", true)]
    [InlineData(SecretSyncConflictMode.PushWins, "99", true)]
    public void MergeRemoteWithLocal_SelectsTheEntireIndexedGroup(
        SecretSyncConflictMode conflictMode,
        string legacyValue,
        bool useNativeArray)
    {
        SecretSyncVault remote = new()
        {
            Resources = { ["apphost"] = new JsonObject { ["Numbers:0"] = legacyValue, ["Numbers:1"] = "72" } }
        };
        SecretSyncVault local = new()
        {
            Resources = { ["apphost"] = new JsonObject { ["Numbers"] = new JsonArray(42) } }
        };

        JsonObject merged = SecretSyncVaultMerger.MergeRemoteWithLocal(remote, local, conflictMode).Resources["apphost"];

        Assert.Equal(useNativeArray, merged["Numbers"] is JsonArray);
        Dictionary<string, string?> values = VaultFlattener.Flatten(merged);
        Assert.Equal(useNativeArray ? "42" : legacyValue, values["Numbers:0"]);
        Assert.Equal(useNativeArray ? null : "72", values.GetValueOrDefault("Numbers:1"));
    }
}
