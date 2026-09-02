# WinDrop

An AirDrop implementation for Windows, built from scratch by reverse-engineering the
protocol. Learning project: understanding over shortcuts, no wrapping of existing tools.

## Status

| Milestone | State |
|---|---|
| 0. Capture Apple's Continuity BLE beacon on Windows | **done** — beacon captured on iOS 26.6, contact-hash field confirmed |
| 1. Repo scaffold + transport seam | done |
| 2. Binary plist (`bplist00`) encode/decode | **done** — 39 tests, differentially checked against Python plistlib |
| 3. Self-signed TLS + minimal HTTP/1.1 | not started |
| 4. Discover -> Ask -> Upload state machine | not started |
| 5. DVZip chunked compression + CPIO `newc` | not started |
| 6. Transport decision (see ADR-001) | **blocked on milestone 0 findings** |

## The one thing to understand first

AirDrop's discovery runs over **AWDL**, a second Wi-Fi link layer that time-slices the
radio against your normal AP connection on a synchronized schedule. Windows' NDIS stack
exposes no way to drive the 802.11 MAC that way, and iOS implements no alternative
transport (it has never supported Wi-Fi Direct). See
[ADR-001](docs/adr-001-transport-selection.md) for the full argument and the options.

Everything above the link layer — bplist, TLS, HTTP, the state machine, DVZip, CPIO — is
transport-agnostic and is being built first, behind an `ITransport` seam.

## Layout

```
src/WinDrop.Protocol/               pure protocol, no Windows dependencies
  Discovery/ContinuityParser.cs     Apple BLE Continuity TLV parsing
src/WinDrop.Tools.ContinuitySniffer/   milestone 0 capture tool (WinRT BLE)
docs/                               ADRs and reverse-engineering notes
captures/                           raw JSONL captures (gitignored)
```

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

## Running milestone 0

```
dotnet run --project src/WinDrop.Tools.ContinuitySniffer
```

Then on the iPhone: open any share sheet and tap AirDrop. The phone begins broadcasting
its Continuity beacon; green lines are AirDrop (type `0x05`). `--all` widens the scan
beyond Apple, `--verbose` prints every repeat rather than only new payloads.

Raw captures land in `captures/` as JSONL for later analysis.
