using System.Collections;
using System.Globalization;

namespace WinDrop.Protocol.Http;

public sealed class AirDropHttpException(string message) : Exception(message);

/// <summary>
/// Case-insensitive header collection preserving insertion order.
///
/// Deliberately not a general-purpose HTTP header implementation: no obs-fold support
/// (obsolete and a request-smuggling vector), no multi-value semantics beyond a joined
/// string. AirDrop exchanges a handful of simple headers, and a smaller surface is
/// easier to reason about when the input arrives from an unauthenticated peer.
/// </summary>
public sealed class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _entries = [];

    public int Count => _entries.Count;

    public string? this[string name]
    {
        get
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }

            return null;
        }
    }

    public HttpHeaders Set(string name, string value)
    {
        _entries.RemoveAll(e => string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase));
        _entries.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    public void Add(string name, string value) =>
        _entries.Add(new KeyValuePair<string, string>(name, value));

    public bool Contains(string name) => this[name] is not null;

    /// <summary>
    /// Content-Length, or null when absent. Rejects a value that is not a plain
    /// non-negative integer rather than coercing it: a header like "0, 100" or "+5" is
    /// how request smuggling starts.
    /// </summary>
    public long? ContentLength
    {
        get
        {
            string? raw = this["Content-Length"];
            if (raw is null) return null;

            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                throw new AirDropHttpException($"Malformed Content-Length '{raw}'.");

            return value;
        }
    }

    public bool IsChunked =>
        string.Equals(this["Transfer-Encoding"]?.Trim(), "chunked", StringComparison.OrdinalIgnoreCase);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record HttpRequestHead(string Method, string Target, HttpHeaders Headers)
{
    public override string ToString() => $"{Method} {Target}";
}

public sealed record HttpResponseHead(int StatusCode, string ReasonPhrase, HttpHeaders Headers)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public override string ToString() => $"{StatusCode} {ReasonPhrase}";
}
