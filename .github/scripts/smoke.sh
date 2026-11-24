#!/usr/bin/env bash
# Boots a real Vintage Story dedicated server with the mod installed, runs the
# mod's built-in self test from the server console, then shuts the server down.
# Passes only if the server starts, the self test passes and no exception
# mentions the mod.
#
#   smoke.sh          the mod on its own
#   smoke.sh doctor   with Vintage Doctor installed too: also opens the doctor's pages from the console and checks
#                     Embalmed Eats' cards, page and a fix are on them. The doctor zip comes from get-doctor.sh: the
#                     newest release with the DOCTOR_TOKEN secret, else the copy in tools/vendor/vintagedoctor
#                     (ci/doctor-version.txt says which)
set -uo pipefail

mode=${1:-plain}
suffix=""
[ "$mode" = doctor ] && suffix="-doctor"

zip=$(ls out/*.zip 2>/dev/null | head -1)
if [ -z "$zip" ]; then
  echo "SKIPPED: no mod zip" | tee "ci/smoke-summary$suffix.txt"
  exit 0
fi

ROOT=$PWD
DATA=$ROOT/vsdata$suffix
CMDS=$ROOT/server-commands$suffix.txt
LOG=$ROOT/ci/server-stdout$suffix.log
mkdir -p "$DATA/Mods" "ci/server-logs$suffix"
cp "$zip" "$DATA/Mods/"
if [ "$mode" = doctor ]; then
  doctor_zip=$(bash .github/scripts/get-doctor.sh)
  [ -n "$doctor_zip" ] || { echo "No Vintage Doctor zip (no DOCTOR_TOKEN and nothing in tools/vendor/vintagedoctor)"; exit 1; }
  cp "$doctor_zip" "$DATA/Mods/"
  mkdir -p ci/doctor
fi
: > "$CMDS"

cd vs
( tail -f "$CMDS" | dotnet VintagestoryServer.dll --dataPath "$DATA" > "$LOG" 2>&1 ) &
cd "$ROOT"

wait_for() { # pattern timeout_seconds
  local pattern=$1 t=$2 i=0
  while [ $i -lt "$t" ]; do
    if grep -qE "$pattern" "$LOG" "$DATA"/Logs/server-main.* 2>/dev/null; then return 0; fi
    if ! pgrep -f "VintagestoryServer.dll --dataPath $DATA" > /dev/null; then return 2; fi
    sleep 5; i=$((i+5))
  done
  return 1
}

# Sends a console command and saves what the server printed for it: everything new in the console until it has
# been quiet for 3 seconds (at most 60).
run_cmd() { # command outfile
  local before quiet=0 waited=0 size last
  before=$(wc -l < "$LOG")
  echo "$1" >> "$CMDS"
  last=$before
  while [ $quiet -lt 3 ] && [ $waited -lt 60 ]; do
    sleep 1; waited=$((waited+1))
    size=$(wc -l < "$LOG")
    if [ "$size" -gt "$before" ] && [ "$size" -eq "$last" ]; then quiet=$((quiet+1)); else quiet=0; fi
    last=$size
  done
  tail -n +$((before+1)) "$LOG" > "$2"
}

started=no
if wait_for "Dedicated Server now running|Server now running|now running on Port" 1200; then
  started=yes
  sleep 10
  echo "/embalmedeats selftest" >> "$CMDS"
  wait_for "SELFTEST (PASS|FAIL)" 180
  sleep 3
  echo "/embalmedeats help" >> "$CMDS"
  sleep 4
  echo "/embalmedeats" >> "$CMDS"
  sleep 4
  run_cmd "/embalmedeats check" "ci/check$suffix.txt"
  if [ "$mode" = doctor ]; then
    run_cmd "/doctor" ci/doctor/overview.txt
    run_cmd "/doctor embalmedeats" ci/doctor/page.txt
    run_cmd "/doctor embalmedeats item game:redmeat-raw" ci/doctor/page-item.txt
    run_cmd "/doctor mod embalmedeats" ci/doctor/mod.txt
    run_cmd "/doctor item game:redmeat-raw" ci/doctor/item-meat.txt
    run_cmd "/doctor item game:rot" ci/doctor/item-rot.txt
    run_cmd "/doctor fix bus:embalmedeats:cure all" ci/doctor/fix-cure.txt
    run_cmd "/doctor api" ci/doctor/api.txt
    run_cmd "/doctor log embalmedeats" ci/doctor/log.txt
  fi
fi

echo "/stop" >> "$CMDS"
for _ in $(seq 1 30); do pgrep -f "VintagestoryServer.dll --dataPath $DATA" > /dev/null || break; sleep 4; done
pkill -f "VintagestoryServer.dll --dataPath $DATA" 2>/dev/null
pkill -f "tail -f $CMDS" 2>/dev/null

cp -r "$DATA"/Logs/* "ci/server-logs$suffix/" 2>/dev/null
[ "$mode" = doctor ] && cp -r "$DATA"/ModData/vintagedoctor ci/doctor/moddata 2>/dev/null

summary="ci/smoke-summary$suffix.txt"
{
  echo "mode: $mode"
  echo "server started: $started"
  echo "--- self test ---"
  grep -hE "SELFTEST" "$LOG" "ci/server-logs$suffix"/* 2>/dev/null | sort -u
  echo "--- help output ---"
  grep -hE -A11 "Embalmed Eats commands:" "$LOG" 2>/dev/null | head -40
  echo "help shown: $(grep -c "Embalmed Eats commands:" "$LOG" 2>/dev/null) time(s) (expected 2: /embalmedeats help and /embalmedeats)"
  echo "--- /embalmedeats check ---"
  cat "ci/check$suffix.txt" 2>/dev/null
  echo "--- mod lines ---"
  grep -hiE "embalmed" "ci/server-logs$suffix"/server-main* "ci/server-logs$suffix"/server-debug* "$LOG" 2>/dev/null | head -300
  echo "--- errors / exceptions (all) ---"
  grep -hE "\[Error\]|\[Fatal\]|Exception" "ci/server-logs$suffix"/server-main* "$LOG" 2>/dev/null | sort -u | head -200
} > "$summary"
cat "$summary"

[ "$started" = yes ] || { echo "Server did not start"; exit 1; }
grep -qE "SELFTEST PASS" "$summary" || { echo "Self test did not pass"; exit 1; }
[ "$(grep -c "Embalmed Eats commands:" "$LOG")" -ge 1 ] || { echo "/embalmedeats help printed nothing"; exit 1; }
grep -q "✓" "ci/check$suffix.txt" || { echo "/embalmedeats check printed no passed check"; exit 1; }
if grep -hE "Exception" "ci/server-logs$suffix"/* "$LOG" 2>/dev/null | grep -qi "embalmed"; then
  echo "Exception mentioning the mod"; exit 1
fi
if grep -hq "Answering Vintage Doctor" "ci/server-logs$suffix"/* "$LOG" 2>/dev/null; then
  echo "Answering Vintage Doctor failed (see the log)"; exit 1
fi

if [ "$mode" = doctor ]; then
  failed=0
  expect() { # file text what
    if grep -qF -- "$2" "$1"; then echo "ok: $3"; else echo "MISSING: $3 ($1 has no '$2')"; failed=1; fi
  }
  refuse() { # file text what
    if grep -qF -- "$2" "$1"; then echo "FOUND: $3 ($1 has '$2')"; failed=1; else echo "ok: $3"; fi
  }
  {
    expect ci/doctor/overview.txt "of its changes to the game are in place" "the check-up has Embalmed Eats' cards"
    refuse ci/doctor/overview.txt "(Embalmed Eats, grid/" "no generated recipe has another recipe's ingredients (Vintage Doctor 1.3.0's clash check)"
    expect ci/doctor/overview.txt "Something is wrong with preserved food" "the check-up offers our question"
    expect ci/doctor/page.txt "What counts as food" "/doctor embalmedeats is our page"
    expect ci/doctor/page-item.txt "Feeds you:" "/doctor embalmedeats item is the item's rules"
    expect ci/doctor/mod.txt "Embalmed Eats: It can preserve" "the mod's page has our cards"
    expect ci/doctor/item-meat.txt "Can be preserved" "the item page has our card (console)"
    expect ci/doctor/item-rot.txt "Can't be preserved" "rot's item page says why not"
    expect ci/doctor/fix-cure.txt "Cured" "a fix through /doctor fix"
    for f in ci/doctor/*.txt; do
      if grep -q "embalmedeats:doctor-" "$f"; then echo "MISSING TEXT in $f"; failed=1; fi
    done
  } | tee ci/doctor/expectations.txt
  grep -qE "^(MISSING|FOUND)" ci/doctor/expectations.txt && { echo "Vintage Doctor didn't show what it should"; exit 1; }
fi
echo "SMOKE TEST PASSED ($mode)"
