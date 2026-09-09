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

Its Broadcom is driven by `b43`, which supports monitor mode and injection on many
BCM43xx parts. Kali ships more non-free firmware than Ubuntu does, which matters for
Broadcom specifically — if Wi-Fi does not come up at all, that is usually why, and it is
worth checking `dmesg | grep -i firmware` before concluding the card is unsuitable.

**Then the main desktop, if you want a second data point.** Its Intel card is a dead end
*on Windows*, but `iwlwifi` on a modern kernel does support monitor mode and injection,
with caveats. Useful for answering the question early; not a solution.

### Run the experiment

```bash
sudo apt update && sudo apt install -y owl
sudo owl -i wlan0
```

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

### Options, cheapest first

**1. The Broadcom you already own.** An old laptop's built-in card, driven by `b43`,
which supports monitor mode and injection on many BCM43xx parts. Costs nothing, and a
Linux live USB does not need the machine's Windows password. Run the two commands above.
If they pass, buy nothing.

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

Note that options 1 and 2 avoid the driver build in Phase 2 entirely, since `b43` and
`brcmfmac` ship differently — check whether your kernel already has them before assuming
you need to compile anything.

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
sudo ./owl -i wlan0
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
