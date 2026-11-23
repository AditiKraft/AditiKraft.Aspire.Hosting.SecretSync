using System.Globalization;
using System.Text.Json.Nodes;

namespace AditiKraft.Aspire.Hosting.SecretSync.Configuration;

internal static class VaultFlattener
{
    private static readonly JsonNodeOptions _caseInsensitiveNames = new() { PropertyNameCaseInsensitive = true };

    public static Dictionary<string, string?> Flatten(JsonObject source)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        FlattenNode(source, null, values);
        return values;
    }

    public static Dictionary<string, JsonNode?> FlattenValues(JsonObject source)
    {
        Dictionary<string, JsonNode?> values = new(StringComparer.OrdinalIgnoreCase);
        FlattenValues(source, null, values);
        return values;
    }

    public static JsonObject Unflatten(IReadOnlyDictionary<string, JsonNode?> source)
    {
        JsonObject root = new(_caseInsensitiveNames);
        foreach ((string key, JsonNode? value) in source.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddValue(root, key, value);
        }

        return root;
    }

    public static Dictionary<string, string?> FlattenArray(string key, JsonArray array)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        FlattenNode(array, key, values);
        return values;
    }

    public static string[] GetArrayIndexKeys(string arrayKey, IEnumerable<string> keys)
    {
        string prefix = $"{arrayKey}:";
        return [.. keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            IsArrayIndex(key.AsSpan(prefix.Length)))];
    }

    private static bool IsArrayIndex(ReadOnlySpan<char> path)
    {
        int separator = path.IndexOf(':');
        ReadOnlySpan<char> index = separator < 0 ? path : path[..separator];
        return int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static void AddValue(JsonObject root, string key, JsonNode? value)
    {
        JsonObject cursor = root;
        string[] segments = key.Split(':');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i];
            if (!cursor.TryGetPropertyValue(segment, out JsonNode? existing))
            {
                JsonObject child = new(_caseInsensitiveNames);
                cursor[segment] = child;
                cursor = child;
            }
            else if (existing is JsonObject { Count: > 0 } child)
            {
                cursor = child;
            }
            else
            {
                // Configuration supports a value and descendants at the same path.
                string remainingPath = string.Join(":", segments, i, segments.Length - i);
                cursor[remainingPath] = value?.DeepClone();
                return;
            }
        }

        cursor[segments[^1]] = value?.DeepClone();
    }

    private static void FlattenValues(JsonObject source, string? prefix, Dictionary<string, JsonNode?> values)
    {
        foreach ((string key, JsonNode? value) in source)
        {
            string path = prefix is null ? key : $"{prefix}:{key}";
            if (value is JsonObject { Count: > 0 } nested)
            {
                FlattenValues(nested, path, values);
            }
            else
            {
                values[path] = value;
            }
        }
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
