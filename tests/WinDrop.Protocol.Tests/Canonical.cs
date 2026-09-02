using System.Globalization;
using System.Text.Json.Nodes;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// Mirrors the canonical() function in tools/plist_oracle.py, so a value decoded by our
/// reader can be compared against the same value decoded by Python's plistlib without
/// the comparison tripping over representation differences between the two languages.
///
/// Types are tagged rather than flattened — {"int": "1"} not 1 — because JSON cannot
/// otherwise distinguish an integer 1 from a real 1.0 from a boolean true, and those
/// are three different objects in a binary plist. A test that could not tell them apart
/// would pass while the codec confused them.
/// </summary>
public static class Canonical
{
    public static JsonNode ToNode(object? value)
    {
        switch (value)
        {
            case bool b:
                return new JsonObject { ["bool"] = b };

            case long l:
                return new JsonObject { ["int"] = l.ToString(CultureInfo.InvariantCulture) };

            case double d:
                return new JsonObject { ["real"] = Repr(d) };

            case string s:
                return new JsonObject { ["string"] = s };

            case byte[] data:
                return new JsonObject { ["data"] = Convert.ToBase64String(data) };

            case DateTime dt:
                return new JsonObject { ["date"] = Iso(dt) };

            case IDictionary<string, object?> dict:
            {
                var inner = new JsonObject();
                foreach (string key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    inner[key] = ToNode(dict[key]);

                return new JsonObject { ["dict"] = inner };
            }

            case IEnumerable<object?> list:
            {
                var array = new JsonArray();
                foreach (object? item in list)
                    array.Add(ToNode(item));

                return new JsonObject { ["array"] = array };
            }

            case null:
                throw new InvalidOperationException(
                    "null has no canonical form: plistlib cannot represent it, so it never appears in a fixture.");

            default:
                throw new InvalidOperationException($"No canonical form for {value.GetType().Name}.");
        }
    }

    /// <summary>
    /// Matches Python's repr() for floats. Both languages emit the shortest string that
    /// round-trips to the same double, so for every value a plist can hold these agree.
    /// </summary>
    private static string Repr(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Matches datetime.isoformat() on a UTC-aware value, which omits the fractional
    /// part entirely when it is zero rather than writing ".000000".
    /// </summary>
    private static string Iso(DateTime value)
    {
        DateTime utc = value.ToUniversalTime();

        return utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
    }
}
