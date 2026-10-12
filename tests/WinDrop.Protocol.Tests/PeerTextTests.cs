using WinDrop.Cli;

namespace WinDrop.Protocol.Tests;

/// <summary>
/// Names from a peer reach a person through a terminal or a window. These pin what may not
/// get through, and just as much what must: a sanitiser that mangles an ordinary iPhone name
/// would soon be worked around.
/// </summary>
public class PeerTextTests
{
    /// <summary>
    /// Every explicit formatting character in Unicode's bidirectional algorithm (UAX #9):
    /// the embeddings, overrides and isolates, their two pops, and the three implicit marks.
    /// Listed here from the standard rather than derived from the code under test, so the
    /// test cannot agree with a gap in it.
    /// </summary>
    private static readonly char[] BidiFormatting =
    [
        '‪', '‫', '‬', '‭', '‮', // LRE RLE PDF LRO RLO
        '⁦', '⁧', '⁨', '⁩',           // LRI RLI FSI PDI
        '‎', '‏', '؜',                     // LRM RLM ALM
    ];

    public static TheoryData<char> EachBidiFormattingCharacter()
    {
        var data = new TheoryData<char>();
        foreach (char c in BidiFormatting) data.Add(c);
        return data;
    }

    [Theory]
    [InlineData("\u001b[2J", "?[2J")]              // ESC: clear the screen
    [InlineData("a\rb", "a?b")]                    // CR: back to the line start, print over it
    [InlineData("a\nb", "a?b")]                    // LF: a fake second line
    [InlineData("a\u0007b", "a?b")]                // BEL
    [InlineData("a\u007Fb", "a?b")]                // DEL
    [InlineData("a\u009B2Jb", "a?2Jb")]            // C1 CSI, which some terminals act on alone
    [InlineData("photo‮gpj.exe", "photo?gpj.exe")] // displays as "photoexe.jpg" unless caught
    public void Unsafe_characters_become_a_visible_mark(string raw, string shown)
    {
        Assert.Equal(shown, PeerText.Printable(raw));
    }

    [Theory]
    [MemberData(nameof(EachBidiFormattingCharacter))]
    public void Every_bidi_formatting_character_is_replaced(char c)
    {
        Assert.Equal("a?b", PeerText.Printable($"a{c}b"));
    }

    [Fact]
    public void Every_control_character_is_replaced()
    {
        for (char c = '\0'; c <= 'ÿ'; c++)
        {
            if (char.IsControl(c))
                Assert.Equal("a?b", PeerText.Printable($"a{c}b"));
        }
    }

    [Theory]
    [InlineData("IMG_2350.HEIC")]
    [InlineData("Uvejs’s iPhone")]                 // the apostrophe iOS really uses
    [InlineData("Ålesund café – résumé.pdf")]
    [InlineData("صورة العائلة.jpg")]                // right-to-left letters are text, not controls
    [InlineData("משפחה.png")]
    [InlineData("📱 iPhone 👨‍👩‍👧")]                 // emoji, including a joined sequence
    [InlineData("")]
    public void Ordinary_names_pass_through_untouched(string name)
    {
        Assert.Equal(name, PeerText.Printable(name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_consent_prompt_shows_no_control_characters(bool autoAccepted)
    {
        // The request a hostile sender would craft: escape sequences and a carriage return in
        // the names the prompt is built from, and an override to disguise an executable.
        var request = new AirDropAskRequest(
            SenderComputerName: "Phone\u001b[2J\rAccept? [y/N] y",
            SenderModelName: "iPhone\u001b]0;pwned\u0007",
            SenderId: "id",
            BundleId: AirDropAskRequest.FinderBundleId,
            Files:
            [
                AirDropFileEntry.ForFile("photo‮gpj.exe", "public.jpeg\u001b[31m"),
                AirDropFileEntry.ForFile("notes\r\n.txt"),
            ]);

        IReadOnlyList<string> lines = ConsoleText.DescribeRequest(request, autoAccepted);

        Assert.All(lines, line => Assert.DoesNotContain(line, IsUnsafe));

        // Still legible: the person is warned, not left with nothing.
        string all = string.Join("\n", lines);
        Assert.Contains("Phone?[2J?Accept? [y/N] y", all);
        Assert.Contains("photo?gpj.exe", all);
        Assert.Contains("notes??.txt", all);
    }

    [Fact]
    public void Log_lines_are_cleaned_whole()
    {
        // The receiver cleans what it knows to be peer text; the CLI cleans every line again,
        // so a field the receiver forgot cannot reach the terminal either.
        string line = ConsoleText.LogLine(new DateTime(2026, 10, 6, 9, 41, 5), "request POST /Up\u001b[2Jload");

        Assert.Equal("  09:41:05 request POST /Up?[2Jload", line);
    }

    [Fact]
    public void A_failed_connection_says_when_and_how_far_it_got()
    {
        // Session 13's resets read exactly like failed transfers, with no time to place them.
        var at = new DateTime(2026, 10, 8, 0, 42, 7);
        const string reset = "Unable to read data from the transport connection: Connection reset by peer.";

        Assert.Equal(
            "  00:42:07 connection failed during the TLS handshake, before any request: " + reset,
            ConsoleText.ConnectionError(at, handshakeDone: false, reset));

        Assert.Equal(
            "  00:42:07 connection failed: Connection closed before a chunk header.",
            ConsoleText.ConnectionError(at, handshakeDone: true, "Connection closed before a chunk header."));

        // Still peer text: a refused member name can be quoted in the message.
        Assert.DoesNotContain('\u001b', ConsoleText.ConnectionError(at, true, "member '\u001b[2J' escapes"));
    }

    [Fact]
    public void The_radio_note_says_whether_an_iphone_can_be_reached_and_what_to_do()
    {
        // A receiver started before OWL joins no AWDL interface and never shows on the phone.
        // On Linux that has a fix worth printing; elsewhere the advice would be wrong.
        Assert.Contains("can be reached", ConsoleText.RadioNote(joinedAwdl: true, onLinux: true));

        string linux = ConsoleText.RadioNote(joinedAwdl: false, onLinux: true);
        Assert.Contains("no iPhone can be reached", linux);
        Assert.Contains("tools/owl-session.sh", linux);

        string elsewhere = ConsoleText.RadioNote(joinedAwdl: false, onLinux: false);
        Assert.Contains("no iPhone can be reached", elsewhere);
        Assert.DoesNotContain("owl-session", elsewhere);
    }

    [Fact]
    public void The_repeat_note_names_the_option_and_its_numbers()
    {
        // A session's record has to show how the receiver announced. The decimal point is
        // fixed, whatever the machine's language.
        Assert.Equal(
            "Repeating the announcement on AWDL: bursts of 64 copies 16.4 ms apart, 3 s between bursts (--repeat).",
            ConsoleText.RepeatNote(WinDrop.Protocol.Discovery.AnnouncementBurst.FixedRadio));
    }

    [Theory]
    [InlineData("awdl0/IPv6", true)]
    [InlineData("awdl1/IPv6", true)]
    [InlineData("wlan0/IPv6", false)]
    [InlineData("eth0/IPv4", false)]
    public void Only_an_awdl_interface_counts_as_reaching_an_iphone(string joined, bool expected) =>
        Assert.Equal(expected, WinDrop.Protocol.Discovery.InfraWifiTransport.IsAwdl(joined));

    private static bool IsUnsafe(char c) => char.IsControl(c) || BidiFormatting.Contains(c);
}
