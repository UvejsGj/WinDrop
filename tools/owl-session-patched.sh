#!/usr/bin/env bash
# The old radio bring-up with the patched OWL: every step of owl-session.sh, unchanged, but
# OWL is the one tools/owl-build.sh builds into ~/owl-patched/owl instead of Kali's package.
# UNTESTED on real hardware.
#
# Exists to split what session 15's ad-hoc arm changes at once. owl-exclusive.sh moves to an
# ad-hoc interface AND to the patched OWL; this keeps plain monitor mode on channel 6 and
# changes only the OWL. The patch it is for is omdrop-owl's owl-07-intel-reliability, which
# needs no ad-hoc interface: when the driver's queue is full it keeps a unicast frame and tries
# again rather than dropping it with "unable to inject packet", it counts a data frame as a
# sign of life from a peer, and it waits 5 s instead of 2 before "remove peer". Those two log
# lines are where the old way's stalls end. The build also carries owl-08-amsdu-bound, which
# stops a received A-MSDU writing past a fixed array; Kali's owl has that unbounded write.
#
#   bash tools/owl-session-patched.sh              # wlan0, channel 6
#   RELOAD=0 bash tools/owl-session-patched.sh     # skip the driver reload, as in owl-session.sh
#   OWL=<path> bash tools/owl-session-patched.sh   # another build of the patched OWL
#
# owl-session.sh stays as it is: field sessions and their handoffs depend on it.
# The log lands in /tmp/owl-<time>-ch<channel>-patched.log; the path is printed before OWL starts.
set -euo pipefail

INTERFACE=${1:-wlan0}
CHANNEL=${2:-6}
OWL=${OWL:-$HOME/owl-patched/owl}
LOG=/tmp/owl-$(date +%H%M%S)-ch${CHANNEL}-patched.log

# Checked before the card is taken, so a missing build costs nothing. A live USB forgets the
# build at every boot, and building needs the internet, which the steps below take away.
if [ ! -x "$OWL" ]; then
  echo "No patched OWL at ${OWL}. Run tools/owl-build.sh first, while still online."
  exit 1
fi

# Which of the patched OWL's options, worked out from its source. Stock OWL advertises the -c
# channel in all 16 slots of its channel sequence, so it never moves the radio off it. The fork
# this OWL is built from copies the sync master's sequence instead (-S verbatim, its default)
# and retunes the radio to follow it slot by slot. In plain monitor mode nothing holds the card
# on channel 6, so it would hop to 149 or 44, and each of its channel switches asks for 40 MHz.
#   -S pin alone pins the peer's busiest channel, which is rarely 6.
#   -S intersect stays on 6 but advertises only the slots the sync master already spends there,
#     and sends unicast only in those: neither is what stock OWL does.
#   -S pin -F pins the -c channel: 6 in all 16 slots, the sequence stock OWL sends, so the phone
#     sees what it saw in every earlier session and the radio stays where this script puts it.
# -F has two more effects, and no set of options gives stock OWL's sequence without them: a
# unicast data frame goes out once with no retries, and multicast goes out in any slot rather
# than only slots 0 and 10. So a transfer that works here cannot be put down to owl-07 alone.
# -E (election by metric), -R (data rate) and -m (AWDL address) are left out: each changes
# something stock OWL does, and none is needed to keep the radio on channel 6.
OPTIONS=(-S pin -F)

echo "== stopping anything that manages the radio"
sudo airmon-ng check kill

# Sessions 9, 11 and 12 found no peer at all on the first start, every time until the driver
# was reloaded by hand. Same reasoning as owl-session.sh, which has the full story.
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

# The patched OWL still asks for HT40+ on its one channel switch at start, as stock OWL does,
# so the width is set back to 20 MHz once it is up and the result printed (session 10; see
# owl-session.sh).
(
  sleep 5
  sudo iw dev "$INTERFACE" set channel "$CHANNEL" HT20
  echo "== width with OWL running: $(iw dev "$INTERFACE" info | grep -o 'width: [0-9]* MHz')"
) &

# Kept from owl-session.sh: one share failed with the screen blanked. Unproven, and free.
xset s off -dpms 2>/dev/null || true

# The first line of the log says which OWL ran and how, so logs from the three ways can be
# told apart afterwards without relying on the file name.
echo "== patched OWL: ${OWL} ${OPTIONS[*]} (owl-session-patched.sh)" > "$LOG"

echo "== logging to ${LOG}"
echo "   count injection errors later with: grep -c unable ${LOG}"
echo "   few are expected: this OWL retries a full queue without logging, so judge by transfers"
echo "   start the receiver in another terminal AFTER owl prints its banner"

sudo "$OWL" -i "$INTERFACE" -c "$CHANNEL" -v -N "${OPTIONS[@]}" 2>&1 | tee -a "$LOG"
