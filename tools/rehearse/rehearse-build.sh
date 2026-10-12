#!/usr/bin/env bash
# Rehearsal 1: the OWL build step, from the tree as it will be committed.
#   $1  a tar of that tree (LF line ends); without it, the tree this file is in, as a commit
#       made now would hold it
# Needs the internet and root, or sudo, for the build tools.
set -uo pipefail
W=/tmp/wd-build
rm -rf "$W" && mkdir -p "$W/tree"
if [ -n "${1:-}" ]; then
  tar -C "$W/tree" -xf "$1"
else
  ROOT=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
  # Tracked and new files as they are now, so an uncommitted change is what is rehearsed.
  git -C "$ROOT" ls-files -z --cached --others --exclude-standard | tar -C "$ROOT" --null -T - -cf - | tar -C "$W/tree" -xf -
fi
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

echo "##### done"
