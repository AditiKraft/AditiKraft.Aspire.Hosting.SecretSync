namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal sealed record SecretSyncLocalSnapshot(
    SecretSyncVault Vault,
    IReadOnlyList<SecretSyncLocalEdit> ProjectEdits,
    bool HasMissingBaselineValues);

internal sealed record SecretSyncLocalEdit(
    string ResourceName,
    string Key,
    string? Value,
    string? BaselineHash);
