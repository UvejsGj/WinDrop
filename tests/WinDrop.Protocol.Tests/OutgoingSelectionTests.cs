using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using WinDrop.Protocol;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Tls;
using Xunit;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// A selection is one item as the user sees it. A folder is one thing to consent to but
/// many entries in the archive, and the mapping between those two views is where a
/// folder transfer goes wrong.
/// </summary>
public class OutgoingSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"windrop-sel-{Guid.NewGuid():N}");

    public OutgoingSelectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string MakeFile(string relative, string content)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_single_file_is_one_member()
    {
        string path = MakeFile("solo.txt", "hello");
        var selection = AirDropOutgoingFile.FromPath(path);

        Assert.False(selection.IsDirectory);
        Assert.Equal("./solo.txt", selection.BomPath);

        ArchiveMember member = Assert.Single(selection.EnumerateMembers());
        Assert.Equal("./solo.txt", member.BomPath);
        Assert.False(member.IsDirectory);
        Assert.Equal(5, member.Length);
    }

    [Fact]
    public void A_folder_expands_to_itself_plus_its_tree()
    {
        MakeFile("pics/a.txt", "aaa");
        MakeFile("pics/nested/b.txt", "bb");
        MakeFile("pics/nested/deeper/c.txt", "c");

        var selection = AirDropOutgoingFile.FromPath(Path.Combine(_root, "pics"));
        Assert.True(selection.IsDirectory);

        var members = selection.EnumerateMembers().ToList();
        var paths = members.Select(m => m.BomPath).ToHashSet();

        Assert.Contains("./pics", paths);
        Assert.Contains("./pics/a.txt", paths);
        Assert.Contains("./pics/nested", paths);
        Assert.Contains("./pics/nested/b.txt", paths);
        Assert.Contains("./pics/nested/deeper", paths);
        Assert.Contains("./pics/nested/deeper/c.txt", paths);

        // Only files carry bytes; the total must not count directories.
        Assert.Equal(6, selection.TotalBytes);
    }

    [Fact]
    public void Parents_precede_their_children()
    {
        // A receiver creating directories as it walks the archive depends on this. If a
        // child arrived first it would have to infer the parent, or fail.
        MakeFile("tree/one/two/leaf.txt", "x");

        var members = AirDropOutgoingFile.FromPath(Path.Combine(_root, "tree"))
            .EnumerateMembers()
            .Select(m => m.BomPath)
            .ToList();

        foreach (string path in members)
        {
            int slash = path.LastIndexOf('/');
            if (slash <= 1) continue; // "./tree" has no parent inside the archive

            string parent = path[..slash];
            Assert.True(
                members.IndexOf(parent) < members.IndexOf(path),
                $"'{parent}' must appear before '{path}'");
        }
    }

    [Fact]
    public void An_empty_folder_still_produces_its_own_entry()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        ArchiveMember member = Assert.Single(
            AirDropOutgoingFile.FromPath(Path.Combine(_root, "empty")).EnumerateMembers());

        Assert.True(member.IsDirectory);
        Assert.Equal("./empty", member.BomPath);
        Assert.Null(member.LocalPath);
    }

    [Fact]
    public void A_trailing_separator_does_not_produce_an_empty_name()
    {
        Directory.CreateDirectory(Path.Combine(_root, "folder"));

        var selection = AirDropOutgoingFile.FromPath(Path.Combine(_root, "folder") + Path.DirectorySeparatorChar);

        Assert.Equal("folder", selection.FileName);
        Assert.Equal("./folder", selection.BomPath);
    }

    [Fact]
    public void A_folder_is_advertised_as_a_directory_in_the_ask()
    {
        Directory.CreateDirectory(Path.Combine(_root, "shared"));

        AirDropFileEntry entry = AirDropOutgoingFile.FromPath(Path.Combine(_root, "shared")).ToEntry();

        Assert.True(entry.IsDirectory);
        Assert.Equal("./shared", entry.FileBomPath);
    }

    [Fact]
    public async Task A_folder_survives_a_real_transfer_with_its_tree_intact()
    {
        MakeFile("album/one.txt", "first");
        MakeFile("album/sub/two.txt", "second");
        MakeFile("album/sub/deep/three.txt", new string('x', 4097));

        string downloads = Path.Combine(_root, "downloads");

        var receiver = new AirDropReceiver(new AirDropReceiverOptions
        {
            DownloadDirectory = downloads,
            ConsentHandler = (_, _) => Task.FromResult(true),
        });

        using X509Certificate2 serverCert = AirDropCertificate.CreateSelfSigned("Receiver");
        using X509Certificate2 clientCert = AirDropCertificate.CreateSelfSigned("Sender");

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
            await using var session = new AirDropSenderSession(ssl, AirDropReceiverFlags.SupportsDvZip);

            var selection = AirDropOutgoingFile.FromPath(Path.Combine(_root, "album"));

            Assert.True(await session.AskAsync(new AirDropAskRequest(
                "Sender", "Windows", "id", AirDropAskRequest.FinderBundleId, [selection.ToEntry()])));

            await session.UploadAsync([selection]);
        }
        finally
        {
            listener.Stop();
        }

        await serverTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(downloads, "album", "one.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(downloads, "album", "sub", "two.txt")));
        Assert.Equal(4097, (await File.ReadAllTextAsync(Path.Combine(downloads, "album", "sub", "deep", "three.txt"))).Length);
    }
}
