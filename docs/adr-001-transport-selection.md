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

## Addendum, 2026-09-02: hardware survey and a threat to the premise

Researched after the app layer was finished. Three findings, in increasing order of
how much they matter.

### 1. The recommended adapter was subtly wrong

OWL's README recommends the **AR9280**, which is a *PCIe* part using `ath9k`. The
AR9271 named earlier in this project is a *USB* part using `ath9k_htc` — a different
driver with different capabilities.

OWL asks for **active monitor mode**, meaning the card acknowledges received frames.
`ath9k_htc` does not advertise that capability; there is an open firmware issue asking
for it with no resolution. However:

- OWL's own wording is that lacking it "might suffer from throughput degradation because
  the sender will re-transmit each frame up to 7 times" — degraded, not broken.
- Mathy Vanhoef's injection survey reports that Atheros/Qualcomm dongles, `ath9k_htc`
  among them, **do acknowledge frames in practice** even without the flag being exposed.

So the AR9271 is probably fine. One unquantified risk remains: that survey also notes
the stock `ath9k_htc` firmware **overwrites the sequence and fragment numbers of
injected frames**. Whether AWDL tolerates that is unknown; patched firmware exists.

**MediaTek is worth considering instead.** The same survey lists `mt76` and `mt7601u` as
supporting active monitor mode outright — the thing OWL actually asks for — and those
adapters are dual-band, where the AR9271 is 2.4 GHz only.

### 2. A modern Raspberry Pi may need no adapter at all

The 2019-era guidance was that only the Pi 3B worked, because nexmon had no frame
injection for the BCM43455 in the 3B+. That is out of date. Kali 2025.1 ships
`brcmfmac-nexmon-dkms` and `firmware-nexmon` with **monitor mode and frame injection**
tested on Pi 5, Pi 4, Pi 3B, Pi Zero 2 W and Pi Zero W, using the on-board radio.

Unknown: whether nexmon provides *active* monitor mode. Probably not, which puts it in
the same degraded-but-working category as the AR9271.

Also worth noting: **owlink.org is gone.** The domain now serves an unrelated gambling
site. Any link to it in these notes is dead, and the surviving documentation is
whatever is in the GitHub repositories.

### 3. The premise itself may be broken, independently of any hardware

This is the finding that matters, and it is not about radios.

**GoDrop**, a Go reimplementation of opendrop, archived August 2024, states plainly:
"This library only works until macOS Ventura and iOS 15. Apple apparently changed the
way their AirDrop protocol works."

**opendrop issue #80** reports being able to find and send to a Mac but *not* to
iPhones, unresolved.

**opendrop's own README** warns it "might be incompatible with future AirDrop versions."

The target here is **iOS 26.6** — many major versions past iOS 15.

Corroborating evidence from our own work: the beacon we captured carries version byte
**0x03** where the published research described `0x01`. We recorded that as a curiosity
at the time. Read alongside the above, it is consistent with the protocol having moved.

**Confidence: suggestive, not conclusive.** GoDrop's claim is one project's README with
no analysis behind it. Issue #80 has confounds — an unrelated `AttributeError` and
injection failures — so it may be a broken setup rather than a protocol change. Nobody
has published what Apple actually changed, or when.

**What this means for the hardware decision.** The adapter was previously framed as
buying iPhone interoperability. It should now be framed as buying *the ability to find
out whether iPhone interoperability is still possible at all*. That is a materially
different purchase, and the first thing to test is the cheapest one: whether the devices
can see each other, before any file is ever sent.


## Testing strategy consequences

Target **Everyone** mode, not Contacts Only: Contacts Only requires an Apple-issued
certificate chain and a validating contact-hash exchange in `/Discover` that we cannot
forge. Note iOS 16.2+ renamed this "Everyone for 10 Minutes" and auto-reverts — a real
ergonomic tax on iterative testing that we should design the test harness around.

**Unverified assumption to kill early:** that current iOS still accepts a non-Apple
AirDrop peer at all. OpenDrop is lightly maintained and recent iOS has hardened AirDrop.
Milestone 0 should be an interop smoke test against the actual target iPhone before we
build six layers on top of an assumption.
