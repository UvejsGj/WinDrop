namespace WinDrop.Protocol.Discovery;

/// <summary>
/// The seam that isolates the one part of AirDrop that Windows cannot do natively.
///
/// Everything above this interface — bplist, TLS, HTTP, Discover/Ask/Upload, DVZip,
/// CPIO — is identical no matter which link layer is underneath, so the transport
/// decision in ADR-001 does not block any of it.
///
/// Deliberately returns a RAW byte stream, not a TLS stream: TLS terminates inside our
/// process under every transport. A bridge implementation therefore never holds our
/// private key and never sees plaintext — it is a byte mover, and all the protocol
/// logic stays in the Windows app.
///
/// Planned implementations:
///   InfraWifiTransport  - mDNS over ordinary LAN. Peers: a Mac with
///                         `defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true`,
///                         and opendrop. Good enough to validate the whole app layer.
///   BridgeTransport     - relays to a Linux host running OWL on a real awdl0.
///                         The only implementation that can reach an iPhone.
///   WifiDirectTransport - WinRT Wi-Fi Direct. Peers: our own software only.
///                         Cannot reach an iPhone (see ADR-001).
/// </summary>
public interface IAirDropTransport : IAsyncDisposable
{
    string Name { get; }

    /// <summary>True if this transport can plausibly reach an unmodified Apple device.</summary>
    bool CanReachAppleDevices { get; }

    /// <summary>Publish ourselves as a receiver. Dispose the result to stop advertising.</summary>
    Task<IAsyncDisposable> AdvertiseAsync(AirDropServiceRecord record, CancellationToken ct = default);

    /// <summary>Continuously yield peers as they appear. Runs until cancelled.</summary>
    IAsyncEnumerable<AirDropPeer> BrowseAsync(CancellationToken ct = default);

    /// <summary>Open a raw TCP stream to a peer's AirDrop port. Caller layers TLS on top.</summary>
    Task<Stream> ConnectAsync(AirDropPeer peer, CancellationToken ct = default);

    /// <summary>
    /// Accept inbound raw connections on the AirDrop port. /Ask and /Upload must share one
    /// connection, so the accepted stream is handed to the protocol layer intact rather
    /// than being demultiplexed per request here.
    /// </summary>
    IAsyncEnumerable<Stream> AcceptAsync(int port, CancellationToken ct = default);
}
