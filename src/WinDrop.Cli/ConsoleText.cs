using System.Text;
using WinDrop.Protocol;
using WinDrop.Protocol.Web;

namespace WinDrop.Cli;

/// <summary>
/// The CLI's lines that carry a peer's words, built apart from the console writes so a test
/// can see exactly what a person would.
///
/// The consent prompt is the protocol's one real security boundary, and every name on it was
/// chosen by the sender: computer name, model, file names and types. Printed raw, a name
/// holding an escape sequence or a carriage return can rewrite the prompt that asks about it.
/// So every one passes through <see cref="PeerText.Printable"/>.
/// </summary>
internal static class ConsoleText
{
    /// <summary>What a person reads before answering, or what --yes accepted without asking.</summary>
    public static IReadOnlyList<string> DescribeRequest(AirDropAskRequest request, bool autoAccepted)
    {
        string sender = PeerText.Printable(request.SenderComputerName);

        var lines = new List<string>
        {
            autoAccepted
                ? $"Auto-accepting {request.Files.Count} file(s) from '{sender}' (--yes)"
                : $"'{sender}' ({PeerText.Printable(request.SenderModelName)}) wants to send:",
        };

        // Listed in full even with no prompt. Types and names are evidence too: a HEIC sent
        // unconverted in one share and converted to JPEG in another went unrecorded because
        // --yes used to print only a count.
        foreach (AirDropFileEntry file in request.Files)
            lines.Add($"  {PeerText.Printable(file.FileName)}  [{PeerText.Printable(file.FileType)}]");

        // Only the preview's size and signature. Decoding it would mean pointing an image codec
        // at a stranger's bytes from a console tool that has nothing to show them on.
        if (request.FileIcon is { } icon)
            lines.Add($"  preview: {icon.Length:N0} bytes, {PreviewImage.Sniff(icon)}");

        lines.Add($"  convert media formats: {(request.ConvertMediaFormats ? "yes" : "no")}");
        return lines;
    }

    /// <summary>
    /// A receiver log line, timestamped so a transfer's duration can be read straight off the
    /// log: from "request POST /Upload" to "upload complete". The receiver already cleans what
    /// it knows to be peer text, and the whole line is cleaned again here, because a log line
    /// is one line and never has a reason to hold a control character.
    /// </summary>
    public static string LogLine(DateTime time, string line) => $"  {time:HH:mm:ss} {PeerText.Printable(line)}";

    /// <summary>
    /// A connection that ended in an error, timestamped like the log and saying how far it got.
    ///
    /// The line used to be "Connection failed: ..." with no time and no stage. Session 13 saw
    /// three resets by the phone that read exactly like a failed transfer, and could neither
    /// place them in time nor tell where they happened. A reset during the TLS handshake is the
    /// phone dropping a connection before it said anything; one after it cut an exchange short.
    /// </summary>
    public static string ConnectionError(DateTime time, bool handshakeDone, string message) =>
        LogLine(time, handshakeDone
            ? $"connection failed: {message}"
            : $"connection failed during the TLS handshake, before any request: {message}");

    /// <summary>One prompt at a time: the phone page serves connections in parallel.</summary>
    public static readonly SemaphoreSlim PromptGate = new(1, 1);

    /// <summary>
    /// The phone page's version of <see cref="DescribeRequest"/>. The sender's label and the
    /// file names are the phone's to choose, so all of it is cleaned.
    /// </summary>
    public static IReadOnlyList<string> DescribeUpload(PhoneUploadRequest request, bool autoAccepted)
    {
        string sender = request.Sender.Length > 0 ? PeerText.Printable(request.Sender) : "A phone";

        var lines = new List<string>
        {
            autoAccepted
                ? $"Auto-accepting an upload from '{sender}' at {request.RemoteAddress} (--yes)"
                : $"'{sender}' at {request.RemoteAddress} wants to send:",
        };

        foreach (PhoneUploadFile file in request.Files)
            lines.Add($"  {PeerText.Printable(file.Name)}  ({file.Size:N0} bytes)");

        lines.Add($"  {request.TotalBytes:N0} bytes in all");
        return lines;
    }

    /// <summary>
    /// A QR code in block characters, two rows of modules per line so the modules come out
    /// roughly square.
    ///
    /// Light modules are drawn as blocks and dark ones as blanks, so on the usual dark
    /// terminal the code reads dark on light, the way scanners expect. The quiet zone is the
    /// standard's four modules, drawn light like the rest; without it a scanner cannot find
    /// the code's edge against a dark background.
    /// </summary>
    public static IReadOnlyList<string> QrLines(QrCode code)
    {
        const int quiet = 4;
        int size = code.Size + quiet * 2;

        bool Light(int row, int column)
        {
            if (row >= size) return false;

            int r = row - quiet, c = column - quiet;
            bool inside = r >= 0 && c >= 0 && r < code.Size && c < code.Size;
            return !inside || !code[r, c];
        }

        var lines = new List<string>();

        for (int row = 0; row < size; row += 2)
        {
            var line = new StringBuilder(size);

            for (int column = 0; column < size; column++)
            {
                line.Append((Light(row, column), Light(row + 1, column)) switch
                {
                    (true, true) => '█',  // full block
                    (true, false) => '▀', // upper half
                    (false, true) => '▄', // lower half
                    _ => ' ',
                });
            }

            lines.Add(line.ToString());
        }

        return lines;
    }
}
