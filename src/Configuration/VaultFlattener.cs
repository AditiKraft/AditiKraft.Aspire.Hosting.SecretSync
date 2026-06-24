using System.Globalization;
using System.Text.Json.Nodes;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal static class VaultFlattener
{
    public static Dictionary<string, string?> Flatten(JsonObject source)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FlattenNode(source, null, values);
        return values;
    }

    public static JsonObject Unflatten(IReadOnlyDictionary<string, string?> source)
    {
        var root = new JsonObject();

        foreach ((string key, string? value) in source)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string[] segments = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            JsonObject cursor = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string segment = segments[i];
                if (cursor[segment] is not JsonObject child)
                {
                    child = new JsonObject();
                    cursor[segment] = child;
                }

                cursor = child;
            }

            cursor[segments[^1]] = value;
        }

        return root;
    }

    private static void FlattenNode(JsonNode? node, string? path, IDictionary<string, string?> values)
    {
        switch (node)
        {
            case null:
                if (path is not null)
                {
                    values[path] = null;
                }
                break;
            case JsonObject obj:
                foreach ((string key, JsonNode? child) in obj)
                {
                    string childPath = path is null ? key : $"{path}:{key}";
                    FlattenNode(child, childPath, values);
                }
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    string childPath = path is null ? i.ToString(CultureInfo.InvariantCulture) : $"{path}:{i}";
                    FlattenNode(array[i], childPath, values);
                }
                break;
            case JsonValue value:
                if (path is null)
                {
                    return;
                }

                values[path] = ConvertValue(value);
                break;
        }
    }

    private static string? ConvertValue(JsonValue value)
    {
        if (value.TryGetValue(out string? stringValue))
        {
            return stringValue;
        }

        if (value.TryGetValue(out bool boolValue))
        {
            return boolValue ? "true" : "false";
        }

        if (value.TryGetValue(out int intValue))
        {
            return intValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue(out long longValue))
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue(out decimal decimalValue))
        {
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue(out double doubleValue))
        {
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        return value.ToJsonString();
    }
}
