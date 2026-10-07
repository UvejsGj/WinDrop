#!/usr/bin/env bash
# Brings OWL up on a card that refuses active monitor mode, and logs it.
#
# Exists because the log has been lost twice by retyping the command without `tee`,
# and because the order matters: monitor mode and channel are set by hand, OWL is
# told not to touch them with -N, and the receiver must be started *after* this.
#
#   bash tools/owl-session.sh              # wlan0, channel 6
#   RELOAD=0 bash tools/owl-session.sh     # skip the driver reload described below
#
# The log lands in /tmp/owl-<time>-ch<channel>.log; the path is printed before OWL starts.
set -euo pipefail

INTERFACE=${1:-wlan0}
CHANNEL=${2:-6}
LOG=/tmp/owl-$(date +%H%M%S)-ch${CHANNEL}.log

echo "== stopping anything that manages the radio"
sudo airmon-ng check kill

# Sessions 9, 11 and 12 found no peer at all on the first start, every time until the driver
# was reloaded by hand, after which the phone appeared within a second. The card arrives here
# straight from an ordinary Wi-Fi connection, and a freshly loaded driver is what reliably
# works, so it is reloaded first. Done after the managers are stopped, so nothing grabs the
# card back in between. The screen may glitch for a moment; terminals keep working.
if [ "${RELOAD:-1}" != 0 ]; then
  echo "== reloading the Wi-Fi driver (RELOAD=0 skips this)"
  sudo modprobe -r iwlmvm iwlwifi
  sleep 3
  sudo modprobe iwlwifi

  for _ in $(seq 1 20); do
    [ -e "/sys/class/net/${INTERFACE}" ] && break
    sleep 0.5
  done

  if [ ! -e "/sys/class/net/${INTERFACE}" ]; then
    echo "${INTERFACE} did not come back after the reload; check ip link for its new name"
    exit 1
  fi
fi

echo "== ${INTERFACE} to monitor mode on channel ${CHANNEL}"
sudo ip link set "$INTERFACE" down
sudo iw dev "$INTERFACE" set type monitor
sudo ip link set "$INTERFACE" up
sudo iw dev "$INTERFACE" set channel "$CHANNEL" HT20

# Session 10 found the card 40 MHz wide (HT40+, center 2447) once OWL was running, on every
# start and after driver reloads, although the channel was set here without HT40. No
# transfer succeeded at 40 MHz; setting HT20 by hand with OWL running fixed the width for the
# rest of the session, and the next three transfers succeeded. Later failures at 20 MHz say
# the width is not the whole story, but it is free to rule out. So it is set again once OWL
# is up, and the result printed rather than assumed.
(
  sleep 5
  sudo iw dev "$INTERFACE" set channel "$CHANNEL" HT20
  echo "== width with OWL running: $(iw dev "$INTERFACE" info | grep -o 'width: [0-9]* MHz')"
) &

# One share failed while the laptop screen had blanked, and the same share succeeded with it
# kept awake. Two things changed at once, so this is not proven; it is also free.
xset s off -dpms 2>/dev/null || true

echo "== logging to ${LOG}"
echo "   count injection errors later with: grep -c unable ${LOG}"
echo "   start the receiver in another terminal AFTER owl prints its banner"

sudo owl -i "$INTERFACE" -c "$CHANNEL" -v -N 2>&1 | tee "$LOG"
