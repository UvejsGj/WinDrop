using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WinDrop.Protocol;
using WinDrop.Protocol.Archive;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Http;
using WinDrop.Protocol.Tls;
using Xunit;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// The whole stack over a real socket: TLS, our HTTP, bplist bodies, cpio, DVZip. Our
/// sender talking to our receiver cannot prove Apple compatibility — only a real device
/// can do that — but it does prove the layers compose, which is the thing that has to
/// be true before a real device is worth the trouble.
/// </summary>
public class AirDropFlowTests : IDisposable
{
    private readonly string _downloadDir =
        Path.Combine(Path.GetTempPath(), $"windrop-flow-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_downloadDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed record Harness(
        Task<AirDropTransferResult?> ServerTask,
        AirDropSenderSession Session,
        Func<Task> Shutdown);

    private async Task<Harness> StartAsync(
        Func<AirDropAskRequest, CancellationToken, Task<bool>> consent,
        AirDropReceiverFlags flags = AirDropReceiverFlags.SupportsDvZip,
        Action<string>? log = null)
    {
        var receiver = new AirDropReceiver(new AirDropReceiverOptions
        {
            DownloadDirectory = _downloadDir,
            ConsentHandler = consent,
            Flags = flags,
            Log = log,
        });

        X509Certificate2 serverCert = AirDropCertificate.CreateSelfSigned("WinDrop-Receiver");
        X509Certificate2 clientCert = AirDropCertificate.CreateSelfSigned("WinDrop-Sender");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using var ssl = await AirDropTls.AuthenticateAsServerAsync(accepted.GetStream(), serverCert);
            return await receiver.HandleConnectionAsync(ssl);
        });

        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var clientSsl = await AirDropTls.AuthenticateAsClientAsync(tcp.GetStream(), clientCert);

        var session = new AirDropSenderSession(clientSsl, flags);

        return new Harness(serverTask, session, async () =>
        {
            await session.DisposeAsync();
            await clientSsl.DisposeAsync();
            tcp.Dispose();
            listener.Stop();
            serverCert.Dispose();
            clientCert.Dispose();
        });
    }

    private static string WriteTempFile(string name, string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"windrop-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Full_flow_transfers_files_when_consent_is_given()
    {
        AirDropAskRequest? seen = null;

        Harness h = await StartAsync((request, _) =>
        {
            seen = request;
            return Task.FromResult(true);
        });

        try
        {
            AirDropReceiverIdentity? identity = await h.Session.DiscoverAsync();
            Assert.Equal("WinDrop", identity!.ComputerName);

            var files = new[]
            {
                AirDropOutgoingFile.FromPath(WriteTempFile("photo.jpg", new string('p', 5001))),
                AirDropOutgoingFile.FromPath(WriteTempFile("notes.txt", "hello from the sender")),
            };

            bool accepted = await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", Guid.NewGuid().ToString(),
                AirDropAskRequest.FinderBundleId,
                files.Select(f => f.ToEntry()).ToList()));

            Assert.True(accepted);
            await h.Session.UploadAsync(files);
        }
        finally
        {
            await h.Shutdown();
        }

        AirDropTransferResult? result = await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(result);
        Assert.Equal(2, result.Files.Count);

        // The metadata the user consented to must describe the bytes that arrived.
        Assert.Equal("Sender PC", seen!.SenderComputerName);
        Assert.Equal(["photo.jpg", "notes.txt"], seen.Files.Select(f => f.FileName));

        Assert.Equal(new string('p', 5001), await File.ReadAllTextAsync(Path.Combine(_downloadDir, "photo.jpg")));
        Assert.Equal("hello from the sender", await File.ReadAllTextAsync(Path.Combine(_downloadDir, "notes.txt")));
    }

    [Fact]
    public async Task Declining_consent_blocks_the_upload()
    {
        Harness h = await StartAsync((_, _) => Task.FromResult(false));

        try
        {
            var file = AirDropOutgoingFile.FromPath(WriteTempFile("unwanted.txt", "should never land"));

            bool accepted = await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", "id", AirDropAskRequest.FinderBundleId, [file.ToEntry()]));

            Assert.False(accepted);

            // The sender refuses locally as well, so a bug in one side is not enough.
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Session.UploadAsync([file]));
        }
        finally
        {
            await h.Shutdown();
        }

        await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(Directory.Exists(_downloadDir) && Directory.GetFiles(_downloadDir).Length > 0);
    }

    [Fact]
    public async Task The_preview_reaches_the_consent_prompt_intact()
    {
        // The receiver has to show the preview before the user answers, so it must be
        // fully available inside the consent callback — not something fetched later.
        byte[] icon = new byte[70_000];
        Random.Shared.NextBytes(icon);

        byte[]? seen = null;

        Harness h = await StartAsync((request, _) =>
        {
            seen = request.FileIcon;
            return Task.FromResult(false);
        });

        try
        {
            var file = AirDropOutgoingFile.FromPath(WriteTempFile("photo.jpg", "pixels"));

            await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", "id", AirDropAskRequest.FinderBundleId, [file.ToEntry()],
                FileIcon: icon));
        }
        finally
        {
            await h.Shutdown();
        }

        await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(icon, seen);
    }

    [Theory]
    [InlineData(AirDropReceiverFlags.SupportsDvZip, "upload: dvzip, first bytes ")]
    [InlineData(AirDropReceiverFlags.None, "upload: gzip, first bytes 1F8B")]
    public async Task The_receiver_reports_which_encoding_arrived(AirDropReceiverFlags flags, string expected)
    {
        var log = new List<string>();
        Harness h = await StartAsync((_, _) => Task.FromResult(true), flags, log.Add);

        try
        {
            var file = AirDropOutgoingFile.FromPath(WriteTempFile("logged.txt", "which encoding?"));

            Assert.True(await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", "id", AirDropAskRequest.FinderBundleId, [file.ToEntry()])));

            await h.Session.UploadAsync([file]);
        }
        finally
        {
            await h.Shutdown();
        }

        await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(log, line => line.StartsWith(expected, StringComparison.Ordinal));
        Assert.Contains("member ./logged.txt (15 bytes)", log);
    }

    [Fact]
    public async Task Each_request_and_its_answer_is_logged_as_it_happens()
    {
        var log = new List<string>();
        Harness h = await StartAsync((_, _) => Task.FromResult(true), log: log.Add);

        try
        {
            await h.Session.DiscoverAsync();

            var file = AirDropOutgoingFile.FromPath(WriteTempFile("traced.txt", "trace me"));

            Assert.True(await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", "id", AirDropAskRequest.FinderBundleId, [file.ToEntry()])));

            await h.Session.UploadAsync([file]);
        }
        finally
        {
            await h.Shutdown();
        }

        await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(log, line => line.StartsWith("request POST /Discover (", StringComparison.Ordinal));
        Assert.Contains(log, line => line.StartsWith("request POST /Ask (", StringComparison.Ordinal) && line.EndsWith(" bytes)"));
        Assert.Contains("-> 200 accepted", log);
        Assert.Contains("request POST /Upload (chunked)", log);
        Assert.Contains("upload complete: 1 file(s), 8 bytes -> 200", log);
        Assert.Equal("connection closed by peer", log[^1]);
    }

    [Fact]
    public async Task A_second_upload_after_one_completed_is_refused_and_named_in_the_log()
    {
        // Pins today's rule: one /Upload per accepted /Ask on a connection. Whether iOS
        // sends a multi-item share as several uploads is still open. If it does, this is
        // the test to change, and the log line is how the field test will show it.
        var log = new List<string>();
        Harness h = await StartAsync((_, _) => Task.FromResult(true), log: log.Add);

        try
        {
            var first = AirDropOutgoingFile.FromPath(WriteTempFile("first.txt", "one"));
            var second = AirDropOutgoingFile.FromPath(WriteTempFile("second.txt", "two"));

            Assert.True(await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", "id", AirDropAskRequest.FinderBundleId,
                [first.ToEntry(), second.ToEntry()])));

            await h.Session.UploadAsync([first]);
            await Assert.ThrowsAsync<AirDropHttpException>(() => h.Session.UploadAsync([second]));
        }
        finally
        {
            await h.Shutdown();
        }

        await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("-> 401: a second /Upload on this connection, after one already completed", log);
        Assert.False(File.Exists(Path.Combine(_downloadDir, "second.txt")));
    }

    [Fact]
    public async Task Upload_without_a_preceding_ask_is_refused_by_the_receiver()
    {
        // The property that makes consent meaningful. Driven with a raw connection so
        // the sender's own guard cannot mask a missing check on the receiving side.
        var receiver = new AirDropReceiver(new AirDropReceiverOptions
        {
            DownloadDirectory = _downloadDir,
            ConsentHandler = (_, _) => Task.FromResult(true),
        });

        using X509Certificate2 serverCert = AirDropCertificate.CreateSelfSigned("WinDrop-Receiver");
        using X509Certificate2 clientCert = AirDropCertificate.CreateSelfSigned("WinDrop-Sender");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using var ssl = await AirDropTls.AuthenticateAsServerAsync(accepted.GetStream(), serverCert);
            return await receiver.HandleConnectionAsync(ssl);
        });

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);

            await using var ssl = await AirDropTls.AuthenticateAsClientAsync(tcp.GetStream(), clientCert);
            await using var connection = new HttpConnection(ssl, ownsStream: false);

            await connection.WriteRequestAsync("POST", "/Upload", new HttpHeaders(), "not-an-archive"u8.ToArray());

            HttpResponseHead response = await connection.ReadResponseHeadAsync();
            await connection.ReadBodyAsync(response.Headers, 4096);

            Assert.Equal(401, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }

        AirDropTransferResult? result = await serverTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(result);
    }

    [Fact]
    public async Task Gzip_fallback_carries_the_transfer_when_the_peer_lacks_dvzip()
    {
        Harness h = await StartAsync((_, _) => Task.FromResult(true), AirDropReceiverFlags.None);

        try
        {
            Assert.False(h.Session.UsesDvZip);

            var file = AirDropOutgoingFile.FromPath(WriteTempFile("plain.txt", "gzip path"));

            Assert.True(await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", "id", AirDropAskRequest.FinderBundleId, [file.ToEntry()])));

            await h.Session.UploadAsync([file]);
        }
        finally
        {
            await h.Shutdown();
        }

        await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("gzip path", await File.ReadAllTextAsync(Path.Combine(_downloadDir, "plain.txt")));
    }

    [Fact]
    public async Task A_large_file_streams_through_the_whole_stack()
    {
        Harness h = await StartAsync((_, _) => Task.FromResult(true));

        string source = Path.Combine(Path.GetTempPath(), $"windrop-big-{Guid.NewGuid():N}.bin");
        var payload = new byte[3 * 1024 * 1024 + 7]; // deliberately not block-aligned
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(source, payload);

        try
        {
            var file = new AirDropOutgoingFile(source, "big.bin");

            Assert.True(await h.Session.AskAsync(new AirDropAskRequest(
                "Sender PC", "Windows", "id", AirDropAskRequest.FinderBundleId, [file.ToEntry()])));

            await h.Session.UploadAsync([file]);
        }
        finally
        {
            await h.Shutdown();
            File.Delete(source);
        }

        AirDropTransferResult? result = await h.ServerTask.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(payload.Length, result!.TotalBytes);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_downloadDir, "big.bin")));
    }
}

/// <summary>
/// Archive member names arrive from an unauthenticated peer. These are the names that
/// turn a transfer the user accepted for one file into a write somewhere else entirely.
/// </summary>
public class PathTraversalTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "windrop-root");

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("./../escape.txt")]
    [InlineData("subdir/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("subdir\\..\\..\\escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/evil.dll")]
    [InlineData("C:evil.txt")]
    [InlineData("./")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public void Escaping_names_are_refused(string name)
    {
        Assert.Throws<AirDropHttpException>(() => AirDropReceiver.ResolveSafePath(Root, name));
    }

    [Theory]
    [InlineData("./photo.jpg", "photo.jpg")]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("./deeper/photo.jpg", "deeper/photo.jpg")]
    [InlineData("./a/b/c/photo.jpg", "a/b/c/photo.jpg")]
    [InlineData("./dots..in..name.txt", "dots..in..name.txt")]
    public void Ordinary_names_resolve_inside_the_root(string name, string expectedRelative)
    {
        string resolved = AirDropReceiver.ResolveSafePath(Root, name);
        string expected = Path.GetFullPath(Path.Combine(Root, expectedRelative.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void A_name_that_merely_contains_dot_dot_is_still_allowed()
    {
        // Rejecting on a substring match for ".." would break legitimate names; the
        // check has to be on the resolved path, not on the text.
        string resolved = AirDropReceiver.ResolveSafePath(Root, "./my..file.txt");
        Assert.StartsWith(Path.GetFullPath(Root), resolved);
    }

    [Fact]
    public void Names_containing_nul_are_refused()
    {
        Assert.Throws<AirDropHttpException>(() => AirDropReceiver.ResolveSafePath(Root, "ok\0evil.txt"));
    }
}

/// <summary>
/// The archive-root member. iOS 26.6 opens its upload archive with a directory named ".",
/// and the first real iPhone transfer died on it. The fix has to let that exact shape
/// through without loosening the traversal guard for anything else.
/// </summary>
public class ArchiveRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"windrop-archive-root-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AirDropReceiver Receiver() => new(new AirDropReceiverOptions
    {
        DownloadDirectory = _root,
        ConsentHandler = (_, _) => Task.FromResult(true),
    });

    private static async Task<MemoryStream> ArchiveAsync(Func<CpioWriter, Task> build)
    {
        var output = new MemoryStream();
        var writer = new CpioWriter(output);
        await build(writer);
        await writer.CompleteAsync();
        output.Position = 0;
        return output;
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("./.")]
    public async Task A_root_directory_member_is_skipped_and_the_file_after_it_extracted(string rootName)
    {
        MemoryStream archive = await ArchiveAsync(async writer =>
        {
            await writer.WriteDirectoryAsync(rootName);
            await writer.WriteFileAsync("./IMG_2350.JPG", "jpeg bytes"u8.ToArray());
        });

        AirDropTransferResult result = await Receiver().ExtractAsync(archive, default);

        string expected = Path.Combine(_root, "IMG_2350.JPG");
        Assert.Equal(expected, Assert.Single(result.Files));
        Assert.Equal("jpeg bytes", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task A_file_claiming_to_be_the_root_is_still_refused()
    {
        MemoryStream archive = await ArchiveAsync(writer => writer.WriteFileAsync(".", "not a directory"u8.ToArray()));

        await Assert.ThrowsAsync<AirDropHttpException>(() => Receiver().ExtractAsync(archive, default));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("./..")]
    [InlineData("/")]
    [InlineData("../elsewhere")]
    public async Task Escaping_directory_members_are_still_refused(string name)
    {
        MemoryStream archive = await ArchiveAsync(writer => writer.WriteDirectoryAsync(name));

        await Assert.ThrowsAsync<AirDropHttpException>(() => Receiver().ExtractAsync(archive, default));
    }

    [Theory]
    [InlineData(".", true)]
    [InlineData("./", true)]
    [InlineData("./.", true)]
    [InlineData(".\\", true)]
    [InlineData("", true)]
    [InlineData("/", false)]
    [InlineData("..", false)]
    [InlineData("./photo.jpg", false)]
    [InlineData(".hidden", false)]
    public void Archive_root_detection(string name, bool isRoot)
    {
        Assert.Equal(isRoot, AirDropReceiver.IsArchiveRoot(name));
    }

    [Fact]
    public void Logged_names_cannot_carry_terminal_escapes()
    {
        Assert.Equal("a?[2Jb.txt", AirDropReceiver.Printable("a[2Jb.txt"));
    }
}
