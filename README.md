# WinDrop

An AirDrop implementation for Windows, built from scratch by reverse-engineering the
protocol. Learning project: understanding over shortcuts, no wrapping of existing tools.

## Status

| Milestone | State |
|---|---|
| 0. Capture Apple's Continuity BLE beacon on Windows | **in progress** |
| 1. Repo scaffold + transport seam | done (scaffold) |
| 2. Binary plist (`bplist00`) encode/decode | not started |
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

## Running milestone 0

```
dotnet run --project src/WinDrop.Tools.ContinuitySniffer
```

Then on the iPhone: open any share sheet and tap AirDrop. The phone begins broadcasting
its Continuity beacon; green lines are AirDrop (type `0x05`). `--all` widens the scan
beyond Apple, `--verbose` prints every repeat rather than only new payloads.

Raw captures land in `captures/` as JSONL for later analysis.
