"""Passively watch mDNS on the ordinary network and report everything advertised.

Does not rely on the _services._dns-sd meta-query, which may or may not be answered.
Just listens to the multicast group and parses whatever arrives, so anything that
speaks up is seen.

A WinDrop receiver should be running alongside this as a positive control: if the
control does not appear, the instrument is broken and a negative result means nothing.
"""

import socket
import struct
import time
from collections import defaultdict

from zeroconf import DNSIncoming

import sys

IFACE = sys.argv[1] if len(sys.argv) > 1 else "0.0.0.0"
DURATION = 45

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
sock.bind(("", 5353))
sock.setsockopt(
    socket.IPPROTO_IP,
    socket.IP_ADD_MEMBERSHIP,
    struct.pack("4s4s", socket.inet_aton("224.0.0.251"), socket.inet_aton(IFACE)),
)
sock.settimeout(2)

print(f"listening on 224.0.0.251:5353 for {DURATION}s\n")

by_source = defaultdict(set)
packets = 0
deadline = time.time() + DURATION

while time.time() < deadline:
    try:
        data, addr = sock.recvfrom(9000)
    except socket.timeout:
        continue

    packets += 1

    try:
        msg = DNSIncoming(data)
    except Exception:
        continue

    names = set()
    for q in getattr(msg, "questions", []) or []:
        names.add(f"?{q.name}")
    try:
        answers = msg.answers() if callable(getattr(msg, "answers", None)) else msg.answers
    except Exception:
        answers = []
    for rec in answers or []:
        names.add(rec.name)

    by_source[addr[0]] |= names

print(f"{packets} packets from {len(by_source)} source(s)\n")

for source in sorted(by_source):
    print(f"{source}")
    for name in sorted(by_source[source]):
        flag = ""
        low = name.lower()
        if "airdrop" in low:
            flag = "   <-- AIRDROP"
        elif any(k in low for k in ("companion-link", "apple-mobdev", "airplay", "raop", "rdlink")):
            flag = "   <-- apple service"
        print(f"    {name}{flag}")
    print()

everything = {n for names in by_source.values() for n in names}
airdrop = {n for n in everything if "airdrop" in n.lower()}

print("=" * 70)
print(f"names mentioning airdrop: {len(airdrop)}")
for n in sorted(airdrop):
    print(f"  {n}")
