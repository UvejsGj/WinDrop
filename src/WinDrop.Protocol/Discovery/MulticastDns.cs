using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinDrop.Protocol.Dns;

namespace WinDrop.Protocol.Discovery;

public sealed record ReceivedDnsMessage(DnsMessage Message, IPEndPoint From, int InterfaceIndex);

/// <summary>
/// Multicast DNS transport: UDP on port 5353 to 224.0.0.251 and ff02::fb.
///
/// Two details that this protocol lives or dies on.
///
/// FIRST, the interface a packet arrived on is part of the address. AirDrop peers are
/// at IPv6 link-local addresses, and fe80::1 on Wi-Fi and fe80::1 on Ethernet are
/// different machines. A link-local address without its scope ID is not routable, so we
/// ask the stack for packet information on every receive and carry the arrival
/// interface index alongside the address. Losing it means being able to see a peer and
/// never being able to connect to it.
///
/// SECOND, port 5353 is shared. Bonjour, Windows' own mDNS resolver and any other
/// service browser on the machine are all bound to it, so the socket must set
/// ReuseAddress before binding or startup fails on a normal desktop.
/// </summary>
public sealed class MulticastDns : IAsyncDisposable
{
    public const int Port = 5353;
    public static readonly IPAddress Ipv4Group = IPAddress.Parse("224.0.0.251");
    public static readonly IPAddress Ipv6Group = IPAddress.Parse("ff02::fb");

    private readonly List<Socket> _sockets = [];
    private readonly List<int> _interfaces = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _readers = [];

    public event Action<ReceivedDnsMessage>? MessageReceived;

    /// <summary>Interface indices we joined, in the order they were found.</summary>
    public IReadOnlyList<int> Interfaces => _interfaces;

    public void Start()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            IPInterfaceProperties properties = nic.GetIPProperties();

            if (nic.Supports(NetworkInterfaceComponent.IPv6))
                TryJoin(AddressFamily.InterNetworkV6, properties.GetIPv6Properties()?.Index);

            if (nic.Supports(NetworkInterfaceComponent.IPv4))
                TryJoin(AddressFamily.InterNetwork, properties.GetIPv4Properties()?.Index);
        }

        if (_sockets.Count == 0)
            throw new InvalidOperationException("No multicast-capable interface is up.");
    }

    private void TryJoin(AddressFamily family, int? index)
    {
        if (index is not { } interfaceIndex) return;

        Socket? socket = null;

        try
        {
            socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (family == AddressFamily.InterNetworkV6)
            {
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                    new IPv6MulticastOption(Ipv6Group, interfaceIndex));
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, interfaceIndex);
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, Port));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(Ipv4Group, interfaceIndex));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                    IPAddress.HostToNetworkOrder(interfaceIndex));
            }

            // Loopback on, so two instances on one machine can see each other — which is
            // how this gets tested without a second device.
            socket.SetSocketOption(
                family == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP,
                SocketOptionName.MulticastLoopback, true);

            _sockets.Add(socket);
            _interfaces.Add(interfaceIndex);
            _readers.Add(Task.Run(() => ReadLoopAsync(socket, family, _stopping.Token)));
        }
        catch (SocketException)
        {
            // An interface can refuse the join (disconnected mid-enumeration, or a
            // virtual adapter that claims multicast support it does not have). Skipping
            // it is right; failing startup because one adapter is odd is not.
            socket?.Dispose();
        }
    }

    private async Task ReadLoopAsync(Socket socket, AddressFamily family, CancellationToken ct)
    {
        var buffer = new byte[9000];
        EndPoint any = family == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;

            try
            {
                result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            DnsMessage message;

            try
            {
                message = DnsMessage.Parse(buffer.AsSpan(0, result.ReceivedBytes));
            }
            catch (DnsFormatException)
            {
                // Port 5353 carries traffic from every mDNS implementation on the link.
                // A packet we cannot parse is normal, not an error worth surfacing.
                continue;
            }

            MessageReceived?.Invoke(new ReceivedDnsMessage(
                message,
                (IPEndPoint)result.RemoteEndPoint,
                result.PacketInformation.Interface));
        }
    }

    /// <summary>Sends on every joined interface, since we cannot know where a peer is.</summary>
    public async Task SendAsync(DnsMessage message, CancellationToken ct = default)
    {
        byte[] payload = message.ToBytes();

        for (int i = 0; i < _sockets.Count; i++)
        {
            Socket socket = _sockets[i];

            var destination = socket.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPEndPoint(Ipv6Group, Port)
                : new IPEndPoint(Ipv4Group, Port);

            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, destination, ct);
            }
            catch (SocketException)
            {
                // One interface going away mid-send must not stop the others.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        foreach (Socket socket in _sockets)
        {
            try { socket.Dispose(); } catch { /* already gone */ }
        }

        try { await Task.WhenAll(_readers).WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }

        _stopping.Dispose();
    }
}
