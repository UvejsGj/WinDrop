# Reverse-engineering notes

Running log of what we have **observed**, kept separate from what we have **read**.
Anything sourced from a third party stays marked as unverified until our own capture
confirms it — the point of this project is to not take the protocol on faith.

## Continuity BLE beacon (milestone 0)

### Hypothesis under test

Apple manufacturer data, company ID `0x004C`, is a sequence of TLV records:
`type(1) | length(1) | value(length)`, several per advertisement.

AirDrop is type `0x05`, payload length `0x12` (18 bytes):

| Offset | Size | Meaning (hypothesised) |
|---|---|---|
| 0 | 8 | zero padding / reserved |
| 8 | 1 | version (expected `0x01`) |
| 9 | 8 | 4 x 2-byte truncated SHA-256 of contact identifiers |
| 17 | 1 | zero terminator |

### What matters about this frame

It contains **no channel, no SSID, no address, no transport identifier**. It cannot say
"meet me over here" because there is no field in which to say it. That is the structural
evidence for ADR-001's claim that the beacon is a wake-up for AWDL specifically, and
that replicating it from Windows cannot redirect an iPhone onto another transport.

### Observations

_(to be filled in from the first capture — record iOS version, whether the layout
matched, what the version byte actually was, and whether hashes are non-zero in
Everyone mode vs Contacts Only)_

| Date | iOS version | AirDrop mode | Payload | Layout matched? | Notes |
|---|---|---|---|---|---|

### Open questions

1. Does the beacon appear at all in "Everyone / Everyone for 10 Minutes", or only when
   the share sheet is actively open?
2. Are the contact hashes populated in Everyone mode, or zeroed?
3. Does the sending iPhone also emit `NearbyAction` (0x0F) alongside AirDrop (0x05)?
   If so, what distinguishes them?
4. Has the layout changed on current iOS versus the published research?
