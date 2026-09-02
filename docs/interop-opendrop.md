# Interop testing against opendrop

opendrop is an independent implementation of the same AirDrop protocol, written by the
SEEMOO lab. Testing against it is the same discipline as the `plistlib` and `bsdtar`
oracles: our reader agreeing with our writer proves only that they share any
misunderstanding. opendrop was written by people who read the protocol separately, so a
transfer that works against it is evidence, not self-congratulation.

**It does not need AWDL.** opendrop defaults to `awdl0` but accepts any interface, so it
runs over ordinary Ethernet or Wi-Fi. That is what makes this test free.

**What it does and does not prove.** It exercises bplist, TLS, HTTP/1.1, the
Discover/Ask/Upload state machine, cpio and the gzip path against a third party. It says
nothing about whether an iPhone will accept us — only Apple hardware can answer that.

## Machines

| Role | Machine | Notes |
|---|---|---|
| WinDrop | the main Windows PC | already built and working |
| opendrop | the old dv6, booted from a Linux live USB | **use Ethernet** |

Use a cable on the dv6. Broadcom Wi-Fi on a live USB usually needs non-free firmware
that is not installed, and fetching it requires the network you are trying to bring up.
Ethernet sidesteps that entirely, and this test does not care which medium carries it.

Both machines must be on the same L2 segment, since mDNS is link-local multicast. A
normal home router bridging its Wi-Fi and Ethernet into one subnet is fine.

## 1. Open the Windows firewall

The loopback test passed because nothing crossed a network boundary. This one does, and
Windows Firewall will silently drop it. In an **Administrator** PowerShell:

```powershell
netsh advfirewall firewall add rule name="WinDrop 8770" dir=in action=allow protocol=TCP localport=8770
netsh advfirewall firewall add rule name="WinDrop mDNS" dir=in action=allow protocol=UDP localport=5353
```

To remove them afterwards:

```powershell
netsh advfirewall firewall delete rule name="WinDrop 8770"
netsh advfirewall firewall delete rule name="WinDrop mDNS"
```

Symptom if you skip this: `windrop browse` finds nothing, or finds a peer and then hangs
on connect.

## 2. Install opendrop on the Linux box

```bash
sudo apt update
sudo apt install -y python3-venv python3-pip libarchive-dev
python3 -m venv ~/od
source ~/od/bin/activate
pip install opendrop
```

The venv is not optional on recent Ubuntu: PEP 668 makes a bare `pip install` fail with
`externally-managed-environment`.

**Expect friction here.** opendrop has been lightly maintained since around 2021 and its
dependencies (`zeroconf`, `libarchive-c`) have moved since. If the install or first run
breaks on Python 3.12, the quickest fix is an older interpreter rather than debugging
the dependency tree:

```bash
sudo apt install -y python3.10 python3.10-venv
python3.10 -m venv ~/od
```

Find the interface name to pass to opendrop:

```bash
ip -brief addr
```

Ethernet is usually `enp*` or `eth0`. Everything below assumes `eth0` — substitute yours.

Confirm the flags, since they have changed across versions:

```bash
opendrop --help
```

## 3. Test A — WinDrop sends, opendrop receives

This is the harder direction and the one worth doing first: it exercises our bplist
writer, our cpio writer, the compression negotiation and the whole sender state machine.

On Linux:

```bash
opendrop receive -n eth0
```

On Windows:

```powershell
dotnet run --project src\WinDrop.Cli -- send C:\path\to\photo.jpg
```

Expected output on our side:

```
Looking for a peer...
Sending to <name> @ [...]:8770 via infra-wifi (flags=0x...)
Peer identifies as '...'
Waiting for the peer to accept...
Accepted. Uploading via gzip...
Done.
```

**Watch the compression line.** opendrop is unlikely to advertise the DVZip capability
bit, so our sender should fall back to gzip on its own. Seeing `via gzip` there is the
negotiation in `AirDropCompression.ShouldUseDvZip` working against a real peer rather
than against a test. If it says DVZip, opendrop claimed the bit and the upload is about
to be a more interesting experiment.

Verify the file landed on the Linux side and its bytes match.

## 4. Test B — opendrop sends, WinDrop receives

This exercises our bplist *reader*, our cpio reader, and the path-traversal defence
against names produced by software we did not write.

On Windows:

```powershell
dotnet run --project src\WinDrop.Cli -- receive
```

On Linux:

```bash
opendrop find -n eth0
opendrop send -r <id-from-find> -f /path/to/file -n eth0
```

Our receiver should print the consent prompt naming the sender and the file, and write
the file to `%USERPROFILE%\Downloads\WinDrop` after you accept.

## 5. What is likely to break, and what each failure means

| Symptom | Most likely cause |
|---|---|
| `browse` finds nothing in either direction | Firewall, or the two machines are on different subnets |
| Peer found, connect hangs | Firewall on 8770, or a link-local address without its scope ID |
| TLS handshake fails | opendrop expecting a client certificate we are not presenting, or a protocol version mismatch |
| `/Ask` rejected with a plist error | A field name or type mismatch in `AirDropAskRequest.ToPlist` — the most likely real finding |
| Transfer completes, file is corrupt | Compression mismatch: check the `Content-Encoding` we sent against what opendrop expected |
| opendrop cannot extract the archive | A cpio padding bug that bsdtar happened to tolerate |

The `/Ask` row is where I would put money. Field names and the boolean-versus-integer
choices in that plist come from reading opendrop's source, and a mismatch there is
exactly the kind of thing that survives every test we can write alone.

## 6. Record the result

Whatever happens, add it to [protocol-notes.md](protocol-notes.md) under Observations,
in the same confirmed/refuted form as the beacon findings. A failure here is a finding
about our implementation; a success is the strongest evidence available short of Apple
hardware.
