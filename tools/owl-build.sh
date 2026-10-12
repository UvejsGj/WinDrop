#!/usr/bin/env bash
# Builds the patched OWL that owl-exclusive.sh runs, and leaves the stock one alone.
#
# Exists because a Kali live USB keeps nothing between boots, and the OWL that Kali packages
# cannot do what owl-exclusive.sh asks of it: stay on one channel and say so (-F), and keep
# a peer and its frames through the pauses that end transfers on an Intel card.
#
#   bash WinDrop/tools/owl-build.sh
#
# The patches are not WinDrop's. They belong to omdrop-owl (github.com/t4t5/omdrop-owl,
# GPL-3.0-or-later), which builds OWL from the jedbillyb/owl fork (GPL-3.0) with its own
# patches on top. Neither is copied into this repository: omdrop-owl is cloned here at the
# commit named below and built with its own build script, as a separate program. That
# commit fixes the OWL commit and the patches too, through omdrop-owl's pins file.
#
# The result is ~/owl-patched/owl. The packaged owl in /usr/bin is not touched, and
# owl-session.sh goes on using it.
#
# Needs the internet, so run it before a script takes the Wi-Fi card. It can be run again:
# a build that is already there, from the same commit, is kept.
set -euo pipefail

OMDROP_REPO=https://github.com/t4t5/omdrop-owl.git
# 2026-10-12, "Publish omdrop-owl to AUR". It has all eight patches: 07 is the one written
# for the stall WinDrop's sessions keep hitting, and 08 bounds a write in OWL's receive path
# that a received frame could otherwise push past the end of an array on the stack.
OMDROP_COMMIT=8c38540267641ba2dcdf16b0ae115ac71f11be94

SRC=${SRC:-$HOME/omdrop-owl}
# Not OUT: session-setup.sh uses that name for the receiver.
OWL_OUT=${OWL_OUT:-$HOME/owl-patched}
STAMP=$OWL_OUT/built-from
# libnl-route-3-dev is not in omdrop-owl's own list for Debian. OWL's build asks for it by
# name, and a rehearsal on Ubuntu 22.04 stopped there without it.
PACKAGES="build-essential cmake git pkg-config libev-dev libpcap-dev libnl-3-dev libnl-genl-3-dev libnl-route-3-dev"

# Every option owl-exclusive.sh passes, ending in an -h with no name after it. OWL stops at
# an option it cannot use before it opens anything, so this touches no radio and needs no
# root. An option this build lacks is named in what it prints.
accepts_the_flags() {
  local said
  said=$("$1" -N -S pin -F -E -v -h 2>&1 || true)
  ! grep -q "invalid option" <<<"$said"
}

if [ -x "$OWL_OUT/owl" ] && [ "$(cat "$STAMP" 2>/dev/null)" = "$OMDROP_COMMIT" ] && accepts_the_flags "$OWL_OUT/owl"; then
  echo "== patched OWL is already built: ${OWL_OUT}/owl"
  exit 0
fi

echo "== 1/3 build tools"
missing=""
for package in $PACKAGES; do
  dpkg -s "$package" >/dev/null 2>&1 || missing="$missing $package"
done

if [ -z "$missing" ]; then
  echo "   already installed"
else
  # A stale mirror fails the update and still has the packages, so the install is tried anyway.
  sudo apt-get update || echo "   apt-get update had errors; trying the install anyway"
  # shellcheck disable=SC2086  # a list of names, meant to split
  sudo apt-get install -y $missing
fi

echo "== 2/3 omdrop-owl at ${OMDROP_COMMIT:0:7}"
if [ ! -d "$SRC/.git" ]; then
  git clone -q "$OMDROP_REPO" "$SRC"
fi

# A clone left by an earlier run may predate the commit.
git -C "$SRC" cat-file -e "${OMDROP_COMMIT}^{commit}" 2>/dev/null || git -C "$SRC" fetch -q origin
git -C "$SRC" -c advice.detachedHead=false checkout -q "$OMDROP_COMMIT"

if [ "$(git -C "$SRC" rev-parse HEAD)" != "$OMDROP_COMMIT" ]; then
  echo "${SRC} is not at ${OMDROP_COMMIT}. Remove the folder and run this again."
  exit 1
fi

echo "   OWL commit it builds: $(grep -E '^OWL_COMMIT=' "$SRC/pins" | cut -d= -f2)"
echo "   patches: $(find "$SRC/patches" -name 'owl-*.patch' | wc -l)"

# omdrop-owl's own script: it clones the OWL fork, applies the patches and compiles. It is
# quiet while it works, which takes a minute or two.
echo "== 3/3 building (quiet for a minute or two)"
bash "$SRC/tools/build-owl.sh"

BUILT=$SRC/upstream/owl/build/daemon/owl

if [ ! -x "$BUILT" ]; then
  echo "The build finished but ${BUILT} is missing."
  exit 1
fi

if ! accepts_the_flags "$BUILT"; then
  echo "The build does not take the options owl-exclusive.sh passes (-N -S pin -F -E -v)."
  exit 1
fi

mkdir -p "$OWL_OUT"
install -m 755 "$BUILT" "$OWL_OUT/owl"
echo "$OMDROP_COMMIT" > "$STAMP"

echo
echo "== patched OWL ready: ${OWL_OUT}/owl"
echo "   the packaged owl is untouched: $(command -v owl || echo not installed)"
echo "   it is used by:  bash $(cd "$(dirname "$0")" && pwd)/owl-exclusive.sh"
