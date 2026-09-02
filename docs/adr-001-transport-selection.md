# ADR-001: Transport selection for a Windows AirDrop implementation

Status: **proposed** — blocking decision before any code is written.
Date: 2026-09-02

## Context

The goal is a Windows-native app that interoperates with **real iPhones** over Apple's
AirDrop protocol. AirDrop is a five-layer stack; only the bottom layer is contentious:

| Layer | AirDrop | Portable to Windows? |
|---|---|---|
| Link / discovery | BLE wake-up + AWDL | **No — see below** |
| Network | IPv6 link-local (`fe80::`) | Yes |
| Security | TLS 1.2+, self-signed cert, no peer verification in Everyone mode | Yes |
| Transport | HTTP/1.1, port 8770 | Yes |
| Encoding | Apple binary plist (`bplist00`) | Yes |
| Compression | Apple chunked-zlib ("DVZip"), gzip fallback | Yes |
| Archive | CPIO `newc` | Yes |

Everything from IPv6 upward is transport-agnostic byte-wrangling. The architecture
question is exclusively about layer 1.

## Hardware findings (this machine, measured)

`netsh wlan show wirelesscapabilities`, Intel Wi-Fi 6E AX211, driver 24.50.0.4:

```
Network monitor mode                        : Not supported
Promiscuous Mode                            : Not Supported
IBSS                                        : Not Supported
Soft AP                                     : Not supported
Wi-Fi Direct Device / GO / Client           : Supported
P2P Device Discovery                        : Supported
Action Frame                                : Supported
```

Windows 11 build 26200. Bluetooth: Intel Wireless Bluetooth (BLE capable).
No Npcap. No .NET SDK (runtime 7.0.7 only). No real Python (Store alias stub only).

## Decision drivers

### 1. AWDL on Windows is not "hard", it is closed

OWL needs three things from the radio:

1. **Monitor mode** — to receive Apple vendor-specific action frames (AF) that carry
   AWDL's synchronization and election state.
2. **Raw 802.11 injection** — to transmit those AFs itself.
3. **Microsecond-accurate channel hopping** — AWDL peers only listen during periodic
   Availability Windows derived from a shared master clock; miss the window, the peer
   never hears you.

The adapter reports (1) and promiscuous mode as *not supported*, and Windows' NDIS/WDI
model exposes no userspace OID for (3) or for injecting an arbitrary vendor action
frame. Npcap wouldn't rescue this: its raw-802.11 support is a passthrough to a driver
capability this driver does not have. This is not an Intel-specific gap that a different
Windows card fixes — it is the NDIS abstraction. `AWDL on Windows = requires a custom
driver.`

### 2. The BLE advertisement is a *wake-up*, not a transport announcement

This is the load-bearing finding, and it is what invalidates path B.

Apple's AirDrop discovery beacon is a **Continuity** BLE advertisement: manufacturer-
specific data, company ID `0x004C` (Apple), a type byte identifying the Continuity
message (AirDrop is type `0x05`), then a short payload carrying a version byte and
truncated SHA-256 prefixes of the sender's contact identifiers (phone numbers / emails),
used for the Contacts-Only match.

*(Exact byte offsets are TO BE CONFIRMED by capture — that's real RE work for us, not
something to take on faith from a blog post.)*

Its semantics are: **"an AirDrop sender is nearby — power up your AWDL interface and
start browsing `_airdrop._tcp.local` on `awdl0`."** There is no field in the
advertisement that names a transport, a channel, an SSID, or an address. A byte-perfect
replica broadcast from Windows makes the iPhone light up **awdl0** — a radio link
Windows cannot join. We would have successfully rung a doorbell on a house with no door.

### 3. iOS does not implement Wi-Fi Direct

Apple has never shipped Wi-Fi Alliance P2P on iOS; AWDL *is* their replacement for it.
An iPhone will not do GO negotiation and will not join a P2P group as a P2P client.
(A P2P GO is structurally a soft-AP, so an iPhone could in principle join one as a
*legacy* station — but only via manual network selection in Settings, and per (2) iOS
still won't browse AirDrop on that interface.)

### 4. `sharingd` binds AirDrop to AWDL — with one exception, and it isn't iOS

macOS has an undocumented override that makes the AirDrop browser use every interface
instead of just `awdl0`:

```
defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true
```

This is how OpenDrop is normally tested against Apple software. **iOS has no equivalent
escape hatch.** So: a Mac is a usable app-layer test peer over ordinary Wi-Fi/Ethernet;
an iPhone is not, ever, without AWDL.

## Consequences

- **Path B (Wi-Fi Direct substitution) cannot produce iPhone interop.** It is still a
  real project — a Windows↔Windows/Android AirDrop-*style* transfer app — and the WinRT
  work is genuine. It just is not the stated goal. Any existing "AirDrop on Windows"
  repo should be read with one litmus test: *does it ever demonstrate a transfer with an
  unmodified iPhone, or only with its own peers?*
- **Path A (OWL bridge) is the only route to real iPhone interop**, and it does not have
  to mean "the app isn't Windows-native" — see the radio-head design below.
- Layers 2–7 are identical under every path, so they can and should be built first,
  behind a transport interface, before the transport question is settled.

## Proposed architecture: pluggable transport

```
        Windows app (all protocol logic, all crypto)
        bplist · TLS · HTTP · Discover/Ask/Upload · DVZip · CPIO
                              |
                      ITransport interface
          advertise() · browse() · connect(peer) -> Stream
                              |
        +---------------------+---------------------+
        |                     |                     |
  InfraWifiTransport    BridgeTransport      WifiDirectTransport
  mDNS on the LAN       relay to OWL box     WinRT P2P (experimental)
  peers: Mac w/         peers: REAL          peers: our own
  BrowseAllInterfaces,  IPHONES              Windows/Android peers
  opendrop
```

### The radio-head bridge (keeps the app genuinely Windows-native)

The Linux box is demoted from "where the app runs" to "a peripheral that owns an
antenna" — conceptually a USB dongle that happens to speak Ethernet:

- Pi/old laptop + AWDL-capable card (AR9280 / AR9271 class) runs OWL → real `awdl0`.
- A thin daemon on it does two dumb jobs: (a) publish/browse `_airdrop._tcp.local` on
  `awdl0` on our behalf, (b) TCP-relay port 8770 in both directions.
- **TLS terminates on Windows.** The relay moves opaque bytes; it never holds our key,
  never sees plaintext, and contains zero protocol logic. Every layer we care about
  learning lives in the Windows app.
- The iPhone connects to the relay's `awdl0` link-local address:8770; that address is
  what goes in our mDNS record.

Cost: ~$15–30 for a used Atheros card, plus a Pi/spare laptop.

## Testing strategy consequences

Target **Everyone** mode, not Contacts Only: Contacts Only requires an Apple-issued
certificate chain and a validating contact-hash exchange in `/Discover` that we cannot
forge. Note iOS 16.2+ renamed this "Everyone for 10 Minutes" and auto-reverts — a real
ergonomic tax on iterative testing that we should design the test harness around.

**Unverified assumption to kill early:** that current iOS still accepts a non-Apple
AirDrop peer at all. OpenDrop is lightly maintained and recent iOS has hardened AirDrop.
Milestone 0 should be an interop smoke test against the actual target iPhone before we
build six layers on top of an assumption.
