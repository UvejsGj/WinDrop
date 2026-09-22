using System.Diagnostics;
using System.Globalization;
using WinDrop.Protocol.Archive;
using WinDrop.Protocol.Compression;
using WinDrop.Protocol.Discovery;
using WinDrop.Protocol.Http;
using WinDrop.Protocol.Plist;

namespace WinDrop.Protocol;

public sealed class AirDropReceiverOptions
{
    public string ComputerName { get; init; } = "WinDrop";
    public string ModelName { get; init; } = "Windows";
    public required string DownloadDirectory { get; init; }
    public AirDropReceiverFlags Flags { get; init; } = AirDropReceiverFlags.SupportsDvZip;

    /// <summary>
    /// Decides whether to accept a transfer. This is the security boundary of the whole
    /// protocol: TLS authenticates nobody, so a human saying yes is the only thing
    /// standing between an arbitrary peer on the link and the download directory.
    /// </summary>
    public required Func<AirDropAskRequest, CancellationToken, Task<bool>> ConsentHandler { get; init; }

    /// <summary>
    /// Receives one line per notable step of an upload: which encoding arrived, with its
    /// first bytes, and each archive member. Optional; the protocol behaves the same
    /// without it. It exists because some facts can only be observed during a real
    /// device's transfer. The first iPhone upload failed before anyone could tell which
    /// encoding it had used.
    /// </summary>
    public Action<string>? Log { get; init; }

    public const long DefaultMaxExtractedBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>The most an /Upload body may be as sent, before decompression.</summary>
    public long MaxUploadBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaxAskBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// The most an upload may unpack to, cpio headers included. Separate from
    /// <see cref="MaxUploadBytes"/> because the two are different quantities: deflate
    /// reaches about a thousand to one, so a body well inside the upload limit can still
    /// inflate without bound. Checked against each member's declared size as its header
    /// arrives, so a member that cannot fit is refused before any of it is written, and
    /// against the unpacked bytes as they are read, which catches what no header declares.
    /// </summary>
    public long MaxExtractedBytes { get; init; } = DefaultMaxExtractedBytes;

    /// <summary>
    /// The most members one archive may hold. Without it a bomb needs no large file at all:
    /// a million empty members is a million files, and the bookkeeping for each is memory.
    /// </summary>
    public int MaxArchiveMembers { get; init; } = 100_000;

    /// <summary>
    /// Answers /Ask with 200 before reading its body, then asks for consent before reading
    /// the upload. Without this, iOS times out the /Ask round trip on a slow AWDL link and
    /// reports a decline; with it, the same transfers succeed, which is why it is on by
    /// default. The cost is that the sender learns of a refusal at /Upload rather than at
    /// /Ask. See <see cref="AirDropReceiver"/>.
    /// </summary>
    public bool EarlyAskReply { get; init; } = true;
}

public sealed record AirDropTransferResult(IReadOnlyList<string> Files, long TotalBytes);

/// <summary>
/// The receiving half of AirDrop: /Discover, /Ask and /Upload over one TLS connection.
///
/// THE STATE MACHINE IS THE SECURITY MODEL. /Upload is refused unless /Ask was accepted
/// on this same connection. Without that rule a peer could skip straight to /Upload and
/// write files with no consent prompt at all, which is why the connection has to be
/// owned rather than handed to a pooling HTTP client that might move requests around.
/// </summary>
public sealed class AirDropReceiver(AirDropReceiverOptions options)
{
    private enum State { Fresh, Accepted, Completed }

    public AirDropReceiverFlags Flags => options.Flags;

    /// <summary>Serves one already-authenticated connection until the peer disconnects.</summary>
    public async Task<AirDropTransferResult?> HandleConnectionAsync(Stream tlsStream, CancellationToken ct = default)
    {
        await using var connection = new HttpConnection(tlsStream, ownsStream: false);

        var state = State.Fresh;
        AirDropAskRequest? accepted = null;
        AirDropTransferResult? result = null;

        // Set only in early-ask mode, where the answer to /Ask goes out before anyone has
        // been asked. It carries the decision that is still being made, and /Upload waits
        // on it before reading anything.
        Task<bool>? pendingConsent = null;

        while (await connection.ReadRequestHeadAsync(ct) is { } head)
        {
            // Compare on the path only; a peer may append a query string.
            string path = head.Target.Split('?', 2)[0].TrimEnd('/');

            // Logged as it arrives, not when the connection ends. A peer can keep the
            // connection open after a transfer, and a CLI that reports only at close then
            // looks idle while requests are still coming in.
            options.Log?.Invoke($"request {Printable(head.Method)} {Printable(path)} ({DescribeBody(head.Headers)})");

            switch (path)
            {
                case "/Discover":
                    await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    options.Log?.Invoke("-> 200");
                    await RespondPlistAsync(connection, 200, "OK",
                        new AirDropReceiverIdentity(options.ComputerName, options.ModelName).ToPlist(), ct);
                    break;

                case "/Ask":
                {
                    if (options.EarlyAskReply)
                    {
                        (state, accepted, pendingConsent) = await HandleEarlyAskAsync(connection, head.Headers, ct);
                        break;
                    }

                    // Timed because this round trip takes seconds over AWDL and sits right
                    // on the edge of iOS's patience: an /Ask answered too late shows on the
                    // phone as a decline. The parts are timed apart so the blame is visible
                    // — reading the body, which carries the preview image, then the consent
                    // decision, then writing the reply.
                    var askTimer = Stopwatch.StartNew();

                    byte[] body = await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    long bodyMs = askTimer.ElapsedMilliseconds;

                    options.Log?.Invoke($"/Ask body: {body.Length:N0} bytes read in {bodyMs:N0} ms");

                    if (BinaryPlistReader.Parse(body) is not IReadOnlyDictionary<string, object?> plist)
                        throw new AirDropHttpException("/Ask body was not a plist dictionary.");

                    AirDropAskRequest request = AirDropAskRequest.FromPlist(plist);

                    long consentStart = askTimer.ElapsedMilliseconds;

                    if (await options.ConsentHandler(request, ct))
                    {
                        state = State.Accepted;
                        accepted = request;

                        long consentMs = askTimer.ElapsedMilliseconds - consentStart;

                        await RespondPlistAsync(connection, 200, "OK",
                            new AirDropReceiverIdentity(options.ComputerName, options.ModelName).ToPlist(), ct);

                        long replyMs = askTimer.ElapsedMilliseconds - consentStart - consentMs;

                        options.Log?.Invoke(
                            $"-> 200 accepted (body {bodyMs:N0} ms, consent {consentMs:N0} ms, reply {replyMs:N0} ms, total {askTimer.ElapsedMilliseconds:N0} ms)");
                    }
                    else
                    {
                        // Apple signals refusal with 401. It does not mean "authenticate
                        // and retry" here; it means the human said no.
                        state = State.Fresh;
                        accepted = null;
                        options.Log?.Invoke("-> 401 declined");
                        await connection.WriteResponseAsync(401, "Unauthorized", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    }

                    break;
                }

                case "/Upload":
                {
                    if (state != State.Accepted || accepted is null)
                    {
                        // Named apart from a plain missing /Ask because it is an open question
                        // whether iOS sends a multi-item share as several uploads after one
                        // /Ask. If it does, this refusal is the bug, and the log should say so.
                        options.Log?.Invoke(state == State.Completed
                            ? "-> 401: a second /Upload on this connection, after one already completed"
                            : "-> 401: /Upload without an accepted /Ask on this connection");
                        // Drain so the connection stays framed, then refuse.
                        await using (Stream ignored = connection.OpenBody(head.Headers))
                            await ignored.CopyToAsync(Stream.Null, ct);

                        await connection.WriteResponseAsync(401, "Unauthorized", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                        break;
                    }

                    result = await ReceiveUploadAsync(connection, head.Headers, accepted, pendingConsent, ct);
                    state = State.Completed;

                    if (result is null)
                    {
                        // Early-ask only: the person said no after /Ask was answered, so the
                        // upload was drained unread. The refusal still has to reach the sender.
                        options.Log?.Invoke("-> 401: declined after /Ask was answered; nothing read or written");
                        await connection.WriteResponseAsync(401, "Unauthorized", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                        break;
                    }

                    options.Log?.Invoke($"upload complete: {result.Files.Count} file(s), {result.TotalBytes:N0} bytes -> 200");

                    await connection.WriteResponseAsync(200, "OK", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    break;
                }

                default:
                    await connection.ReadBodyAsync(head.Headers, options.MaxAskBodyBytes, ct);
                    options.Log?.Invoke("-> 404");
                    await connection.WriteResponseAsync(404, "Not Found", new HttpHeaders(), ReadOnlyMemory<byte>.Empty, ct);
                    break;
            }
        }

        options.Log?.Invoke("connection closed by peer");
        return result;
    }

    /// <summary>
    /// Early-ask: answer /Ask before reading its body, then ask the person while the upload
    /// is already arriving.
    ///
    /// Measured against iOS 27 (2026-09-19/20). The /Ask body is ~97% preview image and
    /// takes 3–19 s to arrive over an AWDL link this slow. iOS runs a timer on the /Ask
    /// round trip, and an answer that waits for the body lands too late: every such transfer
    /// came back as "Declined" on the phone. Replying first stops that clock — three of
    /// three succeeded afterwards, including one whose body took 8.3 s.
    ///
    /// It does *not* save the preview transfer. iOS sends the whole body regardless, so the
    /// RFC 9110 permission for a client to stop uploading after a final response is either
    /// unimplemented or unused here. The win is purely in the timing of the answer.
    ///
    /// Consent therefore moves rather than disappears. The 200 only says "go ahead and
    /// send"; the decision that matters is whether the upload is read, and /Upload waits on
    /// the returned task before reading any of it. Declining drains it unbuffered, leaves
    /// nothing on disk and answers with 401. An unapproved sender gets exactly what it gets
    /// without early-ask: the /Ask body, capped at <see cref="AirDropReceiverOptions.MaxAskBodyBytes"/>.
    ///
    /// The first version read the upload while the prompt was open and decided only before
    /// the write. That made a stranger able to fill our memory with an archive of any size
    /// before anyone said yes, which ruled it out as a default.
    ///
    /// NOT MEASURED: how long iOS tolerates an upload held back by TCP while a person
    /// decides. Every field session so far auto-accepted, so none of them waited.
    /// </summary>
    private async Task<(State, AirDropAskRequest?, Task<bool>?)> HandleEarlyAskAsync(
        HttpConnection connection, HttpHeaders headers, CancellationToken ct)
    {
        options.Log?.Invoke("early-ask: answering 200 before reading the body; the upload waits for consent");

        await RespondPlistAsync(connection, 200, "OK",
            new AirDropReceiverIdentity(options.ComputerName, options.ModelName).ToPlist(), ct);

        var timer = Stopwatch.StartNew();

        // If a sender ever does stop writing without a terminating chunk, the body read
        // would block forever, so the drain is bounded.
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drain.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            byte[] body = await connection.ReadBodyAsync(headers, options.MaxAskBodyBytes, drain.Token);

            options.Log?.Invoke(
                $"early-ask: body of {body.Length:N0} bytes arrived {timer.ElapsedMilliseconds:N0} ms after the answer");

            if (BinaryPlistReader.Parse(body) is IReadOnlyDictionary<string, object?> plist)
            {
                AirDropAskRequest request = AirDropAskRequest.FromPlist(plist);

                // Started, not awaited: the prompt runs while the upload streams in.
                Task<bool> consent = StartConsentAsync(request, ct);
                return (State.Accepted, request, consent);
            }

            options.Log?.Invoke("early-ask: the body was not a plist, so there is nothing to consent to");
        }
        catch (OperationCanceledException) when (drain.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            options.Log?.Invoke($"early-ask: no complete body {timer.ElapsedMilliseconds:N0} ms after the answer");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            options.Log?.Invoke($"early-ask: body read ended after {timer.ElapsedMilliseconds:N0} ms: {ex.Message}");
        }

        // No readable request means nothing to put in front of a person, so nothing may be
        // written. The connection still proceeds, so the refusal is observable rather than
        // a silent hang.
        return (State.Accepted,
            new AirDropAskRequest("unknown (early-ask)", "unknown", "", AirDropAskRequest.FinderBundleId, []),
            Task.FromResult(false));
    }

    /// <summary>Runs the consent handler, turning any failure into a refusal.</summary>
    private async Task<bool> StartConsentAsync(AirDropAskRequest request, CancellationToken ct)
    {
        try
        {
            return await options.ConsentHandler(request, ct);
        }
        catch (Exception ex)
        {
            options.Log?.Invoke($"consent failed, treating as declined: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads one upload. Returns null when it was declined after /Ask had been answered,
    /// which only happens in early-ask mode, where the answer preceded the decision.
    /// </summary>
    private async Task<AirDropTransferResult?> ReceiveUploadAsync(
        HttpConnection connection,
        HttpHeaders headers,
        AirDropAskRequest? consented,
        Task<bool>? pendingConsent,
        CancellationToken ct)
    {
        // The compression is NOT signalled in a header. opendrop sends only
        // "Content-Type: application/x-cpio" and gzips the body regardless, and an
        // earlier version of this code guessed from our own advertised capability
        // flags — which fed a gzip stream to the DVZip reader, whose first "block
        // length" came out as 0x1F8B0800, the gzip magic read as a big-endian int.
        //
        // All three encodings are self-identifying, so read the bytes instead of
        // guessing. Content-Encoding is still honoured when a peer bothers to send it,
        // but only as a cross-check we log, never as the deciding vote.
        await using Stream rawBody = connection.OpenBody(headers);
        var limited = new LimitedStream(rawBody, options.MaxUploadBytes);

        // Early-ask only: the decision is taken before a byte of the upload is read, not
        // after. Reading first would let every unapproved sender make us unpack an archive
        // of any size, and the app serves connections in parallel. Unread,
        // the upload waits in the socket and TCP holds the sender back, so an unapproved
        // sender costs no more than the /Ask body it already had to send.
        if (pendingConsent is not null && !await pendingConsent)
        {
            // Drained unbuffered so the connection stays framed and the refusal can reach
            // the sender as a response rather than a reset.
            await limited.CopyToAsync(Stream.Null, ct);
            options.Log?.Invoke("upload discarded unread: declined");
            return null;
        }

        var prefix = new byte[6];
        int sniffed = 0;

        while (sniffed < prefix.Length)
        {
            int read = await limited.ReadAsync(prefix.AsMemory(sniffed), ct);
            if (read == 0) break;
            sniffed += read;
        }

        Stream body = new PrefixedStream(prefix.AsMemory(0, sniffed), limited);

        // gzip: 1f 8b. cpio newc: the ASCII magic "070701". Anything else is taken as
        // DVZip, whose frame begins with a length rather than a recognisable magic.
        bool isGzip = sniffed >= 2 && prefix[0] == 0x1F && prefix[1] == 0x8B;
        bool isRawCpio = sniffed >= 6
            && prefix[0] == (byte)'0' && prefix[1] == (byte)'7' && prefix[2] == (byte)'0'
            && prefix[3] == (byte)'7' && prefix[4] == (byte)'0' && prefix[5] == (byte)'1';

        // The first bytes settle the question beyond the classification. DVZip should show
        // a four-byte block length followed by a zlib header (78 xx).
        options.Log?.Invoke(
            $"upload: {(isRawCpio ? "raw cpio" : isGzip ? "gzip" : "dvzip")}, first bytes {Convert.ToHexString(prefix, 0, sniffed)}");

        // Decoded as it is unpacked, never held whole. This used to inflate the entire
        // upload into a MemoryStream before extracting it, which put the whole transfer in
        // RAM and set no limit on how far a small compressed body could expand.
        await using Stream decoded = isRawCpio
            ? body
            : AirDropCompression.OpenDecompressor(body, isDvZip: !isGzip, options.Log);

        var unpacked = new LimitedStream(decoded, options.MaxExtractedBytes, "Unpacked upload");

        return await ExtractAsync(unpacked, consented, ct);
    }

    /// <summary>A member unpacked but not yet moved into place. Directories have no staged file.</summary>
    private sealed record StagedMember(string Name, string Destination, string? StagedPath, long Size);

    /// <summary>
    /// Unpacks a decompressed cpio archive into the download directory, streaming each
    /// member to disk as it is read.
    ///
    /// ALL OR NOTHING. Members are written into a hidden staging directory inside the
    /// download directory, and moved into place only once the archive has ended cleanly:
    /// its trailer read, and the stream after it read to its end. Until then nothing is
    /// visible, and any failure removes everything this upload wrote. The in-memory version
    /// had that property for free, since nothing reached disk until the whole upload had
    /// decompressed. Streaming straight to the destination would trade it away, and over
    /// AWDL a transfer dying part-way is the common case: a share of three photos would
    /// leave one and a half behind. Staging inside the download directory keeps the final
    /// move a rename on one volume.
    ///
    /// Because unpacking streams, "member" lines are logged as each header arrives, before
    /// its content. They say what the archive holds, not what has been saved; only "upload
    /// complete" says that.
    /// </summary>
    internal async Task<AirDropTransferResult> ExtractAsync(
        Stream archive,
        AirDropAskRequest? consented,
        CancellationToken ct)
    {
        Directory.CreateDirectory(options.DownloadDirectory);

        var reader = new CpioReader(archive);
        var staged = new List<StagedMember>();
        var committed = new List<string>();
        string staging = Path.Combine(options.DownloadDirectory, $".windrop-incoming-{Guid.NewGuid():N}");
        long declared = 0;
        bool complete = false;

        try
        {
            while (await reader.ReadNextAsync(ct) is { } entry)
            {
                options.Log?.Invoke(entry.IsDirectory
                    ? $"member {Printable(entry.Name)} (directory)"
                    : $"member {Printable(entry.Name)} ({entry.Size:N0} bytes)");

                // iOS 26.6 opens its upload archive with a directory member named ".": the
                // root the archive was built from (observed 2026-09-13). That is the download
                // directory itself, so there is nothing to create. Refusing it, as the first
                // real upload did, failed every transfer from an iPhone. Only a *directory*
                // gets this pass. A file claiming to be the root still reaches
                // ResolveSafePath and is refused there, which is the guard doing its job.
                if (entry.IsDirectory && IsArchiveRoot(entry.Name)) continue;

                if (staged.Count >= options.MaxArchiveMembers)
                    throw new AirDropHttpException($"Archive holds more than {options.MaxArchiveMembers:N0} members.");

                // Consent covers the names listed in /Ask. Apple's own archives also carry
                // members outside that list, the root skipped above among them, so a
                // mismatch is reported rather than refused. Reported, though: a person
                // agreeing to these files is the only thing standing behind writing them.
                if (consented is not null && !IsConsented(consented, entry.Name))
                    options.Log?.Invoke($"member {Printable(entry.Name)} was not in the accepted /Ask list");

                // Resolved before any content is read, so a member aimed outside the
                // download directory is refused without a byte of it touching the disk.
                string destination = ResolveSafePath(options.DownloadDirectory, entry.Name);

                if (entry.IsDirectory)
                {
                    staged.Add(new StagedMember(entry.Name, destination, null, 0));
                    continue;
                }

                // The size is the peer's claim, but the reader holds the member to it, so
                // the running total bounds what can be written. Checked before the content,
                // a header claiming four gigabytes costs nothing but the header.
                declared += entry.Size;

                if (declared > options.MaxExtractedBytes)
                {
                    throw new AirDropHttpException(
                        $"Archive members declare {declared:N0} bytes, over the {options.MaxExtractedBytes:N0} byte limit.");
                }

                if (!Directory.Exists(staging)) CreateStagingDirectory(staging);

                string stagedPath = Path.Combine(staging, staged.Count.ToString(CultureInfo.InvariantCulture));

                await using (var file = new FileStream(
                    stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await reader.CopyContentToAsync(file, ct);
                }

                staged.Add(new StagedMember(entry.Name, destination, stagedPath, entry.Size));
            }

            // The trailer is not the end of the body. What follows it is read too: an upload
            // cut off after its trailer is still a failed upload, bytes smuggled after it
            // still count against the unpacked limit, and DVZip's summary line is written
            // only when its stream ends.
            await archive.CopyToAsync(Stream.Null, ct);

            long total = 0;

            foreach (StagedMember member in staged)
            {
                if (member.StagedPath is null)
                {
                    Directory.CreateDirectory(member.Destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(member.Destination)!);
                committed.Add(MoveIntoPlace(member));
                total += member.Size;
            }

            complete = true;
            return new AirDropTransferResult(committed, total);
        }
        finally
        {
            // A commit that failed part-way takes back what it had already placed. Only
            // files this upload moved in are named here, never one that was there before.
            if (!complete)
            {
                foreach (string path in committed)
                    TryDelete(() => File.Delete(path), path);
            }

            if (Directory.Exists(staging))
                TryDelete(() => Directory.Delete(staging, recursive: true), staging);
        }
    }

    /// <summary>
    /// Moves a staged member to its destination, or beside it under the first free name.
    ///
    /// Two members can name the same file: iOS names every edited photo
    /// FullSizeRender.heic. Overwriting would hand over one file where the user accepted
    /// three, and say nothing about it. The move itself never overwrites either, so a name
    /// taken between the check and the move, by another transfer finishing at the same
    /// moment, fails the move and the next free name is tried.
    /// </summary>
    private string MoveIntoPlace(StagedMember member)
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
                options.Log?.Invoke(
                    $"member {Printable(member.Name)} saved as {Path.GetFileName(free)}; that name was taken");
            }

            return free;
        }
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
            options.Log?.Invoke($"could not remove {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// True for a member that names the archive root: ".", "./", "./.", and the like. An
    /// absolute "/" is not the root in this sense. It is an absolute path, and it is left
    /// for ResolveSafePath to refuse.
    /// </summary>
    internal static bool IsArchiveRoot(string name)
    {
        string relative = name.Replace('\\', '/');
        if (relative.StartsWith('/')) return false;

        while (relative.StartsWith("./", StringComparison.Ordinal))
            relative = relative[2..];

        return relative.TrimEnd('/') is "" or ".";
    }

    /// <summary>
    /// Member names come from an unauthenticated peer and end up in a terminal. Control
    /// characters are replaced, so a name cannot carry escape sequences to whoever reads
    /// the log.
    /// </summary>
    internal static string Printable(string text) =>
        new(text.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private static string DescribeBody(HttpHeaders headers) =>
        headers.IsChunked ? "chunked"
        : headers.ContentLength is { } length ? $"{length:N0} bytes"
        : "no body";

    /// <summary>Was this member among the files the user accepted?</summary>
    private static bool IsConsented(AirDropAskRequest request, string memberName)
    {
        string normalised = memberName.Replace('\\', '/');

        return request.Files.Any(file =>
            string.Equals(file.FileBomPath, normalised, StringComparison.Ordinal)
            || string.Equals(file.FileName, Path.GetFileName(normalised), StringComparison.Ordinal));
    }

    /// <summary>
    /// The given path, or the first free "name (n).ext" beside it, so a second member of
    /// the same name cannot replace the first.
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

        throw new AirDropHttpException($"Too many members named like '{Path.GetFileName(path)}'.");
    }

    /// <summary>
    /// Maps an archive member name onto a path inside the download directory, refusing
    /// anything that would escape it.
    ///
    /// The member names come from an unauthenticated peer, so this is the boundary that
    /// stops a transfer the user accepted for "photo.jpg" from also dropping a file into
    /// their startup folder. Checking for ".." by string match is not sufficient — the
    /// only reliable test is to resolve the full path and confirm it is still under the
    /// root.
    /// </summary>
    internal static string ResolveSafePath(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new AirDropHttpException("Archive member has an empty name.");

        if (entryName.Contains('\0'))
            throw new AirDropHttpException("Archive member name contains a NUL.");

        string relative = entryName.Replace('\\', '/');

        while (relative.StartsWith("./", StringComparison.Ordinal))
            relative = relative[2..];

        if (relative.Length == 0)
            throw new AirDropHttpException("Archive member resolves to the root directory.");

        if (Path.IsPathRooted(relative) || relative.StartsWith('/'))
            throw new AirDropHttpException($"Archive member '{entryName}' is an absolute path.");

        // A Windows drive-relative name like "C:evil" is not caught by IsPathRooted.
        if (relative.Length >= 2 && relative[1] == ':')
            throw new AirDropHttpException($"Archive member '{entryName}' names a drive.");

        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string candidate = Path.GetFullPath(Path.Combine(rootFull, relative));

        if (!candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new AirDropHttpException($"Archive member '{entryName}' escapes the download directory.");
        }

        if (string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new AirDropHttpException($"Archive member '{entryName}' resolves to the download directory itself.");

        return candidate;
    }

    private static async Task RespondPlistAsync(
        HttpConnection connection,
        int status,
        string reason,
        Dictionary<string, object?> payload,
        CancellationToken ct)
    {
        var headers = new HttpHeaders();
        headers.Set("Content-Type", "application/octet-stream");

        await connection.WriteResponseAsync(status, reason, headers, BinaryPlistWriter.Write(payload), ct);
    }
}

/// <summary>
/// Caps how many bytes a peer can push before we give up on the transfer. Used twice on an
/// upload: on the body as sent, and on what it decompresses to.
/// </summary>
internal sealed class LimitedStream(Stream inner, long limit, string what = "Upload") : Stream
{
    private long _read;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await inner.ReadAsync(buffer, ct);
        _read += read;

        if (_read > limit)
            throw new AirDropHttpException($"{what} exceeded the {limit} byte limit.");

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Replays a handful of bytes already read from a stream, then continues with the rest.
/// Needed because identifying the upload encoding means consuming its first bytes, and
/// the decompressor still has to see them.
/// </summary>
internal sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
{
    private int _offset;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset < prefix.Length)
        {
            int take = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.Slice(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }

        return await rest.ReadAsync(buffer, ct);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
