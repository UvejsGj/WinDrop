using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WinDrop.Cli;
using WinDrop.Protocol;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Tls;
using WinDrop.Protocol.Web;

// WinDrop CLI. AirDrop over the infrastructure-Wi-Fi transport, and the phone page:
//
//   receive [--dir <path>]   advertise over mDNS and accept transfers
//   send <file> [file...]    browse for a peer, ask, and upload
//   browse                   list peers and stop
//   link                     the phone page: a QR code any phone's browser can open
//
// AirDrop here reaches another WinDrop instance, opendrop, or a Mac running with
// BrowseAllInterfaces enabled. It does NOT reach an iPhone: iOS binds AirDrop's browser
// to awdl0, which Windows cannot join. See docs/adr-001-transport-selection.md. The phone
// page does reach an iPhone, as a web page rather than as AirDrop: see docs/phone-page.md.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

try
{
    return args[0].ToLowerInvariant() switch
    {
        "receive" => await ReceiveAsync(args, stopping.Token),
        "send" => await SendAsync(args, stopping.Token),
        "browse" => await BrowseAsync(args, stopping.Token),
        "link" => await LinkAsync(args, stopping.Token),
        _ => PrintUsage(),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nStopped.");
    return 0;
}
catch (Exception ex)
{
    // Left to the runtime, a failure prints its message raw, and messages quote the peer: a
    // rejected upload carries the receiver's own reason phrase. The messages are cleaned; the
    // stack trace is ours and stays, since a tool that hides where it failed is worse.
    for (Exception? e = ex; e is not null; e = e.InnerException)
        Console.Error.WriteLine($"{e.GetType().Name}: {PeerText.Printable(e.Message)}");

    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}

static int PrintUsage()
{
    Console.WriteLine("""
        WinDrop - an AirDrop implementation for Windows

          windrop receive [--dir <path>] [--port <n>] [--flags <hex>] [--yes] [--no-early-ask]
          windrop send [--to <name>| --peer <host>] [--icon <image>] <file> [file...]
          windrop browse                   list nearby peers
          windrop link [--dir <path>] [--port <n>] [--yes] [--offer <file>]...
                                           the phone page: scan its code with a phone on this Wi-Fi

        Add --bridge <host> to any command to use a Linux box running OWL as the radio.

        On an ordinary network, reaches another WinDrop instance, opendrop, or a Mac with
        `defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true`.

        An iPhone only looks for AirDrop on awdl0, a link layer Windows cannot join.
        It is reachable from Linux running OWL, directly or through --bridge.
        See docs/adr-001-transport-selection.md.

        `link` needs neither: an iPhone opens a page in Safari to send and receive files,
        over ordinary Wi-Fi. See docs/phone-page.md.
        """);

    return 1;
}

static async Task<int> ReceiveAsync(string[] args, CancellationToken ct)
{
    string directory = ArgumentValue(args, "--dir")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinDrop");

    bool autoAccept = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);

    Directory.CreateDirectory(directory);

    // --flags overrides what the TXT record advertises, so a session can test what iOS does
    // in response: whether it still attaches a preview, and whether it picks DVZip or gzip.
    var flags = AirDropReceiverFlags.SupportsDvZip | AirDropReceiverFlags.SupportsMixedTypes;

    if (ArgumentValue(args, "--flags") is { } flagText)
    {
        string digits = flagText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? flagText[2..] : flagText;

        if (!int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed))
        {
            Console.Error.WriteLine($"--flags takes hex, such as 0x0A or 0. Got '{flagText}'.");
            return 1;
        }

        flags = (AirDropReceiverFlags)parsed;
    }

    Console.WriteLine($"Advertising flags 0x{(int)flags:X2} ({flags})");

    // Early-ask is the default: /Ask is answered before its preview-laden body is read,
    // which is what stops iOS timing the exchange out on a slow link, and the upload is not
    // read until the prompt is answered. --early-ask is still accepted so older runbooks
    // work; --no-early-ask restores the classic order for comparison.
    bool earlyAsk = !args.Contains("--no-early-ask", StringComparer.OrdinalIgnoreCase);
    Console.WriteLine(earlyAsk
        ? "Early-ask on: /Ask is answered first; the upload is read only once the prompt says yes."
        : "Early-ask off: /Ask is answered after the prompt, as Apple's own receivers do.");

    var receiver = new AirDropReceiver(new AirDropReceiverOptions
    {
        ComputerName = Environment.MachineName,
        DownloadDirectory = directory,
        Flags = flags,
        ConsentHandler = autoAccept ? AutoAcceptAsync : PromptAsync,
        EarlyAskReply = earlyAsk,
        Log = line => Console.WriteLine(ConsoleText.LogLine(DateTime.Now, line)),
    });

    await using IAirDropTransport transport = CreateTransport(args);

    // --port lets a second receiver share the machine with the app, which already holds
    // 8770. The SRV record carries the port, so browsers find it wherever it listens.
    int port = int.TryParse(ArgumentValue(args, "--port"), out int requested) ? requested : AirDropServiceRecord.DefaultPort;

    string instance = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..6]}";
    var record = new AirDropServiceRecord(instance, port, flags);

    await using IAsyncDisposable advertisement = await transport.AdvertiseAsync(record, ct);

    Console.WriteLine($"Receiving as '{instance}' on port {record.Port}");

    if (transport is InfraWifiTransport infra)
        Console.WriteLine($"mDNS on {string.Join(", ", infra.ListeningOn)}");
    Console.WriteLine($"Saving to {directory}");
    Console.WriteLine("Ctrl+C to stop.\n");

    using X509Certificate2 certificate = AirDropCertificate.CreateSelfSigned(Environment.MachineName);

    await foreach (Stream raw in transport.AcceptAsync(record.Port, ct))
    {
        // One connection at a time is deliberate: a consent prompt on the console cannot
        // sensibly be answered for two senders at once.
        bool handshakeDone = false;

        try
        {
            await using var ssl = await AirDropTls.AuthenticateAsServerAsync(raw, certificate, ct);
            handshakeDone = true;

            AirDropTransferResult? result = await receiver.HandleConnectionAsync(ssl, ct);

            if (result is not null)
            {
                // The paths end in names the sender chose.
                Console.WriteLine($"Received {result.Files.Count} file(s), {result.TotalBytes:N0} bytes:");
                foreach (string file in result.Files) Console.WriteLine($"  {PeerText.Printable(file)}");
                Console.WriteLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages quote what the peer sent: a refused member name, a bad cpio
            // header, an HTTP line. LogLine cleans them.
            Console.Error.WriteLine(ConsoleText.ConnectionError(DateTime.Now, handshakeDone, ex.Message));
        }
        finally
        {
            await raw.DisposeAsync();
        }
    }

    return 0;
}

static Task<bool> AutoAcceptAsync(AirDropAskRequest request, CancellationToken ct)
{
    // --yes exists so the receiver can be driven from a script or a test. It
    // removes the consent prompt, which is the protocol's only real security
    // boundary, so it says so loudly rather than accepting in silence.
    foreach (string line in ConsoleText.DescribeRequest(request, autoAccepted: true))
        Console.WriteLine(line);

    return Task.FromResult(true);
}

static Task<bool> PromptAsync(AirDropAskRequest request, CancellationToken ct)
{
    Console.WriteLine();

    foreach (string line in ConsoleText.DescribeRequest(request, autoAccepted: false))
        Console.WriteLine(line);

    Console.Write("Accept? [y/N] ");
    string? answer = Console.ReadLine();

    bool accepted = answer?.Trim().StartsWith('y') == true;
    Console.WriteLine(accepted ? "Accepted." : "Declined.");

    return Task.FromResult(accepted);
}

static async Task<int> BrowseAsync(string[] args, CancellationToken ct)
{
    await using IAirDropTransport transport = CreateTransport(args);

    Console.WriteLine("Browsing for AirDrop peers (Ctrl+C to stop)...\n");

    using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
    window.CancelAfter(TimeSpan.FromSeconds(15));

    int found = 0;

    try
    {
        await foreach (AirDropPeer peer in transport.BrowseAsync(window.Token))
        {
            // The instance name is whatever the peer advertised over mDNS.
            Console.WriteLine($"  {PeerText.Printable(peer.ToString())}");
            found++;
        }
    }
    catch (OperationCanceledException) { }

    Console.WriteLine($"\n{found} peer(s).");

    if (found == 0)
        Console.WriteLine("No peers. An iPhone will never appear here - see docs/adr-001-transport-selection.md.");

    return 0;
}

static async Task<int> SendAsync(string[] args, CancellationToken ct)
{
    // Parse positionals by hand rather than filtering out anything starting with "--":
    // that filter drops the flag but keeps its value, so `send --to foo a.jpg` would
    // try to send a file literally named "foo".
    var paths = new List<string>();
    string? filter = null;
    string? peerHost = null;
    int peerPort = AirDropServiceRecord.DefaultPort;

    for (int i = 1; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--to", StringComparison.OrdinalIgnoreCase))
        {
            if (++i < args.Length) filter = args[i];
            continue;
        }

        if (string.Equals(args[i], "--bridge", StringComparison.OrdinalIgnoreCase))
        {
            if (++i < args.Length) { /* consumed by CreateTransport */ }
            continue;
        }

        if (string.Equals(args[i], "--icon", StringComparison.OrdinalIgnoreCase))
        {
            if (++i < args.Length) { /* read below, once the paths are validated */ }
            continue;
        }

        if (string.Equals(args[i], "--peer", StringComparison.OrdinalIgnoreCase))
        {
            if (++i < args.Length) peerHost = args[i];
            continue;
        }

        if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase))
        {
            if (++i < args.Length) int.TryParse(args[i], out peerPort);
            continue;
        }

        if (args[i].StartsWith("--")) continue;

        paths.Add(args[i]);
    }

    if (paths.Count == 0)
    {
        Console.Error.WriteLine("No files given.");
        return 1;
    }

    foreach (string path in paths)
    {
        if (File.Exists(path) || Directory.Exists(path)) continue;

        Console.Error.WriteLine($"Not found: {path}");
        return 1;
    }

    // --icon attaches a file's bytes, unexamined, as the /Ask preview. It is for the first
    // session against a real Apple receiver: the same send with no icon, with a JPEG, and
    // with opendrop's JPEG 2000 tells "the preview format matters" apart from everything
    // else that could make an iPhone refuse a request.
    byte[]? icon = null;

    if (ArgumentValue(args, "--icon") is { } iconPath)
    {
        if (!File.Exists(iconPath))
        {
            Console.Error.WriteLine($"Not found: {iconPath}");
            return 1;
        }

        icon = await File.ReadAllBytesAsync(iconPath, ct);
        Console.WriteLine($"Attaching a {icon.Length:N0}-byte preview ({PreviewImage.Sniff(icon)})");
    }

    await using IAirDropTransport transport = CreateTransport(args);

    AirDropPeer? target = null;

    if (peerHost is not null)
    {
        // Skip discovery entirely. Useful whenever multicast cannot reach the peer - a
        // WSL NAT boundary, a router that drops mDNS - since the app-layer protocol is
        // what this is meant to exercise, not our mDNS implementation.
        // Try a literal first. A scoped link-local like fe80::1%45 is exactly what
        // an IPv6-only peer needs, and pushing it through DNS resolution loses the
        // scope - which makes the address unroutable rather than merely wrong.
        IPAddress[] addresses = IPAddress.TryParse(peerHost, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(peerHost, ct);

        if (addresses.Length == 0)
        {
            Console.Error.WriteLine($"Could not resolve '{peerHost}'.");
            return 1;
        }

        // Without a TXT record the peer's capabilities are unknown, so assume it
        // supports nothing optional. That selects gzip over DVZip, which is the safe
        // assumption about an implementation that never told us what it understands.
        target = new AirDropPeer(
            peerHost,
            new IPEndPoint(addresses[0], peerPort),
            AirDropReceiverFlags.None,
            "direct");
    }
    else
    {
        Console.WriteLine("Looking for a peer...");

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await foreach (AirDropPeer peer in transport.BrowseAsync(window.Token))
            {
                if (filter is not null
                    && !peer.InstanceName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  skipping {PeerText.Printable(peer.InstanceName)} (does not match --to {filter})");
                    continue;
                }

                target = peer;
                break;
            }
        }
        catch (OperationCanceledException) { }
    }

    if (target is null)
    {
        Console.Error.WriteLine("No peer found.");
        return 1;
    }

    Console.WriteLine($"Sending to {PeerText.Printable(target.ToString())}");

    var files = paths.Select(AirDropOutgoingFile.FromPath).ToList();

    using X509Certificate2 certificate = AirDropCertificate.CreateSelfSigned(Environment.MachineName);

    await using Stream raw = await transport.ConnectAsync(target, ct);
    await using var ssl = await AirDropTls.AuthenticateAsClientAsync(raw, certificate, ct);
    await using var session = new AirDropSenderSession(ssl, target.Flags);

    AirDropReceiverIdentity? identity = await session.DiscoverAsync(ct);
    if (identity is not null)
        Console.WriteLine($"Peer identifies as '{PeerText.Printable(identity.ComputerName)}' ({PeerText.Printable(identity.ModelName)})");

    var request = new AirDropAskRequest(
        Environment.MachineName,
        "Windows",
        Guid.NewGuid().ToString(),
        AirDropAskRequest.FinderBundleId,
        files.Select(f => f.ToEntry()).ToList(),
        FileIcon: icon);

    Console.WriteLine("Waiting for the peer to accept...");

    if (!await session.AskAsync(request, ct))
    {
        Console.Error.WriteLine("Declined.");
        return 1;
    }

    Console.WriteLine($"Accepted. Uploading via {(session.UsesDvZip ? "DVZip" : "gzip")}...");
    long totalBytes = files.Sum(f => f.TotalBytes);
    var progress = new Progress<long>(sent =>
    {
        if (totalBytes > 0) Console.Write($"\r  {sent * 100 / totalBytes,3}%");
    });

    await session.UploadAsync(files, progress, ct);
    Console.WriteLine();

    Console.WriteLine("Done.");
    return 0;
}


static async Task<int> LinkAsync(string[] args, CancellationToken ct)
{
    string directory = ArgumentValue(args, "--dir")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinDrop");

    bool autoAccept = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
    int port = int.TryParse(ArgumentValue(args, "--port"), out int requested) ? requested : PhonePageServer.DefaultPort;

    var offers = new List<string>();
    for (int i = 1; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--offer", StringComparison.OrdinalIgnoreCase)) offers.Add(args[++i]);
    }

    foreach (string path in offers.Where(p => !File.Exists(p)))
    {
        Console.Error.WriteLine($"Not found, or a folder: {path}");
        return 1;
    }

    Directory.CreateDirectory(directory);

    // A fresh secret every run. The app keeps one across restarts so a Shortcut keeps
    // working; the CLI is for sessions, and a link that dies with the process is the safer
    // default for something whose output may end up pasted in a report.
    string token = PhonePageServer.NewToken();

    var server = new PhonePageServer(new PhonePageOptions
    {
        DownloadDirectory = directory,
        Token = token,
        ComputerName = Environment.MachineName,
        ConsentHandler = autoAccept ? AutoAcceptUploadAsync : PromptUploadAsync,
        Log = line => Console.WriteLine(ConsoleText.LogLine(DateTime.Now, line)),
    });

    int actualPort = server.Listen(port);

    if (offers.Count > 0)
    {
        foreach (PhoneOfferedFile file in server.OfferFiles(offers))
            Console.WriteLine($"Offering {file.Name} ({file.Size:N0} bytes)");
    }

    IReadOnlyList<IPAddress> addresses = PhonePageAddresses.Find();

    if (addresses.Count == 0)
    {
        Console.Error.WriteLine("This PC has no network address a phone could reach. Is it on Wi-Fi?");
        return 1;
    }

    string url = PhonePageServer.PageUrl(addresses[0].ToString(), actualPort, token);

    // The code is drawn in block characters, which the console needs UTF-8 to show.
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine();
    foreach (string line in ConsoleText.QrLines(QrCode.Encode(url))) Console.WriteLine(line);
    Console.WriteLine();
    Console.WriteLine("Scan it with the iPhone's Camera, on the same Wi-Fi as this PC, or open:");
    Console.WriteLine($"  {url}");

    foreach (IPAddress other in addresses.Skip(1))
        Console.WriteLine($"  or {PhonePageServer.PageUrl(other.ToString(), actualPort, token)}");

    Console.WriteLine();
    Console.WriteLine("Anyone with this link can ask to send you files and can download what is offered.");
    Console.WriteLine("It stops working when this does.");
    if (autoAccept) Console.WriteLine("--yes: uploads are accepted without asking. For testing only.");
    Console.WriteLine($"Saving to {directory}");
    Console.WriteLine("Ctrl+C to stop.");
    Console.WriteLine();

    await server.RunAsync(ct);
    return 0;
}

static Task<bool> AutoAcceptUploadAsync(PhoneUploadRequest request, CancellationToken ct)
{
    foreach (string line in ConsoleText.DescribeUpload(request, autoAccepted: true))
        Console.WriteLine(line);

    return Task.FromResult(true);
}

static async Task<bool> PromptUploadAsync(PhoneUploadRequest request, CancellationToken ct)
{
    // Uploads arrive on connections of their own, so two can ask at once; one console
    // cannot sensibly take two answers at the same time.
    await ConsoleText.PromptGate.WaitAsync(ct);

    try
    {
        Console.WriteLine();
        foreach (string line in ConsoleText.DescribeUpload(request, autoAccepted: false))
            Console.WriteLine(line);

        Console.Write("Accept? [y/N] ");
        string? answer = Console.ReadLine();

        bool accepted = answer?.Trim().StartsWith('y') == true;
        Console.WriteLine(accepted ? "Accepted." : "Declined.");
        return accepted;
    }
    finally
    {
        ConsoleText.PromptGate.Release();
    }
}

/// <summary>
/// Picks the transport. Without --bridge we drive the local radio over mDNS, which
/// reaches other WinDrop instances, opendrop and a Mac with BrowseAllInterfaces. With
/// it, a Linux box running OWL lends us its AWDL interface — the only route to an
/// iPhone, and the only one that has never been tested against a real device.
/// </summary>
static IAirDropTransport CreateTransport(string[] args)
{
    string? bridge = ArgumentValue(args, "--bridge");

    if (bridge is null) return new InfraWifiTransport();

    Console.WriteLine($"Using bridge at {bridge}");
    return new BridgeTransport(bridge);
}
static string? ArgumentValue(string[] args, string name)
{
    int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
