#!/usr/bin/env bash
# Brings OWL up on a card that refuses active monitor mode, and logs it.
#
# Exists because the log has been lost twice by retyping the command without `tee`,
# and because the order matters: monitor mode and channel are set by hand, OWL is
# told not to touch them with -N, and the receiver must be started *after* this.
#
#   bash tools/owl-session.sh              # wlan0, channel 6
#   bash tools/owl-session.sh wlan0 44     # another channel
#
# The log lands in /tmp/owl-<time>-ch<channel>.log; the path is printed before OWL starts.
set -euo pipefail

INTERFACE=${1:-wlan0}
CHANNEL=${2:-6}
LOG=/tmp/owl-$(date +%H%M%S)-ch${CHANNEL}.log

echo "== stopping anything that manages the radio"
sudo airmon-ng check kill

echo "== ${INTERFACE} to monitor mode on channel ${CHANNEL}"
sudo ip link set "$INTERFACE" down
sudo iw dev "$INTERFACE" set type monitor
sudo ip link set "$INTERFACE" up
sudo iw dev "$INTERFACE" set channel "$CHANNEL"

echo "== logging to ${LOG}"
echo "   count injection errors later with: grep -c unable ${LOG}"
echo "   start the receiver in another terminal AFTER owl prints its banner"

sudo owl -i "$INTERFACE" -c "$CHANNEL" -v -N 2>&1 | tee "$LOG"
