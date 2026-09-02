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

### Target device

iPhone on **iOS 26.6**.

The hypothesised layout above comes from public BLE research conducted against
iOS 12-era devices. That is a large version gap across which Apple has reworked
proximity sharing more than once. **The hypothesis is a starting guess for this target,
not a specification.** A `LAYOUT MISMATCH` from the sniffer against iOS 26.6 is the
expected-plausible outcome and is a finding to record, not a defect to fix.

Confirmed independently of the AirDrop record: the outer TLV framing itself
(`type | length | value`, several records per advertisement, company ID `0x004C`) does
hold on current Apple devices — a first capture decoded `NearbyInfo` (0x10), `FindMy`
(0x12) and `Handoff` (0x0C) correctly from ambient traffic. So if the `0x05` payload
does not match, the framing is not the reason; the payload definition changed.

One capture also produced an advertisement whose trailing bytes declared a length
overrunning the frame. Unresolved. Candidate explanations, in rough order of
likelihood: truncation at the 31-byte legacy advertisement limit; padding between
records; or a record type whose length field we are misreading. The parser now returns
those bytes so the next occurrence can be examined rather than counted.

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
5. Does iOS 26.6 advertise AirDrop as a BLE 5 **extended** advertisement rather than a
   legacy one? Extended advertising is not delivered to a watcher unless explicitly
   requested, so a naive scanner sees silence and misreads it as "the phone is not
   advertising". It would also lift the 31-byte payload cap, which is one candidate
   explanation for truncated tails observed in legacy captures.
