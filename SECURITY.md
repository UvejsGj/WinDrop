# Security policy

WinDrop's receiver accepts files from devices it has never met, over a protocol where the
sender proves nothing about who it is. That makes receiving the part of this project where
a bug matters most, and reports about it are welcome.

## Reporting a vulnerability

Please do not open a public issue. Report it privately through GitHub: the repository's
**Security** tab, then **Report a vulnerability**
([direct link](https://github.com/UvejsGj/WinDrop/security/advisories/new)).

Say what you sent, what happened, and which commit you tested. A receiver log helps; redact
device names, MAC addresses, UUIDs, IP addresses and anything else that identifies a person
or a device.

This is a one-person learning project, so there is no guaranteed response time. Expect an
acknowledgement within about a week, then a fix or a written explanation.

## Supported versions

Only the latest commit on `main`. There are no releases yet, and nothing is backported.

## In scope

The receiver depends on a few boundaries. Getting past any of them is a vulnerability:

- **Consent.** Nothing from a transfer may be written unless a person accepted it. An
  `/Upload` without an accepted `/Ask` on the same connection must be refused, and with
  early-ask (the default) the upload must not even be read before the decision.
- **Paths.** Archive member names come from the sender. A member that lands outside the
  download directory, or replaces a file that was already there, is a vulnerability.
- **Resource limits.** The receiver bounds the upload as sent (`MaxUploadBytes`), what it
  unpacks to (`MaxExtractedBytes`), how many members an archive holds (`MaxArchiveMembers`)
  and the `/Ask` body (`MaxAskBodyBytes`). An input that exhausts memory or disk anyway, or
  leaves files behind after a failed transfer, is in scope.
- **Parsers.** The binary plist, HTTP/1.1, DVZip, cpio (newc and odc) and mDNS readers all
  handle bytes from strangers. A crash, hang or out-of-bounds read from crafted input counts.
- **Previews.** The app decodes only JPEG and PNG previews, through named decoders, after
  checking the image's dimensions. Getting any other format, or an oversized image, to a
  decoder counts.
- **Terminal output.** Names and other text from a peer must not be able to carry escape
  sequences to a terminal.

## Not in scope

These are properties of AirDrop's Everyone mode, or documented test switches, not bugs:

- **TLS authenticates nobody.** In Everyone mode both sides use self-signed certificates and
  neither verifies the other; that is how the mode works. A person saying yes is the boundary.
- **`--yes` skips the consent prompt** on purpose, for scripted testing. It removes the one
  real security boundary, which is why the receiver announces it.
- **Early-ask answers "go ahead" before the person decides.** The upload is still not read
  until they do. The reasoning is in `src/WinDrop.Protocol/AirDropReceiver.cs`.
- **Anyone nearby can see the receiver** while it advertises. That is what advertising is for.
- **The bridge** (`tools/windrop-bridge.py`) can drop or delay traffic. It parses nothing and
  terminates no TLS, so it cannot read a transfer, but it can deny service.
- **Radio limitations**, such as the slow transfers on cards without active monitor mode.
