using WinDrop.Protocol.Dns;

namespace WinDrop.Protocol.Discovery;

/// <summary>
/// An announcement sent many times over, for a radio that is held on one channel.
///
/// AWDL devices hop. A period is sixteen slots of 64 TU each, and a device spends each slot
/// on the channel its sequence names for it. OWL normally keeps the phone's schedule, and
/// an announcement sent once, in a slot both are on the same channel, is heard.
///
/// A radio that cannot leave its channel is different. tools/owl-exclusive.sh holds an Intel
/// card on channel 6, to get the card to acknowledge frames, and the OWL it runs sends
/// multicast the moment it is handed it. omdrop-owl (github.com/t4t5/omdrop-owl), where that
/// way of running the card comes from, saw a phone give channel 6 a slot or two in sixteen.
/// An announcement sent once then mostly goes out while the phone is elsewhere, and the
/// phone asks its own questions in slots it mostly spends on another channel, where this
/// radio does not hear them.
///
/// omdrop-owl answers that by sending a copy in every availability window, 16 TU, for one
/// whole period, so that whichever slot the phone spends here holds a few copies, and doing
/// so again after three seconds of quiet. The numbers below are its numbers. No WinDrop
/// session had run them when this was written (2026-10-12).
///
/// Never the default: where a peer shares the channel it is 64 datagrams doing the work of
/// two, and RFC 6762 asks for restraint with multicast.
/// </summary>
public sealed record AnnouncementBurst(int Copies, TimeSpan Spacing, TimeSpan Pause)
{
    /// <summary>One availability window: 16 time units of 1.024 ms.</summary>
    private static readonly TimeSpan AvailabilityWindow = TimeSpan.FromMilliseconds(16 * 1.024);

    /// <summary>A copy in each of the 64 windows of one period, then three seconds of quiet.</summary>
    public static AnnouncementBurst FixedRadio { get; } = new(64, AvailabilityWindow, TimeSpan.FromSeconds(3));

    /// <summary>
    /// Sends bursts until cancelled, and ends quietly when it is: it runs beside the
    /// receiver, and nothing waits on it but the shutdown.
    ///
    /// The spacing is a floor, since each wait is a timer and each send takes a moment, so a
    /// burst runs a little longer than a period. That is harmless: the point is a copy in
    /// every slot, and a slot is four windows long.
    /// </summary>
    internal async Task RepeatAsync(
        Func<DnsMessage> build,
        Func<DnsMessage, CancellationToken, Task> send,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;

        try
        {
            while (true)
            {
                // Built again for each burst, as every answer to a query is: the addresses
                // in it are read at that moment.
                DnsMessage announcement = build();

                for (int copy = 0; copy < Copies; copy++)
                {
                    if (copy > 0) await delay(Spacing, ct);
                    await send(announcement, ct);
                }

                await delay(Pause, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
