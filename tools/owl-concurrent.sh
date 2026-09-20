#!/usr/bin/env bash
# Runs OWL on a 5 GHz channel by keeping the card joined to a network on that same channel.
#
# The AX211 marks channel 44 IR-CONCURRENT: it may transmit there only while a concurrent
# connection exists on that channel. Channel 6 needs no such condition, which is why every
# session so far has used it - and why every session has been slow, because the phone spends
# fifteen of its sixteen AWDL slots on 44.
#
# So this script differs from owl-session.sh in two deliberate ways:
#   * it does NOT kill NetworkManager, because the association is the whole point
#   * it adds a second, monitor-type interface beside the station rather than converting it
#
#   bash tools/owl-concurrent.sh              # wlan0, channel 44
#   bash tools/owl-concurrent.sh wlan0 44
#
# Channel 149 is deliberately not offered: in ETSI countries 5745 MHz is not available for
# this, which is what the no-IR flag on it reports.
set -euo pipefail

INTERFACE=${1:-wlan0}
CHANNEL=${2:-44}
MONITOR=mon0
LOG=/tmp/owl-$(date +%H%M%S)-ch${CHANNEL}-concurrent.log
PHY=$(iw dev "$INTERFACE" info | awk '/wiphy/ {print "phy"$2}')

echo "== the card must already be joined to a network on channel ${CHANNEL}"
iw dev "$INTERFACE" link || true
echo

echo "== does ${PHY} allow a monitor interface beside a station?"
iw "$PHY" info | sed -n '/valid interface combinations/,/HT Capability/p' | head -20
echo

echo "== what the regulatory rules say about the 5 GHz channels"
iw "$PHY" info | grep -E "5180|5200|5220|5240|5745" || true
echo

echo "== adding ${MONITOR} beside ${INTERFACE}"
if ! sudo iw dev "$INTERFACE" interface add "$MONITOR" type monitor; then
  echo
  echo "REFUSED: this card will not run a monitor interface alongside a station."
  echo "That closes the concurrent route; only different hardware reaches 5 GHz."
  exit 1
fi

sudo ip link set "$MONITOR" up
iw dev "$MONITOR" info

echo
echo "== logging to ${LOG}"
echo "   watch for: Channel ${CHANNEL} is available for frame injection"
echo "   if it says it cannot inject, IR-CONCURRENT was not satisfied - check the link above"
echo "   start the receiver in another terminal AFTER owl prints its banner"

sudo owl -i "$MONITOR" -c "$CHANNEL" -v -N 2>&1 | tee "$LOG"
