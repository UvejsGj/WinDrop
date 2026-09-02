"""Third-party validation of WinDrop's mDNS records.

python-zeroconf is an independent implementation of multicast DNS. Our own browser
finding our own responder proves only that they share any misreading, so this browses
for the service with someone else's code and checks that every field survives.
"""

import socket
import sys
import time

from zeroconf import ServiceBrowser, ServiceListener, Zeroconf

SERVICE = "_airdrop._tcp.local."


class Collector(ServiceListener):
    def __init__(self):
        self.found = {}

    def add_service(self, zc, type_, name):
        info = zc.get_service_info(type_, name, timeout=5000)
        if info is not None:
            self.found[name] = info

    def update_service(self, zc, type_, name):
        self.add_service(zc, type_, name)

    def remove_service(self, zc, type_, name):
        self.found.pop(name, None)


zc = Zeroconf()
collector = Collector()
browser = ServiceBrowser(zc, SERVICE, collector)

print(f"browsing {SERVICE} for 20s ...\n")
deadline = time.time() + 20
while time.time() < deadline and not collector.found:
    time.sleep(0.5)
time.sleep(2)  # let the resolve settle

browser.cancel()
zc.close()

if not collector.found:
    print("FAIL: python-zeroconf discovered nothing")
    sys.exit(1)

problems = []

for name, info in collector.found.items():
    print(f"instance : {name}")
    print(f"server   : {info.server}")
    print(f"port     : {info.port}")
    print(f"addresses: {info.parsed_addresses()}")
    print(f"props    : { {k.decode(): v.decode() for k, v in info.properties.items() if v is not None} }")
    print()

    if not name.endswith(SERVICE):
        problems.append(f"{name}: instance name not under {SERVICE}")
    if not info.server or not info.server.endswith(".local."):
        problems.append(f"{name}: server '{info.server}' is not a .local. name")
    if info.port != 8770:
        problems.append(f"{name}: port {info.port}, expected 8770")
    if not info.parsed_addresses():
        problems.append(f"{name}: resolved no addresses")
    if b"flags" not in info.properties:
        problems.append(f"{name}: TXT has no 'flags' key")
    else:
        raw = info.properties[b"flags"]
        try:
            flags = int(raw)
            print(f"flags decoded: {flags} (0x{flags:X})")
            if not flags & 0x02:
                problems.append(f"{name}: SupportsDvZip bit not set in flags {flags}")
        except (TypeError, ValueError):
            problems.append(f"{name}: flags {raw!r} is not an integer")

print()
if problems:
    print("FAIL:")
    for p in problems:
        print("  -", p)
    sys.exit(1)

print(f"PASS: python-zeroconf discovered and fully resolved {len(collector.found)} service(s)")
