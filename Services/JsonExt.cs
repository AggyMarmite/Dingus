using System.Globalization;
using System.Text.Json;

namespace Dingus.Services;

/// <summary>
/// Xtream panels are loose with types (numbers as strings, nulls, arrays where objects are expected),
/// so every field is read defensively.
/// </summary>
public static class JsonExt
{
    public static string Str(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null
        };
    }

    public static string NonEmpty(this JsonElement e, string name)
    {
        var s = e.Str(name);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public static int Int(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s)) return 0;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)d;
        return 0;
    }

    public static long Long(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (string.IsNullOrWhiteSpace(s)) return 0;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (long)d;
        return 0;
    }

    public static double Dbl(this JsonElement e, string name)
    {
        var s = e.Str(name);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>Reads a string, or the first non-empty string of an array.</summary>
    public static string FirstString(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString();
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    return x.GetString();
        return null;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => e.EnumerateArray(),
        JsonValueKind.Object => e.EnumerateObject().Select(p => p.Value),
        _ => Enumerable.Empty<JsonElement>()
    };
}
