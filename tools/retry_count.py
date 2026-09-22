#!/usr/bin/env python3
"""Counts how often the frames sent to this card were sent more than once.

    retry_count.py CAPTURE.pcap OUR_MAC [OUR_MAC ...]

Reads a radiotap pcap, as tcpdump writes from a monitor interface, and reports on the
data frames addressed to us: how many carry the 802.11 Retry bit, and how many times each
sequence number was seen.

Why: session 9's leading explanation for the flat ~16 KB/s is that the AX211 runs plain
monitor mode, not active monitor, so it never acknowledges the phone's unicast frames. A
sender that hears no ACK resends the frame, with the Retry bit set, until it gives up. If
that is happening, every frame shows up several times here. If the phone is being
acknowledged, each shows up about once and the Retry bit is rare.

Prints counts only. No address is ever printed: transmitters are labelled A, B, C, most
frames first, so the output can be photographed and shared as it is.
"""

import struct
import sys
from collections import Counter, defaultdict

RADIOTAP = 127
DATA = 2


def frames(path):
    """Yields each captured 802.11 frame, radiotap header removed."""
    with open(path, "rb") as capture:
        header = capture.read(24)
        if len(header) < 24:
            return

        # Classic pcap, either byte order, microsecond or nanosecond timestamps. tcpdump -w
        # writes this by default; pcapng is not handled, and is refused rather than misread.
        if header[:4] in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1"):
            order = "<"
        elif header[:4] in (b"\xa1\xb2\xc3\xd4", b"\xa1\xb2\x3c\x4d"):
            order = ">"
        else:
            sys.exit("not a classic pcap file")

        link = struct.unpack(order + "I", header[20:24])[0] & 0x0FFFFFFF
        if link != RADIOTAP:
            sys.exit(f"link type {link}, expected radiotap ({RADIOTAP}): was the interface in monitor mode?")

        while True:
            record = capture.read(16)
            if len(record) < 16:
                return

            length = struct.unpack(order + "I", record[8:12])[0]
            data = capture.read(length)
            if len(data) < length or len(data) < 4:
                return

            # Radiotap is little-endian whatever the file's byte order; its own length says
            # where the 802.11 header starts.
            radiotap_length = struct.unpack("<H", data[2:4])[0]
            yield data[radiotap_length:]


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)

    ours = {bytes.fromhex(address.replace(":", "")) for address in sys.argv[2:]}

    all_data = 0
    all_retried = 0
    to_us = Counter()
    to_us_retried = Counter()
    copies = defaultdict(Counter)

    for frame in frames(sys.argv[1]):
        if len(frame) < 24:
            continue

        control, flags = frame[0], frame[1]

        if (control >> 2) & 0x3 != DATA:
            continue
        if (control >> 4) & 0x4:
            continue  # null-function subtypes carry no data, only power-save signals
        if flags & 0x3 == 0x3:
            continue  # four-address frames are mesh or WDS, never AWDL

        retried = bool(flags & 0x08)
        all_data += 1
        all_retried += retried

        receiver, transmitter = frame[4:10], frame[10:16]
        if receiver not in ours:
            continue

        sequence = struct.unpack("<H", frame[22:24])[0] >> 4
        to_us[transmitter] += 1
        to_us_retried[transmitter] += retried
        copies[transmitter][sequence] += 1

    def share(part, whole):
        return f"{100 * part / whole:.0f}%" if whole else "n/a"

    print(f"data frames on the channel: {all_data}, with the Retry bit: {all_retried} ({share(all_retried, all_data)})")

    if not to_us:
        print("no data frames addressed to this card were captured.")
        print("was a transfer running during the capture, and was OWL up?")
        return

    print(f"data frames addressed to this card: {sum(to_us.values())}, from {len(to_us)} transmitter(s)")

    for label, (transmitter, count) in zip("ABCDEFGH", to_us.most_common(8)):
        distinct = len(copies[transmitter])
        print(
            f"  {label}: {count} frames, {to_us_retried[transmitter]} with the Retry bit "
            f"({share(to_us_retried[transmitter], count)}), {distinct} distinct sequence numbers, "
            f"so each frame seen {count / distinct:.1f} times on average")

    print("reading it, for the transmitter with the most frames (the phone):")
    print("  about 1.0 times each and few retries: the phone IS being acknowledged; missing ACKs are not the limit")
    print("  2 or more times each and mostly retries: the phone never hears an ACK and resends everything")


if __name__ == "__main__":
    main()
