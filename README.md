# WinDrop

An AirDrop implementation for Windows, built from scratch by reverse-engineering the
protocol. Learning project: understanding over shortcuts, no wrapping of existing tools.

## Status

| Milestone | State |
|---|---|
| 0. Capture Apple's Continuity BLE beacon | **done** — captured on iOS 26.6, contact-hash field confirmed |
| 1. Repo scaffold + transport seam | **done** |
| 2. Binary plist (`bplist00`) encode/decode | **done** — differentially checked against Python `plistlib` |
| 3. Self-signed TLS + minimal HTTP/1.1 | **done** |
| 4. Discover → Ask → Upload state machine | **done** — verified against opendrop, an independent implementation |
| 5. DVZip compression + CPIO `newc` archive | **done** — CPIO checked against bsdtar/libarchive |
| 6. Reaching an iPhone | **working, slowly** — real iPhones on iOS 26.6 and 27.0 have AirDropped files to WinDrop, arriving intact. Over Linux + OWL, at ~16 KB/s on the hardware tested. See [where it stands](#where-it-stands-with-an-iphone) |

198 tests, all passing.

## The one thing to understand first

AirDrop's discovery runs over **AWDL**, a second Wi-Fi link layer that time-slices the
radio against your normal AP connection on a schedule synchronised across every Apple
device in range. Windows' NDIS stack exposes no way to drive the 802.11 MAC that way,
and iOS implements no alternative — it has never supported Wi-Fi Direct, and binds
AirDrop's browser to `awdl0` with no override.

This was argued structurally in [ADR-001](docs/adr-001-transport-selection.md) and then
confirmed by measurement: the captured beacon is 18 bytes, every one of them accounted
for, with no field in which to name a channel, an address or a transport. **The beacon
cannot redirect a peer anywhere.** It can only mean "wake AWDL".

So everything above the link layer is built, tested and working, and has been proven
against a real iPhone. The link layer is what limits it, and that is a hardware question,
not a software one.

## Where it stands with an iPhone

An iPhone has sent files to WinDrop's receiver running on Linux, with
[OWL](https://github.com/seemoo-lab/owl) providing `awdl0`. Photos arrived byte-for-byte
intact on iOS 26.6 and iOS 27.0: one image, several images in one share, and PNGs whose
upload mixed compressed and stored DVZip blocks. The log of every session is in
[protocol-notes.md](docs/protocol-notes.md).

| | |
|---|---|
| iPhone → WinDrop | **works**, verified on real devices |
| WinDrop → iPhone | **untested**. The sender is proven only against opendrop |
| Speed | **21–27 KB/s** on the one card tested (Intel AX211) at 20 MHz, 15–40 KB/s across all sessions. A 2.5 MB photo takes ~100 s. Largest to arrive: 3.6 MB as one file, 6.4 MB as a four-photo share (267 s). ~25 MB fails |
| Phone setting | **Everyone for 10 Minutes** only. Contacts Only requires an Apple-issued identity, which a non-Apple device cannot hold |
| Windows alone | **cannot reach an iPhone** (see above). The radio has to be Linux: booted directly, or later a bridge |

**The speed is the radio, not the protocol, and that is measured.** The AX211 can only
run plain monitor mode, not *active* monitor mode, so it never acknowledges the frames the
phone sends it. The phone takes every frame as lost and sends it again until its retry
limit. Six captures in session 10 all showed the same thing: 86% of the phone's frames to
us were resends, and each frame went out about seven times. Nine sessions of software and
configuration changes could not move the rate, because none of them could make this card
acknowledge. The record of what was tried and why each failed is in the notes.

The fix is an adapter whose Linux driver supports active monitor mode, typically MediaTek
on the `mt76` driver. [bridge-hardware-setup.md](docs/bridge-hardware-setup.md) has the
criterion and a one-line test for any card.

## Running it

```
dotnet build
dotnet run --project src/WinDrop.App              # the GUI
dotnet run --project src/WinDrop.Cli -- receive
dotnet run --project src/WinDrop.Cli -- send path\to\file.jpg
dotnet run --project src/WinDrop.Cli -- browse
```

`receive` takes `--dir <path>`, `--yes` (skip the consent prompt — it is the only real
security boundary in the protocol, so only for scripted testing) and `--no-early-ask`.

**Early-ask is on by default.** Over AWDL the `/Ask` request is slow, mostly the preview
image iOS attaches, and iOS gives up on it before a normal answer arrives, reporting a
decline. So the receiver answers `/Ask` first and asks the person afterwards. The upload
is not read until they say yes, so an unapproved sender gets no more than the `/Ask` body
it already had to send. `--no-early-ask` restores the classic order.

**Who this reaches from Windows:** another WinDrop instance, opendrop, or a Mac started
with `defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true`.

**Reaching an iPhone** means running the receiver on Linux alongside OWL, which is how
every iPhone transfer so far was made. On Linux, `awdl0` is an ordinary interface, so the
CLI works there unchanged. The runbook is [bridge-hardware-setup.md](docs/bridge-hardware-setup.md).

### Bridge transport

Getting the hardware working is written up in
[bridge-hardware-setup.md](docs/bridge-hardware-setup.md) — including the finding that
WSL2 already ships `vhci-hcd`, `cfg80211` and `mac80211`, so a USB adapter can be
passed into WSL and only one driver module needs building. No second machine.

When the hardware exists, `--bridge <host>` routes everything through a Linux box
running OWL:

```powershell
dotnet run --project src\WinDrop.Cli -- receive --bridge 192.168.1.50
dotnet run --project src\WinDrop.Cli -- send --bridge 192.168.1.50 photo.jpg
```

On that box:

```bash
sudo ./tools/windrop-bridge.py --interface awdl0
```

The bridge relays mDNS datagrams and pipes TCP. It parses no property lists, holds no
keys and terminates no TLS — every protocol decision stays in the Windows process, so a
compromised bridge still cannot read a transfer.

**What is verified.** Both directions of the relay, end to end, against the real daemon
run over an ordinary interface (`--interface eth0`), including an inbound transfer
driven by opendrop. **Since settled by the field sessions:** `awdl0` behaves like an
ordinary interface to a socket, and a current iPhone does talk to a non-Apple peer, which
the ADR-001 addendum had doubted. **Still not verified:** the bridge itself over `awdl0`,
since every iPhone transfer so far used the CLI directly on the Linux machine. Also
whether OWL holds a link long enough for large files, which on this hardware it does not.

### Building a standalone app

```powershell
dotnet publish src\WinDrop.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Produces a single `publish\WinDrop.exe` of about half a megabyte, which needs the .NET 8
runtime present. Pass `--self-contained true` instead to get a ~150 MB executable that
runs on a machine with no .NET installed.

The icon is generated rather than committed as an opaque binary:

```powershell
.\tools\make-icon.ps1
```

### Milestone 0 tools

```
dotnet run --project src/WinDrop.Tools.ContinuitySniffer -- --seconds 30
dotnet run --project src/WinDrop.Tools.ContactHash -- --observed 1A2B,3C4D <identifier>
```

The sniffer captures Apple Continuity BLE advertisements to `captures/*.jsonl`; open a
share sheet on an iPhone and tap AirDrop while it runs. `contact-hash` tests whether a
contact identifier reproduces a hash prefix seen in a beacon — it runs entirely locally
and prints only digest prefixes, so an identifier never has to leave the machine.

## Environment prerequisite: Smart App Control

**Smart App Control (SAC) must be off on the development machine.**

SAC allows binaries by cloud reputation. A freshly compiled assembly has no reputation,
so it is blocked at load with:

```
System.IO.FileLoadException: ... An Application Control policy has blocked this file. (0x800711C7)
```

Every rebuild produces a new hash, so this recurs unpredictably — one build runs, the
next does not. Check the current state with:

```powershell
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy').VerifiedAndReputablePolicyState
# 0 = Off, 1 = Enforced, 2 = Evaluation
```

Turn it off under: Settings > Privacy & security > Windows Security >
App & browser control > Smart App Control settings > Off.

**This is a one-way door.** Windows does not allow SAC to be re-enabled afterwards
without resetting or reinstalling Windows. Microsoft's own guidance is that SAC is not
intended for machines used to build software. The alternatives are worse for this
project specifically: code-signing with a certificate SAC already trusts needs a
reputation-bearing commercial cert, and moving development into a VM cuts off the
Bluetooth and Wi-Fi radio access that every milestone here depends on.

Ruled out as a workaround: driving the WinRT BLE APIs from Windows PowerShell 5.1 to
avoid compiling anything. PowerShell's in-box WinRT projection cannot subscribe to WinRT
events at all (`Register-ObjectEvent` fails with "Windows PowerShell cannot subscribe to
Windows RT events"), and `BluetoothLEAdvertisementWatcher.Start()` refuses to run
without a `Received` handler. `Add-Type` would compile an assembly and hit the same SAC
block.

Python 3.12 is also needed to regenerate the bplist test fixtures, but not to run the
tests — the fixtures are committed.

## Layout

```
src/WinDrop.Protocol/
  Plist/          bplist00 reader and writer
  Http/           minimal HTTP/1.1 client and server over one owned connection
  Tls/            self-signed certificates and the no-validation TLS setup
  Archive/        CPIO newc reader and writer
  Compression/    DVZip chunked zlib, gzip fallback
  Dns/            DNS wire format for mDNS
  Discovery/      ITransport seam, mDNS, the infra-Wi-Fi and bridge transports
  AirDrop*.cs     the Discover/Ask/Upload state machine, both halves
src/WinDrop.App/                    WPF desktop app, AirDrop-styled
src/WinDrop.Cli/                    send / receive / browse
src/WinDrop.Tools.ContinuitySniffer/  milestone 0 BLE capture
src/WinDrop.Tools.ContactHash/        local contact-hash probe
tools/windrop-bridge.py             the Linux-side AWDL relay daemon
tools/plist_oracle.py               plistlib fixtures and differential oracle
docs/                               ADRs and reverse-engineering notes
```

## On testing

Layers are checked against implementations we did not write, because a codec can be
wrong in a way that round-trips through itself perfectly:

- **bplist** — fixtures generated by Python `plistlib`, and `plistlib` reads back what
  our writer emits.
- **CPIO** — bsdtar/libarchive extracts our archives, and we read archives it produces,
  in both the newc and odc variants. (Trap: `tar --format cpio` means *odc*, magic
  `070707`. We write *newc*, `070701`; use `--format newc` to compare against ours.)
- **mDNS** — python-zeroconf discovers and fully resolves our service: PTR, SRV, TXT and
  both address families (`tools/mdns_browse.py`).
- **The Continuity beacon** — captured from a real iPhone, not taken from a write-up.
- **The full stack, both directions** — real transfers to and from **opendrop**, an
  independent implementation of the same protocol, byte-identical each way. Sending
  exercises our writers; receiving exercises our readers, and found two real bugs.
  Together they cover the bplist writer and reader, cpio in both variants, gzip in both
  directions, the state machine on both sides, and TLS as client and as server.

- **DVZip and the receive path, against Apple itself** — no third-party implementation of
  DVZip exists (opendrop only ever speaks gzip), so real iPhone uploads were the only
  possible check. They decoded intact, and they are how the stored-block flag was found.

opendrop is a reimplementation, so a shared misreading of Apple would pass it unnoticed.
For **receiving**, an Apple device has now settled that. For **sending** it has not:
nothing has yet been sent to an iPhone.
