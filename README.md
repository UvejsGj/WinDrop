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
| 6. Reaching an iPhone | **blocked, and possibly closed** — needs AWDL hardware, and there is evidence modern iOS no longer accepts non-Apple peers at all. See the addendum in [ADR-001](docs/adr-001-transport-selection.md) |

145 tests, all passing.

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

So everything above the link layer is built, tested and working. The link layer is the
open question, and it is a hardware question, not a software one.

## Running it

```
dotnet build
dotnet run --project src/WinDrop.App              # the GUI
dotnet run --project src/WinDrop.Cli -- receive
dotnet run --project src/WinDrop.Cli -- send path\to\file.jpg
dotnet run --project src/WinDrop.Cli -- browse
```

`receive` takes `--dir <path>` and `--yes` (skip the consent prompt — it is the only
real security boundary in the protocol, so only for scripted testing).

**Who this reaches:** another WinDrop instance, opendrop, or a Mac started with
`defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true`.

**Who it does not reach:** an iPhone. See above.

### Bridge transport

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
driven by opendrop. **What is not:** that `awdl0` behaves like an ordinary interface to
a socket, that OWL holds the link up under load, and — see the ADR-001 addendum — that a
current iPhone will talk to a non-Apple peer at all.

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
dotnet run --project src/WinDrop.Tools.ContactHash -- --observed HSH1,PHON <identifier>
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

One layer still has **no** external validation, and the source says so rather than
implying otherwise: **DVZip**. No third-party implementation of it exists — opendrop
only ever speaks gzip — so Apple is the only thing that can ever validate that layer.

What none of this proves is Apple compatibility: opendrop is a reimplementation, so a
shared misreading of Apple would pass unnoticed. Only an Apple device settles that.
