using System.Net;
using System.Text;

namespace WinDrop.Protocol.Discovery;

/// <summary>
/// Capability bits published in the `flags` key of the `_airdrop._tcp.local` TXT record.
///
/// UNVERIFIED: this mapping comes from opendrop's reading of the protocol, not from
/// Apple. We should confirm each bit against a real Apple peer's TXT record before
/// relying on it. The one that changes our behaviour today is <see cref="SupportsDvZip"/>:
/// if a peer does not advertise it, /Upload must fall back to plain gzip.
/// </summary>
[Flags]
public enum AirDropReceiverFlags
{
    None = 0,
    SupportsUrl = 0x01,
    SupportsDvZip = 0x02,
    SupportsPipelining = 0x04,
    SupportsMixedTypes = 0x08,
    SupportsUnknown1 = 0x10,
    SupportsUnknown2 = 0x20,
    SupportsIris = 0x40,
    SupportsDiscoverMayb = 0x80,
    SupportsAssetBundle = 0x100,
}

/// <summary>What a receiver publishes over mDNS so senders can find it.</summary>
public sealed record AirDropServiceRecord(
    string InstanceName,
    int Port,
    AirDropReceiverFlags Flags)
{
    public const string ServiceType = "_airdrop._tcp.local";
    public const int DefaultPort = 8770;

    public IReadOnlyDictionary<string, string> ToTxtRecord() => new Dictionary<string, string>
    {
        ["flags"] = ((int)Flags).ToString(),
    };
}

/// <summary>
/// A discovered peer. <see cref="EndPoint"/> is usually an IPv6 link-local address, which
/// is only meaningful together with its scope ID (the interface index) — a link-local
/// address without a scope is unroutable, and getting this wrong is the classic way an
/// AirDrop implementation fails to connect.
/// </summary>
public sealed record AirDropPeer(
    string InstanceName,
    IPEndPoint EndPoint,
    AirDropReceiverFlags Flags,
    string TransportName)
{
    public bool SupportsDvZip => Flags.HasFlag(AirDropReceiverFlags.SupportsDvZip);

    public override string ToString() =>
        $"{InstanceName} @ [{EndPoint.Address}]:{EndPoint.Port} via {TransportName} (flags=0x{(int)Flags:X})";
}
