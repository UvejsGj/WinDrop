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

> **A note on the redactions below.** The captured beacon carries four two-byte values,
> two of which are truncated SHA-256 digests of the capturing device owner's own contact
> identifiers. Publishing them would let anyone who already had a candidate email or
> phone number confirm it, so they appear here as placeholders: `HSH1` and `HSH2` for the
> two that were never attributed, `PHON` and `MAIL` for the two that were, and `p1`–`p4`
> for the unexplained four-byte prefix. Byte-level diffs use per-byte placeholders of the
> same width.
>
> Nothing analytical is lost. Which values held position, which swapped, which were
> identified and by what normalisation are all still legible — arguably more so, since
> the placeholders say what each slot turned out to be.


#### 2026-09-09 — MEASURED: iOS really does keep AirDrop off ordinary Wi-Fi

ADR-001 asserted from documentation that iOS advertises AirDrop only on `awdl0`. That
was reasoned, never measured. It is now measured, with `tools/mdns_sniff.py` and
`tools/mdns_probe.py`.

**A false negative caught first.** The initial attempt reported zero services and zero
packets on a LAN that demonstrably carries mDNS. The cause was joining the multicast
group on `INADDR_ANY`, which picks one interface by routing and had picked a virtual
adapter. A negative result from an uncalibrated instrument is worthless, so the test was
re-run with a WinDrop receiver alongside as a **positive control** and the group joined
on the real adapter by address.

With the control visible, the negative means something:

```
22 packets from 5 sources
192.168.1.6   UVEJSLAPTOP-a242d3._airdrop._tcp.local.   <- our control, instrument works
192.168.1.9   ?_companion-link._tcp  ?_rdlink._tcp
192.168.1.10  ?_companion-link._tcp  ?_rdlink._tcp
192.168.1.89  ?_companion-link._tcp  ?_rdlink._tcp
192.168.1.8   ?_airplay._tcp  ?_raop._tcp  ?_airplay-bds._tcp
```

Every AirDrop name on the wire is ours. Several Apple devices are present and talking,
and none of them mentions AirDrop.

Actively querying twelve Apple service types — airdrop, companion-link, rdlink, airplay,
raop, apple-mobdev2, touch-able, sleep-proxy, smb, afpovertcp, ipp, ipps — produced
exactly one answer, again our own service. The Apple devices *browse* for
companion-link and rdlink but advertise nothing reachable.

**Conclusion.** There is no hidden AirDrop-over-infrastructure channel to piggyback on,
and no other open Apple service on the link to borrow. The AWDL requirement is real, and
ADR-001's central claim now rests on measurement rather than on reading.


#### 2026-09-02 — CONFIRMED: mDNS records resolve in a third-party browser

`tools/mdns_browse.py` drives python-zeroconf, an independent mDNS implementation,
against a running `windrop receive`. Full round trip, not just a parse:

```
instance : UVEJSLAPTOP-0cedda._airdrop._tcp.local.
server   : uvejslaptop.local.
port     : 8770
addresses: ['172.27.48.1', '192.168.1.39', 'fe80::17fb:83c4:f49a:5cd5', 'fe80::2fe1:aac2:4e23:9954']
props    : {'flags': '10'}
```

PTR discovery, SRV resolution, TXT decoding and both address families all survive. The
`flags` value decodes to 0xA — `SupportsDvZip | SupportsMixedTypes` — which is what the
receiver advertises, so the capability bits a peer would negotiate against arrive intact.

Run on Windows rather than in WSL deliberately: opendrop's browser is `V6Only` bound to
one interface address, which cannot cross the WSL NAT boundary and collides with the
host's address under mirrored networking. Neither limitation is ours, and neither says
anything about the records.

**Known wart, not yet addressed.** We advertise every address on every non-loopback
interface, which here includes `172.27.48.1` — the WSL virtual switch, unreachable from
anywhere else. A peer on the real network that tries addresses in order may stall on it
before reaching a useful one. Apple's own responder also advertises broadly, so this is
not obviously wrong, but it is worth revisiting if a real device is slow to connect.


#### 2026-09-02 — CONFIRMED: opendrop sending to us, and two real bugs found

`opendrop send` to `windrop receive`, over IPv4 between WSL2 and the Windows host.
64-byte file, byte-identical on arrival (MD5 `8e87b52c…` both sides). This is the
direction that exercises our **readers**, and unlike the sending direction it did not
work first time. It found two genuine defects.

**Bug 1 — the upload compression is not signalled in a header.**

opendrop sends only `Content-Type: application/x-cpio` and gzips the body regardless.
It never sets `Content-Encoding`. Our receiver had been guessing from its own advertised
capability flags: "we said we support DVZip, so this is probably DVZip." It was gzip.

The failure named itself. The DVZip reader read the first four bytes as a block length
and reported `Block of 529205248 bytes exceeds the 16777216 byte limit` — and
529205248 is `0x1F8B0800`, the gzip magic read as a big-endian integer.

Fixed by sniffing instead of guessing. All three encodings are self-identifying: gzip
starts `1f 8b`, cpio starts with an ASCII magic, and anything else is DVZip, whose frame
opens with a length rather than a recognisable constant. `Content-Encoding` is still
read when a peer sends one, but it no longer gets the deciding vote — the bytes do.

**Bug 2 — opendrop writes odc cpio, not newc.**

Its uploads arrive with magic `070707`, and our reader accepted only `070701`. The cause
is the libarchive trap already recorded here: opendrop calls
`libarchive.custom_writer(..., "cpio", ...)`, and libarchive's format name `cpio` selects
**odc**, not newc.

Since opendrop interoperates with real Apple devices, odc must be acceptable to AirDrop,
which makes a newc-only reader stricter than the protocol actually is. The reader now
accepts both. They differ in more than a magic number: newc uses hexadecimal fields in a
110-byte header and pads both name and data to four bytes; odc uses **octal** fields in a
76-byte header and pads **nothing**. We continue to write newc, which opendrop reads
without complaint.

#### What the two directions together now cover

| | WinDrop → opendrop | opendrop → WinDrop |
|---|---|---|
| bplist | writer | **reader** |
| cpio | writer (newc) | **reader (odc)** |
| compression | gzip encode | **gzip decode, sniffed** |
| state machine | sender | **receiver, incl. consent gate** |
| TLS + HTTP | client | **server** |

Still untested against a third party: **mDNS discovery** and **DVZip**. Both transfers
bypassed discovery with a hand-written peer address, and opendrop only ever speaks gzip,
so our chunked-zlib framing has still never been read by anything but us.

#### More opendrop facts worth recording

- `opendrop send` has **no direct-address option**. It reads
  `~/.opendrop/discover.last.json`, written by `opendrop find`, and `-r` selects an entry
  from it by index, ID or name. Writing that file by hand is a clean way to bypass
  discovery, which is how this test was run.
- Its zeroconf is constructed `ip_version=IPVersion.V6Only`, bound to the interface's
  link-local address. Under WSL mirrored networking, WSL and Windows share that address,
  so opendrop sees our announcements as self-originated and discards them. Verified our
  packets do arrive, and that standalone python-zeroconf parses them correctly — PTR,
  SRV, TXT and both address records, `valid: True`. The records are fine; the address
  collision is the problem.
- It is broken with `libarchive-c` 5.x: it calls `ArchiveEntry(None, entry_p)` against a
  constructor whose argument order has since changed, so the entry pointer lands in
  `header_codec` and raises `TypeError: encode() argument 'encoding' must be str, not
  int`. `pip install libarchive-c==2.9` fixes it.


#### 2026-09-02 — CONFIRMED: full interop with opendrop

WinDrop sender to `opendrop receive`, over IPv6 link-local between Windows and WSL2
Ubuntu 22.04. Two files, 69 and 6756 bytes, both arriving byte-identical (MD5 verified
against the originals).

```
Sending to fe80::215:5dff:fe69:142%45 @ [...]:8771 via direct (flags=0x0)
Peer identifies as 'UvejsLaptop' (OpenDrop)
Accepted. Uploading via gzip...
Done.
```

This is the strongest validation available without Apple hardware. Every layer was
exercised against an implementation written by people who read the protocol
independently of us:

| Layer | Evidence |
|---|---|
| TLS, self-signed, no validation | handshake completed |
| HTTP/1.1 framing on one connection | three requests, no desync |
| bplist **writer** | opendrop parsed our /Discover and /Ask bodies |
| bplist **reader** | we parsed its /Discover response and read ReceiverModelName |
| Discover / Ask / Upload state machine | consent prompt raised, upload accepted |
| cpio newc writer | opendrop extracted both members |
| gzip path | selected and understood |

Notably the `/Ask` plist was where a mismatch seemed most likely, since its field names
and its boolean-versus-integer choices were read off opendrop's source rather than
observed. They were right.

Still unproven: that any of this satisfies **Apple**. opendrop is a reimplementation, so
a shared misreading of Apple's behaviour would pass this test. Only an Apple device
settles that.

#### opendrop facts worth recording

Learned by reading its source and running it, not from documentation:

- **Its listener is IPv6-only.** `server.py` uses `HTTPServerV6` with
  `address_family = socket.AF_INET6`, bound to the interface's link-local address. An
  IPv4 address for the same host will never connect.
- **It listens on port 8771**, not AirDrop's 8770.
- **`-n` is the display name, `-i` is the interface.** `-n eth0` is silently accepted and
  leaves it bound to `awdl0`, which then fails with a message about `owl` not running —
  an error that points at the wrong thing entirely.
- It reports `ReceiverModelName` as `OpenDrop`.


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

#### CONFIRMED: the values are truncated SHA-256 of contact identifiers

Two of the four observed values were reproduced exactly by hashing the device owner's
own identifiers, which settles the reading of the field:

| Observed | Identifier kind | Normalisation that matched |
|---|---|---|
| `MAIL` | iCloud email address | hashed as-is (input was already lowercase) |
| `PHON` | phone number | **digits only** — `+` and punctuation stripped, country code retained |

The phone result is the informative one, because the alternatives were tested in the
same run and all missed: the `+`-prefixed form, the last-10-digit form and the
last-9-digit form produced unrelated digests. So Apple hashes the full international
number with every non-digit removed.

Residual unknown on the email side: the address that matched was already entirely
lowercase, so the capture cannot distinguish "hashed as-is" from "lowercased first".
`ContactHash.Normalize` assumes lowercasing, since that is what makes a user-typed
address match, but a mixed-case address has not been tested. This must be resolved
before the rule is relied on for contact matching in `/Discover`.

#### REFUTED: the remaining two values are not iCloud aliases

An iCloud account carries automatic `@me.com` and `@mac.com` aliases, which would have
neatly accounted for the other two slots. Hashing both produced `B8BD` and `96AF` —
neither is `HSH1` or `HSH2`. Hypothesis dead.

#### Byte-level diff, which reframes the slot reading

Aligning the two payloads:

```
idx:  0  1  2  3  4  5  6  7  8  9 10 11 12 13 14 15 16 17
A:   p1 p2 p3 p4 00 00 00 00 03 a1 a2 f1 f2 b1 b2 e1 e2 00
B:   p1 p2 p3 p4 00 00 00 00 03 a1 a2 e1 e2 b1 b2 f1 f2 00
                                      ^^^^^       ^^^^^
```

Bytes 0–10 and byte 17 are byte-identical. Only 11–16 differ, and they differ by
swapping the confirmed phone hash with the confirmed email hash.

This weakens the "four homogeneous hash slots" reading. `HSH1` sits inside the invariant
region alongside the prefix and version byte, so it may belong to a header rather than
to the hash array. `HSH2` is stranger still: it holds position while sitting *between*
the two values that swap.

Candidates for `HSH1` and `HSH2`, unresolved:

1. Further contact identifiers the owner has on the account — a non-iCloud Apple ID
   address, a rescue email, or a second phone number. Testable with `contact-hash`.
2. Not contact hashes at all. `HSH1`'s position in the invariant region is consistent
   with it being a header field that happens to be two bytes wide.
3. A different normalisation of an identifier already tested.

Note that neither remaining value blocks implementation: the field's type and its
normalisation are both established, which is what the sender side needs.

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
