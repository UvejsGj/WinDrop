using WinDrop.Protocol;

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
}
