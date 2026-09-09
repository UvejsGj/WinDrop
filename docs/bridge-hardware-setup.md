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

## Phase 0 — what to buy

**Recommendation: an AR9271 adapter** (Alfa AWUS036NHA, or any "ath9k_htc" dongle), ~$20.

The reasoning, since it is not obvious:

- OWL is developed and tested against Atheros, and every published AWDL result uses it.
  That matters more than spec sheets.
- OWL asks for *active* monitor mode, which `ath9k_htc` does not advertise. It is
  probably still fine: OWL calls the absence a throughput problem rather than a failure,
  and Vanhoef's injection survey found Atheros dongles acknowledge frames in practice
  anyway.
- MediaTek `mt76` supports active monitor mode properly and is dual-band, which makes it
  look better on paper. Nobody has run OWL on it. Both need a driver built, so the
  usual reason to prefer MediaTek — better in-tree support — does not apply here.

Known risk either way: stock `ath9k_htc` firmware rewrites the sequence and fragment
numbers of injected frames. Whether AWDL tolerates that is unknown. Patched firmware
exists if it turns out to matter.

The AR9271 is 2.4 GHz only. AWDL's social channels are 6, 44 and 149, so you would be
limited to the 2.4 GHz rendezvous on channel 6. That generally works for discovery.

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
