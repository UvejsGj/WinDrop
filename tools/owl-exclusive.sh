#!/usr/bin/env bash
# Brings OWL up on an Intel card with an ad-hoc interface beside the monitor one, so that
# the card acknowledges what the phone sends it. A sibling of owl-session.sh, which stays
# the way that is known to work.
#
# UNTRIED ON REAL HARDWARE when written (2026-10-12). It was rehearsed against stand-ins
# for iw, ip and owl on a machine with no Wi-Fi card, which proves the order of the steps
# and the way back, and nothing about the radio.
#
# Why it exists. Session 10 measured that the AX211 never acknowledges the phone's frames in
# plain monitor mode: 86% of what the phone sent were resends, and that is the 20 KB/s every
# session has seen. A monitor interface has no address the firmware answers for. An ad-hoc
# (IBSS) interface has one, and AWDL's frames are ad-hoc frames with one fixed BSSID, so an
# ad-hoc interface joined to that BSSID gives the firmware something to acknowledge for.
# OWL still sends and receives through a monitor interface, which carries the same address.
#
# Where it comes from. This is the "exclusive mode" of omdrop-owl for iwlwifi cards
# (github.com/t4t5/omdrop-owl, GPL-3.0-or-later; userspace/omdrop-discoverable at commit
# 8c38540). It reports an AX201 and two AX211s working this way, and a 2.4 MB photo in 10
# to 17 s on the AX201. The steps below are written from that method, not copied from its
# script, and the patched OWL they need is omdrop-owl's own, built apart from this
# repository by owl-build.sh. Its AX211s have other PCI IDs (8086:51f0, 8086:51f1) than the
# one here (8086:7a70), so whether this card behaves the same is what a session has to find
# out.
#
#   bash tools/owl-build.sh                  # once, while online
#   bash tools/owl-exclusive.sh              # wlan0
#   RELOAD=0 bash tools/owl-exclusive.sh     # skip the driver reload described below
#
# Ctrl+C stops OWL and puts the card back as an ordinary station. If it is left in ad-hoc
# mode anyway, reloading the driver undoes everything this script can do to it:
#
#   sudo modprobe -r iwlmvm iwlwifi
#   sudo modprobe iwlwifi
#
# The log lands in /tmp/owl-exclusive-<time>.log; the path is printed before OWL starts.
set -euo pipefail

INTERFACE=${1:-wlan0}
OWL=${OWL:-$HOME/owl-patched/owl}
TOOLS=$(cd "$(dirname "$0")" && pwd)

CELL_NAME=awdlibss0
MONITOR=mon0
# Channel 6 and no other. The Intel cards omdrop-owl tested start no ad-hoc cell on 5 GHz,
# and session 9 found this card deaf on channel 44 in any case.
CHANNEL=6
FREQUENCY=2437
# The BSSID every AWDL frame carries.
AWDL_BSSID=00:25:00:ff:94:73
LOG=/tmp/owl-exclusive-$(date +%H%M%S).log

# ---- checked before anything is changed ----------------------------------------------

if [ ! -x "$OWL" ]; then
  echo "No patched OWL at ${OWL}. Build it first, while online:"
  echo "   bash ${TOOLS}/owl-build.sh"
  echo "The packaged owl will not do: it lacks the options this script passes."
  exit 1
fi

if [ ! -e "/sys/class/net/${INTERFACE}" ]; then
  echo "No interface ${INTERFACE} here. Check ip link for the card's name, or, if the"
  echo "driver was unloaded: sudo modprobe iwlwifi"
  exit 1
fi

DRIVER=$(basename "$(readlink -f "/sys/class/net/${INTERFACE}/device/driver" 2>/dev/null)" 2>/dev/null || true)

if [ "$DRIVER" != iwlwifi ]; then
  echo "${INTERFACE} is driven by ${DRIVER:-something unknown}, not iwlwifi."
  echo "This way of starting AWDL is for Intel cards. Use owl-session.sh."
  exit 1
fi

# ---- the way back --------------------------------------------------------------------

NM_WAS_RUNNING=0
TOUCHED=0    # 1 once the station has been taken down; before that there is nothing to undo
CELL=        # the ad-hoc interface once there is one: ${CELL_NAME}, or the station itself
IPV6_WAS=    # the station's own IPv6 setting, kept only if it became the cell
WATCHER=

# Runs on every way out once the radio has been touched: OWL stopping, Ctrl+C, a step
# failing. Each line undoes one step and none may stop the rest, so nothing here is fatal.
cleanup() {
  local status=$? type
  trap '' INT TERM HUP    # a second Ctrl+C must not leave the card half put back
  trap - EXIT
  set +e

  [ -n "$WATCHER" ] && kill "$WATCHER" 2>/dev/null

  echo
  echo "== putting ${INTERFACE} back"

  if [ "$TOUCHED" = 1 ]; then
    sudo ip link set "$MONITOR" down 2>/dev/null
    sudo iw dev "$MONITOR" del 2>/dev/null

    if [ "$CELL" = "$CELL_NAME" ]; then
      sudo iw dev "$CELL_NAME" ibss leave 2>/dev/null
      sudo iw dev "$CELL_NAME" del 2>/dev/null
    fi

    # Harmless on a station that never became the cell: it is in none, and it is down.
    sudo iw dev "$INTERFACE" ibss leave 2>/dev/null
    sudo ip link set "$INTERFACE" down
    sudo iw dev "$INTERFACE" set type managed
    [ -n "$IPV6_WAS" ] && sudo sysctl -qw "net.ipv6.conf.${INTERFACE}.disable_ipv6=${IPV6_WAS}"
    sudo ip link set "$INTERFACE" up

    type=$(iw dev "$INTERFACE" info 2>/dev/null | awk '$1 == "type" { print $2 }')

    if [ "$type" = managed ]; then
      echo "   ${INTERFACE} is an ordinary station again"
    else
      echo "   ${INTERFACE} is NOT back: it reads '${type:-missing}'. Reload the driver:"
      echo "      sudo modprobe -r iwlmvm iwlwifi"
      echo "      sudo modprobe iwlwifi"
    fi
  else
    echo "   it had not been changed"
  fi

  if [ "$NM_WAS_RUNNING" = 1 ]; then
    sudo systemctl start NetworkManager && echo "   NetworkManager is started again; Wi-Fi reconnects by itself"
  else
    echo "   NetworkManager was not running when this started, so it is left stopped."
    echo "   For ordinary Wi-Fi: sudo systemctl start NetworkManager"
  fi

  if [ -f "$LOG" ]; then
    echo "== the log, counted"
    bash "$TOOLS/owl-counts.sh" "$LOG"
  fi

  exit "$status"
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

# ---- the radio -----------------------------------------------------------------------

# The same first step as owl-session.sh, so a session that runs both compares two ways of
# driving the radio and nothing else. NetworkManager is given back at the end only if it
# was there at the start: after owl-session.sh it is not.
if systemctl is-active --quiet NetworkManager 2>/dev/null; then
  NM_WAS_RUNNING=1
fi

echo "== stopping anything that manages the radio"
sudo airmon-ng check kill

# owl-session.sh reloads the driver because sessions 9, 11 and 12 saw no peer at all on a
# first start, every time, until it was reloaded. Whether this way of starting needs it is
# not known. A freshly loaded driver is the state this card is known to hear the phone in,
# and it clears whatever a run before this one left behind, so it is done here too.
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

echo "== ${INTERFACE} off the air"
# Without a reload, the last run may have left pieces: owl-session.sh leaves a monitor, and
# this script, killed outright, leaves its two interfaces and perhaps the cell.
TOUCHED=1
sudo iw dev "$MONITOR" del 2>/dev/null || true
sudo iw dev "$CELL_NAME" del 2>/dev/null || true
sudo iw dev "$INTERFACE" ibss leave 2>/dev/null || true
sudo ip link set "$INTERFACE" down
sudo iw dev "$INTERFACE" set type managed

PHY=$(cat "/sys/class/net/${INTERFACE}/phy80211/name")
# Read now, while it is down and nothing manages it, so nothing changes it afterwards.
ADDRESS=$(cat "/sys/class/net/${INTERFACE}/address")

echo "== an ad-hoc interface on AWDL's BSSID"
# Tried as a second interface first, which leaves the station as it is. omdrop-owl found
# that some AX211s refuse one while the station exists, even down (EOPNOTSUPP), and that
# the station itself can then be switched. Which of the two this card takes is a result,
# so it is printed, with the card's own words for the refusal.
if refusal=$(sudo iw phy "$PHY" interface add "$CELL_NAME" type ibss 2>&1); then
  CELL=$CELL_NAME
  # Injected frames carry this address, and it is how the kernel picks the interface that
  # sends them, so all three must agree.
  sudo ip link set "$CELL" address "$ADDRESS"
  echo "   SEPARATE: ${CELL} added beside ${INTERFACE}"
else
  echo "   the card refused a second interface: ${refusal:-no reason given}"
  IPV6_WAS=$(sysctl -n "net.ipv6.conf.${INTERFACE}.disable_ipv6" 2>/dev/null || true)
  CELL=$INTERFACE

  if ! sudo iw dev "$INTERFACE" set type ibss; then
    echo "The card took neither a second interface nor the switch; its reason is the line above."
    echo "There is nothing further to try from here. owl-session.sh still works."
    exit 1
  fi

  echo "   CONVERTED: ${INTERFACE} itself is now the ad-hoc interface"
fi

# IP runs on awdl0. Left alone, the kernel takes this interface for a network of its own,
# gives it an address, and the receiver's mDNS would join it and send plain IPv6 on
# AWDL's BSSID.
sudo sysctl -qw "net.ipv6.conf.${CELL}.disable_ipv6=1" || echo "   could not turn IPv6 off on ${CELL}; carrying on"
sudo ip link set "$CELL" up

# HT20, because an iPhone sends its unicast at HT rates, which a cell without HT neither
# receives nor acknowledges. fixed-freq, so the cell never follows another onto a different
# channel. The beacon interval is the longest allowed: the beacons carry AWDL's BSSID,
# which no AWDL device beacons with, and omdrop-owl saw a restarted iPhone fail to list a
# machine whose cell beaconed ten times a second.
if ! sudo iw dev "$CELL" ibss join windrop "$FREQUENCY" HT20 fixed-freq "$AWDL_BSSID" beacon-interval 10000; then
  echo "The card refused to start the ad-hoc cell on channel ${CHANNEL}; its reason is the line above."
  echo "There is nothing further to try from here. owl-session.sh still works."
  exit 1
fi

# The kernel scans the channel once before it starts the cell.
joined=
for _ in $(seq 1 60); do
  joined=$(iw dev "$CELL" link 2>/dev/null | grep '^Joined IBSS' || true)
  [ -n "$joined" ] && break
  sleep 0.25
done

if [ -z "$joined" ]; then
  echo "The ad-hoc cell did not start within 15 s. iw says: $(iw dev "$CELL" link 2>&1 | head -1)"
  echo "owl-session.sh still works."
  exit 1
fi

echo "   ${joined}"

echo "== a monitor interface for OWL"
if ! sudo iw phy "$PHY" interface add "$MONITOR" type monitor; then
  echo "The card refused a monitor interface beside the ad-hoc one; its reason is the line above."
  echo "There is nothing further to try from here. owl-session.sh still works."
  exit 1
fi

sudo ip link set "$MONITOR" address "$ADDRESS"
sudo ip link set "$MONITOR" up

agree=yes
for interface in "$CELL" "$MONITOR"; do
  [ "$(cat "/sys/class/net/${interface}/address")" = "$ADDRESS" ] || agree="NO, ${interface} differs"
done
echo "   one address on ${CELL} and ${MONITOR}: ${agree}"

# owl-session.sh sets the width again once OWL is up, because session 10 found the card at
# 40 MHz behind its back. Here the channel belongs to the cell, which was joined at HT20, and
# the monitor follows it; it cannot be set from the monitor side. So the state is only
# printed, once OWL is running: still joined, the width, and whether awdl0 is there.
(
  trap - EXIT INT TERM HUP
  sleep 5
  echo "== with OWL running: $(iw dev "$CELL" link 2>&1 | head -1), $(iw dev "$CELL" info 2>/dev/null | sed -n 's/.*\(width: [^,]*\).*/\1/p')"

  if [ -e /sys/class/net/awdl0 ]; then
    echo "== awdl0 is there"
  else
    echo "== NO awdl0: OWL did not come up; its last lines say why"
  fi
) &
WATCHER=$!

# One share failed while the laptop screen had blanked, and the same share succeeded with it
# kept awake. Two things changed at once, so this is not proven; it is also free.
xset s off -dpms 2>/dev/null || true

echo "== logging to ${LOG}"
echo "   counted when OWL stops, or at any time with: bash ${TOOLS}/owl-counts.sh"
echo "   start the receiver in another terminal AFTER owl prints its banner"

# -N        the interfaces are set up above; OWL must not change them
# -F        the radio cannot leave this channel: send multicast in any slot, and unicast
#           whenever the peer is here
# -S pin    advertise this channel in all sixteen slots, which with -F is the truth
# -E        elect the sync master the way Apple devices do, by metric first
# The first line of the log says which of the two ways the cell was made.
{
  echo "== owl-exclusive: cell on ${CELL} ($([ "$CELL" = "$CELL_NAME" ] && echo SEPARATE || echo CONVERTED)), OWL built from omdrop-owl $(cat "$(dirname "$OWL")/built-from" 2>/dev/null || echo unknown)"
  sudo "$OWL" -i "$MONITOR" -c "$CHANNEL" -N -S pin -F -E -v
} 2>&1 | tee "$LOG"
