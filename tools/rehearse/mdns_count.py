#!/usr/bin/env python3
"""Counts the mDNS datagrams that arrive on one interface, and how they are spaced.

    mdns_count.py <interface> <seconds>

Announcements (TTL 120) and goodbyes (TTL 0) are told apart by the first answer's TTL.
A gap of more than half a second starts a new group, so a burst shows as one line.
"""
import socket
import statistics
import struct
import sys
import time

interface, seconds = sys.argv[1], float(sys.argv[2])

sock = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
sock.bind(("::", 5353))
group = socket.inet_pton(socket.AF_INET6, "ff02::fb") + struct.pack("@I", socket.if_nametoindex(interface))
sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_JOIN_GROUP, group)
sock.settimeout(0.2)


def skip_name(data, at):
    while True:
        length = data[at]
        if length & 0xC0 == 0xC0:
            return at + 2
        if length == 0:
            return at + 1
        at += 1 + length


def describe(data):
    flags, questions, answers = struct.unpack("!HHH", data[2:8])
    if not flags & 0x8000:
        return "query"
    at = 12
    for _ in range(questions):
        at = skip_name(data, at) + 4
    if answers == 0:
        return "empty response"
    at = skip_name(data, at)
    ttl = struct.unpack("!I", data[at + 4:at + 8])[0]
    return "goodbye" if ttl == 0 else f"announcement ttl {ttl}"


start = time.monotonic()
seen = []
while time.monotonic() - start < seconds:
    try:
        data, _ = sock.recvfrom(9000)
    except socket.timeout:
        continue
    try:
        kind = describe(data)
    except Exception as error:  # a datagram this cannot read is still a datagram
        kind = f"unreadable ({error})"
    seen.append((time.monotonic() - start, kind))

print(f"{len(seen)} datagrams on {interface} in {seconds:.0f} s")
groups = []
for at, kind in seen:
    if groups and groups[-1][0] == kind and at - groups[-1][2][-1] < 0.5:
        groups[-1][2].append(at)
    else:
        groups.append((kind, at, [at]))
for kind, first, times in groups:
    gaps = [b - a for a, b in zip(times, times[1:])]
    spacing = f", median gap {statistics.median(gaps) * 1000:.1f} ms, longest {max(gaps) * 1000:.1f} ms" if gaps else ""
    print(f"  at {first:5.2f} s: {len(times):3d} x {kind}, over {times[-1] - times[0]:.2f} s{spacing}")
