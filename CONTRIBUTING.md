# Contributing to WinDrop

WinDrop is a learning project: an AirDrop implementation for Windows, built from scratch by
reverse-engineering the protocol. Understanding comes before speed here. Contributions are
welcome in that spirit, and a correct explanation of why something works is worth as much
as the code that does it.

## Before you start

- For anything bigger than a small fix, open an issue first, so the approach can be agreed
  before the work is done.
- Read [The one thing to understand first](README.md#the-one-thing-to-understand-first) in
  the README, and skim [docs/protocol-notes.md](docs/protocol-notes.md) for the area you are
  touching. Most of the protocol's surprises are recorded there with their evidence.

## Building and testing

```powershell
dotnet build
dotnet test
```

You need the .NET 8 SDK. The app is WPF and needs Windows; the protocol library and the CLI
also run on Linux. Every test must pass. Python 3.12 is only needed to regenerate the bplist
fixtures, which are committed. On Windows, see the README's note on Smart App Control.

## How changes are judged

- **Evidence over assumption.** If a change rests on how Apple's implementation behaves,
  say how you know: a capture, a test on a real device, or an independent implementation.
  Entries in `docs/protocol-notes.md` are dated and marked OBSERVED, MEASURED, CONFIRMED or
  REFUTED. Add one when you learn something about the protocol, including when an idea
  turns out to be wrong.
- **Test against something we did not write.** A codec can be wrong in a way that
  round-trips through itself perfectly. Existing layers are checked against Python's
  `plistlib`, bsdtar/libarchive, python-zeroconf and opendrop; see
  [On testing](README.md#on-testing). New parsers should be too, where an independent
  implementation exists.
- **Comments explain why.** The code comments the reasoning and the evidence behind
  decisions that are not obvious, not what the next line does. Please match that.
- **The security boundaries stay intact.** Consent before anything is read or written,
  `ResolveSafePath` for every member name, the resource limits, and `PeerText.Printable`
  on any peer text shown to a person. A change that touches any of them needs a test that
  fails without it. See [SECURITY.md](SECURITY.md).

## Tests with real devices

Reaching an iPhone needs Linux with [OWL](https://github.com/seemoo-lab/owl) and a
suitable Wi-Fi card; [docs/bridge-hardware-setup.md](docs/bridge-hardware-setup.md) is the
runbook. Results from real devices are the most valuable thing you can contribute, working
or not, and there is an issue template for them.

The repository is public, so redact before you share a log or a capture: device names,
MAC addresses, UUIDs, IP addresses, and file names beyond the `IMG_xxxx` pattern.

## Pull requests

- Keep one concern per pull request. The template asks what changed, why, and how it was
  tested.
- Write commit messages as a short summary line, then a body that explains why, especially
  what was observed that made the change necessary.
- By contributing, you agree that your work is licensed under the [MIT License](LICENSE).

Everyone taking part is expected to follow the [code of conduct](CODE_OF_CONDUCT.md).
