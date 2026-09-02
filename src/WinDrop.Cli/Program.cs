using System.Net;
using System.Security.Cryptography.X509Certificates;
using WinDrop.Protocol;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Tls;

// WinDrop CLI. Two commands, both over the infrastructure-Wi-Fi transport:
//
//   receive [--dir <path>]   advertise over mDNS and accept transfers
//   send <file> [file...]    browse for a peer, ask, and upload
//   browse                   list peers and stop
//
// This reaches another WinDrop instance, opendrop, or a Mac running with
// BrowseAllInterfaces enabled. It does NOT reach an iPhone: iOS binds AirDrop's browser
// to awdl0, which Windows cannot join. See docs/adr-001-transport-selection.md.

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
        "browse" => await BrowseAsync(stopping.Token),
        _ => PrintUsage(),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nStopped.");
    return 0;
}

static int PrintUsage()
{
    Console.WriteLine("""
        WinDrop - an AirDrop implementation for Windows

          windrop receive [--dir <path>] [--yes]   advertise and accept transfers
          windrop send [--to <name>| --peer <host>] <file> [file...]
          windrop browse                   list nearby peers

        Reaches another WinDrop instance, opendrop, or a Mac with
        `defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true`.

        Does NOT reach an iPhone. iOS binds AirDrop discovery to awdl0, a link layer
        Windows cannot join. See docs/adr-001-transport-selection.md.
        """);

    return 1;
}

static async Task<int> ReceiveAsync(string[] args, CancellationToken ct)
{
    string directory = ArgumentValue(args, "--dir")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "WinDrop");

    bool autoAccept = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);

    Directory.CreateDirectory(directory);

    var flags = AirDropReceiverFlags.SupportsDvZip | AirDropReceiverFlags.SupportsMixedTypes;

    var receiver = new AirDropReceiver(new AirDropReceiverOptions
    {
        ComputerName = Environment.MachineName,
        DownloadDirectory = directory,
        Flags = flags,
        ConsentHandler = autoAccept ? AutoAcceptAsync : PromptAsync,
    });

    await using var transport = new InfraWifiTransport();

    string instance = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..6]}";
    var record = new AirDropServiceRecord(instance, AirDropServiceRecord.DefaultPort, flags);

    await using IAsyncDisposable advertisement = await transport.AdvertiseAsync(record, ct);

    Console.WriteLine($"Receiving as '{instance}' on port {record.Port}");
    Console.WriteLine($"Saving to {directory}");
    Console.WriteLine("Ctrl+C to stop.\n");

    using X509Certificate2 certificate = AirDropCertificate.CreateSelfSigned(Environment.MachineName);

    await foreach (Stream raw in transport.AcceptAsync(record.Port, ct))
    {
        // One connection at a time is deliberate: a consent prompt on the console cannot
        // sensibly be answered for two senders at once.
        try
        {
            await using var ssl = await AirDropTls.AuthenticateAsServerAsync(raw, certificate, ct);
            AirDropTransferResult? result = await receiver.HandleConnectionAsync(ssl, ct);

            if (result is not null)
            {
                Console.WriteLine($"Received {result.Files.Count} file(s), {result.TotalBytes:N0} bytes:");
                foreach (string file in result.Files) Console.WriteLine($"  {file}");
                Console.WriteLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Connection failed: {ex.Message}");
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
    Console.WriteLine($"Auto-accepting {request.Files.Count} file(s) from '{request.SenderComputerName}' (--yes)");
    return Task.FromResult(true);
}

static Task<bool> PromptAsync(AirDropAskRequest request, CancellationToken ct)
{
    Console.WriteLine();
    Console.WriteLine($"'{request.SenderComputerName}' ({request.SenderModelName}) wants to send:");

    foreach (AirDropFileEntry file in request.Files)
        Console.WriteLine($"  {file.FileName}  [{file.FileType}]");

    Console.Write("Accept? [y/N] ");
    string? answer = Console.ReadLine();

    bool accepted = answer?.Trim().StartsWith('y') == true;
    Console.WriteLine(accepted ? "Accepted." : "Declined.");

    return Task.FromResult(accepted);
}

static async Task<int> BrowseAsync(CancellationToken ct)
{
    await using var transport = new InfraWifiTransport();

    Console.WriteLine("Browsing for AirDrop peers (Ctrl+C to stop)...\n");

    using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
    window.CancelAfter(TimeSpan.FromSeconds(15));

    int found = 0;

    try
    {
        await foreach (AirDropPeer peer in transport.BrowseAsync(window.Token))
        {
            Console.WriteLine($"  {peer}");
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

    await using var transport = new InfraWifiTransport();

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
                    Console.WriteLine($"  skipping {peer.InstanceName} (does not match --to {filter})");
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

    Console.WriteLine($"Sending to {target}");

    var files = paths.Select(AirDropOutgoingFile.FromPath).ToList();

    using X509Certificate2 certificate = AirDropCertificate.CreateSelfSigned(Environment.MachineName);

    await using Stream raw = await transport.ConnectAsync(target, ct);
    await using var ssl = await AirDropTls.AuthenticateAsClientAsync(raw, certificate, ct);
    await using var session = new AirDropSenderSession(ssl, target.Flags);

    AirDropReceiverIdentity? identity = await session.DiscoverAsync(ct);
    if (identity is not null)
        Console.WriteLine($"Peer identifies as '{identity.ComputerName}' ({identity.ModelName})");

    var request = new AirDropAskRequest(
        Environment.MachineName,
        "Windows",
        Guid.NewGuid().ToString(),
        AirDropAskRequest.FinderBundleId,
        files.Select(f => f.ToEntry()).ToList());

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

static string? ArgumentValue(string[] args, string name)
{
    int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
