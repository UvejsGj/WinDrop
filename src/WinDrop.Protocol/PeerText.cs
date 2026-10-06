namespace WinDrop.Protocol;

/// <summary>
/// Text from a peer, made safe to show a person.
///
/// Every name in an AirDrop exchange is chosen by whoever is nearby, and nothing
/// authenticates it: the sender's computer and model names and the file names in /Ask, the
/// member names in the archive, the instance names in mDNS, and anything an exception message
/// quotes from them. Shown as it arrives, such a name can do more than lie about itself:
///
///   * In a terminal, an escape sequence can clear the screen, move the cursor or recolour
///     text, so a name can rewrite the very prompt that asks whether to accept it. A carriage
///     return manages the same with no escape at all, by going back to the start of the line
///     and printing over it. Some terminals also act on the single-byte C1 controls.
///   * Anywhere text is shown, a direction override reverses what follows it: "photo",
///     then U+202E, then "gpj.exe" is displayed as "photoexe.jpg".
///
/// Each such character becomes '?', so a person sees that something was there instead of
/// what it was arranged to look like. Right-to-left letters are untouched; only the
/// invisible formatting characters that reorder text are replaced, and emoji, which are
/// surrogate pairs and joiners rather than controls, pass through.
/// </summary>
public static class PeerText
{
    public static string Printable(string text) =>
        text.Any(IsUnsafe) ? new string(text.Select(c => IsUnsafe(c) ? '?' : c).ToArray()) : text;

    /// <summary>
    /// The characters this class replaces. Shared with the receiver, which keeps the same ones
    /// out of the names it saves files under, so what is shown and what is saved cannot drift.
    /// </summary>
    internal static bool IsUnsafe(char c) =>
        char.IsControl(c) // C0, DEL and C1, which include ESC, CR, LF and the one-byte CSI
        || c is '؜'  // Arabic letter mark
        || c is '‎' or '‏'                // left-to-right and right-to-left marks
        || c is >= '‪' and <= '‮'         // embeddings and overrides
        || c is >= '⁦' and <= '⁩';        // isolates
}
