using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using WinDrop.Protocol.Dns;

namespace WinDrop.Protocol.Discovery;

public sealed class BridgeException(string message) : Exception(message);

/// <summary>
/// Discovery and transport through a Linux box running OWL, which owns a real `awdl0`.
///
/// WHY THIS EXISTS. AWDL is a second Wi-Fi link layer that time-slices the radio on a
/// synchronised schedule, and Windows exposes no way to drive the 802.11 MAC that way
/// (ADR-001). An iPhone will only ever look for AirDrop on `awdl0`. So the radio has to
/// be somewhere else.
///
/// WHAT THE BRIDGE IS. Not a second copy of the app — a peripheral that owns an antenna.
/// It does two dumb things: relay mDNS datagrams on and off the AWDL link, and pipe TCP
/// bytes. It parses no plists, holds no keys and terminates no TLS. Every protocol
/// decision stays here, in the Windows process, which is the whole point of the design:
/// the bridge could be replaced by different hardware without touching a line of
/// protocol code.
///
/// THE PROTOCOL. A control connection carrying newline-delimited JSON, plus one TCP
/// connection per transfer:
///
///   -> {"op":"hello","reversePort":N}      we tell it where to reach us
///   &lt;- {"ok":true,"interface":"awdl0","address":"fe80::..."}
///   -> {"op":"mdns","data":"&lt;base64&gt;"}      send this DNS message on the link
///   &lt;- {"event":"mdns","data":"&lt;base64&gt;"}   one arrived on the link
///
///   outbound: we dial the data port, send {"connect":"fe80::...","port":8770},
///             then it is raw bytes in both directions
///   inbound:  the bridge dials our reverse port, sends {"peer":"fe80::..."},
///             then it is raw bytes in both directions
///
/// WHAT IS AND IS NOT VERIFIED. The relay is exercised end to end against the real
/// daemon in `tools/windrop-bridge.py`, run over an ordinary interface. What that cannot
/// prove is that `awdl0` behaves like an ordinary interface to a socket, or that OWL
/// keeps the link up under load. Those assumptions are stated rather than tested, and
/// there is a live question in ADR-001 about whether current iOS accepts a non-Apple
/// peer at all.
/// </summary>
public sealed class BridgeTransport : IAirDropTransport
{
    public const int DefaultControlPort = 7788;
    public const int DefaultDataPort = 7789;

    private readonly string _host;
    private readonly int _controlPort;
    private readonly int _dataPort;

    private readonly Channel<AirDropPeer> _discovered = Channel.CreateUnbounded<AirDropPeer>();
    private readonly Channel<Stream> _inbound = Channel.CreateUnbounded<Stream>();
    private readonly ServiceAssembler _assembler = new("bridge");
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private TcpClient? _control;
    private StreamWriter? _controlWriter;
    private TcpListener? _reverse;
    private AirDropServiceRecord? _advertised;
    private string _bridgeAddress = "";
    private string _bridgeInterface = "";
    private bool _started;

    public BridgeTransport(string host, int controlPort = DefaultControlPort, int dataPort = DefaultDataPort)
    {
        _host = host;
        _controlPort = controlPort;
        _dataPort = dataPort;
    }

    public string Name => "bridge";

    /// <summary>
    /// The only transport that can. Whether a current iPhone still *accepts* one is a
    /// separate and unresolved question — see the addendum in ADR-001.
    /// </summary>
    public bool CanReachAppleDevices => true;

    /// <summary>The link-local address the bridge holds on its AWDL interface.</summary>
    public string BridgeAddress => _bridgeAddress;

    public string BridgeInterface => _bridgeInterface;

    // ---- connection --------------------------------------------------------

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct);

        try
        {
            if (_started) return;

            // Bind the reverse listener before saying hello: the bridge needs somewhere
            // to hand inbound connections, and it needs to know that port up front.
            // Any, not Loopback: the bridge is a different machine and has to be
            // able to dial back. Dual-mode so it can reach us over either family.
            _reverse = new TcpListener(IPAddress.IPv6Any, 0);
            _reverse.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            _reverse.Start();
            int reversePort = ((IPEndPoint)_reverse.LocalEndpoint).Port;

            _control = new TcpClient();
            await _control.ConnectAsync(_host, _controlPort, ct);

            NetworkStream stream = _control.GetStream();
            _controlWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(stream, new UTF8Encoding(false));

            await SendAsync(new JsonObject { ["op"] = "hello", ["reversePort"] = reversePort }, ct);

            string? line = await reader.ReadLineAsync(ct)
                ?? throw new BridgeException("Bridge closed the control connection during the handshake.");

            JsonNode hello = JsonNode.Parse(line)
                ?? throw new BridgeException($"Unparseable handshake reply: {Truncate(line)}");

            if (hello["ok"]?.GetValue<bool>() != true)
                throw new BridgeException($"Bridge refused the handshake: {Truncate(line)}");

            _bridgeInterface = hello["interface"]?.GetValue<string>() ?? "";
            _bridgeAddress = hello["address"]?.GetValue<string>() ?? "";

            if (string.IsNullOrEmpty(_bridgeAddress))
            {
                throw new BridgeException(
                    $"Bridge reported no address on '{_bridgeInterface}'. Is OWL running and has awdl0 come up?");
            }

            _ = ControlLoopAsync(reader, _shutdown.Token);
            _ = ReverseLoopAsync(_reverse, _shutdown.Token);

            _started = true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (_controlWriter is null) throw new BridgeException("Control connection is not open.");

        await _controlWriter.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
    }

    private async Task ControlLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                JsonNode? message;

                try
                {
                    message = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // a malformed line is not worth tearing the link down for
                }

                if (message?["event"]?.GetValue<string>() != "mdns") continue;

                string? encoded = message["data"]?.GetValue<string>();
                if (encoded is null) continue;

                HandleDatagram(Convert.FromBase64String(encoded));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* bridge went away; browsing simply stops */ }
    }

    private void HandleDatagram(byte[] payload)
    {
        DnsMessage message;

        try
        {
            message = DnsMessage.Parse(payload);
        }
        catch (DnsFormatException)
        {
            return; // the AWDL link carries other mDNS traffic too
        }

        if (AirDropRecords.IsQueryForService(message))
        {
            if (_advertised is { } record) _ = AnnounceAsync(record, CancellationToken.None);
            return;
        }

        // Interface index 0: the address is scoped to the bridge's interface, not to
        // anything on this machine, and we never dial it directly — the bridge does.
        foreach (AirDropPeer peer in _assembler.Absorb(message, 0))
            _discovered.Writer.TryWrite(peer);
    }

    private async Task ReverseLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct);
                NetworkStream stream = client.GetStream();

                // The bridge announces which peer this is before handing over the bytes.
                string? header = await ReadLineAsync(stream, ct);
                if (header is null) { client.Dispose(); continue; }

                _inbound.Writer.TryWrite(stream);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- advertising and browsing ------------------------------------------

    public async Task<IAsyncDisposable> AdvertiseAsync(AirDropServiceRecord record, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);

        _advertised = record;
        _assembler.OwnInstance = record.InstanceName;

        await AnnounceAsync(record, ct);

        return new Advertisement(this);
    }

    private Task AnnounceAsync(AirDropServiceRecord record, CancellationToken ct)
    {
        // The host name and address are the bridge's, because the bridge is what a peer
        // will actually connect to. It then pipes those bytes here.
        string host = $"{record.InstanceName.ToLowerInvariant()}.local";
        IPAddress address = IPAddress.Parse(StripScope(_bridgeAddress));

        DnsMessage announcement = AirDropRecords.BuildAnnouncement(record, host, [address]);
        return SendDatagramAsync(announcement, ct);
    }

    private sealed class Advertisement(BridgeTransport owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner._advertised = null;
            return ValueTask.CompletedTask;
        }
    }

    private Task SendDatagramAsync(DnsMessage message, CancellationToken ct) =>
        SendAsync(new JsonObject
        {
            ["op"] = "mdns",
            ["data"] = Convert.ToBase64String(message.ToBytes()),
        }, ct);

    public async IAsyncEnumerable<AirDropPeer> BrowseAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SendDatagramAsync(AirDropRecords.BuildQuery(), ct);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { return; }
            }
        }, ct);

        await foreach (AirDropPeer peer in _discovered.Reader.ReadAllAsync(ct))
            yield return peer;
    }

    // ---- connections -------------------------------------------------------

    public async Task<Stream> ConnectAsync(AirDropPeer peer, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);

        var client = new TcpClient();

        try
        {
            await client.ConnectAsync(_host, _dataPort, ct);
            NetworkStream stream = client.GetStream();

            // Send the address without a scope suffix. Any scope in it belongs to the
            // bridge's interface numbering, not ours, so the bridge re-attaches its own.
            var request = new JsonObject
            {
                ["connect"] = StripScope(peer.EndPoint.Address.ToString()),
                ["port"] = peer.EndPoint.Port,
            };

            byte[] line = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
            await stream.WriteAsync(line, ct);
            await stream.FlushAsync(ct);

            string? reply = await ReadLineAsync(stream, ct)
                ?? throw new BridgeException("Bridge closed the data connection without replying.");

            JsonNode? parsed = JsonNode.Parse(reply);

            if (parsed?["ok"]?.GetValue<bool>() != true)
            {
                throw new BridgeException(
                    $"Bridge could not reach {peer.EndPoint}: {parsed?["error"]?.GetValue<string>() ?? Truncate(reply)}");
            }

            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Inbound transfers. The port is ignored: the bridge listens on the AirDrop port on
    /// its own AWDL interface, because that is the address a peer was told to use, and
    /// dials back here for each connection.
    /// </summary>
    public async IAsyncEnumerable<Stream> AcceptAsync(
        int port,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);

        await foreach (Stream stream in _inbound.Reader.ReadAllAsync(ct))
            yield return stream;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Reads one newline-terminated line without buffering past it.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];

        while (bytes.Count < 8192)
        {
            int read = await stream.ReadAsync(one, ct);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] != (byte)'\r') bytes.Add(one[0]);
        }

        throw new BridgeException("Bridge sent a line longer than 8192 bytes.");
    }

    private static string StripScope(string address)
    {
        int percent = address.IndexOf('%');
        return percent < 0 ? address : address[..percent];
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();

        try { _reverse?.Stop(); } catch { /* already gone */ }
        _control?.Dispose();
        _shutdown.Dispose();
        _startGate.Dispose();
    }
}
