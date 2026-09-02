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

#### 2026-09-02 — iPhone on iOS 26.6, share sheet open on AirDrop

`continuity-sniff --seconds 90`, extended (BLE 5) advertisements enabled. 421 Apple
advertisements, 14 distinct payloads. Two distinct AirDrop payloads from one device
(`40:0C:E4:xx:xx:xx`), repeated 42 and 123 times — stable, not noise.

```
p1p2p3p4 00000000 03 HSH1 PHON HSH2 MAIL 00      42x, first seen 14:20:32
p1p2p3p4 00000000 03 HSH1 MAIL HSH2 PHON 00     123x, first seen 14:20:45
```

Scored against the hypothesis:

| Element | Predicted | Observed | Verdict |
|---|---|---|---|
| Payload length | 18 bytes | 18 bytes | **confirmed** |
| Bytes [0..8) | 8 zero bytes | `p1 p2 p3 p4 00 00 00 00` | **refuted** — first 4 carry data |
| Byte [8], version | `0x01` | `0x03` | position confirmed, value differs |
| Bytes [9..17) | 4 x 2-byte contact hashes | 4 plausible 2-byte values | unconfirmed |
| Byte [17] | `0x00` | `0x00` | **confirmed** |

The 18-byte frame size and the trailing zero survive intact from iOS 12-era research all
the way to iOS 26.6.

#### The reordering

Both payloads carry the same four values in a different order, from the same BLE
address, 13 seconds apart, with prefix and version unchanged:

```
A:  HSH1  PHON  HSH2  MAIL
B:  HSH1  MAIL  HSH2  PHON
```

Slots 1 and 3 swap; slots 0 and 2 hold. Exactly two orderings across 165 advertisements
points to a rotation event rather than per-advertisement randomisation — the latter
would yield many distinct orderings, not two with high repeat counts.

Two explanations, not yet separated:

1. The ordering is shuffled on some trigger, so the beacon cannot be fingerprinted by
   the sequence. The *set* would then be the identity and the order would carry nothing.
2. The 2-byte grouping is wrong and we are slicing a field with different internal
   structure, making the "reordering" an artefact of a bad decode.

Explanation (2) does not disturb any byte-level fact in the table above; it only
reinterprets `[9..17)`.

#### Verifying the contact-hash reading

`contact-hash` (in `src/WinDrop.Tools.ContactHash`) tests whether those four values are
truncated SHA-256 digests of the sender's contact identifiers. Apple's normalisation is
unknown and consequential — `+1 (555) 010-9999` and `15550109999` hash to unrelated
digests — so the tool sweeps the plausible variants and reports which one lands, if any.
A hit identifies the field and the normalisation together. A total miss falsifies the
contact-hash reading.

It runs entirely locally and prints only 4-hex-digit prefixes, so an identifier never
has to leave the machine in order to check a match.

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
6. What triggers the hash reordering? Re-opening the share sheet, a timer, or something
   else? Separating explanation (1) from (2) needs a capture long enough to count
   distinct orderings, plus one where the share sheet is deliberately closed and
   reopened.
7. What are the first four bytes (`p1 p2 p3 p4`)? Constant across both payloads from
   this device; unknown whether they are constant across devices, across sessions, or
   over time. A capture from a second Apple device would separate device identity from
   protocol constant.
8. Why is the version byte `0x03`? Is it an AirDrop protocol revision, and does an older
   Apple device on the same network emit a lower value?
