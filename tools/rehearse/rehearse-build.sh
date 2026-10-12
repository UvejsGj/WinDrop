#!/usr/bin/env bash
# Rehearsal 1: the OWL build step, in WSL, from the tree as it will be committed.
#   $1  a tar of that tree (LF line ends)
set -uo pipefail
TAR=$1
export HOME=/root
W=/tmp/wd-build
rm -rf "$W" && mkdir -p "$W/tree"
tar -C "$W/tree" -xf "$TAR"
B=$W/tree/tools/owl-build.sh
export SRC=$W/omdrop-owl OWL_OUT=$W/owl-patched

echo "##### first run"
SECONDS=0
bash "$B" 2>&1 | grep -v -E "warning|^\s+[0-9]+ \||^\s+\|| In function |CMake Deprecation|Compatibility with CMake|Update the VERSION|CMake that the project|^$"
echo "[rc=${PIPESTATUS[0]}, ${SECONDS} s]"
ls -la "$OWL_OUT"; cat "$OWL_OUT/built-from"
echo "--- with the options owl-exclusive.sh passes, and a bare -h:"
"$OWL_OUT/owl" -N -S pin -F -E -v -h; echo "[rc=$?]"

echo "##### second run: kept, not rebuilt"
bash "$B"; echo "[rc=$?]"

echo "##### a build from another commit is replaced"
echo deadbeef > "$OWL_OUT/built-from"
bash "$B" 2>&1 | grep -E "^==|^   "; echo "[rc=${PIPESTATUS[0]}]"
cat "$OWL_OUT/built-from"

echo "##### a build that lacks the options is not accepted"
printf '#!/bin/sh\necho "owl: invalid option -- F" >&2\nexit 1\n' > "$W/old-owl"; chmod +x "$W/old-owl"
bash -c "$(sed -n '/^accepts_the_flags()/,/^}/p' "$B"); accepts_the_flags '$W/old-owl' && echo ACCEPTED || echo refused"
bash -c "$(sed -n '/^accepts_the_flags()/,/^}/p' "$B"); accepts_the_flags '$OWL_OUT/owl' && echo accepted || echo REFUSED"

mkdir -p /root/wd-rehearse-keep && cp "$OWL_OUT/owl" /root/wd-rehearse-keep/owl
echo "##### done"
