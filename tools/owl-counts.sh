#!/usr/bin/env bash
# Counts the four OWL log lines a field session records, in every OWL log in /tmp or in
# the logs named.
#
# Exists because the phrases have spaces in them, and a session's commands are typed from a
# phone, where quotes are the first thing to go wrong.
#
#   bash tools/owl-counts.sh                          # every /tmp/owl-*.log
#   bash tools/owl-counts.sh /tmp/owl-142233-ch6.log
#
# Works while OWL is still writing the log. Both scripts' logs match: owl-session.sh writes
# /tmp/owl-<time>-ch<channel>.log and owl-exclusive.sh /tmp/owl-exclusive-<time>.log.
#
# What the lines mean:
#   add peer                a device was heard and taken as a peer
#   remove peer             a peer was dropped as gone; mid-transfer, it was not gone
#   unable to inject        the driver refused a frame, which was lost. The patched OWL
#                           keeps a unicast frame and tries again when the driver's queue is
#                           full, without this line, so few of them there is not by itself
#                           proof of anything
#   Drop frame to non-peer  a frame for a device OWL had already dropped, thrown away
set -euo pipefail
shopt -s nullglob

if [ $# -gt 0 ]; then
  logs=("$@")
else
  logs=(/tmp/owl-*.log)
fi

if [ ${#logs[@]} -eq 0 ]; then
  echo "No OWL log in /tmp. owl-session.sh and owl-exclusive.sh print the path of theirs."
  exit 1
fi

# grep -c prints the count and still fails on a count of zero.
count() { grep -c -- "$1" "$2" || true; }

for log in "${logs[@]}"; do
  if [ ! -r "$log" ]; then
    echo "${log}: cannot be read"
    continue
  fi

  echo "$log"
  # "could not add peer" is a failure to add one, and holds the same two words.
  echo "   add peer                $(grep -v 'could not add peer' "$log" | grep -c 'add peer' || true)"
  echo "   remove peer             $(count 'remove peer' "$log")"
  echo "   unable to inject        $(count 'unable to inject' "$log")"
  echo "   Drop frame to non-peer  $(count 'Drop frame to non-peer' "$log")"
done
