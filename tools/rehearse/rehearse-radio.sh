#!/usr/bin/env bash
# Rehearsal 2: owl-exclusive.sh, owl-counts.sh and the scripts' syntax, in one go, as root
# (unshare -m and mount --bind) on a machine with no Wi-Fi card.
#   $1  a tar of the tree as it will be committed (LF line ends); without it, the tree this
#       file is in, as a commit made now would hold it
# The radio cannot be rehearsed: there is no card, and no simulated one in the kernel.
# Part C runs the real script against stand-ins for iw, ip, modprobe and owl, on a pretend
# /sys/class/net, typed at through a pseudo-terminal. The stand-ins come from the tree.
set -uo pipefail
W=/tmp/wd-radio

# The rehearsal clears /tmp/owl-*.log between scenarios, and a field session's logs are kept
# there under the same names.
if compgen -G '/tmp/owl-*.log' >/dev/null; then
  echo "/tmp holds OWL logs, which this rehearsal would delete. Move them first."
  exit 1
fi

rm -rf "$W" && mkdir -p "$W/tree"
if [ -n "${1:-}" ]; then
  tar -C "$W/tree" -xf "$1"
else
  ROOT=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
  # Tracked and new files as they are now, so an uncommitted change is what is rehearsed.
  git -C "$ROOT" ls-files -z --cached --others --exclude-standard | tar -C "$ROOT" --null -T - -cf - | tar -C "$W/tree" -xf -
fi
T=$W/tree/tools
KIT=$T/rehearse
chmod +x "$KIT/fake"
pass=0; fail=0
check() {  # DESCRIPTION, then a command that must succeed
  local what=$1; shift
  if "$@" >/dev/null 2>&1; then pass=$((pass + 1)); echo "   PASS  $what"; else fail=$((fail + 1)); echo "   FAIL  $what"; fi
}

echo "##### A. syntax"
for f in owl-exclusive.sh owl-build.sh owl-counts.sh session-receive.sh session-setup.sh owl-session.sh \
         rehearse/fake rehearse/rehearse-build.sh rehearse/rehearse-radio.sh rehearse/rehearse-receiver.sh; do
  check "bash -n $f" bash -n "$T/$f"
  check "$f has LF line ends" bash -c "! grep -q \$'\\r' '$T/$f'"
done
command -v shellcheck >/dev/null || apt-get install -y shellcheck >/dev/null 2>&1
if command -v shellcheck >/dev/null; then
  echo "--- shellcheck (the files this work adds or changes); anything it finds is printed here:"
  touched=("$T/owl-exclusive.sh" "$T/owl-build.sh" "$T/owl-counts.sh" "$T/session-receive.sh"
           "$KIT/fake" "$KIT/rehearse-build.sh" "$KIT/rehearse-radio.sh" "$KIT/rehearse-receiver.sh")
  shellcheck "${touched[@]}"
  check "shellcheck is clean" shellcheck "${touched[@]}"
else
  echo "   shellcheck could not be installed; skipped"
fi

BIN=$W/bin
mkdir -p "$BIN" "$W/owlbin"
for c in sudo iw ip airmon-ng modprobe sysctl systemctl xset; do ln -sf "$KIT/fake" "$BIN/$c"; done
ln -sf "$KIT/fake" "$W/owlbin/owl"
echo 8c38540267641ba2dcdf16b0ae115ac71f11be94 > "$W/owlbin/built-from"

echo
echo "##### B. checked before anything is changed, on this machine's own /sys/class/net"
# The stand-ins are first on the PATH, so a check that failed to stop the script would run
# them and not the machine's own tools, and would show in their log.
before() {  # LABEL, EXPECTED, then the script's arguments; settings come from the caller
  local label=$1 expected=$2; shift 2
  local state=$W/state-b
  rm -rf "$state" && mkdir -p "$state" && : > "$state/commands"
  echo "--- $label"
  FAKE_STATE=$state PATH="$BIN:$PATH" bash "$T/owl-exclusive.sh" "$@" > "$state/output" 2>&1
  local rc=$?
  cat "$state/output"; echo "[rc=$rc]"
  check "exit status 1" test "$rc" = 1
  check "said: $expected" grep -qF -- "$expected" "$state/output"
  check "nothing was run" test ! -s "$state/commands"
}
OWL=/nonexistent/owl before "B1 no patched OWL" "No patched OWL at /nonexistent/owl"
if [ -e /sys/class/net/wlan0 ]; then
  echo "--- B2 skipped: this machine has a wlan0"
else
  OWL=$W/owlbin/owl before "B2 an OWL, but no wlan0" "No interface wlan0 here"
fi
OWL=$W/owlbin/owl before "B3 an interface that is not Wi-Fi" "lo is not a Wi-Fi interface." lo

echo
echo "##### C. the steps and the way back, against stand-ins"

# NAME, then VAR=value settings, then --, then the waits before each Ctrl+C.
# CARD=wlan1 is not passed on: it puts a second card, wlan1 on phy1 with the mt76x0u driver,
# beside the built-in one, and runs the script on it. The built-in card is then a station
# that is up and must be left so.
scenario() {
  local name=$1; shift
  local settings=() s card=wlan0
  while [ "$1" != -- ]; do
    case "$1" in CARD=*) card=${1#CARD=} ;; *) settings+=("$1") ;; esac
    shift
  done
  shift
  STATE=$W/state NET=$W/net
  rm -rf "$STATE" "$NET" /tmp/owl-exclusive-*.log
  mkdir -p "$STATE" "$NET"
  if [ "$card" = wlan0 ]; then
    # As owl-session.sh leaves the card: a monitor, and up.
    pretend_card wlan0 phy0 iwlwifi 02:11:22:33:44:55 monitor up
    echo phy0 > "$STATE/phyname"
    echo iwlwifi > "$STATE/drivername"
  else
    pretend_card wlan0 phy0 iwlwifi 02:11:22:33:44:55 managed up
    pretend_card "$card" phy1 mt76x0u 02:66:77:88:99:aa monitor up
    # The stand-ins act on the card under test; any other radio is not there to them.
    echo phy1 > "$STATE/phyname"
    echo mt76x0u > "$STATE/drivername"
  fi
  : > "$STATE/commands"
  WLAN0_BEFORE=$(wlan0_now)

  local args=() shown="${settings[*]}"
  [ "$card" = wlan0 ] || { args=("$card"); shown="$shown, argument $card"; }
  echo
  echo "=================== $name   [$shown]"
  env "${settings[@]}" FAKE_STATE="$STATE" PATH="$BIN:$PATH" OWL="$W/owlbin/owl" \
    unshare -m bash -c "mount --bind '$NET' /sys/class/net && exec python3 '$KIT/pty_run.py' $* -- bash '$T/owl-exclusive.sh' ${args[*]}" \
    > "$STATE/output" 2>&1
  cat "$STATE/output"
  echo "--- commands, in order:"
  sed 's/^/      /' "$STATE/commands"
  for s in wlan0 "${args[@]}"; do
    echo "--- left behind: $s type=$(cat "$STATE/type.$s" 2>/dev/null) link=$(cat "$STATE/link.$s" 2>/dev/null)"
  done
  echo "    interfaces: $(cd "$NET" && echo *)"
}
pretend_card() {  # NAME PHY DRIVER ADDRESS TYPE LINK
  mkdir -p "$STATE/drivers/$3" "$NET/$1/phy80211" "$NET/$1/device"
  echo "$2" > "$NET/$1/phy80211/name"
  echo "$4" > "$NET/$1/address"
  ln -sfn "$STATE/drivers/$3" "$NET/$1/device/driver"
  echo "$5" > "$STATE/type.$1"
  echo "$6" > "$STATE/link.$1"
}
wlan0_now() {  # everything about the built-in card that a run could change
  cat "$STATE/type.wlan0" "$STATE/link.wlan0" "$NET/wlan0/address" "$NET/wlan0/phy80211/name" 2>&1
  readlink "$NET/wlan0/device/driver"
}
back_to_station() {  # [INTERFACE]
  local i=${1:-wlan0}
  check "$i is a managed station again, and up" bash -c "[ \"\$(cat $STATE/type.$i)\" = managed ] && [ \"\$(cat $STATE/link.$i)\" = up ]"
  check "no mon0 and no awdlibss0 left" bash -c "[ ! -e $NET/mon0 ] && [ ! -e $NET/awdlibss0 ]"
}
said() { grep -qF -- "$1" "$STATE/output"; }
ran() { grep -qxF -- "$1" "$STATE/commands"; }
log_line() { [ "$(sed -n "$1p" /tmp/owl-exclusive-*.log)" = "$2" ]; }
log_has() { sed -n "$1p" /tmp/owl-exclusive-*.log | grep -qF -- "$2"; }
order() {  # each line must appear in the command log after the one before
  awk -v want="$(printf '%s\n' "$@")" 'BEGIN { n = split(want, w, "\n"); i = 1 } i <= n && $0 == w[i] { i++ } END { exit i > n ? 0 : 1 }' "$STATE/commands"
}

scenario "C1 a second interface is allowed; NetworkManager was running; Ctrl+C after OWL is up" FAKE_NM=running -- 11
check "exit status 130" said "[pty_run: exit status 130]"
check "named the card at start" said "== card: wlan0, driver iwlwifi, address 02:11:22:33:44:55"
check "took the SEPARATE path" said "SEPARATE: awdlibss0 added beside wlan0"
check "steps in the method's order, on the radio's name after the reload" order \
  "airmon-ng check kill" "modprobe -r iwlmvm iwlwifi" "modprobe iwlwifi" \
  "ip link set wlan0 down" "iw dev wlan0 set type managed" \
  "iw phy phy1 interface add awdlibss0 type ibss" "ip link set awdlibss0 address 02:11:22:33:44:55" \
  "sysctl -qw net.ipv6.conf.awdlibss0.disable_ipv6=1" "ip link set awdlibss0 up" \
  "iw dev awdlibss0 ibss join windrop 2437 HT20 fixed-freq 00:25:00:ff:94:73 beacon-interval 10000" \
  "iw phy phy1 interface add mon0 type monitor" "ip link set mon0 address 02:11:22:33:44:55" "ip link set mon0 up" \
  "owl -i mon0 -c 6 -N -S pin -F -E -v" \
  "ip link set mon0 down" "iw dev mon0 del" "iw dev awdlibss0 ibss leave" "iw dev awdlibss0 del" \
  "ip link set wlan0 down" "iw dev wlan0 set type managed" "ip link set wlan0 up" "systemctl start NetworkManager"
check "the name the radio had before the reload is in no command" bash -c "! grep -q 'phy phy0' $STATE/commands"
check "one address on cell and monitor" said "one address on awdlibss0 and mon0: yes"
check "the watcher reported the cell, the width and awdl0" bash -c "grep -qF '== with OWL running: Joined IBSS 00:25:00:ff:94:73 (on awdlibss0), width: 20 MHz' $STATE/output && grep -qF '== awdl0 is there' $STATE/output"
check "the log's first line names the card" log_line 1 "== owl-exclusive: wlan0, driver iwlwifi, phy1, address 02:11:22:33:44:55"
check "the log's second line names the path and the build" log_has 2 "cell on awdlibss0 (SEPARATE), OWL built from omdrop-owl 8c38540267641ba2dcdf16b0ae115ac71f11be94"
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
check "the log's first line keeps the radio's name, with no reload" log_line 1 "== owl-exclusive: wlan0, driver iwlwifi, phy0, address 02:11:22:33:44:55"
check "the log's second line says CONVERTED" log_has 2 "cell on wlan0 (CONVERTED)"
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

scenario "C8 a second card beside the built-in one, the reload left at its default" CARD=wlan1 FAKE_NM=running -- 11
check "exit status 130" said "[pty_run: exit status 130]"
check "named the card at start" said "== card: wlan1, driver mt76x0u, address 02:66:77:88:99:aa"
check "said why there is no reload" said "== no driver reload: it is only done for iwlwifi, and wlan1 is mt76x0u"
check "no modprobe was run" bash -c "! grep -q '^modprobe' $STATE/commands"
check "no command named wlan0 or phy0" bash -c "! grep -qE 'wlan0|phy0' $STATE/commands"
check "steps in order, on wlan1's radio and with wlan1's address" order \
  "airmon-ng check kill" "ip link set wlan1 down" "iw dev wlan1 set type managed" \
  "iw phy phy1 interface add awdlibss0 type ibss" "ip link set awdlibss0 address 02:66:77:88:99:aa" \
  "iw dev awdlibss0 ibss join windrop 2437 HT20 fixed-freq 00:25:00:ff:94:73 beacon-interval 10000" \
  "iw phy phy1 interface add mon0 type monitor" "ip link set mon0 address 02:66:77:88:99:aa" \
  "owl -i mon0 -c 6 -N -S pin -F -E -v" \
  "iw dev mon0 del" "iw dev awdlibss0 del" "ip link set wlan1 down" "iw dev wlan1 set type managed" \
  "ip link set wlan1 up" "systemctl start NetworkManager"
check "the log's first line names the second card" log_line 1 "== owl-exclusive: wlan1, driver mt76x0u, phy1, address 02:66:77:88:99:aa"
check "the log's second line says SEPARATE" log_has 2 "cell on awdlibss0 (SEPARATE)"
check "wlan0 is as it was" test "$(wlan0_now)" = "$WLAN0_BEFORE"
back_to_station wlan1

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

scenario "C12 the second card refuses a second interface, and then cannot be turned back" CARD=wlan1 FAKE_SEPARATE=no FAKE_RESTORE=no FAKE_NM=stopped -- 8
check "took the CONVERTED path first" said "CONVERTED: wlan1 itself is now the ad-hoc interface"
check "says it is not back, names its driver, and to unplug it" bash -c "grep -qF \"wlan1 is NOT back: it reads 'ibss'. Reload its driver (mt76x0u);\" $STATE/output && grep -qF 'for a USB card, unplug it and plug it in again.' $STATE/output"
check "says nothing of iwlmvm" bash -c "! grep -q iwlmvm $STATE/output"
check "no modprobe was run" bash -c "! grep -q '^modprobe' $STATE/commands"
check "wlan0 is as it was" test "$(wlan0_now)" = "$WLAN0_BEFORE"

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
[ "$fail" = 0 ]
