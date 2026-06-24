using System.Text.Json.Nodes;

namespace AditiKraft.Aspire.Hosting.SecretSync;

public sealed class SecretSyncVault
{
    public int Version { get; set; } = 1;
    public Dictionary<string, JsonObject> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Resources.Count == 0;
}
