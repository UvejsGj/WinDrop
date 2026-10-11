#!/usr/bin/env bash
# Everything a field session needs before OWL starts, in one go: OWL, the .NET SDK, a
# published receiver, and the Wi-Fi scan.
#
# Exists because a Kali live USB keeps nothing between boots, so these steps were typed by
# hand at the start of every session, one at a time, read off a phone.
#
#   git clone -b linux https://github.com/UvejsGj/WinDrop.git
#   bash WinDrop/tools/session-setup.sh
#
# Needs the internet, so run it before owl-session.sh takes the Wi-Fi card. It can be run
# again: a step whose result is already there is skipped.
set -euo pipefail

REPO=$(cd "$(dirname "$0")/.." && pwd)
OUT=${OUT:-$HOME/wd}
DOTNET=$HOME/.dotnet/dotnet

# The SDK will not start without ICU, and a live image may not have it.
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "== 1/4 OWL"
if command -v owl >/dev/null; then
  echo "   already installed"
else
  # A stale mirror fails the update and still has the package, so the install is tried anyway.
  sudo apt-get update || echo "   apt-get update had errors; trying the install anyway"

  if ! sudo apt-get install -y owl; then
    echo "Could not install OWL. Kali packages it as owl; elsewhere it is built from source,"
    echo "see Phase 4 in docs/bridge-hardware-setup.md."
    exit 1
  fi
fi

echo "== 2/4 .NET 8 SDK"
if [ -x "$DOTNET" ]; then
  echo "   already installed"
else
  wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0
fi

echo "== 3/4 building the receiver into ${OUT}"
"$DOTNET" publish "$REPO/src/WinDrop.Cli" -c Release -o "$OUT"

if [ ! -f "$OUT/windrop.dll" ]; then
  echo "The build finished but ${OUT}/windrop.dll is missing."
  exit 1
fi

# Taken now because OWL takes the card next, and the scan is the session's record of what
# else was on the air. Only channels and signal are listed, never network names.
echo "== 4/4 nearby Wi-Fi"
nmcli -f IN-USE,CHAN,FREQ,SIGNAL dev wifi list || echo "   nmcli could not scan; carry on"

echo
echo "== ready"
echo "   branch: $(git -C "$REPO" rev-parse --abbrev-ref HEAD)"
echo "   commit: $(git -C "$REPO" log --oneline -1)"
echo "   next, in this terminal:  bash ${REPO}/tools/owl-session.sh"
echo "   then, in a second one:   bash ${REPO}/tools/session-receive.sh --yes"
