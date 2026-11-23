#!/usr/bin/env bash
# Developer helper: from a fresh launch, wait for the title screen, continue, and wait for the main menu.
cd "$(dirname "$0")/.."
c(){ python -I tools/devcon.py "$@" 2>/dev/null; }
first(){ c "find $1" | grep -E '^[0-9A-F]+ ' | head -1 | awk '{print $1}'; }
for i in $(seq 1 120); do T=$(first "@HB.Golf|-|BootScreenTitle"); [ -n "$T" ] && break; sleep 1; done
echo "title screen at $(date +%T)"
sleep 2; c "call @HB.Golf|-|BootScreenTitle DoContinue 1 $T i:0" >/dev/null
for i in $(seq 1 90); do
  S=$(c "statics @HB.Golf|MVC|ScreenContainer" | grep screenStack | awk '{print $(NF-1)}')
  [ -n "$S" ] && c "list $S" | grep -q MainMenu && break; sleep 1
done
echo "main menu at $(date +%T)"
