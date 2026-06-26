using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal static class SecretSyncConfigurationInjector
{
    public static void AddSyncedSecrets(
        IConfiguration configuration,
        IReadOnlyDictionary<string, string?> secrets,
        SecretSyncPrecedence precedence)
    {
        if (secrets.Count == 0)
        {
            return;
        }

        if (configuration is not IConfigurationManager manager)
        {
            throw new InvalidOperationException("SecretSync requires AppHost configuration to implement IConfigurationManager.");
        }

        MemoryConfigurationSource source = new()
        {
            InitialData = secrets.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value))
        };

        if (precedence == SecretSyncPrecedence.Highest)
        {
            manager.Sources.Add(source);
        }
        else if (precedence == SecretSyncPrecedence.Lowest)
        {
            manager.Sources.Insert(0, source);
        }
        else
        {
            int insertAt = FindFirstEnvironmentOrCommandLineSource(manager.Sources);
            if (insertAt < 0)
            {
                manager.Sources.Add(source);
            }
            else
            {
                manager.Sources.Insert(insertAt, source);
            }
        }

        if (configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
    }

    private static int FindFirstEnvironmentOrCommandLineSource(IList<IConfigurationSource> sources)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            string name = sources[i].GetType().Name;
            if (name.Contains("EnvironmentVariables", StringComparison.Ordinal) ||
                name.Contains("CommandLine", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
