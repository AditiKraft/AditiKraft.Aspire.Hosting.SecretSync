namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal sealed record SecretSyncLocalSnapshot(
    SecretSyncVault Vault,
    IReadOnlyList<SecretSyncLocalEdit> ProjectEdits);

internal sealed record SecretSyncLocalEdit(
    string ResourceName,
    string Key,
    string? Value,
    string? MaterializedHash);
