#!/usr/bin/env bash
# Rehearsal 2: owl-exclusive.sh, owl-counts.sh and the scripts' syntax, in WSL, in one go.
#   $1  a tar of the tree as it will be committed (LF line ends)
#   $2  the folder this file is in
# The radio cannot be rehearsed: WSL has no Wi-Fi card and its kernel has no simulated one.
# Part C runs the real script against stand-ins for iw, ip, modprobe and owl, on a pretend
# /sys/class/net, typed at through a pseudo-terminal.
set -uo pipefail
TAR=$1
KIT=$2
export HOME=/root
W=/tmp/wd-radio
rm -rf "$W" && mkdir -p "$W/tree" "$W/kit"
tar -C "$W/tree" -xf "$TAR"
cp "$KIT/fake" "$KIT/pty_run.py" "$W/kit/"
sed -i 's/\r$//' "$W/kit/fake" "$W/kit/pty_run.py"
chmod +x "$W/kit/fake"
T=$W/tree/tools
pass=0; fail=0
check() {  # DESCRIPTION, then a command that must succeed
  local what=$1; shift
  if "$@" >/dev/null 2>&1; then pass=$((pass + 1)); echo "   PASS  $what"; else fail=$((fail + 1)); echo "   FAIL  $what"; fi
}

echo "##### A. syntax"
for f in owl-exclusive.sh owl-build.sh owl-counts.sh session-receive.sh session-setup.sh owl-session.sh; do
  check "bash -n $f" bash -n "$T/$f"
  check "$f has LF line ends" bash -c "! grep -q \$'\\r' '$T/$f'"
done
command -v shellcheck >/dev/null || apt-get install -y shellcheck >/dev/null 2>&1
if command -v shellcheck >/dev/null; then
  echo "--- shellcheck (the four files this work adds or changes):"
  shellcheck "$T/owl-exclusive.sh" "$T/owl-build.sh" "$T/owl-counts.sh" "$T/session-receive.sh" && echo "   shellcheck: clean"
else
  echo "   shellcheck could not be installed; skipped"
fi

echo
echo "##### B. checked before anything is changed, on this machine as it is (no card)"
echo "--- B1: no patched OWL"
OWL=/nonexistent/owl bash "$T/owl-exclusive.sh"; echo "[rc=$?]"
echo "--- B2: a patched OWL, but no wlan0"
OWL=/root/wd-rehearse-keep/owl bash "$T/owl-exclusive.sh"; echo "[rc=$?]"

echo
echo "##### C. the steps and the way back, against stand-ins"
BIN=$W/bin
mkdir -p "$BIN" "$W/owlbin"
for c in sudo iw ip airmon-ng modprobe sysctl systemctl xset; do ln -sf "$W/kit/fake" "$BIN/$c"; done
ln -sf "$W/kit/fake" "$W/owlbin/owl"
echo 8c38540267641ba2dcdf16b0ae115ac71f11be94 > "$W/owlbin/built-from"

# NAME, then VAR=value settings, then --, then the waits before each Ctrl+C.
scenario() {
  local name=$1; shift
  local settings=()
  while [ "$1" != -- ]; do settings+=("$1"); shift; done
  shift
  local state=$W/state net=$W/net
  rm -rf "$state" "$net" /tmp/owl-exclusive-*.log
  mkdir -p "$state/drivers/iwlwifi" "$state/drivers/mt7921e" "$net/wlan0/phy80211" "$net/wlan0/device"
  echo phy0 > "$net/wlan0/phy80211/name"
  echo 02:11:22:33:44:55 > "$net/wlan0/address"
  # As owl-session.sh leaves the card: a monitor, and up.
  echo monitor > "$state/type.wlan0"
  echo up > "$state/link.wlan0"
  : > "$state/commands"
  local driver=iwlwifi s
  for s in "${settings[@]}"; do [ "${s%%=*}" = FAKE_DRIVER ] && driver=${s#*=}; done
  ln -sfn "$state/drivers/$driver" "$net/wlan0/device/driver"

  echo
  echo "=================== $name   [${settings[*]}]"
  env "${settings[@]}" FAKE_STATE="$state" PATH="$BIN:$PATH" OWL="$W/owlbin/owl" \
    unshare -m bash -c "mount --bind '$net' /sys/class/net && exec python3 '$W/kit/pty_run.py' $* -- bash '$T/owl-exclusive.sh'" \
    > "$state/output" 2>&1
  cat "$state/output"
  echo "--- commands, in order:"
  sed 's/^/      /' "$state/commands"
  echo "--- left behind: wlan0 type=$(cat "$state/type.wlan0" 2>/dev/null) link=$(cat "$state/link.wlan0" 2>/dev/null); interfaces: $(ls "$net" | tr '\n' ' ')"
  STATE=$state NET=$net
}
back_to_station() {
  check "wlan0 is a managed station again, and up" bash -c "[ \"\$(cat $STATE/type.wlan0)\" = managed ] && [ \"\$(cat $STATE/link.wlan0)\" = up ]"
  check "no mon0 and no awdlibss0 left" bash -c "[ ! -e $NET/mon0 ] && [ ! -e $NET/awdlibss0 ]"
}
said() { grep -qF -- "$1" "$STATE/output"; }
ran() { grep -qxF -- "$1" "$STATE/commands"; }
order() {  # each line must appear in the command log after the one before
  awk -v want="$(printf '%s\n' "$@")" 'BEGIN { n = split(want, w, "\n"); i = 1 } i <= n && $0 == w[i] { i++ } END { exit i > n ? 0 : 1 }' "$STATE/commands"
}

scenario "C1 a second interface is allowed; NetworkManager was running; Ctrl+C after OWL is up" FAKE_NM=running -- 11
check "exit status 130" said "[pty_run: exit status 130]"
check "took the SEPARATE path" said "SEPARATE: awdlibss0 added beside wlan0"
check "steps in the method's order" order \
  "airmon-ng check kill" "modprobe -r iwlmvm iwlwifi" "modprobe iwlwifi" \
  "ip link set wlan0 down" "iw dev wlan0 set type managed" \
  "iw phy phy0 interface add awdlibss0 type ibss" "ip link set awdlibss0 address 02:11:22:33:44:55" \
  "sysctl -qw net.ipv6.conf.awdlibss0.disable_ipv6=1" "ip link set awdlibss0 up" \
  "iw dev awdlibss0 ibss join windrop 2437 HT20 fixed-freq 00:25:00:ff:94:73 beacon-interval 10000" \
  "iw phy phy0 interface add mon0 type monitor" "ip link set mon0 address 02:11:22:33:44:55" "ip link set mon0 up" \
  "owl -i mon0 -c 6 -N -S pin -F -E -v" \
  "ip link set mon0 down" "iw dev mon0 del" "iw dev awdlibss0 ibss leave" "iw dev awdlibss0 del" \
  "ip link set wlan0 down" "iw dev wlan0 set type managed" "ip link set wlan0 up" "systemctl start NetworkManager"
check "one address on cell and monitor" said "one address on awdlibss0 and mon0: yes"
check "the watcher reported the cell, the width and awdl0" bash -c "grep -qF '== with OWL running: Joined IBSS 00:25:00:ff:94:73 (on awdlibss0), width: 20 MHz' $STATE/output && grep -qF '== awdl0 is there' $STATE/output"
check "the log's first line names the path and the build" bash -c "head -1 /tmp/owl-exclusive-*.log | grep -qF 'cell on awdlibss0 (SEPARATE), OWL built from omdrop-owl 8c38540267641ba2dcdf16b0ae115ac71f11be94'"
check "counts printed at the end: add peer 2" said "   add peer                2"
check "counts: remove peer 1, Drop frame 1, unable 0" bash -c "grep -qF '   remove peer             1' $STATE/output && grep -qF '   Drop frame to non-peer  1' $STATE/output && grep -qF '   unable to inject        0' $STATE/output"
back_to_station
cp /tmp/owl-exclusive-*.log "$W/sample.log"

scenario "C2 a second interface is refused (the AX211 case); NetworkManager already stopped; no reload" FAKE_SEPARATE=no FAKE_NM=stopped RELOAD=0 -- 8
check "exit status 130" said "[pty_run: exit status 130]"
check "printed the card's refusal" said "the card refused a second interface: command failed: Operation not supported (-95)"
check "took the CONVERTED path" said "CONVERTED: wlan0 itself is now the ad-hoc interface"
check "no reload was done" bash -c "! grep -q modprobe $STATE/commands"
check "steps in order" order \
  "ip link set wlan0 down" "iw dev wlan0 set type managed" "iw phy phy0 interface add awdlibss0 type ibss" \
  "sysctl -n net.ipv6.conf.wlan0.disable_ipv6" "iw dev wlan0 set type ibss" \
  "sysctl -qw net.ipv6.conf.wlan0.disable_ipv6=1" "ip link set wlan0 up" \
  "iw dev wlan0 ibss join windrop 2437 HT20 fixed-freq 00:25:00:ff:94:73 beacon-interval 10000" \
  "iw phy phy0 interface add mon0 type monitor" "ip link set mon0 address 02:11:22:33:44:55" "ip link set mon0 up" \
  "owl -i mon0 -c 6 -N -S pin -F -E -v" \
  "ip link set mon0 down" "iw dev mon0 del" "iw dev wlan0 ibss leave" "ip link set wlan0 down" \
  "iw dev wlan0 set type managed" "sysctl -qw net.ipv6.conf.wlan0.disable_ipv6=0" "ip link set wlan0 up"
check "the log's first line says CONVERTED" bash -c "head -1 /tmp/owl-exclusive-*.log | grep -qF 'cell on wlan0 (CONVERTED)'"
check "NetworkManager is not started, and the way to start it is printed" bash -c "! grep -q 'systemctl start' $STATE/commands && grep -qF 'sudo systemctl start NetworkManager' $STATE/output"
back_to_station

scenario "C3 the cell never joins" FAKE_SEPARATE=no FAKE_NM=stopped RELOAD=0 FAKE_JOIN=never --
check "exit status 1" said "[pty_run: exit status 1]"
check "said so" said "The ad-hoc cell did not start within 15 s. iw says: Not connected."
check "OWL was never started" bash -c "! grep -q '^owl ' $STATE/commands"
back_to_station

scenario "C4 the join is refused" FAKE_NM=stopped RELOAD=0 FAKE_JOIN=refuse --
check "exit status 1" said "[pty_run: exit status 1]"
check "said so, with the card's reason above it" bash -c "grep -qF 'command failed: Invalid argument (-22)' $STATE/output && grep -qF 'The card refused to start the ad-hoc cell on channel 6' $STATE/output"
back_to_station

scenario "C5 neither a second interface nor the switch" FAKE_SEPARATE=no FAKE_CONVERT=no FAKE_NM=stopped RELOAD=0 --
check "exit status 1" said "[pty_run: exit status 1]"
check "said so" said "The card took neither a second interface nor the switch"
back_to_station

scenario "C6 the monitor interface is refused" FAKE_NM=stopped RELOAD=0 FAKE_MONITOR=no --
check "exit status 1" said "[pty_run: exit status 1]"
check "said so" said "The card refused a monitor interface beside the ad-hoc one"
back_to_station

scenario "C7 OWL stops at once with an error" FAKE_NM=running RELOAD=0 FAKE_OWL=fail --
check "exit status 1" said "[pty_run: exit status 1]"
check "OWL's error is on screen and in the log" bash -c "grep -qF 'Could not open device: mon0' $STATE/output && grep -qF 'Could not open device: mon0' /tmp/owl-exclusive-*.log"
check "the watcher did not report after the way back" bash -c "! grep -q 'with OWL running' $STATE/output"
check "NetworkManager started again" ran "systemctl start NetworkManager"
back_to_station

scenario "C8 not an Intel card" FAKE_DRIVER=mt7921e --
check "exit status 1" said "[pty_run: exit status 1]"
check "said so" said "wlan0 is driven by mt7921e, not iwlwifi."
check "nothing was run" bash -c "[ ! -s $STATE/commands ]"

scenario "C9 a second Ctrl+C in the middle of the way back" FAKE_NM=stopped RELOAD=0 FAKE_SLOW=0.4 -- 12 0.7
check "exit status 130" said "[pty_run: exit status 130]"
back_to_station

scenario "C10 the card cannot be turned back into a station" FAKE_SEPARATE=no FAKE_NM=stopped RELOAD=0 FAKE_RESTORE=no -- 8
check "took the CONVERTED path first" said "CONVERTED: wlan0 itself is now the ad-hoc interface"
check "says it is not back, and how to reload the driver" bash -c "grep -qF \"wlan0 is NOT back: it reads 'ibss'\" $STATE/output && grep -qF 'sudo modprobe -r iwlmvm iwlwifi' $STATE/output"

scenario "C11 the card does not come back after the reload" FAKE_NM=running FAKE_RELOAD=gone --
check "exit status 1" said "[pty_run: exit status 1]"
check "said so" said "wlan0 did not come back after the reload"
check "NetworkManager started again" ran "systemctl start NetworkManager"

echo
echo "##### D. owl-counts.sh"
rm -f /tmp/owl-*.log
echo "--- D1: no logs"
bash "$T/owl-counts.sh"; echo "[rc=$?]"
cp "$W/sample.log" /tmp/owl-exclusive-120000.log
printf '%s\n' "12:00:00 ERROR: unable to inject packet (send: Resource temporarily unavailable)" "12:00:01 INFO: add peer a:b:c:d:e:f (iPhone)" "12:00:02 ERROR: unable to inject packet (send: Resource temporarily unavailable)" > /tmp/owl-115900-ch6.log
echo "--- D2: every log in /tmp"
bash "$T/owl-counts.sh"; echo "[rc=$?]"
echo "--- D3: one named, one missing"
bash "$T/owl-counts.sh" /tmp/owl-115900-ch6.log /tmp/nope.log; echo "[rc=$?]"
rm -f /tmp/owl-*.log

echo
echo "##### result: $pass passed, $fail failed"
