#!/usr/bin/env bash
# Starts the receiver the way a field session does, so the folder and the path to the build
# are not retyped at every restart. Anything after the name is passed on to the receiver:
#
#   bash tools/session-receive.sh --yes     accept without asking, for test runs
#   bash tools/session-receive.sh           ask before each transfer
#
# Run session-setup.sh first, and start OWL before this (owl-session.sh): a receiver
# started before OWL joins no AWDL interface, and says so in its first lines.
#
# Files land in /tmp/rx, which a live USB forgets at reboot. DIR=<path> changes that.
set -euo pipefail

OUT=${OUT:-$HOME/wd}
DIR=${DIR:-/tmp/rx}

if [ ! -f "$OUT/windrop.dll" ]; then
  echo "No build in ${OUT}. Run tools/session-setup.sh first."
  exit 1
fi

mkdir -p "$DIR"
exec "$HOME/.dotnet/dotnet" "$OUT/windrop.dll" receive --dir "$DIR" "$@"
