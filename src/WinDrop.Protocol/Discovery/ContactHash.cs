using System.Security.Cryptography;
using System.Text;

namespace WinDrop.Protocol.Discovery;

public sealed record ContactHashCandidate(string Variant, string Normalized, ushort Prefix)
{
    public string PrefixHex => $"{Prefix:X4}";
}

/// <summary>
/// Tests the hypothesis that the four 2-byte values in an AirDrop beacon are truncated
/// SHA-256 digests of the sender's contact identifiers.
///
/// The exact normalisation Apple applies is unknown to us, and it matters: hashing
/// "+1 (555) 010-9999" and "15550109999" gives completely unrelated digests. So rather
/// than guess one form, we generate the plausible variants and report which — if any —
/// produces an observed prefix. A hit identifies both the field AND the normalisation
/// in a single experiment; a total miss falsifies the contact-hash reading of the field.
/// </summary>
public static class ContactHash
{
    /// <summary>First two bytes of SHA-256(UTF-8(value)), big-endian.</summary>
    public static ushort Prefix(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return (ushort)((digest[0] << 8) | digest[1]);
    }


    /// <summary>
    /// The normalisation Apple applies, confirmed by capture against iOS 26.6 on
    /// 2026-09-02: two identifiers reproduced observed beacon prefixes exactly.
    ///
    ///   email  hashed with no transformation beyond trimming (see caveat below)
    ///   phone  digits only — '+' and all punctuation stripped, COUNTRY CODE RETAINED.
    ///          Confirmed against the alternatives: the last-10 and last-9 digit forms
    ///          and the '+'-prefixed form all produced non-matching digests.
    ///
    /// CAVEAT: the email that confirmed this was already entirely lowercase, so the
    /// capture cannot distinguish "hashed as-is" from "lowercased first". Lowercasing is
    /// assumed here because it is the behaviour that makes user-entered addresses match,
    /// but a mixed-case address has not been tested. Resolve before relying on this for
    /// contact matching in /Discover.
    /// </summary>
    public static string Normalize(string identifier)
    {
        string trimmed = identifier.Trim();

        if (trimmed.Contains('@'))
            return trimmed.ToLowerInvariant();

        return new string(trimmed.Where(char.IsDigit).ToArray());
    }

    /// <summary>Prefix of an identifier under the confirmed normalisation.</summary>
    public static ushort NormalizedPrefix(string identifier) => Prefix(Normalize(identifier));

    public static IReadOnlyList<ContactHashCandidate> Candidates(string identifier)
    {
        string trimmed = identifier.Trim();
        string digits = new string(trimmed.Where(char.IsDigit).ToArray());

        var variants = new List<(string Name, string Value)>
        {
            ("as-is", trimmed),
            ("lowercase", trimmed.ToLowerInvariant()),
            ("uppercase", trimmed.ToUpperInvariant()),
            ("digits-only", digits),
            ("digits-plus", digits.Length > 0 ? "+" + digits : ""),
            // A phone number stored nationally vs internationally is the most likely
            // place a normalisation mismatch hides, so probe the trailing-N forms too.
            ("digits-last10", digits.Length >= 10 ? digits[^10..] : ""),
            ("digits-last9", digits.Length >= 9 ? digits[^9..] : ""),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ContactHashCandidate>();

        foreach (var (name, value) in variants)
        {
            if (string.IsNullOrEmpty(value) || !seen.Add(value))
                continue;

            results.Add(new ContactHashCandidate(name, value, Prefix(value)));
        }

        return results;
    }
}
