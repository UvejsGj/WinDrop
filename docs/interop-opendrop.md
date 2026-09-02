# Interop testing against opendrop

opendrop is an independent implementation of the same AirDrop protocol, written by the
SEEMOO lab. Testing against it is the same discipline as the `plistlib` and `bsdtar`
oracles: our reader agreeing with our writer proves only that they share any
misunderstanding. opendrop was written by people who read the protocol separately, so a
transfer that works against it is evidence rather than self-congratulation.

**It does not need AWDL.** opendrop defaults to `awdl0` but accepts any interface, so it
runs over ordinary networking. That is what makes this test free.

**What it does and does not prove.** It exercises bplist, TLS, HTTP/1.1, the
Discover/Ask/Upload state machine, cpio and the gzip path against a third party. It says
nothing about whether an iPhone will accept us — only Apple hardware answers that.

## Where to run opendrop

**WSL2 on the main PC is the easiest option** — no second machine, no USB stick, no live
boot. In an **Administrator** PowerShell:

```powershell
wsl --install
```

That installs WSL2 with Ubuntu and needs a reboot. Nothing else on the machine changes.

The old dv6 with a Linux live USB still works as a fallback. If you go that route, **use
Ethernet**: Broadcom Wi-Fi on a live USB usually wants non-free firmware that is not
installed, and fetching it needs the network you are trying to bring up.

## The networking wrinkle, and why `--peer` exists

WSL2 defaults to NAT, putting Linux on its own subnet behind a virtual switch. TCP works
in both directions, but **mDNS multicast does not reliably cross that boundary**, so
discovery may find nothing even though everything else would work.

Two ways around it.

**Option A — skip discovery.** `windrop send --peer <address>` connects straight to an
address and never browses. Since the app-layer protocol is what we are testing and not
our mDNS implementation, this loses nothing that matters. Get the WSL address with:

```powershell
wsl hostname -I
```

**Option B — mirrored networking.** Create `%USERPROFILE%\.wslconfig`:

```ini
[wsl2]
networkingMode=mirrored
```

Then `wsl --shutdown` and restart. WSL shares the host's interfaces, and multicast
should cross. This is the only way to run Test B below, since opendrop has no
direct-address option and has to discover us.

## 1. Open the Windows firewall

The loopback tests never crossed a network boundary. This does, and Windows Firewall
will drop it silently. In an **Administrator** PowerShell:

```powershell
netsh advfirewall firewall add rule name="WinDrop 8770" dir=in action=allow protocol=TCP localport=8770
netsh advfirewall firewall add rule name="WinDrop mDNS" dir=in action=allow protocol=UDP localport=5353
```

To remove them afterwards:

```powershell
netsh advfirewall firewall delete rule name="WinDrop 8770"
netsh advfirewall firewall delete rule name="WinDrop mDNS"
```

## 2. Get a Python old enough for opendrop

**Check first — this is the step most likely to sink the whole exercise.** In WSL:

```bash
python3 --version
```

opendrop has not been meaningfully maintained since around 2021. On Python 3.12 expect
friction; on 3.13+ expect failure, because its dependencies (`libarchive-c`, older
`zeroconf`) predate those releases and will not build or import.

Ubuntu 26.04 ships **Python 3.14 and offers no older interpreter in apt**, so if that is
what `wsl --install` gave you, there is nothing to fix inside that distro. Install an
older one alongside it instead — WSL runs several distros at once, and they share the
same virtual network, so the address does not change:

```powershell
wsl --install -d Ubuntu-22.04
```

Ubuntu 22.04 ships Python 3.10. Afterwards, enter that distro specifically:

```powershell
wsl -d Ubuntu-22.04
```

## 3. Install opendrop

Inside the 22.04 distro:

```bash
sudo apt update
sudo apt install -y python3-venv python3-pip libarchive-dev
python3 -m venv ~/od
source ~/od/bin/activate
pip install opendrop
```

The venv is not optional on recent Ubuntu: PEP 668 makes a bare `pip install` fail with
`externally-managed-environment`.

Find the interface name and confirm the flags, which have changed across versions:

```bash
ip -brief addr
opendrop --help
```

## 4. Test A — WinDrop sends, opendrop receives

**Status: passing as of 2026-09-02.** See [protocol-notes.md](protocol-notes.md).

The harder direction and the one to do first: it exercises our bplist writer, our cpio
writer, the compression negotiation and the whole sender state machine.

Three facts about opendrop that are not obvious and will each cost you an hour:

- **Its listener is IPv6-only** (`HTTPServerV6`, `AF_INET6`), bound to the interface's
  link-local address. The IPv4 address of the same host will never connect.
- **It listens on port 8771**, not AirDrop's 8770.
- **`-i` selects the interface; `-n` is the display name.** Passing `-n eth0` is silently
  accepted, leaves it on `awdl0`, and then fails with a message about `owl` not running,
  which points at entirely the wrong problem.

In WSL:

```bash
opendrop receive -i eth0
```

It prints the link-local address and port it bound to. To reach that from Windows you
need the address with the **Windows-side** scope ID, which is the index of the WSL
virtual adapter, not the index Linux uses:

```powershell
Get-NetAdapter | Where-Object { $_.Name -like "*WSL*" } | Format-Table Name,ifIndex
```

Then send, combining opendrop's address with that index:

```powershell
dotnet run --project src\WinDrop.Cli -- send --peer "fe80::215:5dff:fe69:142%45" --port 8771 C:\path\to\photo.jpg
```

opendrop prompts for consent in its own terminal; answer `y`. Expected:

```
Sending to fe80::...%45 @ [fe80::...%45]:8771 via direct (flags=0x0)
Peer identifies as 'UvejsLaptop' (OpenDrop)
Waiting for the peer to accept...
Accepted. Uploading via gzip...
Done.
```

**Watch the compression line.** With `--peer` there is no TXT record, so we assume the
peer supports nothing optional and pick gzip — the safe assumption about an
implementation that never told us what it understands.

Then verify the bytes rather than trusting "Done". opendrop writes into the working
directory it was started from:

```powershell
Get-FileHash C:\path\to\photo.jpg -Algorithm MD5
```

```bash
md5sum ~/photo.jpg
```

## 5. Test B — opendrop sends, WinDrop receives

Exercises our bplist *reader*, our cpio reader, and the path-traversal defence against
names produced by software we did not write. **Needs mirrored networking** (Option B
above), because opendrop has to discover us and offers no direct-address flag.

On Windows:

```powershell
dotnet run --project src\WinDrop.Cli -- receive
```

In WSL:

```bash
opendrop find -i eth0
opendrop send -r <id-from-find> -f /path/to/file -i eth0
```

Our receiver should print a consent prompt naming the sender and the file, then write it
to `%USERPROFILE%\Downloads\WinDrop` once accepted.

## 6. What is likely to break, and what each failure means

| Symptom | Most likely cause |
|---|---|
| Discovery finds nothing | mDNS not crossing the WSL NAT boundary — use `--peer`, or switch to mirrored networking |
| Peer found, connect hangs | Firewall on 8770, or a link-local address missing its scope ID |
| TLS handshake fails | opendrop expecting a client certificate we do not present, or a protocol version mismatch |
| `/Ask` rejected with a plist error | A field name or type mismatch in `AirDropAskRequest.ToPlist` — the most likely real finding |
| Transfer completes, file corrupt | Compression mismatch: check the `Content-Encoding` we sent against what opendrop expected |
| opendrop cannot extract the archive | A cpio padding bug that bsdtar happened to tolerate |

The `/Ask` row is where I would put money. The field names and the
boolean-versus-integer choices in that plist were read off opendrop's source, and a
mismatch there is exactly the kind of thing that survives every test we can write alone.

## 7. Record the result

Whatever happens, add it to [protocol-notes.md](protocol-notes.md) under Observations,
in the same confirmed/refuted form as the beacon findings. A failure here is a finding
about our implementation; a success is the strongest evidence available short of Apple
hardware.
