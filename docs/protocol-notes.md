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
- `opendrop send` fails for **every image** on current Pillow. Generating the /Ask
  preview calls `Image.ANTIALIAS`, which Pillow 10 removed, so the send dies with
  `AttributeError` before a byte reaches the network. Non-image files are unaffected,
  which is why earlier interop runs never hit it. `pip install "Pillow<10"` fixes it.


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

## /Ask preview image (`FileIcon`)

The receiver's prompt shows a picture of what is being sent. That picture travels inside
the /Ask body. It is the only field there that is not text, and a receiver has to
decode it **before** the user has agreed to anything.

#### 2026-09-12 — READ: how opendrop builds it

From the installed opendrop's `client.py` and `util.py`:

- `FileIcon` is a **top-level** key holding a `data` object. There is one preview for
  the whole request, not one per file.
- It is attached only when the first file's leading 128 bytes sniff as an image (via
  `fleep`). Otherwise the key is absent. It is never present and empty.
- `generate_file_icon` applies the EXIF rotation, fits the image in a **540 × 540** box
  and saves it as **JPEG 2000**. A 64 px variant exists but is commented out.
- `server.py` never reads the key. opendrop's receiver ignores previews completely.

The JPEG 2000 choice is unusual enough that it is probably copied from observed Apple
traffic. That is an inference, not an observation.

#### 2026-09-12 — MEASURED: the bytes, and what Windows can do with them

Generated through opendrop's own code path (`tools/fileicon_oracle.py`, the fixture
`opendrop-ask-with-icon.bplist`):

```
thumbnail (540, 360), icon 19276 bytes, ask body 19634 bytes
icon head 00 00 00 0c 6a 50 20 20 0d 0a 87 0a
```

That is the JP2 signature box. Its body `0D 0A 87 0A` is designed so that CRLF
translation or 7-bit stripping in transit breaks it visibly.

Windows cannot decode it. `BitmapDecoder.Create` tries every installed WIC codec by
content, the widest net available, and it fails:

```
control.jpg       -> decoded by 'JPEG Decoder' 540x360
opendrop-icon.jp2 -> FAILED: NotSupportedException No imaging component suitable to complete this operation was found.
```

The JPEG is the same thumbnail, re-encoded. It is the positive control: it shows the
decoder path works, so the JP2 failure is about the format and not the harness.

#### What WinDrop does about it

**Receiving.** The format is identified from its signature (`PreviewImage.Sniff`).
Only JPEG and PNG are decoded, each by its own named decoder. Nothing goes through the
content-sniffing path, because that path reaches every third-party codec on the
machine, and each of those is parsing code a stranger could reach without a click.
Image dimensions come from the header and are capped at 2048 before any pixels are
decompressed. A megabyte of PNG can claim 65535 × 65535. A PNG header claiming
60000 × 60000 is refused in 3 ms. For JPEG 2000, or no icon at all, the prompt shows
the Windows icon for the first file's type instead.

**Sending.** JPEG, from the shell's thumbnail, fitted in opendrop's 540 box. JPEG
because Windows has no JPEG 2000 encoder either. The icon is sent only when the
thumbnail is opaque, because JPEG would fill a transparent PNG's transparent areas
with whatever colour data sat under them.

**Unverified against Apple:** whether an iPhone renders a JPEG here, ignores it, or
rejects the whole /Ask. The CLI's `send --icon <file>` attaches any bytes unexamined,
so one AWDL session can separate these cases: no icon, a JPEG, and opendrop's JP2.

### Open questions

1. ~~Does Apple send JPEG 2000 as well?~~ **Yes.** iOS 26.6 sent JPEG 2000 previews
   of 43,442 and 54,397 bytes (observed 2026-09-13, below).
2. Does iOS accept a JPEG `FileIcon`? A PNG? Does a format it dislikes cost only the
   preview, or the transfer?
3. What size does Apple send, and does it also send the 64 px variant opendrop left
   commented out?

## AWDL from a non-Apple peer (the iPhone path)

Identifiers below are redacted with width-preserving placeholders, as elsewhere in these
notes: device name, AWDL MACs, the link-local addresses derived from them, and OWL's
peer UUIDs.

#### 2026-09-13 — OBSERVED: iOS 26.6 is visible to OWL on awdl0

Setup: MSI Sword 16 HX, Intel AX211 `[8086:7a70]`, Kali Live, OWL
`0~git20220130-0kali1+b1`. iPhone on iOS 26.6, AirDrop set to Everyone for 10 Minutes,
share sheet **closed**. The configuration that worked, and why, is in
`bridge-hardware-setup.md`: monitor mode set by hand, OWL with `-N`, channel 6.

OWL's log:

```
DEBUG: Channel 6 [2437 MHz] is available for frame injection
INFO : add peer xx:xx:xx:xx:xx:xx (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
DEBUG: new election tree: xx:xx:xx:xx:xx:xx -> xx:xx:xx:xx:xx:xx (met 115, ctr 865)
INFO : add peer xx:xx:xx:xx:xx:xx (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
```

The phone advertised a channel sequence of mostly 44 and occasionally 6.

`tcpdump -i awdl0`, three packets of this form:

```
IP6 fe80::xxxx:xxff:fexx:xxxx.5353 > ff02::fb.5353: PTR <owner>'s iPhone._applicationServicePairing._tcp.local., PTR <owner>'s iPhone._appSvcPrePair._tcp.local. (126)
```

The source is the EUI-64 link-local address of the first peer OWL added. That ties the
mDNS traffic to the AWDL peer, rather than to anything else OWL happened to decapsulate.

**What this settles**

- OWL synchronises with a current iPhone: it built an election tree with the phone as
  master, and decapsulated the phone's data frames into `awdl0`. The fear that iOS 16+
  would shut non-Apple devices out does not apply at the link layer, at least not to
  receiving.
- A card without active monitor mode receives AWDL discovery traffic. This was predicted
  and is now observed. Active monitor only affects ACKs for unicast frames, and mDNS is
  multicast.
- The iPhone announces services on AWDL **without** the share sheet open.

**What it does not settle**

- Whether the phone sends **unicast** frames to us, and whether they survive a card that
  cannot ACK them. Multicast needs neither.
- Whether the phone considers us a peer at all, as opposed to us merely following its
  schedule and overhearing it.

**Not observed:** `_airdrop._tcp`. That is expected with the share sheet closed, and the
roles matter here. A **sender** browses, so with the share sheet open, expect mDNS
*queries* for `_airdrop._tcp.local` from the phone, not announcements. An iPhone
*advertises* `_airdrop._tcp` only as a receiver, once woken.

`_applicationServicePairing._tcp` and `_appSvcPrePair._tcp` are new to these notes and
unattributed.

**The second peer** is unidentified: another Apple device nearby, or the same phone
after an AWDL MAC rotation.

### Open questions

1. With the share sheet open, does the phone send mDNS queries for `_airdrop._tcp` on
   awdl0?
2. Given a receiver advertised on awdl0, does the phone open TCP to it? A SYN to the
   advertised port answers this without any application-layer code.
3. Does unicast complete on channel 6 alone, without ACKs, while the phone spends most of
   its time on 44?
4. Does a non-Apple receiver appear in the iOS 26.6 share sheet? That needs a successful
   `/Discover`, so it answers 2 and 3 as well.

#### 2026-09-13 — MEASURED: WinDrop's own receiver runs on Linux

Before pointing our receiver at `awdl0`, a self-contained `linux-x64` build of the CLI was
run in WSL2 (Ubuntu 22.04). Two parts of it had never run anywhere but Windows: TLS, where
.NET uses OpenSSL on Linux instead of SChannel, and the socket options mDNS depends on.

- **Transfers:** Windows CLI to the Linux receiver, over IPv4 and over a scoped IPv6
  link-local address (`fe80::…%39`). Three files arrived byte-identical by MD5. The
  receiver identified the /Ask preview as a 7,733-byte JPEG.
- **mDNS:** `windrop browse` inside WSL found the receiver at its scoped link-local
  address (`%2`, eth0), with flags `0xA`.
- **The interface filter.** Both mDNS and the address list skip interfaces that are not
  `OperationalStatus.Up`. Virtual links often report operstate `unknown`, and OWL's
  `awdl0` may be one of them. If .NET mapped that to anything but `Up`, the receiver
  would ignore `awdl0` and fail silently. Test: a dummy interface with operstate
  `unknown` and a link-local address. Membership of `ff02::fb` was captured before,
  during and after the run, so a join by some other daemon could not pass for ours:

  ```
  BEFORE windrop:  eth0: -        wdtest0: -
  DURING windrop:  eth0: joined   wdtest0: joined
  AFTER windrop:   eth0: -        wdtest0: -
  ```

  The worry is refuted: an operstate-`unknown` link is joined. A dummy interface stands in
  for OWL's device here; `awdl0` itself has not been tested this way.
- **Visibility in the field.** `receive` now prints the joined interfaces at startup
  (`mDNS on eth0/IPv6, …`). Whether `awdl0` was joined becomes a line to read, not
  something inferred from an empty share sheet.

#### 2026-09-13 — OBSERVED: iOS 26.6 AirDrops to WinDrop's receiver, as far as /Upload

Kali Live on the AX211, OWL on channel 6 with `-N`, and WinDrop's CLI receiver built from
source on Kali at `1554b30`. It started with `mDNS on eth0/IPv6, eth0/IPv4, awdl0/IPv6`.
The iPhone had AirDrop set to Everyone for 10 Minutes.

| Rung | Result |
|---|---|
| Phone browses | ✅ mDNS query for `_airdrop._tcp.local` from the phone's AWDL link-local address |
| We answer | ✅ `kali-xxxxxx._airdrop._tcp`, SRV `kali.local.:8770`, TXT `flags=10` |
| TCP | ✅ connection to 8770 established |
| Listed | ✅ "kali" appeared in the AirDrop row, so `/Discover` was accepted |
| `/Ask` | ✅ every attempt; the consent prompt showed the request |
| `/Upload` | ❌ failed, differently in two runs (below) |

**What this settles.** iOS 26.6 does not refuse a non-Apple receiver in Everyone mode. It
browses for one on AWDL, connects over unicast TCP, and accepts its `/Discover` and
`/Ask` responses. The feared iOS 16+ lockout does not exist at any layer tested. Unicast
also works through a card without active monitor mode, though not reliably.

**The `/Ask` from an iPhone**, sanitised:

```
'<owner>'s iPhone' (iPhone) wants to send:
  IMG_xxxx.JPG  [public.jpeg]
  preview: 43,442 bytes, Jpeg2000
```

`SenderModelName` is `iPhone`, and `FileType` is a proper UTI. The preview is **JPEG 2000**,
which confirms what opendrop's source suggested. At 43–54 KB it is more than twice the
size of opendrop's 540 px encode of a flat test image. Its dimensions are unknown, because
nothing on Windows decodes it, which also means WinDrop's GUI can never show an iPhone's
preview. It shows the file-type icon instead.

**Run B, a ~60 KB photo: the upload arrived, and our extractor refused it.**

```
Connection failed: Archive member '.' resolves to the download directory itself.
```

The body came through TLS, was decompressed and was parsed as cpio. The encoding was not
logged, so whether our DVZip reader has now met Apple is still unknown. The archive opens
with a directory member named `.`, the archive root, and the traversal guard rejected it.
**Fixed:** a root *directory* member is skipped. A file named `.`, and every escaping name,
is still refused. The receiver now logs the encoding with its first bytes, and each
archive member, so the next transfer answers the DVZip question.

**Run A, a photo with a 54 KB preview: the connection died before any upload data.**

```
Connection failed: Connection closed before a chunk header.
Connection failed: Unable to read data from the transport connection: Connection reset by peer.
```

tcpdump showed RSTs from the phone after about 60 KB acknowledged (`seq 60601 ack 3189`).
That fits `/Ask`, preview included, arriving, and the connection dying as `/Upload`
began. Nothing reached the extractor, so this failure belongs to the transport, not the
parser.

**The transport.** During both transfers OWL logged bursts of
`ERROR: unable to inject packet (send: Resource temporarily unavailable)`: `EAGAIN` on
its injection socket, so frames toward the phone were dropped. Peers churned throughout,
with repeated add, remove and re-election. The phone's channel sequence favours 44 while
we are held to 6. Together these make throughput, not protocol correctness, the
constraint on this hardware.

**Another Apple device in range** advertised `_airdrop._tcp` with TXT `flags=14335`,
which is `0x37FF`: bits 0–10, 12 and 13. Ours is `0x0A`, DVZip (`0x02`) and MixedTypes
(`0x08`) in opendrop's mapping. Apple sets every bit opendrop names, plus three it does
not. iOS listed us with only two bits, so none of the others gates a photo transfer.

### Open questions

1. Which encoding does iOS use for `/Upload` to a receiver advertising DVZip? The next
   run logs it.
2. With the root-member fix, does a small photo now land?
3. Can the `EAGAIN` bursts be reduced (socket send buffer, `txqueuelen`)? At what file
   size does the transfer give out?
4. Would advertising more flag bits change what iOS sends?

#### 2026-09-13 — OBSERVED: the first complete AirDrop from an iPhone to WinDrop

Same setup, with the receiver at `3bf2e92`.

**Test 1, a 36 KB photo: success.**

```
'<owner>'s iPhone' (iPhone) wants to send:
  IMG_xxxx.JPG  [public.jpeg]
  preview: 48,870 bytes, Jpeg2000
Accepted.
  upload: dvzip, first bytes 0000002D789C
  member . (directory)
  member ./IMG_xxxx.JPG (36,454 bytes)
```

The 36,454-byte file opened correctly. That settles the first two questions above:

- **iOS sends DVZip to a receiver that advertises it, and our reader has the framing
  right.** `0000002D` is a 45-byte block length and `789C` is a zlib header. Our DVZip
  reader has now worked against Apple, not just against our own writer. The other
  direction, whether Apple can read *our* DVZip, is still untested.
- The root-member fix works.
- Blocks are not a fixed size. A 45-byte compressed block can only be the cpio header for
  `.`.

The first attempt at Test 1 failed before any data moved
(`Unable to write data to the transport connection`), while the phone was barely present
on channel 6. With the phone touching the PC it worked.

**Test 2, a photo iOS converted from HEIC: refused by our DVZip reader.**

```
  upload: dvzip, first bytes 0001FEF3789C
Connection failed: Block of 2147614720 bytes exceeds the 16777216 byte limit.
```

2147614720 is `0x80020000`, and without bit 31 it is `0x20000`: exactly 128 KiB. Two
readings of bit 31 were weighed:

- **"More blocks follow."** Refuted by Test 1. Its 45-byte first block was necessarily
  followed by more, yet `0000002D` has bit 31 clear.
- **"Stored raw."** Consistent with everything seen. The file was a JPEG, whose image
  data deflate cannot shrink, so a sender that stores incompressible blocks uncompressed
  would emit exactly a full 128 KiB raw block with a flag. Test 1's blocks never needed
  it.

**Implemented: bit 31 marks a stored block.** Its length is the low 31 bits, the 16 MiB
limit applies to that length, and the bytes are copied through. This is a reading, not a
confirmation. The receiver logs the first stored block's leading bytes and a per-upload
count of zlib and stored blocks. Raw JPEG bytes there, and a file that opens, confirm it;
`78 xx` there would refute it.

Also seen in Test 2:

- **iOS converted the photo.** The file was named with a UUID
  (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.JPG`) rather than `IMG_xxxx`, and arrived as
  JPEG. This fits a comment in opendrop's receiver: advertise no media capabilities and a
  receiver gets legacy formats. Our `/Discover` response advertises none either.
- **"Declined" despite consent.** Twice the phone showed "Declined" although `y` had been
  typed at the prompt. The likeliest explanation is `/Ask` timing out, or the link
  dropping, while waiting on a person; iOS would report either as a decline. Unverified.
  `receive --yes` removes the human delay and will separate the two.

**Injection errors do not separate success from failure.** Cumulative counts of
`unable to inject packet`: 93 after a failed first attempt, 217 after the successful
transfer (about 124 during it), and 327 after the failed large one (about 110 during it).
The success had *more* errors than the failure. `EAGAIN` limits throughput; it did not
cause Test 2 to fail. The `wmem` and `txqueuelen` experiments did not run, because OWL
was never restarted into their log files, so there is no conclusion on either.

**Operational:** restarting OWL destroys and recreates `awdl0`. The receiver keeps its
sockets on the old interface and drops out of the AirDrop row. Start OWL first and the
receiver after it, every time.

### Open questions

1. Does bit 31 mean stored? The next large transfer logs the first stored block.
2. With that fix, does a 1–3 MB photo complete, and at what size does transfer give out?
3. Do the "Declined" results persist with `--yes`?
4. Do `wmem_default` or `txqueuelen` change the `EAGAIN` rate?

#### 2026-09-14 — OBSERVED: stored blocks confirmed; 1.79 MB lands, video does not

Receiver at `b1c99b0`, run with `--yes`, started after OWL, with the phone touching the PC.

**Test A, a 1.79 MB PNG: success in about 1–2 minutes (roughly 15–25 KB/s).**

```
  preview: 29,641 bytes, Jpeg2000
  upload: dvzip, first bytes 0001E3DE789C
  dvzip: block 13 is stored (header 0x80020000), starts 54C38ADF2E2E2E59
  dvzip: 15 block(s), 12 zlib, 3 stored
  member . (directory)
  member ./IMG_xxxx.PNG (1,793,230 bytes)
```

The 1,793,230-byte file opened intact. **The stored-block reading is confirmed.** Block 13
begins `54 C3`, not a zlib header, and the archive decoded correctly. That fits the
content: PNG image data is already deflate-compressed, which makes it exactly the kind of
block a sender would store rather than recompress.

**Test C, three items in one share: one arrived, then nothing.**

```
  preview: 68,940 bytes, Jpeg2000
  upload: dvzip, first bytes 0001E895789C
  dvzip: block 12 is stored (header 0x80001000), starts 33ACDE15FE7A534B
  dvzip: 12 block(s), 11 zlib, 1 stored
  member . (directory)
  member ./FullSizeRender.heic (1,445,596 bytes)
```

- **A 4 KiB stored block (`0x80001000`).** Stored blocks are not a fixed 128 KiB, so bit 31
  is a type flag and not a chunk-size marker.
- **The order of these lines matters.** The receiver decompresses the whole upload body
  into memory before extracting anything, and prints the `dvzip:` summary only once the
  body has ended cleanly. So iOS had already *finished* this `/Upload` body before either
  `member` line appeared, and that body held the first item and not the other two. The
  session read the stall as a transfer that "stopped before it resolved". But the CLI
  prints `Received …` only when the connection closes, so a connection left open looks
  exactly like that.
- **Two explanations remain.** Either iOS sends each item of a share as its own `/Upload`,
  or it ended the transfer early. If it is the first, the receiver is at fault: it accepts
  exactly one `/Upload` per accepted `/Ask`, and would have answered a second with **401**.
  That rule was not changed on a guess. The receiver now logs every request and its answer
  as they happen, and names a refused second upload explicitly, so the next multi-item
  share settles it.
- **HEIC arrived unconverted.** In session 3 the same phone converted a HEIC to JPEG under a
  UUID name; here it sent `FullSizeRender.heic` as HEIC. What decides this is open.
  `--yes` mode printed only a file count, which hid each item's type and the `/Ask`'s
  `ConvertMediaFormats`. It now prints both.

**Test B, a short video (about 5–20 MB): failed, with nothing delivered.** `/Ask` was
accepted (43,331-byte preview). After that there was no `upload:` line in over ten
minutes, only repeated read and write resets, while the phone showed "Sending". Injection
errors rose from about 330 to 3,692 during the attempt, an order of magnitude more than
the successful transfer produced. At 15–25 KB/s, 10 MB needs 7–11 minutes of unbroken
link, and the overlap on channel 6 does not provide that. The receiver cannot help: the
iPhone is the sender, and its retry behaviour is not ours to change. The levers are link
quality and speed.

**`--yes` removed the "Declined" results entirely.** Every transfer was accepted without a
decline, which supports `/Ask` timing out while a person types. A GUI consent prompt faces
the same clock, so the timeout is worth measuring before the prompt is designed around it.

**Not run:** the `wmem` and `txqueuelen` experiments. **Not bracketed:** where between
1.8 MB and 5 MB a single file stops arriving.

### Open questions

1. Does iOS send a multi-item share as one archive, or as several `/Upload` requests?
2. What decides whether iOS converts HEIC, and does `ConvertMediaFormats` predict it?
3. Where between 1.8 MB and 5 MB does a single file stop arriving?
4. Do `wmem_default` or `txqueuelen` change transfer time or the `EAGAIN` rate?
5. How long does iOS wait for an answer to `/Ask`?

#### 2026-09-16 — OBSERVED: one /Upload for many files; EAGAIN exonerated; bigger buffers are worse

Session 5, receiver at `dbe6120`, run with `--yes`, OWL on channel 6. The phone was on
iOS 26.6; it has since updated to iOS 27, so everything here is 26.6.

**A multi-item share is one archive.** Three PNGs, 5,717,820 bytes:

```
08:08:49 request POST /Upload (chunked)
08:11:13 dvzip: 44 block(s), 23 zlib, 21 stored
08:11:13 member ./IMG_xxxx.PNG (2,029,799 bytes)
08:11:13 member ./IMG_xxxx.PNG (2,228,664 bytes)
08:11:13 member ./IMG_xxxx.PNG (1,459,357 bytes)
08:11:13 upload complete: 3 file(s), 5,717,820 bytes -> 200
```

One `/Ask` listing N files, one `/Upload` carrying one cpio archive with all N members. No
second `/Upload` arrived and the 401 branch never fired, so the receiver's
one-upload-per-`/Ask` rule matches what iOS does. The test pinning it stays, now as a
guard against a peer appending an upload nobody agreed to.

**Session 4's size ceiling does not exist.** 5.7 MB arrived intact in 144 s, about
40 KB/s, roughly twice the rate of the single 1.8 MB file. Whatever stops a video, it is
not size.

**Identical names lose files, on the phone's side.** A share of three *edited* photos
produced an `/Ask` listing three files all named `FullSizeRender.heic`, and an upload
whose archive held a single member of 1,439,753 bytes across 12 blocks: one file's worth
of data. The loss therefore happened before the archive was built, not in our extractor.
Ours would have overwritten silently all the same, so a collision now becomes
`name (2).ext` and is logged.

**EAGAIN is not a failure predictor.** 20,857 injection errors over 25 minutes, with the
5.7 MB transfer succeeding inside that window. Session 4 saw about 330 during a success
and 3,692 during a failure. OWL retries and the data gets through. Three sessions treated
this as the prime suspect; it is not one.

**Bigger socket buffers made it slower.** The same three PNGs took 144 s at default
buffers and 266 s with `net.core.wmem_default` and `wmem_max` at 4 MB, about 85% worse.
Offered mechanism: a deeper send queue holds more frames than the channel-6 windows can
drain, so loss is noticed later and retried later. Caveats: one run per configuration on a
drifting link, and the 4 MB run needed three attempts. Keep the direction, not the number.
`txqueuelen` was deliberately skipped rather than stacked on top of a change already
pointing the wrong way. The experiment worth running is the opposite: buffers *below*
default.

**`/Ask` takes 8 seconds to answer, and that is the real flakiness.** Three exchanges took
8 s each (08:08:38 to 46, 08:20:21 to 29, 08:20:50 to 58). The first went on to upload;
the phone abandoned the other two. Identical latency, different outcomes, which puts us
right on iOS's patience threshold. It is not the consent prompt: `--yes` was on
throughout. It explains the "fails on the first tap, works on the second" pattern from
earlier sessions.

Where those 8 s go is now measured rather than guessed: the receiver times the `/Ask` body
read, the consent decision and the reply separately. The body is the suspect, since iOS
attaches a JPEG 2000 preview of 43 to 69 KB to every `/Ask`, and at early-connection rates
that alone is seconds. If that is where the time goes, no change to our code helps; the
lever is a TXT record that asks iOS for less, which `receive --flags <hex>` now makes
testable.

### Open questions

1. How much of the 8 s is reading the preview? Instrumented; the next session reads it off
   the log.
2. Does advertising different flags change whether iOS attaches a preview, or its size?
3. Do socket buffers *below* default beat 144 s for 5.7 MB?
4. Why did three edited photos collapse into one archive member on the phone?
5. What blocks video: duration, codec, or iOS's own transcode path?
6. What does iOS 27 change? It is reported to make AirDrop up to 80% faster with no new
   restrictions, and every observation above predates it.

#### 2026-09-19 — OBSERVED: the /Ask cost is the preview, and iOS 27 tripled it

Session 6, receiver at `9280671`, phone on **iOS 27.0** (all earlier sessions were 26.6).
The three-part `/Ask` timing settled where the seconds go:

```
17:52:21 /Ask body: 131,746 bytes read in 10,522 ms
  IMG_xxxx.WEBP  [org.webmproject.webp]
  preview: 127,286 bytes, Jpeg2000
17:52:21 -> 200 accepted (body 10,522 ms, consent 1 ms, reply 0 ms, total 10,531 ms)
...
17:56:23 /Ask body: 201,469 bytes read in 17,269 ms
  preview: 196,962 bytes, Jpeg2000
17:56:23 -> 200 accepted (body 17,269 ms, consent 0 ms, reply 0 ms, total 17,270 ms)
```

Consent and reply are 0–1 ms. The entire `/Ask` cost is receiving the body, and the body is
~97% preview image (127,286 of 131,746; 196,962 of 201,469). **Our code contributes nothing
measurable.** Every tap this session was declined by the phone: at 10–19 s we are past its
patience.

**iOS 27 made it worse, not better.** On 26.6 previews were 30–70 KB and `/Ask` took ~8 s;
on 27.0 they are 127–215 KB and `/Ask` takes 10–19 s. Whatever iOS 27 speeds up
Apple-to-Apple, the larger preview is a straight loss on a ~20 KB/s link.

**Capability flags do not control the preview.** With `--flags 0x02` the preview was still
210,808 bytes (19,491 ms total). With `--flags 0`, `kali` still appeared (mDNS ignores
flags) and `/Discover` was answered, but iOS never sent an `/Ask` at all — so the flags gate
the handshake but not the preview size. This matches the protocol: the only receiver-side
negotiation in `/Discover` is `ReceiverMediaCapabilities`, which governs file-format
conversion (hence `convert media formats: no` throughout), not the icon. iOS also sent a
`.WEBP` natively — iOS 27 ships WebP without transcoding.

**Why neither proposed fix works as first framed.**

- *Parse the metadata, reply early, drain the preview.* Impossible with this wire format. A
  binary plist is unreadable until its final 32-byte trailer, which points to the offset
  table; the `FileIcon` blob sits in the object region before it. The metadata cannot be
  reached without first receiving the whole preview, and the bytes cross the slow link
  whenever we reply.
- *Ask iOS for no preview.* No protocol expresses it. The sender decides `FileIcon`
  unilaterally from the file type; there is no receiver key to suppress it.

**The one real lever, now testable.** RFC 9110 lets a client stop uploading a body once it
sees a final response. If CFNetwork honours that, an early `200` on `/Ask` could make iOS
abandon the preview mid-upload. `receive --early-ask` (option `EarlyAskReply`) tests it: it
answers 200 before reading the body, then times what iOS does with the body. A full body
still arriving in ~10–19 s means iOS ignored it and the bottleneck is the link, full stop; a
fast or absent body means a real fix exists. It accepts sight-unseen, so it is a measurement
tool under `--yes`, never the product path. Verified locally that switching it on does not
break a normal transfer (our sender writes the whole body before reading the reply, the same
as an iOS that ignores the early 200).

If iOS ignores it, the honest conclusion is that image AirDrop over this card is
link-bound: the durable fix is transmitting on channel 44 (where the phone spends most of
its time) rather than channel 6, which needs different hardware or a way past the AX211's
self-managed 5 GHz rules. Non-image shares, which carry no preview, are unaffected and
should already be fast.

### Open questions

1. Does iOS stop sending the preview when `--early-ask` replies 200 first? The measurement
   this session runs.
2. If it does, what is the smallest change that keeps a real consent prompt — accept, then
   read the body for display, then let iOS proceed?
3. What is the 5.7 MB baseline on iOS 27 at default buffers (the 144 s figure was 26.6)?
4. Do below-default socket buffers beat that baseline?

#### 2026-09-20 — CONFIRMED: answering /Ask first fixes the declines; the link is the rest

Session 7, receiver at `8a96074`, phone on iOS 27.0, `--early-ask --yes`.

**The experiment worked, but not by the mechanism it was built to test.** iOS sent the
complete /Ask body every time (47,847 / 49,806 / 14,128 bytes), so it does not take RFC
9110's option to stop uploading after a final response. The preview is never skipped. What
the early 200 does is stop iOS's timer, which runs on the **/Ask round trip**: an answer
that waits for the body lands too late. Three taps out of three cleared the handshake,
including one whose body took 8,323 ms — squarely in the range that was declined every
single time in session 6.

```
11:37:14 request POST /Ask (chunked)
11:37:14 early-ask: replying 200 before reading the body
11:37:22 early-ask: full body of 49,806 bytes still arrived 8,323 ms after the early reply
11:37:22 request POST /Upload (chunked)
11:41:05 dvzip: 30 block(s), 15 zlib, 15 stored
11:41:05 member ./IMG_xxxx.PNG (3,627,446 bytes)
11:41:05 upload complete: 1 file(s), 3,627,446 bytes -> 200
```

**New record: 3,627,446 bytes, intact, in 223 s (~16 KB/s).** A ~25.6 MB item died one
second into /Upload with nothing delivered, and with a ~40 MB image selected the receiver
stopped appearing in the AirDrop row at all — iOS gives up before reaching us.

**Consent had to move rather than vanish.** The 200 only tells the sender to proceed; what
matters is whether bytes are written. The receiver now answers /Ask first, runs the prompt
*while the upload streams in*, and extracts only if it is granted — a refusal discards the
buffer, answers 401 and leaves nothing on disk. The cost, recorded plainly: an unapproved
sender can make us receive and buffer an upload before anyone agrees to it. Default is
still off pending a decision on whether that trade is acceptable for the product.

**The remaining blocker is channel overlap, and it is not a software problem.** OWL logged
the phone's sequence directly:

```
11:47:12 peer changed channel sequence to 149,149,149,149,149,149,0,0,6,149,149,149,149,149,0,0
11:47:51 peer changed channel sequence to 149,0,149,0,0,0,0,0,6,6,149,0,0,0,0,0
```

Almost all 149, dipping to 6 briefly. The AX211 may only transmit on 6. Throughput measured
15 KB/s, then 6 KB/s, then ~16 KB/s sustained, then collapse — degrading across the session.

**Hypothesis worth testing before buying anything: the phone follows its infrastructure
channel.** Session 5 saw "mostly 44", session 7 "almost exclusively 149". Apple devices fold
the channel of the Wi-Fi network they are joined to into the AWDL sequence, so a single
radio can serve both. If that is what is happening, the sequence is tracking the router's
5 GHz channel, and joining the phone to a **2.4 GHz** network should pull the sequence onto
channel 6, where this card can actually transmit. Free to test: a 2.4 GHz-only SSID, a
hotspot from another device, or Wi-Fi off entirely to see what AWDL falls back to. If it
works, it changes the project's ceiling without new hardware.

### Open questions

1. Does the phone's AWDL sequence follow its infrastructure Wi-Fi channel? Joining it to
   2.4 GHz is the test.
2. Should early-ask be the default, given it buffers an unapproved upload?
3. iOS 27 baselines at default buffers, taken early in a session while the link is fresh:
   1.8 MB and 5.7 MB.
4. Where between 3.6 MB and 25 MB does a single file stop arriving?

#### 2026-09-20 — REFUTED: the phone does not follow its Wi-Fi channel far enough to matter

Session 8, receiver at `702c4e7`, phone on iOS 27.0. The hypothesis was that joining the
phone to a 2.4 GHz network would pull its AWDL sequence onto channel 6.

**The mechanism is real and was confirmed twice.** With the phone moved to a 2.4 GHz SSID,
149 disappeared and an 11 appeared — channel 11 being what the router had auto-selected:

```
20:06:01 peer changed channel sequence to 44,44,44,44,44,44,0,0,6,44,44,44,44,44,0,0
20:06:12 peer changed channel sequence to 11,0,44,0,0,0,0,0,6,0,44,0,0,0,0,0
```

Pinning the router's 2.4 GHz band to channel 6 then made the 11 vanish, so the phone was
tracking the router move. **But the 44s never budged**: one channel-6 slot in sixteen,
before and after. A transfer under the pinned condition reached DVZip block 13 in two
minutes and then died, identical to session 7. The avenue is closed: no home-network
configuration brings this phone onto channel 6 often enough to matter.

**Three Apple devices were in range, and the other two sat on channel 6** — one advertising
all sixteen slots there. Full channel-6 commitment is clearly possible for an Apple device;
it is just not what a 5 GHz-capable phone does. Peer MACs and UUIDs rotate every few
minutes, so identity cannot be tracked by MAC across a session; attribution here rests on
which peer was active during the transfer, and on the fact that a peer sharing one slot in
sixteen is the only one that produces a two-minute crawl.

**What the refutation leaves is a better target, from our own regulatory readings.**
Session 6 recorded the AX211's flags once disconnected: 149 passive/no-IR, 44
**IR-CONCURRENT**, 6 unrestricted. Every session since has used 6 because it needed no
condition. But IR-CONCURRENT is a condition that can be met: the card may transmit on 44
while it holds a concurrent connection on that channel — and 44 is precisely where the
phone spends fifteen of sixteen slots. 149 is not worth pursuing separately: in ETSI
countries 5745 MHz is not available for this, which is what its no-IR flag reports.

So the experiment that was never run is: put the router's 5 GHz band on channel 44, join
Kali to it as an ordinary station, add a **monitor interface beside the station** rather
than converting it, and run OWL on channel 44 with `-N`. `tools/owl-concurrent.sh` does
this, and deliberately does not kill NetworkManager, since the association is the point.

It turns on one unknown: whether `iwlmvm` permits a monitor interface concurrently with a
managed one. The script prints the card's valid interface combinations before trying, so
the answer is legible either way. If the card refuses, hardware is the answer and this is
the last free idea; if it accepts, the overlap goes from one slot in sixteen to fifteen.

### Open questions

1. Does this card allow monitor alongside managed, and does IR-CONCURRENT then permit
   injection on 44?
2. iOS 27 baselines at default buffers, taken early while the link is fresh: 1.8 MB, 5.7 MB.
   Outstanding for three sessions.
3. Where between 3.6 MB and 25 MB does a single file stop arriving?
4. Should early-ask be the default, given it buffers an unapproved upload?

#### 2026-09-21 — REFUTED: a concurrent link on 44 gives transmit, not receive

Session 9, receiver at `5400d1e`, phone on iOS 27.0, router's 5 GHz band fixed on channel
44 at 80 MHz, Kali associated to it throughout.

**The card half-accepted.** `mon0` was created beside `wlan0` without error, although the
card's valid interface combinations do not list monitor at all. OWL reported
`Channel 44 [5220 MHz] is available for frame injection`, a first for this project. No
peer was ever added, where channel 6 finds one within a second.

**Transmit worked; receive was filtered.** `tcpdump -i mon0 -e` showed only our own
injected frames, ~27/s, under AWDL's fixed BSSID `00:25:00:ff:94:73`. No other device
appeared, and no router beacon, though ~11 were due in the window. A second monitor
interface made with `flags otherbss control` did receive real traffic. But it was only data
frames inside our own BSSID, plus two protected action frames between us and the router,
and still no beacons across 5.5 s. While associated, the firmware delivers to a monitor
interface only what the station itself would accept. AWDL is almost entirely broadcast
action frames under a foreign BSSID, exactly the category dropped. `otherbss` does not
override it. Transmit without receive is useless, so this route is closed on the AX211.

**The card does not recover cleanly.** Afterwards, plain channel-6 monitor mode, which
worked in every earlier session, heard only our own transmissions. `iw dev wlan0 info`
showed 40 MHz wide at center 2447 where every earlier session ran 20 MHz; `set channel 6
HT20` fixed the width but not the deafness. Only reloading the driver restored reception
(`modprobe -r iwlmvm iwlwifi`, then `modprobe iwlwifi`), after which peers appeared at
once. The reload also corrupted the desktop display, though terminals stayed usable.

**The iOS 27 channel-6 baseline, at last:** one PNG of 1,333,114 bytes in **83 s, ≈16 KB/s**
from `request POST /Upload` to `upload complete`. That matches iOS 26.6, so iOS 27 changed
nothing measurable here. All three `/Ask` bodies were 39,316 bytes and took 4.9–5.8 s, and
early-ask carried every one of them, with no declines.

**Two failed attempts and a failed two-photo share all died after `block 13 is stored`.**
That is not the decoder. The line is logged only once the whole 128 KiB block has been read
([DvZip.cs](../src/WinDrop.Protocol/Compression/DvZip.cs)), so block 13 arrived and the
link died later. Stored blocks were already proven by a 1.79 MB PNG with three of them. One
inconsistency to settle next time: the successful attempt had 12 blocks, so the failed ones
cannot have been byte-identical uploads of the same file, as the report believed.

**The finding that matters is a channel sequence taken during the successful transfer:**

```
19:40:29 peer changed channel sequence to 6,6,6,6,6,6,44,44,6,6,6,6,6,6,44,44
```

Twelve slots in sixteen on our channel, against one in sixteen in sessions 7 and 8, and
the rate did not move: 16 KB/s, the same crawl. Session 5 reached ~40 KB/s with the phone
mostly on 44. If this peer was the phone (it was the active one, but MACs rotate), then
**channel overlap is not what limits throughput.** That undercuts the reasoning behind
this session's experiment, and behind any plan to get more overlap.

**The better-fitting explanation is active monitor mode.** OWL asks for it, and the AX211
refuses it (`owl-session.sh` exists to work around that). In plain monitor mode the card
receives the phone's unicast frames but never acknowledges them. The phone then counts
every frame as lost, retries it up to its limit, and adapts its rate downward. The data
still arrives, since we heard the first copy, but at a rate set by a link the phone
believes is failing. That predicts a slow rate largely independent of overlap, which is
what this session shows. It is testable without new hardware: frames the phone retries
carry the 802.11 Retry bit, so a passive capture during a transfer shows how many are
repeats.

**Early-ask is now the default,** with one change of design first. The first version read
and inflated the upload while the prompt was open, deciding only before the write. As a
default that would let any stranger fill our memory with an archive of any size, once per
parallel connection in the app. `/Upload` now waits for the decision before reading a
byte. An unapproved sender gets only the `/Ask` body it already had to send, and TCP holds
the rest back. A test pins the order: it fails against the old receiver, whose log shows
the upload inflated before `consent given`.

### Open questions

1. How many of the phone's frames carry the Retry bit during a transfer? If most do, the
   missing acknowledgements are the throughput limit, and only a card with active monitor
   mode fixes it.
2. How long does iOS tolerate an upload held back by TCP while a person decides? Every field
   session so far auto-accepted with `--yes`, so none has waited. Run once without `--yes`
   and answer after about 10, 30 and 60 s.
3. The iOS 27 multi-file baseline (~5.7 MB) is still owed.
4. Where between 3.6 MB and 25 MB does a single file stop arriving?
5. Sending **to** an iPhone has never been tried.
