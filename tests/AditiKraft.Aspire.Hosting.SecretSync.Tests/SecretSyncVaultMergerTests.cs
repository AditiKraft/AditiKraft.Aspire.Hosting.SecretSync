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
            [new SecretSyncLocalEdit("api", "Stripe:ApiKey", "sk_new", SecretValueHasher.Hash("sk_old"))]);

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
                [new SecretSyncLocalEdit("api", "Stripe:ApiKey", "sk_local_new", SecretValueHasher.Hash("sk_old"))]));
    }
}
