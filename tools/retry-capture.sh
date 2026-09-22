#!/usr/bin/env bash
# Measures how often the phone resends the frames it sends us, while a transfer runs.
#
# The test for session 9's leading explanation of the ~16 KB/s ceiling: the AX211 runs
# plain monitor mode, not active monitor, so it never acknowledges the phone's frames, and
# the phone resends each one. tools/retry_count.py explains how to read the result.
#
# Run it in a third terminal while OWL is up, then start the AirDrop straight away:
#   bash tools/retry-capture.sh              # wlan0, 60 seconds
#   bash tools/retry-capture.sh wlan0 90
#
# Only listens: tcpdump on the interface OWL already has in monitor mode. Prints counts,
# never addresses.
set -euo pipefail

INTERFACE=${1:-wlan0}
DURATION=${2:-60}
CAPTURE=/tmp/retry-$(date +%H%M%S).pcap
HERE=$(cd "$(dirname "$0")" && pwd)

# Frames to us may be addressed to the card or to awdl0; take both, since OWL's choice of
# address for awdl0 is not something to assume.
OURS=$(cat /sys/class/net/"$INTERFACE"/address)
if [ -e /sys/class/net/awdl0/address ]; then
  OURS="$OURS $(cat /sys/class/net/awdl0/address)"
fi

echo "== listening on ${INTERFACE} for ${DURATION} s: start the AirDrop now"

# timeout ends tcpdump with status 124, the expected way for this to finish. A snap length
# of 256 keeps the radiotap and 802.11 headers, which is all the count needs. tcpdump's own
# lines are left visible: "link-type IEEE802_11_RADIO" confirms monitor mode, and "dropped
# by kernel" says whether the counts below saw everything.
sudo timeout "$DURATION" tcpdump -i "$INTERFACE" -s 256 -w "$CAPTURE" || true

if [ ! -s "$CAPTURE" ]; then
  echo "no capture was written; tcpdump's message above says why"
  exit 1
fi

echo "== counting"
# shellcheck disable=SC2086 # OURS is a list of addresses, split on purpose
python3 "$HERE/retry_count.py" "$CAPTURE" $OURS
echo "   capture kept at ${CAPTURE}"
