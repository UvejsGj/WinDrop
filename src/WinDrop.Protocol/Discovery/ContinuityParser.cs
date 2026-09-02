namespace WinDrop.Protocol.Discovery;

/// <summary>
/// Continuity message types carried inside Apple's BLE manufacturer-specific data
/// (company ID 0x004C). This table is community-derived from published BLE research,
/// not from Apple documentation — treat every entry as a hypothesis until we have
/// observed it ourselves. The sniffer prints unknown types verbatim so the table can
/// grow from evidence rather than from blog posts.
/// </summary>
public enum ContinuityType : byte
{
    IBeacon = 0x02,
    AirPrint = 0x03,
    AirDrop = 0x05,
    HomeKit = 0x06,
    ProximityPairing = 0x07,
    HeySiri = 0x08,
    AirPlayTarget = 0x09,
    AirPlaySource = 0x0A,
    MagicSwitch = 0x0B,
    Handoff = 0x0C,
    TetheringTargetPresence = 0x0D,
    TetheringSourcePresence = 0x0E,
    NearbyAction = 0x0F,
    NearbyInfo = 0x10,
    FindMy = 0x12,
}

/// <summary>One type-length-value record inside a Continuity advertisement.</summary>
public sealed record ContinuityMessage(byte Type, byte[] Payload)
{
    public string TypeName =>
        Enum.IsDefined(typeof(ContinuityType), Type)
            ? ((ContinuityType)Type).ToString()
            : $"Unknown(0x{Type:X2})";

    public string PayloadHex => Convert.ToHexString(Payload);
}

/// <summary>
/// Decoded AirDrop (type 0x05) beacon.
///
/// WORKING HYPOTHESIS for the 18-byte payload, to be confirmed by capture:
///   [0..8)   8 bytes  zero padding / reserved
///   [8]      1 byte   protocol version (observed 0x01)
///   [9..17)  8 bytes  four 2-byte truncated SHA-256 prefixes of the sender's
///                     contact identifiers (Apple ID, phone, email, email2),
///                     used by the receiver for the Contacts-Only match
///   [17]     1 byte   zero terminator
///
/// Note what is ABSENT: no channel, no SSID, no address, no transport identifier.
/// That absence is the whole argument in ADR-001 — this frame can only mean
/// "wake AWDL and browse there", because it carries nowhere else to go.
/// </summary>
public sealed record AirDropBeacon(
    byte[] Prefix,
    byte Version,
    ushort[] ContactHashes,
    byte Suffix,
    bool MatchedExpectedLayout,
    byte[] RawPayload)
{
    public string ContactHashesHex => string.Join(" ", ContactHashes.Select(h => $"{h:X4}"));
}

public static class ContinuityParser
{
    public const ushort AppleCompanyId = 0x004C;
    private const int ExpectedAirDropPayloadLength = 18;

    /// <summary>
    /// Splits Apple manufacturer data into its TLV records. A single advertisement can
    /// carry several Continuity messages back to back. Returns what it could parse and
    /// reports any trailing bytes it could not — a truncated tail is itself a finding,
    /// so we surface it instead of swallowing it.
    /// </summary>
    public static IReadOnlyList<ContinuityMessage> Parse(ReadOnlySpan<byte> data, out int unparsedTailBytes)
    {
        var messages = new List<ContinuityMessage>();
        int i = 0;

        while (i + 2 <= data.Length)
        {
            byte type = data[i];
            byte length = data[i + 1];

            if (i + 2 + length > data.Length)
                break; // declared length overruns the frame — stop, report the tail

            messages.Add(new ContinuityMessage(type, data.Slice(i + 2, length).ToArray()));
            i += 2 + length;
        }

        unparsedTailBytes = data.Length - i;
        return messages;
    }

    /// <summary>
    /// Decodes an AirDrop TLV payload against the hypothesised layout. Never throws on a
    /// surprise: a payload of unexpected length is decoded as far as it can be and
    /// flagged, because a layout change across iOS versions is exactly the kind of thing
    /// this tool exists to catch.
    /// </summary>
    public static AirDropBeacon DecodeAirDrop(ReadOnlySpan<byte> payload)
    {
        bool matches = payload.Length == ExpectedAirDropPayloadLength;

        byte[] prefix = payload[..Math.Min(8, payload.Length)].ToArray();
        byte version = payload.Length > 8 ? payload[8] : (byte)0;

        var hashes = new List<ushort>();
        for (int off = 9; off + 2 <= payload.Length - 1; off += 2)
            hashes.Add((ushort)((payload[off] << 8) | payload[off + 1]));

        byte suffix = payload.Length > 0 ? payload[^1] : (byte)0;

        return new AirDropBeacon(prefix, version, hashes.ToArray(), suffix, matches, payload.ToArray());
    }
}
