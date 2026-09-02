using WinDrop.Protocol.Discovery;

// Runs entirely locally. Identifiers are read from the command line, hashed in process,
// and only 4-hex-digit prefixes are printed. Nothing is written to disk or sent anywhere,
// so the identifiers themselves never have to leave the machine to check a match.

if (args.Length == 0)
{
    Console.WriteLine("""
        Usage:
          contact-hash --observed HSH1,PHON,HSH2,MAIL <identifier> [identifier...]

        Identifiers are Apple IDs, email addresses or phone numbers to test against the
        hash prefixes seen in an AirDrop beacon. Try the phone number in whatever form
        you would type it; the tool generates the normalisation variants itself.
        """);
    return 0;
}

var observed = new List<ushort>();
var identifiers = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--observed" && i + 1 < args.Length)
    {
        foreach (var part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ushort.TryParse(part.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v))
                observed.Add(v);
            else
                Console.Error.WriteLine($"Ignoring unparseable observed value '{part}'");
        }
    }
    else
    {
        identifiers.Add(args[i]);
    }
}

Console.WriteLine(observed.Count > 0
    ? $"Observed prefixes: {string.Join(" ", observed.Select(o => $"{o:X4}"))}\n"
    : "No --observed values given; printing prefixes only.\n");

bool anyMatch = false;

foreach (var id in identifiers)
{
    // Print a redacted label so the output is safe to share verbatim.
    Console.WriteLine($"identifier: {Redact(id)}");

    foreach (var c in ContactHash.Candidates(id))
    {
        bool hit = observed.Contains(c.Prefix);
        anyMatch |= hit;

        if (hit) Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  {c.Variant,-14} -> {c.PrefixHex}{(hit ? "   *** MATCH ***" : "")}");
        if (hit) Console.ResetColor();
    }

    Console.WriteLine();
}

if (observed.Count > 0)
{
    Console.WriteLine(anyMatch
        ? "RESULT: at least one identifier reproduces an observed prefix.\n"
          + "        The field is truncated SHA-256 of a contact identifier, and the\n"
          + "        matching variant above is the normalisation Apple applies."
        : "RESULT: no match.\n"
          + "        Either the normalisation differs from every variant tried, or the\n"
          + "        field is not a plain SHA-256 prefix of a contact identifier at all\n"
          + "        (a salt, a different digest, or a different field entirely).");
}

return 0;

static string Redact(string s)
{
    if (s.Length <= 4) return new string('*', s.Length);
    return s[..2] + new string('*', s.Length - 4) + s[^2..];
}
