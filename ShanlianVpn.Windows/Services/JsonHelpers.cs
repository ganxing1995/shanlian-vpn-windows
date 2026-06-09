using System.Text.Json;

namespace ShanlianVpn.Windows.Services;

public static class JsonHelpers
{
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static JsonElement UnwrapData(JsonElement element)
    {
        if (TryGetProperty(element, "data", out var data))
        {
            return data;
        }

        if (TryGetProperty(element, "result", out var result))
        {
            return result;
        }

        return element;
    }

    public static string GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return "";
    }

    public static int GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return 0;
    }

    public static decimal GetDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return 0;
    }

    public static bool GetBool(JsonElement element, bool defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number != 0;
            }
        }

        return defaultValue;
    }

    public static IReadOnlyList<int> GetIntArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = new List<int>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                {
                    values.Add(number);
                }
                else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out number))
                {
                    values.Add(number);
                }
            }

            return values;
        }

        return Array.Empty<int>();
    }

    public static DateTimeOffset? GetDateTimeOffset(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
