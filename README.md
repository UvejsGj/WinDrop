# WinDrop

An AirDrop implementation built from scratch by reverse-engineering the protocol. Learning
project: understanding over shortcuts, no wrapping of existing tools.

**This is the `linux` branch.** WinDrop is AirDrop for Windows, and
[`main`](https://github.com/UvejsGj/WinDrop/tree/main) is that project.
But Windows cannot yet drive the radio link an iPhone uses
([why](#the-one-thing-to-understand-first)), and every transfer with a real iPhone so far
ran on Linux. This branch is where that side is worked on: the command line on Linux, with
[OWL](https://github.com/seemoo-lab/owl) as the radio. Above the radio it is the same code
as `main`, and the Windows side [still builds and is still tested here](#windows-pending).

## Branches

The project is kept on three branches. `main` is the base. The other two build on it and
are not merged back, and each has its own README.

| Branch | What it is |
|---|---|
| [`main`](https://github.com/UvejsGj/WinDrop/tree/main) | AirDrop for Windows: the protocol library, the desktop app and the command line. Everything above the radio is built and tested; reaching an iPhone from Windows is what remains |
| `linux` (this one) | The same code, worked on as a Linux command-line tool. Linux with OWL is the one setup that reaches an iPhone today, and every iPhone transfer recorded below ran on it. Adds a Linux build and a README that starts from Linux |
| [`phone-page`](https://github.com/UvejsGj/WinDrop/tree/phone-page) | A separate route between an iPhone and a Windows PC, which is not AirDrop: the phone scans a code on the PC and sends files both ways in Safari, over ordinary Wi-Fi. No extra hardware and no Linux |

Changes to the shared protocol code belong on `main`, and this branch takes them from it.

## Status

| Milestone | State |
|---|---|
| 0. Capture Apple's Continuity BLE beacon | **done** — captured on iOS 26.6, contact-hash field confirmed |
| 1. Repo scaffold + transport seam | **done** |
| 2. Binary plist (`bplist00`) encode/decode | **done** — differentially checked against Python `plistlib` |
| 3. Self-signed TLS + minimal HTTP/1.1 | **done** |
| 4. Discover → Ask → Upload state machine | **done** — verified against opendrop, an independent implementation |
| 5. DVZip compression + CPIO `newc` archive | **done** — CPIO checked against bsdtar/libarchive |
| 6. Reaching an iPhone | **working, slowly and intermittently** — real iPhones on iOS 26.6, 27.0 and 27.0.1 have AirDropped files to WinDrop, arriving intact. Over Linux + OWL, at 12–27 KB/s and roughly one transfer in three in recent sessions, on the hardware tested. See [where it stands](#where-it-stands-with-an-iphone) |
| 7. Reaching an iPhone from Windows | **pending** — blocked below WinDrop, at the radio. See [Windows, pending](#windows-pending) |

292 tests, all passing, on Linux and on Windows.

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

Linux can. OWL implements AWDL in user space on a card with monitor mode and frame
injection, and presents it as an ordinary interface, `awdl0`.

So everything above the link layer is built, tested and working, and has been proven
against a real iPhone. The link layer is what limits it, and that is a hardware question,
not a software one.

## Where it stands with an iPhone

An iPhone has sent files to WinDrop's receiver running on Linux, with
[OWL](https://github.com/seemoo-lab/owl) providing `awdl0`. Photos arrived byte-for-byte
intact on iOS 26.6, 27.0 and 27.0.1: one image, several images in one share, and PNGs
whose upload mixed compressed and stored DVZip blocks. The log of every session is in
[protocol-notes.md](docs/protocol-notes.md).

| | |
|---|---|
| iPhone → WinDrop | **works**, verified on real devices. A share arrives whole or not at all; a failed one leaves nothing behind |
| Reliability | **about one transfer in three** recently: 3 of 10 in session 10, none of 17 at another place in sessions 11–12, then 3 of 10 there in session 13. Every recent success finished within about 15 s, and whether small files fare better is the open question |
| WinDrop → iPhone | **untested**. The sender is proven only against opendrop |
| Speed | **12–27 KB/s** in recent sessions on the one card tested (Intel AX211), 15–40 KB/s earlier. A 200 KB photo takes ~15 s, a 2.5 MB one ~100 s. Largest to arrive: 3.6 MB as one file, 6.4 MB as a four-photo share (267 s). ~25 MB fails |
| Phone setting | **Everyone for 10 Minutes** only. Contacts Only requires an Apple-issued identity, which a non-Apple device cannot hold |
| Windows alone | **cannot reach an iPhone** (see above). The radio has to be Linux: booted directly, or later a bridge. See [Windows, pending](#windows-pending) |

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

There may be a second fix that needs no new hardware.
[omdrop-owl](https://github.com/t4t5/omdrop-owl) reports Intel cards, AX211s among them,
working in both directions once an ad-hoc interface on AWDL's own BSSID stands beside the
monitor interface, which gives the firmware an address to acknowledge for.
`tools/owl-exclusive.sh` brings this card up that way. **It has not been run on real
hardware yet**: see [another way to drive an Intel card](#another-way-to-drive-an-intel-card-untested).

## Running it on Linux

You need the .NET 8 SDK, [OWL](https://github.com/seemoo-lab/owl), and a Wi-Fi card that
can do monitor mode and frame injection.
[bridge-hardware-setup.md](docs/bridge-hardware-setup.md) has the test for any card and
the record of the ones tried. Kali packages OWL (`sudo apt install owl`); elsewhere it is
built from source, as that page shows.

```bash
git clone -b linux https://github.com/UvejsGj/WinDrop.git
cd WinDrop
dotnet publish src/WinDrop.Cli -c Release -o ~/wd

bash tools/owl-session.sh          # terminal 1: takes the Wi-Fi card, brings up awdl0
dotnet ~/wd/windrop.dll receive    # terminal 2: only once OWL is up
```

Without `-b linux` the clone is `main`. It runs on Linux just the same, since the code
above the radio is shared, but it has neither the solution filter nor the receiver's line
about AWDL described below.

On the iPhone, set AirDrop to **Everyone for 10 Minutes**, keep it close to the machine,
and share a photo. The receiver asks before it saves anything, and files land in
`~/Downloads/WinDrop`.

Three things to know:

- **OWL takes the Wi-Fi card.** `owl-session.sh` stops NetworkManager and `wpa_supplicant`
  and puts the card in monitor mode, so the machine is off its normal network while OWL
  runs. Do anything that needs the internet first, the clone and publish above included.
- **Start OWL first, and stop it last.** Restarting OWL recreates `awdl0`, and a receiver
  started earlier is left on no AWDL interface and never shows on the phone. The receiver
  says at start which it is: `AWDL is joined, so an iPhone can be reached from here`, or
  that there is no `awdl0` and what to do. Stop the receiver before OWL, so its goodbye can
  still reach the phone.
- **`owl-session.sh` is written for the one card tested**, an Intel AX211. It reloads the
  `iwlwifi` driver first and runs OWL with `-N`, because that card refuses active monitor
  mode. `RELOAD=0 bash tools/owl-session.sh` skips the reload on another card. A card
  with active monitor mode should need neither; none has been on hand to try.

If `dotnet` stops with a message about ICU, `export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
first.

### From a live USB, in four lines

A live USB keeps nothing between boots, so every session begins by installing OWL and the
SDK again. `tools/session-setup.sh` does that, builds the receiver into `~/wd` and scans the
nearby Wi-Fi. `tools/session-receive.sh` starts the receiver, saving to `/tmp/rx`.

```bash
git clone -b linux https://github.com/UvejsGj/WinDrop.git
bash WinDrop/tools/session-setup.sh             # while still online
bash ~/WinDrop/tools/owl-session.sh             # terminal 1
bash ~/WinDrop/tools/session-receive.sh --yes   # terminal 2; leave out --yes to be asked
```

The setup script installs OWL with `apt`, which works on Kali. Elsewhere, install OWL first
and the script skips that step. It can be run again: whatever is already there is skipped.

### Another way to drive an Intel card, untested

`tools/owl-exclusive.sh` is a sibling of `owl-session.sh` that brings the radio up
differently, to get an Intel card to acknowledge the phone's frames. The method is the
"exclusive mode" of [omdrop-owl](https://github.com/t4t5/omdrop-owl), which reports an
AX201 and two AX211s working with it, and a 2.4 MB photo in 10 to 17 s on the AX201.

```bash
bash WinDrop/tools/owl-build.sh                 # while still online: the patched OWL, into ~/owl-patched
bash ~/WinDrop/tools/owl-exclusive.sh           # terminal 1, in place of owl-session.sh
bash ~/WinDrop/tools/session-receive.sh --yes   # terminal 2, as before
bash ~/WinDrop/tools/owl-counts.sh              # any time: the OWL log lines a session records, counted
```

- **Untested on real hardware.** No session had run it when it was written (2026-10-12).
  It was rehearsed against stand-ins for `iw`, `ip` and `owl` on a machine with no Wi-Fi
  card, which shows that the steps run in order and are undone on every way out, and
  nothing about the radio. omdrop-owl's AX211s have other PCI IDs (`8086:51f0`,
  `8086:51f1`) than the one tested here (`8086:7a70`). `owl-session.sh` is unchanged and
  stays the way that is known to work.
- **What it does.** It stops what manages the radio and reloads the driver, as
  `owl-session.sh` does. Then it takes the station down, adds an ad-hoc interface
  `awdlibss0` carrying the station's address, joins it to AWDL's BSSID on channel 6 at
  HT20, adds a monitor interface `mon0` with the same address, and runs OWL on that. Some
  AX211s refuse an ad-hoc interface beside the station; the station itself is then
  switched to ad-hoc. Ctrl+C undoes each step and leaves an ordinary station. If the card
  is left in ad-hoc mode anyway, reloading the driver clears it:
  `sudo modprobe -r iwlmvm iwlwifi`, then `sudo modprobe iwlwifi`.
- **It needs a patched OWL, which is not WinDrop's.** The OWL that Kali packages lacks
  the options this takes. `tools/owl-build.sh` clones omdrop-owl at a pinned commit and
  builds it with omdrop-owl's own script, from the
  [jedbillyb/owl](https://github.com/jedbillyb/owl) fork plus omdrop-owl's patches, into
  `~/owl-patched/owl`. Both are GPL-3.0, WinDrop is MIT, and none of their code is in
  this repository: the result is a separate program, built when a session needs it. The
  packaged `owl` is not replaced.
- **The receiver announces differently.** A radio held on one channel is where the phone
  is for a slot or two in sixteen, so an announcement sent once mostly misses it.
  `receive --repeat` goes on sending it, on AWDL only, in bursts of 64 copies 16.4 ms
  apart with 3 s between bursts, which are omdrop-owl's numbers, and withdraws it the same
  way when it stops. `session-receive.sh` adds `--repeat` by itself when
  `owl-exclusive.sh` is up, and `REPEAT=0` leaves it out.

### Building and testing

```bash
dotnet test WinDrop.Linux.slnf
```

The filter leaves out the two Windows-only projects, the desktop app and the BLE sniffer.
A plain `dotnet build` or `dotnet test` on Linux stops at them with `NETSDK1100`. Python
3.12 is needed only to regenerate the bplist test fixtures, which are committed.

### The commands

```bash
dotnet ~/wd/windrop.dll receive
dotnet ~/wd/windrop.dll send path/to/file.jpg
dotnet ~/wd/windrop.dll browse
```

`receive` takes `--dir <path>`, `--yes` (skip the consent prompt — it is the only real
security boundary in the protocol, so only for scripted testing), `--no-early-ask`, and
`--repeat` (announce in bursts, for a radio held on one channel;
[untested](#another-way-to-drive-an-intel-card-untested), and off unless asked for).

**Early-ask is on by default.** Over AWDL the `/Ask` request is slow, mostly the preview
image iOS attaches, and iOS gives up on it before a normal answer arrives, reporting a
decline. So the receiver answers `/Ask` first and asks the person afterwards. The upload
is not read until they say yes, so an unapproved sender gets no more than the `/Ask` body
it already had to send. A "no" is answered the moment the upload starts, without reading
any of it. `--no-early-ask` restores the classic order.

`send` has only ever been tested against opendrop and WinDrop itself. Sending **to** an
iPhone has not been tried yet.

**Without OWL**, on Linux and Windows alike, the same commands work over an ordinary
network and reach another WinDrop instance, opendrop, or a Mac started with
`defaults write com.apple.NetworkBrowser BrowseAllInterfaces -bool true`.

## Windows, pending

Everything above the radio runs on Windows today: the desktop app, and the same three
commands.

```powershell
dotnet build
dotnet run --project src\WinDrop.App              # the GUI
dotnet run --project src\WinDrop.Cli -- receive
dotnet run --project src\WinDrop.Cli -- send path\to\file.jpg
dotnet run --project src\WinDrop.Cli -- browse
```

That reaches the peers an ordinary network reaches, and not an iPhone. What Windows is
waiting for is a radio, and there are two ways it could get one:

- **A Linux radio lent to the Windows process.** This is the bridge, below. It is built,
  and tested over an ordinary interface, but it needs a USB Wi-Fi adapter that can be
  passed into WSL2, or a second machine. The built-in card cannot be passed through, and
  no suitable adapter has been on hand.
- **Windows gaining a link that an iPhone will AirDrop over.** Nothing in Windows or its
  Wi-Fi drivers offers one today, and nothing WinDrop can do from user space changes that.

That work belongs on `main`. This branch keeps the Windows side building and its tests
passing, so the two stay one codebase and a fix made on either can move to the other.

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
share sheet on an iPhone and tap AirDrop while it runs. It uses the Windows Bluetooth
APIs, so it is Windows-only. `contact-hash` tests whether a contact identifier reproduces
a hash prefix seen in a beacon — it runs entirely locally, on either platform, and prints
only digest prefixes, so an identifier never has to leave the machine.

### Development prerequisite: Smart App Control

**Smart App Control (SAC) must be off on a Windows development machine.**

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
  PeerText.cs     peer text made safe to show, and the rules for safe saved names
  PreviewImage.cs which preview bytes may reach an image decoder
src/WinDrop.Cli/                    send / receive / browse, on Linux and Windows
src/WinDrop.App/                    WPF desktop app, AirDrop-styled (Windows only)
src/WinDrop.Tools.ContinuitySniffer/  milestone 0 BLE capture (Windows only)
src/WinDrop.Tools.ContactHash/        local contact-hash probe
WinDrop.Linux.slnf                  the solution without the Windows-only projects
tools/windrop-bridge.py             the Linux-side AWDL relay daemon
tools/session-setup.sh              a field session's installs, build and Wi-Fi scan, in one command
tools/owl-session.sh                brings OWL up for a field session on Kali, and logs it
tools/owl-exclusive.sh              the same with an ad-hoc interface beside the monitor one (untested on real hardware)
tools/owl-build.sh                  builds the patched OWL that owl-exclusive.sh needs, beside the packaged one
tools/owl-counts.sh                 counts the OWL log lines a field session records
tools/session-receive.sh            starts the receiver the way a field session does
tools/retry-capture.sh              counts the phone's resent frames during a transfer
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

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Results from real Apple devices are especially
welcome, working or not; there is an issue template for them. Report security problems
privately, as [SECURITY.md](SECURITY.md) describes, not in a public issue.
