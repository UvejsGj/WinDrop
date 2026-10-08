# The phone page

A route from an iPhone to Windows, and back, that needs no AWDL, no Linux and no extra
hardware. WinDrop serves a small web page on the local network. The iPhone's camera
scans a QR code on the PC, Safari opens the page, and files go both ways at ordinary
Wi-Fi speed.

**It is not AirDrop.** The PC does not appear in the iPhone's AirDrop list, and nothing
here changes what [ADR-001](adr-001-transport-selection.md) found: an iPhone only does
AirDrop over AWDL, which Windows cannot drive. This is the honest alternative for the
person who has a Windows PC and an iPhone today. The AirDrop work over Linux and OWL
carries on alongside it.

## Using it

**In the app:** the phone button in the header opens a sheet with the code. Files chosen
in the app are offered on the page while that sheet is open. Uploads from the phone go
through the same consent sheet as AirDrop.

**From the CLI:**

```
windrop link [--dir <path>] [--port <n>] [--yes] [--offer <file>]...
```

It prints the code in the terminal and serves until Ctrl+C.

Each run of the app or the CLI makes a new link, so after restarting WinDrop the phone
scans the code again.

### If the phone cannot open the page

- **Same network.** The phone and the PC must be on the same Wi-Fi, or the PC wired to
  the same router. Guest networks often isolate devices from each other.
- **Windows Firewall.** The first time WinDrop (or `dotnet`, for the CLI) listens,
  Windows asks whether to allow it. It needs to be allowed on private networks. If the
  Wi-Fi is set as a *public* network in Windows, incoming connections are blocked; the
  network's profile is under Settings > Network & internet > Wi-Fi.
- **The address.** The code uses the PC's IPv4 address on the interface with a default
  gateway, Wi-Fi first. The CLI also prints the other addresses it found.

## How it works

Three requests matter, all under `/<token>/`:

| Request | What it does |
|---|---|
| `GET /<token>/` | the page, its script and stylesheet, all embedded in the build |
| `POST /<token>/upload` | a `multipart/form-data` upload: fields first, then files |
| `GET /<token>/files/<generation>/<index>` | an offered file, as an attachment |

`GET /<token>/info` returns the PC's name and the offered files, which the page polls
while it is open. HTTP is our own [`HttpConnection`](../src/WinDrop.Protocol/Http/HttpConnection.cs),
the same one AirDrop uses, over plain TCP.

### The upload

The page sends three kinds of form field, in this order:

1. `from`: a guess at the device ("iPhone", from the browser's user agent). Display only.
2. `manifest`: a JSON list of `{name, size}` for every file it is about to send.
3. `file`, once per file.

The server reads the fields, then the first file's headers, and **asks before reading any
file content**. Until the person answers, the upload waits in the socket and TCP holds the
phone back. This is the same rule as AirDrop's early-ask, for the same reason: reading
first would let anyone with the link make the PC take in a file of any size before anyone
agreed to it. A test sends the head and the list of a 10 MB upload and nothing more, and
checks that the question arrives anyway.

**The list is binding.** What the person agreed to is what lands on disk. Each file is
saved under the name the list gave it, and may be no larger than the list said. The count
must match. A page cannot show "photo.jpg" at the prompt and deliver "photo.exe". Any
mismatch fails the whole upload, and nothing is kept.

**No list, no question.** An upload that does not send the list before its files is
refused before anyone is asked. The list is what the person is shown, so without one there
is nothing honest to ask about.

Files are staged and committed all or nothing by [`IncomingFiles`](../src/WinDrop.Protocol/IncomingFiles.cs),
the same class the AirDrop receiver uses. A failed or refused upload leaves nothing
behind.

### Names

A form sends a bare file name, but nothing stops a client from sending a path, so
[`PhonePageServer.Destination`](../src/WinDrop.Protocol/Web/PhonePage.cs) keeps only the
last segment and makes it safe for Windows:

- control and direction-override characters become `_`, as for AirDrop (`PeerText`)
- `< > : " | ? *` become `_`. A colon would otherwise name an NTFS alternate data stream:
  `a:b.txt` would write a hidden stream called `b.txt` on a file called `a`
- trailing dots and spaces are trimmed, since Windows strips them anyway
- device names (`CON`, `NUL`, `COM1` and the rest) get a leading `_`
- then the same containment check as an AirDrop archive member

Duplicate names get " (2)" and so on, never an overwrite.

### Downloads

Files are offered only while the app's phone sheet is open, or for the CLI's lifetime
with `--offer`. Choosing files in the app is not by itself a decision to let a phone
fetch them, but opening the sheet with them chosen is. Each offer has a generation
number, so a link to an earlier offer stops working the moment the offer changes.

Responses carry `Content-Disposition: attachment` with an ASCII fallback name and the real
name in RFC 8187 form, and declare their length so Safari can show progress. Downloads go
to the iPhone's Files app.

## Security model

**Two locks.**

1. **The token.** 128 random bits in the address. Every request must carry it as the first
   path segment, compared in constant time, or it gets the same 404 as a page that does
   not exist. Without it, nobody else on the network can reach the page, the prompt or the
   offered files.
2. **The prompt.** Anyone who has the link can *ask* to send files, but nothing is written
   until the person at the PC agrees.

The app and the CLI make a new token every run, so a link stops working when WinDrop
closes. **New link** in the app replaces it while running, which is how you take the
address back from a phone you no longer want sending.

**Plain HTTP, on purpose.** Safari shows a full-page warning for a self-signed
certificate, so HTTPS would mean teaching people to click through certificate warnings.
Instead:

- **Protected:** the traffic, by the Wi-Fi's own encryption, from anyone outside the
  network. The page and its files, by the token, from anyone on the network who does not
  have the link.
- **Not protected:** the traffic from someone already on the same network who can
  intercept it. They could read files in transit, and the token with them.

That is why the page and this document say it is for home and other trusted networks.

**Other limits:**

- A Content-Security-Policy allowing only the page's own script and style. Everything the
  PC sends into the page is set with `textContent`, never parsed as HTML.
- Referrer-Policy `no-referrer` and Cache-Control `no-store`, since the token is in the
  address.
- A request that frames its body two ways (Content-Length and Transfer-Encoding) is
  refused, and so is a Transfer-Encoding other than plain chunked.
- At most 16 connections at once; more are closed at once rather than queued.
- A connection with nothing moving for 60 seconds is closed.
- Form header lines are limited to 8 KB, 16 headers per part, 64 KB per text field, 1,000
  files per upload and 8 GiB per request.
- The logs never print the token.

## What is verified, and what is not

**Verified with a real iPhone** (2026-10-08, iOS 27.0.1, the app on Windows, home Wi-Fi):

- The iPhone's camera read the QR code from the app's sheet, and Safari opened the page.
- One photo, sent from the page and accepted at the PC's prompt, arrived intact: 1.6 MB,
  saved as `IMG_xxxx.jpeg`.
- Five photos in one send, about 3.6 MB together and 0.1 to 2.5 MB each, all arrived as
  valid JPEGs.
- A send declined at the PC showed "Declined on" and the PC's name on the phone, and left
  nothing behind: no file and no staging folder. The phone was still uploading when the
  refusal went out, so this is the linger before closing doing its job. Without it, the
  close can reset the connection before the refusal is read, and the phone shows only a
  network error.
- **Safari converts to JPEG.** The camera shoots HEIC, and the file that arrived is a JPEG
  (it starts `FF D8`, JFIF) with a lowercase `.jpeg` name. Safari converts photos picked
  through a web page's file input, which is what most people want on Windows. The other
  side is that the HEIC original, and whatever metadata the conversion drops, does not
  arrive.
- **PC to iPhone:** five images, and then a video, offered by the PC downloaded to the
  phone.

**Verified on this machine:**

- The page, its script and its security headers in a Chromium-based browser at phone size.
- The page's own upload path end to end: the list, the files, and the auto-accepted
  save.
- curl against every route.
- The unit and socket tests in `PhonePageTests`, `MultipartReaderTests` and `QrCodeTests`.

The QR encoder is checked against values published with the standard: Reed-Solomon
codewords, format and version bits, capacities and alignment positions. A test reader,
written from the standard's description of the layout rather than from the encoder's code,
reads codes back under all eight masks and across versions 1 to 10. Breaking the encoder
on purpose (a mask's axes swapped, the zigzag reversed, the block interleaving changed, a
format copy misplaced) fails those tests, and so do the server's equivalents: consent
ignored, the token unchecked, a listed size not enforced, a colon left in a name.

**Not yet verified with a real iPhone:**

- a video from the phone to the PC
- how long Safari waits while the PC's prompt is open (probably about 60 seconds of no
  progress)

## Set aside for now: a share-sheet Shortcut

The first version also explained how to build an iOS Shortcut, *Send to WinDrop*, that
would appear when you tap Share in Photos. It was tried on 2026-10-08 and set aside. A
Shortcut cannot send the list of files ahead of them, so supporting it meant a vaguer
prompt that named only the first file and said more might follow, and it meant a link
that survived restarts, since the Shortcut stores the address. Without it, every prompt
lists exactly what will arrive, and every link dies with the run that made it. Both came
back out with the Shortcut; the commit that removed them is the place to start if it
returns.
