# Getting an AWDL radio, without a second computer

The bridge needs a Linux machine holding a Wi-Fi card that can do monitor mode and frame
injection. `usbipd-win` removes the "second machine" half of that: a USB adapter can be
passed straight into WSL2 on the same box.

**Read this first.** There is real doubt about whether current iOS will talk to a
non-Apple AirDrop peer at all — see the addendum in
[ADR-001](adr-001-transport-selection.md). Phase 5 below is the experiment that settles
it. **Do not do phase 6 until phase 5 passes.** If the iPhone never appears, everything
after that is wasted effort, and knowing that is itself the result.

## What this machine already has

Measured on 2026-09-09, WSL2 kernel `6.18.33.2-microsoft-standard-WSL2`:

| Piece | State |
|---|---|
| `vhci-hcd`, `usbip-core` | **shipped** — USB/IP passthrough works out of the box |
| `cfg80211`, `mac80211` | **shipped** — the whole wireless stack is present |
| `ath9k_htc` / `mt76` / `rt2800usb` | **absent** — the only wireless drivers are `iwlwifi` (Intel PCIe) and `rsi_*` |
| `usbipd-win` | not installed |

So exactly one driver module has to be built. Not a kernel — a module, against a kernel
whose config is already on disk at `/proc/config.gz`.

## Phase -1 — answer the whole question first, for free

**Do this before buying anything or touching WSL.**

The experiment that decides whether this project has a future needs only a Linux machine
with a capable radio. It does not need Windows, WSL, usbipd, a compiled driver, the
bridge daemon, or any WinDrop code. Everything in phases 1 to 4 exists solely to let
WinDrop *use* a radio once you know a radio is worth having.

Kali packages OWL, so there is nothing to build.

### Make the stick

Download the **Kali Linux Live** image and write it with [Rufus](https://rufus.ie). On a
large stick, give it a **persistence partition** — 16 GB is ample — so packages you
install survive a reboot. Without it, every reboot starts from nothing, which is
miserable across a multi-step procedure.

If an old laptop refuses to boot it, set Rufus to **MBR / BIOS** rather than GPT/UEFI.
Machines of the Windows 7 era usually want legacy boot.

### Try both radios you already own — the spare machine first

Boot the stick and run, on each machine:

```bash
lspci -nn | grep -i net          # or lsusb, for a dongle
sudo iw list | grep -A10 "Supported interface modes"
sudo iw phy phy0 info | grep -i "active monitor"
```

**Start with the spare laptop**, not the main desktop. Even if the desktop's card turns
out to be capable, it can never be the bridge: the bridge has to run Linux *while*
Windows runs WinDrop, and one machine cannot do both. The desktop can only answer the
yes/no question. The spare answers the same question and is the machine that would
actually hold the radio afterwards.

A live USB never mounts or writes to the installed system, so a machine whose Windows
password has been forgotten is still perfectly usable here.

**Identify the exact Broadcom part before predicting anything.** An earlier version of
this page said a spare laptop's Broadcom would be driven by `b43`. The first one actually
tested, an HP dv6, was a **BCM4313 `[14e4:4727]`**, whose LCN-PHY `b43` only ever
supported experimentally. In practice it is served by the in-kernel `brcmsmac` (over the
`bcma` bus), or by the proprietary `wl`, which has no monitor mode. It is also **2.4 GHz
only**. If Wi-Fi does not come up at all, check `dmesg | grep -i brcm` for missing
firmware before judging the card.

#### 2026-09-13 — MEASURED: the dv6's BCM4313 fails

Kali live on the HP dv6:

```
lspci -k                    Kernel driver in use: bcma-pci-bridge   (bus; radio driver on top not captured)
Supported interface modes   IBSS, managed, AP, AP/VLAN, monitor
active monitor              (no output)
5 GHz channels              0
```

The card has monitor mode but not active monitor, and a single 2.4 GHz band. That is
two independent strikes, so OWL was not attempted on it. The dv6 stays useful as the
bridge host, because it has RTL8111 gigabit Ethernet for the link to Windows; the radio
has to be a USB adapter.

#### 2026-09-13 — MEASURED: an HP ProBook 6450b fails the same way

The regulatory label reads "Intel WLAN … 112BNHMW", the Intel WiFi Link 1000, a
2.4 GHz-only part. The `lspci` line was not captured, so the identity rests on the
label. Kali live results:

```
active monitor              (no output)
5 GHz channels              0
```

Both laptops on hand have failed on both counts. On machines of this era the internal
radio is a removable PCIe Mini Card, so a dual-band Atheros card is a possible
replacement. HP BIOSes of the period refuse unlisted Wi-Fi cards at POST, so it would
have to be an HP-branded spare listed for the model.

#### 2026-09-13 — MEASURED: the main PC's Intel AX211 passes the band check, not active monitor

MSI Sword 16 HX, Kali live, Secure Boot left on:

```
lspci -nn                   Intel 700 Series Chipset Family CNVi Wi-Fi [8086:7a70] (rev 11)
active monitor              (no output)
channel 6    2437 MHz       (22.0 dBm)
channel 44   5220 MHz       (22.0 dBm)
channel 149  5745 MHz       (22.0 dBm)
6 GHz ch 149 6695 MHz       (22.0 dBm) (no IR)
```

**Correction, same day:** those channel flags depend on state, and this reading does not
hold for OWL. It was taken while Kali was associated to an access point. The AX211's
regulatory domain is **self-managed** (`iw reg get` → `phy#0 (self-managed)`, and
`iw reg set` is ignored), and once `wpa_supplicant` is killed for OWL the table changes:

```
channel 149  5745 MHz   passive / no IR          (no transmission)
channel 44   5220 MHz   IR-CONCURRENT            (transmit only alongside an existing connection)
channel 6    2437 MHz   unrestricted
```

So for OWL this card has **channel 6 only**. It does not advertise active monitor.

OWL is worth attempting on it anyway, for a reason specific to the protocol. Active
monitor governs whether the card ACKs **unicast** frames addressed to it. 802.11 never
ACKs multicast, and AirDrop's discovery is mDNS over multicast. If OWL can synchronise
at all, the iPhone's announcements should show up on `awdl0` whether or not ACKs work,
and that separates "cannot join the link" from "joins but unicast suffers". The first
observation to make is therefore `tcpdump -i awdl0` with the share sheet open, not a
full transfer.

This machine cannot be the permanent bridge: the AX211 is CNVi and cannot be passed
into WSL2, and the PC cannot run Linux while running Windows. It can answer whether
current iOS will sync with a Linux AWDL peer at all.

#### 2026-09-13 — MEASURED: OWL runs on the AX211, with two workarounds

Plain `owl -i wlan0 -c 149 -v` fails immediately:

```
ERROR: Error while receiving via netlink: Operation not supported
ERROR: Could not put device in monitor mode: wlan0
ERROR: could not initialize core
```

OWL requests monitor mode with the *active* flag, which is exactly what `iwlwifi` refuses.
Plain monitor mode works, so set it up by hand and tell OWL not to touch it with `-N`.
The README reserves `-N` for Nexmon, but here it is the workaround. Channel 149 then
starts but reports `Cannot inject frames on channel 149`, which is the self-managed
regulatory rule above. The working sequence:

```bash
sudo airmon-ng check kill
sudo ip link set wlan0 down
sudo iw dev wlan0 set type monitor
sudo ip link set wlan0 up
sudo iw dev wlan0 set channel 6
sudo owl -i wlan0 -c 6 -v -N
```

Check that OWL's log says `Channel 6 [2437 MHz] is available for frame injection`, and
that `awdl0` comes up with a link-local address. With an iPhone nearby, `add peer` lines
follow within seconds. `-f` was not needed. What the phone sent is recorded in
`protocol-notes.md`.

Channel 6 is a compromise. The iPhone's sequence was mostly 44, so the two radios overlap
only in its occasional channel-6 slots. That was enough for discovery; whether it is
enough for a transfer is untested.

An untested idea for 44: `IR-CONCURRENT` permits transmission alongside an existing
connection on that channel. Staying associated to an access point on channel 44, with a
second, monitor-type interface beside it, might satisfy the rule. `iwlmvm` may refuse
that interface combination outright.

**Typing commands from a phone.** Autocorrected quotes break shell quoting, and a quoted
`"libarchive-c==2.9"` arrived as `libarchive-c=2.9`. Pin with `==` and no quotes, and
avoid `<` and `>` in version specifiers: unquoted, the shell reads them as redirections.

**Configuration names are case-sensitive on Linux.** A build typed as `-c release` lands
in `bin/release`, and `dotnet run -c Release --no-build` then finds nothing. Windows
never shows this. The dependable form is to publish to a fixed folder and run the DLL
from it: `dotnet publish … -c Release -o ~/wd`, then `dotnet ~/wd/windrop.dll`.

**Start OWL first, then the receiver, every time.** Restarting OWL destroys and recreates
`awdl0`. A receiver started earlier keeps its sockets on the old interface and silently
drops out of the AirDrop row. Changing any OWL setting therefore means restarting both,
in that order.

**Keep the phone close.** On channel 6 with the AX211, a transfer failed with the phone
barely visible in OWL's peer list, and then succeeded with it touching the PC. The phone
favours other channels, so every channel-6 slot counts.

**Take the human out of timing tests.** `receive --yes` accepts without a prompt. iOS
reported "Declined" twice while a person was still typing `y`, which may be `/Ask`
timing out. Use `--yes` only while testing: it removes the consent prompt, which is the
protocol's only real security boundary.

## What sessions 4 and 5 settled about this link

- **Bring OWL up with `tools/owl-session.sh`.** It sets monitor mode and the channel, runs
  OWL with `-N`, and tees a timestamped log. Retyping the command without `tee` has cost
  the injection-error count twice.
- **Injection errors are not a health metric.** 20,857 `unable to inject packet` errors
  accompanied a successful 5.7 MB transfer. Compare counts per transfer if at all, and
  never read a large number as the cause of a failure.
- **Do not raise socket buffers.** `wmem_default = 4194304` made the same 5.7 MB transfer
  85% slower: 266 s against 144 s. Below-default buffers are the untested direction.
- **Throughput here:** about 40 KB/s for a 5.7 MB multi-file archive, 15 to 25 KB/s for a
  single 1.8 MB file. No video has ever arrived.
- **`/Ask` takes about 8 s to answer over this link,** close to the point where iOS gives
  up. That, and not the consent prompt, is why a first tap often fails and a second works.
- **Session 6 (iOS 27) pinned it to the preview.** The `/Ask` cost is ~97% the JPEG 2000
  preview iOS attaches, and iOS 27 tripled that to 127–215 KB, pushing `/Ask` to 10–19 s and
  declining every tap. Our processing is 1 ms; no advertised flag suppresses the preview.
  `receive --early-ask` tests whether iOS abandons the preview on an early 200. If it does
  not, image AirDrop here is link-bound, and the lever is transmitting on channel 44 (where
  the phone spends most of its time) rather than channel 6 — different hardware, or a way
  past the AX211's self-managed 5 GHz rules. Non-image shares carry no preview and are
  unaffected.

When a second radio is present, use `sudo iw list | grep -i "active monitor"` rather
than naming `phy0`. It covers every phy, and a dongle will not be `phy0` beside an
internal card.

**Then the main desktop, if you want a second data point.** Its Intel card is a dead end
*on Windows*, but `iwlwifi` on a modern kernel does support monitor mode and injection,
with caveats. Useful for answering the question early; not a solution.

### Run the experiment

```bash
sudo apt update && sudo apt install -y owl
sudo airmon-ng check kill        # wpa_supplicant and NetworkManager fight OWL for the card
sudo owl -i wlan0 -c 149 -v
```

**Pass `-c`, and choose the channel from what the card may transmit on.** OWL's default
channel is **6**, read from `daemon/owl.c`. iPhones spend most of their time on 5 GHz:
149 or 44 by common report, and an iOS 26.6 phone measured here showed mostly 44. So 5 GHz
is the first choice where the card allows transmission there. On a card with
self-managed regulatory rules it may not, and then 6 is the only channel that works;
that is what the AX211 needed (below). Check OWL's log for `available for frame
injection`. A `Cannot inject frames` warning on the chosen channel produces the same
silence as an incompatible card.

OWL's options, from its `getopt` string `"Dc:dvi:h:a:t:fN"`:

| Flag | Meaning |
|---|---|
| `-i <iface>` | wireless interface (required) |
| `-c <n>` | channel: 6, 44 or 149 |
| `-v`, `-vv` | more logging |
| `-f` | turn off RSSI filtering, worth trying if a nearby phone is never seen |
| `-D` | daemonize |
| `-d` | dump frames |
| `-N` | skip OWL's own monitor-mode setup. The README reserves it for Nexmon, but it is also the workaround for cards that refuse *active* monitor (`iwlwifi`): set plain monitor mode and the channel by hand first |
| `-h <name>` | name of the interface OWL creates, default `awdl0` |

There is **no help flag**. `owl -h` fails with "option requires an argument -- 'h'",
which looks like a broken install and is not one. Measured on Kali's
`owl 0~git20220130-0kali1+b1`.

In a second terminal, confirm the radio now exists:

```bash
ip addr show awdl0        # expect an fe80:: address
```

Then:

```bash
pipx install opendrop || pip install opendrop
opendrop find -i awdl0
```

On the iPhone: open a share sheet, tap AirDrop, leave it open, AirDrop set to Everyone.

- **The iPhone appears** → the protocol is alive. Now the rest of this document is worth
  doing, because now it is worth getting the radio to Windows.
- **It does not** → that is the answer. Check the ten-minute Everyone timeout first,
  since its symptom is identical, then record the result in
  [protocol-notes.md](protocol-notes.md). Nothing else in this document will help.

## Phase 0 — what to use, when you cannot get an AR9271

The AR9271 is what most AWDL write-ups name, but it is one specific USB part and not
stocked everywhere. Rather than a shopping list, here is the criterion, because it is
testable on whatever you can actually obtain.

### The criterion: active monitor mode

Injection alone is not enough. [OWL issue #61](https://github.com/seemoo-lab/owl/issues/61)
reports an Atheros card with working `aireplay` injection that still could not run OWL —
and the one capability it lacked was active monitor mode, where the card acknowledges
frames it receives. That issue is unresolved, so treat this as the best available
evidence rather than a settled answer, but it is the sharpest signal there is.

So, with any candidate plugged into Linux:

```bash
sudo iw list | grep -A10 "Supported interface modes"   # must include: monitor
sudo iw phy phy0 info | grep -i "active monitor"       # the discriminator
```

A card that shows both is worth trying. A card that shows monitor but not active monitor
may still work — OWL calls the absence a throughput problem, and Atheros dongles are
reported to acknowledge frames in practice regardless — but it is a gamble.

**Check the band as well.** AWDL runs mainly on 5 GHz (channel 149, or 44 in some
regions). A 2.4 GHz channel exists in the protocol, but a single-band 2.4 GHz card is a
long shot however well it scores on the other two checks. This counts the 5 GHz
channels a card offers; zero means it cannot reach them:

```bash
sudo iw list | grep -cE " 5[0-9]{3}(\.0)? MHz"
```

**A weak radio does not rule out the machine.** The bridge needs only some capable radio
plus an IP link to the Windows host. A laptop whose internal card fails these checks can
still host the bridge, with a USB adapter for AWDL and Ethernet to Windows.

### Options, cheapest first

**1. The Broadcom you already own.** Costs nothing, and a Linux live USB does not need
the machine's Windows password. Which driver applies depends on the exact part (see
above). The one on hand is a 2.4 GHz-only BCM4313, so expect this option to fail and treat
it as a free measurement. If all three checks pass, buy nothing.

**2. A Raspberry Pi.** Kali's `brcmfmac-nexmon-dkms` and `firmware-nexmon` packages give
monitor mode and frame injection on the built-in radio of the Pi 5, 4, 3B, Zero 2 W and
Zero W. Pis are stocked far more widely than any particular USB chipset, and this needs
no dongle and no driver compilation. Active monitor support is unverified — run the
check.

**3. A MediaTek `mt76` adapter** (MT7612U, MT7921AU). Vanhoef's injection survey lists
`mt76` and `mt7601u` as supporting active monitor mode outright, which is exactly the
capability the ath5k reporter lacked. These are sold as ordinary dual-band dongles almost
everywhere. Untested with OWL, but it advertises the right thing.

**4. An AR9271 ordered online.** Ubiquitous on the international marketplaces for around
ten dollars even where local shops have none. Slowest, but it is the family OWL is
actually developed against, and that counts for more than a specification sheet.

Note that options 1 and 2 avoid the driver build in Phase 2 entirely, since `brcmsmac`
and `brcmfmac` ship differently — check whether your kernel already has them before
assuming you need to compile anything.

## Phase 1 — pass the adapter into WSL2

In an **Administrator PowerShell**:

```powershell
winget install --interactive --exact dorssel.usbipd-win
```

Plug the adapter in, then list devices and note the BUSID:

```powershell
usbipd list
```

Share it once (persists), then attach it to WSL:

```powershell
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

**Check:** in WSL, `lsusb` should show the adapter. If it does, passthrough works and the
rest is a software problem.

Reattach after every reboot or `wsl --shutdown`.

## Phase 2 — build the driver

The module must match the running kernel exactly.

```bash
uname -r          # note this, e.g. 6.18.33.2-microsoft-standard-WSL2
```

Get Microsoft's kernel source at the matching tag:

```bash
sudo apt update
sudo apt install -y build-essential flex bison libssl-dev libelf-dev bc git dwarves
git clone --depth 1 --branch linux-msft-wsl-$(uname -r | cut -d- -f1) \
    https://github.com/microsoft/WSL2-Linux-Kernel.git ~/wsl-kernel
```

If that tag does not exist, list what does with
`git ls-remote --tags https://github.com/microsoft/WSL2-Linux-Kernel.git | grep msft-wsl`
and take the nearest.

Configure from the *running* kernel rather than a default, then add the driver:

```bash
cd ~/wsl-kernel
zcat /proc/config.gz > .config
scripts/config --module CONFIG_ATH9K_HTC --module CONFIG_ATH9K_HW \
               --module CONFIG_ATH9K_COMMON --module CONFIG_ATH
make olddefconfig
make modules_prepare -j$(nproc)
make M=drivers/net/wireless/ath modules -j$(nproc)
sudo make M=drivers/net/wireless/ath modules_install
sudo depmod -a
```

The driver also needs firmware, which is a separate package:

```bash
sudo apt install -y linux-firmware
sudo modprobe ath9k_htc
```

**Check:** `dmesg | tail` should show the firmware loading, and `ip link` should show a
new `wlan0`.

## Phase 3 — confirm the card can do what OWL needs

```bash
sudo iw list | grep -A10 "Supported interface modes"
```

**Check:** `monitor` must appear. If it does not, stop — nothing downstream can work.

## Phase 4 — build and run OWL

```bash
sudo apt install -y cmake libpcap-dev libnl-3-dev libnl-genl-3-dev libev-dev
git clone --recursive https://github.com/seemoo-lab/owl.git ~/owl
cd ~/owl && mkdir build && cd build && cmake .. && make
sudo ./owl -i wlan0 -c 149 -v
```

**Check:** in another shell, `ip addr show awdl0` should show an interface with an
`fe80::` address. That is the moment the radio exists.

## Phase 5 — the experiment. Stop here and read the result.

This is the whole point of the purchase. It does not involve WinDrop at all — it tests
whether **anything** non-Apple can still see a current iPhone.

```bash
opendrop find -i awdl0
```

On the iPhone: open a share sheet, tap AirDrop, leave it open. AirDrop must be set to
Everyone — note that iOS reverts this after ten minutes, and the symptom of that timeout
is identical to the symptom of failure, so check it first when nothing appears.

- **The iPhone appears** → the protocol still works. Continue to phase 6.
- **It does not** → that is the answer, and it is a finding worth writing down. See the
  ADR-001 addendum; record it in [protocol-notes.md](protocol-notes.md) either way.

## Phase 6 — only if phase 5 passed

```bash
sudo ./tools/windrop-bridge.py --interface awdl0
```

On Windows:

```powershell
dotnet run --project src\WinDrop.Cli -- receive --bridge localhost
```

Everything from here is already built and tested. The relay has been exercised end to
end in both directions over an ordinary interface; the only untested assumption is that
`awdl0` behaves like any other interface to a socket.
