"""Actively query the Apple service types seen on this LAN and report who answers.

The passive sniff showed Apple devices *asking* for these. This asks for them too, and
records what actually responds — which is the set of channels an Apple device leaves
open on an ordinary network, and therefore the only place a non-AWDL workaround could
possibly live.
"""

import time
from zeroconf import ServiceBrowser, ServiceListener, Zeroconf

TYPES = [
    "_airdrop._tcp.local.",
    "_companion-link._tcp.local.",
    "_rdlink._tcp.local.",
    "_airplay._tcp.local.",
    "_raop._tcp.local.",
    "_apple-mobdev2._tcp.local.",
    "_touch-able._tcp.local.",
    "_sleep-proxy._udp.local.",
    "_smb._tcp.local.",
    "_afpovertcp._tcp.local.",
    "_ipp._tcp.local.",
    "_ipps._tcp.local.",
]


class Collector(ServiceListener):
    def __init__(self):
        self.found = {}

    def add_service(self, zc, type_, name):
        info = zc.get_service_info(type_, name, timeout=4000)
        if info:
            self.found[(type_, name)] = info

    def update_service(self, zc, type_, name):
        self.add_service(zc, type_, name)

    def remove_service(self, zc, type_, name):
        pass


zc = Zeroconf()
collector = Collector()
browsers = [ServiceBrowser(zc, t, collector) for t in TYPES]

print(f"querying {len(TYPES)} service types for 30s\n")
time.sleep(30)
for b in browsers:
    b.cancel()
zc.close()

if not collector.found:
    print("nothing answered")
else:
    for (type_, name), info in sorted(collector.found.items(), key=lambda x: x[0][1]):
        print(f"{name}")
        print(f"    host      {info.server}:{info.port}")
        print(f"    addresses {info.parsed_addresses()}")
        props = {}
        for k, v in (info.properties or {}).items():
            try:
                props[k.decode()] = v.decode() if v is not None else None
            except Exception:
                props[str(k)] = repr(v)
        if props:
            for k in sorted(props):
                print(f"      {k} = {props[k]}")
        print()

print("=" * 70)
print(f"{len(collector.found)} service instance(s) answered on ordinary Wi-Fi")
