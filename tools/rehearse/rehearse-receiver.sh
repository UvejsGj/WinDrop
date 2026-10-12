#!/usr/bin/env bash
# Rehearsal 3: the Linux build, the tests, and the receiver's --repeat on a pretend awdl0.
#   $1  a tar of the tree as it will be committed (LF line ends)
#   $2  the folder this file is in
# The pretend awdl0 is one end of a veth pair whose other end sits in its own network
# namespace, so what is counted there is exactly what the receiver sent on awdl0.
set -uo pipefail
TAR=$1
KIT=$2
export HOME=/root DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_ROOT=/home/admin2/.dotnet
DOTNET=/home/admin2/.dotnet/dotnet
W=/tmp/wd-recv
rm -rf "$W" && mkdir -p "$W/tree" "$W/kit"
tar -C "$W/tree" -xf "$TAR"
cp "$KIT/pty_run.py" "$KIT/mdns_count.py" "$W/kit/"
sed -i 's/\r$//' "$W"/kit/*
cd "$W/tree" || exit 1

echo "##### E1. dotnet test WinDrop.Linux.slnf"
"$DOTNET" test WinDrop.Linux.slnf 2>&1 | tail -5

echo
echo "##### E2. publish the receiver"
"$DOTNET" publish src/WinDrop.Cli -c Release -o "$W/wd" 2>&1 | tail -2
ls -la "$W/wd/windrop.dll"

echo
echo "##### E3. the usage text"
"$DOTNET" "$W/wd/windrop.dll" | grep -n -B1 -A1 repeat

echo
echo "##### E4. a pretend awdl0"
ip netns del wdpeer 2>/dev/null
ip link del awdl0 2>/dev/null
ip link del mon0 2>/dev/null
ip netns add wdpeer
ip link add awdl0 type veth peer name wdpeer0
ip link set wdpeer0 netns wdpeer
ip link set awdl0 up
ip netns exec wdpeer ip link set lo up
ip netns exec wdpeer ip link set wdpeer0 up
sleep 3   # duplicate address detection
ip -6 -o addr show dev awdl0 scope link
ip netns exec wdpeer ip -6 -o addr show dev wdpeer0 scope link
RECEIVER=$(ip -6 -o addr show dev awdl0 scope link | awk '{print $4}' | cut -d/ -f1)
PEER_INDEX=$(ip netns exec wdpeer ip -o link show wdpeer0 | cut -d: -f1)

run_receiver() {  # LABEL SECONDS ARGS...
  local label=$1 seconds=$2; shift 2
  echo
  echo "=================== $label   [receive $*]"
  ip netns exec wdpeer python3 "$W/kit/mdns_count.py" wdpeer0 $((seconds + 5)) > "$W/count.txt" 2>&1 &
  local counter=$!
  sleep 1
  python3 "$W/kit/pty_run.py" "$seconds" -- "$DOTNET" "$W/wd/windrop.dll" receive --yes --dir "$W/rx" --port 8771 "$@" 2>&1 | grep -v '^$' | cut -c1-160
  wait "$counter"
  echo "--- seen at the other end of awdl0:"
  cat "$W/count.txt"
}

run_receiver "E5 the default: no --repeat" 8

head -c 300000 /dev/urandom > "$W/photo.bin"
(
  sleep 6
  echo "--- browse from the other end, while the receiver repeats:"
  ip netns exec wdpeer "$DOTNET" "$W/wd/windrop.dll" browse 2>&1 | grep -v '^$' | cut -c1-160
) > "$W/browse.txt" 2>&1 &
(
  sleep 4
  echo "--- send 300,000 bytes from the other end, while the receiver repeats:"
  ip netns exec wdpeer "$DOTNET" "$W/wd/windrop.dll" send --peer "${RECEIVER}%${PEER_INDEX}" --port 8771 "$W/photo.bin" 2>&1 | grep -v '^$' | tr '\r' '\n' | tail -4 | cut -c1-160
) > "$W/send.txt" 2>&1 &
run_receiver "E6 with --repeat" 24 --repeat
wait
cat "$W/send.txt"
cat "$W/browse.txt"
echo "--- the file arrived whole: $(cmp "$W/photo.bin" "$W/rx/photo.bin" && echo yes)"

echo
echo "##### E7. session-receive.sh adds --repeat by itself only behind owl-exclusive.sh"
export HOME=/home/admin2   # the script looks for ~/.dotnet/dotnet
try_script() {  # LABEL, then VAR=value settings
  local label=$1; shift
  echo "--- $label"
  env "$@" OUT="$W/wd" DIR="$W/rx2" timeout -s INT 4 bash "$W/tree/tools/session-receive.sh" --yes --port 8772 2>&1 \
    | grep -E 'adding --repeat|Repeating the announcement|AWDL is joined|No build|mDNS on' | cut -c1-160
}
try_script "no mon0" X=1
ip link add mon0 type dummy 2>/dev/null || ip link add mon0 type veth peer name mon0p
try_script "mon0 is there" X=1
try_script "mon0 is there, REPEAT=0" REPEAT=0
ip link del mon0
echo "--- no build"
OUT=/nonexistent bash "$W/tree/tools/session-receive.sh" --yes; echo "[rc=$?]"

ip link del awdl0 2>/dev/null
ip netns del wdpeer 2>/dev/null
echo
echo "##### done"
