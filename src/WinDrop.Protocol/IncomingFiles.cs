using System.Globalization;

namespace WinDrop.Protocol;

/// <summary>
/// The files of one transfer, kept out of sight until all of them have arrived.
///
/// ALL OR NOTHING. Each file is written into a hidden staging directory inside the
/// download directory, and moved into place only by <see cref="Commit"/>, which the caller
/// reaches once the transfer has ended cleanly. Until then nothing is visible, and
/// disposing without a commit removes everything the transfer wrote. Over AWDL a transfer
/// dying part-way is the common case: a share of three photos would otherwise leave one
/// and a half behind. Staging inside the download directory keeps the final move a rename
/// on one volume.
///
/// Shared by both ways in: the AirDrop receiver's cpio archive and the phone page's form
/// upload. A second copy of this would be a second place for the guarantee to slip.
/// </summary>
internal sealed class IncomingFiles(string downloadDirectory, Action<string>? log) : IDisposable
{
    /// <summary>A file staged but not yet in place. Directories have no staged file.</summary>
    private sealed record Staged(string Name, string Destination, string? StagedPath, long Size);

    private readonly string _staging = Path.Combine(downloadDirectory, $".windrop-incoming-{Guid.NewGuid():N}");
    private readonly List<Staged> _staged = [];
    private readonly List<string> _committed = [];
    private bool _complete;

    /// <summary>Files and directories added so far.</summary>
    public int Count => _staged.Count;

    /// <param name="name">The name as the peer sent it, for the log.</param>
    /// <param name="destination">Where it goes, already checked to be inside the download directory.</param>
    public void AddDirectory(string name, string destination) =>
        _staged.Add(new Staged(name, destination, null, 0));

    /// <summary>Stages one file. Returns the number of bytes written.</summary>
    /// <param name="name">The name as the peer sent it, for the log.</param>
    /// <param name="destination">Where it goes, already checked to be inside the download directory.</param>
    /// <param name="writeContent">Writes the content; anything it throws fails the transfer.</param>
    public async Task<long> AddFileAsync(string name, string destination, Func<Stream, Task> writeContent)
    {
        if (!Directory.Exists(_staging)) CreateStagingDirectory(_staging);

        string stagedPath = Path.Combine(_staging, _staged.Count.ToString(CultureInfo.InvariantCulture));
        long size;

        await using (var file = new FileStream(
            stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await writeContent(file);
            size = file.Length;
        }

        _staged.Add(new Staged(name, destination, stagedPath, size));
        return size;
    }

    /// <summary>Moves everything into place. Call once the transfer has ended cleanly.</summary>
    public AirDropTransferResult Commit()
    {
        long total = 0;

        foreach (Staged member in _staged)
        {
            if (member.StagedPath is null)
            {
                Directory.CreateDirectory(member.Destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(member.Destination)!);
            _committed.Add(MoveIntoPlace(member));
            total += member.Size;
        }

        _complete = true;
        return new AirDropTransferResult(_committed.ToList(), total);
    }

    public void Dispose()
    {
        // A commit that failed part-way takes back what it had already placed. Only files
        // this transfer moved in are named here, never one that was there before.
        if (!_complete)
        {
            foreach (string path in _committed)
                TryDelete(() => File.Delete(path), path);
        }

        if (Directory.Exists(_staging))
            TryDelete(() => Directory.Delete(_staging, recursive: true), _staging);
    }

    /// <summary>
    /// Moves a staged file to its destination, or beside it under the first free name.
    ///
    /// Two files can share a name: iOS names every edited photo FullSizeRender.heic, and a
    /// browser upload of several photos may call each one image.jpg. Overwriting would hand
    /// over one file where the user accepted three, and say nothing about it. The move
    /// itself never overwrites either, so a name taken between the check and the move, by
    /// another transfer finishing at the same moment, fails the move and the next free name
    /// is tried.
    /// </summary>
    private string MoveIntoPlace(Staged member)
    {
        for (int attempt = 0; ; attempt++)
        {
            string free = Unused(member.Destination);

            try
            {
                File.Move(member.StagedPath!, free, overwrite: false);
            }
            catch (IOException) when (attempt < 16 && File.Exists(free))
            {
                continue;
            }

            if (free != member.Destination)
            {
                log?.Invoke(
                    $"member {PeerText.Printable(member.Name)} saved as {PeerText.Printable(Path.GetFileName(free))}; that name was taken");
            }

            return free;
        }
    }

    /// <summary>
    /// The given path, or the first free "name (n).ext" beside it, so a second file of the
    /// same name cannot replace the first.
    /// </summary>
    private static string Unused(string path)
    {
        if (!File.Exists(path)) return path;

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int n = 2; n < 10_000; n++)
        {
            string candidate = Path.Combine(directory, $"{name} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new Http.AirDropHttpException($"Too many files named like '{Path.GetFileName(path)}'.");
    }

    private static void CreateStagingDirectory(string path)
    {
        DirectoryInfo directory = Directory.CreateDirectory(path);

        // The leading dot hides it on Linux; Windows needs the attribute. It is not a place
        // a person should come across half a photo.
        if (OperatingSystem.IsWindows())
            directory.Attributes |= FileAttributes.Hidden;
    }

    /// <summary>Cleanup that must not replace the exception already on its way out.</summary>
    private void TryDelete(Action delete, string path)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"could not remove {PeerText.Printable(path)}: {PeerText.Printable(ex.Message)}");
        }
    }
}
