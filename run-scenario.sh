#!/usr/bin/env bash
# Tier-3 scenario runner: scripted input replay + screenshots, headless.
#
# WHAT IT PROVES (see the header of Assets/_PFE/Core/Profiling/PfeScenarioTest.cs)
#   1. MOVEMENT - injected key state reaches PlayerController and moves transform.position in the
#      expected direction, and the player stops when the keys are released.
#   2. SCREENSHOTS - the headless player renders a non-blank frame, and the frame changes over time.
#      Both capture paths are exercised in one run (RenderTexture vs CaptureScreenshot) because the
#      feasibility doc asserts, without measuring, that the back-buffer path is unreliable headless.
#
# Input is injected at the DEVICE (InputSystem.QueueStateEvent), not at InputReader, so a broken
# binding chain fails the test instead of silently passing it.
#
# Background: docs/Perf_HeadlessProfileHarness_Feasibility.md, Tier 3.
#
# ---------------------------------------------------------------------------------------------
# STEP 1 - BUILD A DEVELOPMENT PLAYER (needs Unity; NOT run by this script)
# ---------------------------------------------------------------------------------------------
#   PREFERRED, and currently the ONLY path that works on this machine:
#       open the project in Unity, then menu bar -> Tools/PFE/Build Development Player
#
#   THE COMMAND-LINE BUILD IS NOT VIABLE HERE YET. Two faults, diagnosed 2026-09-28:
#
#   FAULT 1 - SOLVED: UPM dies because the agent shell does not set PROGRAMDATA. Unity's Package
#   Manager (a Node program) does path.join(process.env.PROGRAMDATA, "Unity", "config"), so with the
#   variable absent it exits 101 before writing any log, and Unity reports
#     [Package Manager] Server process stopped with exit code `101`
#     [Package Manager] Could not connect to IPC stream "Upm-<pid>" after 30.0 seconds.
#   Export PROGRAMDATA='C:\ProgramData' APPDATA='C:\Users\User\AppData\Roaming'
#   ALLUSERSPROFILE='C:\ProgramData' and UPM starts normally ("Connected to IPC stream ... after 0.0
#   seconds", 74 packages registered). Do NOT use -noUpm as a workaround: it hides this fault and
#   produces fault 2 instead.
#
#   FAULT 2 - STILL OPEN: after "Done registering packages" the Editor hangs and never reaches
#   "[ScriptCompilation] Requested script compilation ...", a line a GUI launch passes in seconds.
#   Idle CPU, idle disk, all threads in Wait, no AssetImportWorker / bee_backend / netcorerun.
#   Eliminated one variable at a time: the compile error (fixed + compiler-verified), -noUpm, the
#   proxy, NO_PROXY, -nographics, the sandbox, the full Windows environment, and -executeMethod
#   removed entirely (still hangs - so it is the refresh, not the build method). Not port 80, not
#   orphan holders, not a stale Library lock. Full table:
#   .workbuddy-ai/memory/TOPIC_headless_harness_and_profiling.md
#
#   So: build from the menu (Tools/PFE/Build Development Player), then run this script. Everything
#   below is automatic.
#
# ---------------------------------------------------------------------------------------------
# STEP 2 - RUN (this script)
# ---------------------------------------------------------------------------------------------
#   ./run-scenario.sh [mode] [tag] [frames] [move] [shots]
#     mode    move+shots (default) | move | shots
#     move    right (default) | left | up | down | none
#
# Report and PNGs land in the player's persistentDataPath, NOT the repo:
#   %USERPROFILE%\AppData\LocalLow\DefaultCompany\PFE_Unity\ProfilerCaptures\scenario\
#
# The probe calls Application.Quit() itself; the timeout below is only a safety net for a run that
# throws before its own watchdog fires.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE="$ROOT/Builds/PFE_Dev/PFE_Unity.exe"
OUT="$ROOT/ProfilerCaptures/scenario"
REPORT_DIR="C:/Users/User/AppData/LocalLow/DefaultCompany/PFE_Unity/ProfilerCaptures/scenario"

MODE="${1:-move+shots}"
TAG="${2:-scenario}"
FRAMES="${3:-180}"
MOVE="${4:-right}"
SHOTS="${5:-4}"
WARMUP=60
SAFETY_SECONDS=240

if [ ! -f "$EXE" ]; then
    echo "MISSING: $EXE"
    echo
    echo "Build the Development player first - see STEP 1 in the header of this script."
    exit 2
fi

mkdir -p "$OUT"
LOG="$OUT/player_${TAG}.log"
rm -f "$LOG"

# The player is a NATIVE Windows process and cannot resolve a Git-Bash path, so `-logFile /e/...`
# silently writes nowhere. Convert explicitly rather than relying on MSYS guessing.
LOG_WIN="$(cygpath -w "$LOG")"

echo "running: $EXE"
echo "  mode=$MODE tag=$TAG frames=$FRAMES warmup=$WARMUP move=$MOVE shots=$SHOTS"
echo "  log:   $LOG"

MSYS_NO_PATHCONV=1 "$EXE" -batchmode \
    -logFile "$LOG_WIN" \
    -pfe-scenario "$MODE" \
    -pfe-scenario-tag "$TAG" \
    -pfe-scenario-frames "$FRAMES" \
    -pfe-scenario-warmup "$WARMUP" \
    -pfe-scenario-move "$MOVE" \
    -pfe-scenario-shots "$SHOTS" &
CHILD=$!

for _ in $(seq 1 "$SAFETY_SECONDS"); do
    if ! kill -0 "$CHILD" 2>/dev/null; then break; fi
    sleep 1
done

if kill -0 "$CHILD" 2>/dev/null; then
    echo "WARNING: still running after ${SAFETY_SECONDS}s - killing. Output may be truncated."
    echo "         (The in-build watchdog should have exited long before this.)"
    MSYS_NO_PATHCONV=1 taskkill /F /IM PFE_Unity.exe >/dev/null 2>&1
    sleep 2
fi
wait "$CHILD" 2>/dev/null

echo
echo "=== result ==="
LATEST_TXT="$(ls -t "$REPORT_DIR"/scenario_"$TAG"_*.txt 2>/dev/null | head -1)"
LATEST_CSV="$(ls -t "$REPORT_DIR"/scenario_"$TAG"_*.csv 2>/dev/null | head -1)"

if [ -n "${LATEST_TXT:-}" ]; then
    cat "$LATEST_TXT"
    echo
    echo "csv: $LATEST_CSV"

    SHOT_DIR="${LATEST_TXT%.txt}"
    if [ -d "$SHOT_DIR" ]; then
        echo "pngs in $SHOT_DIR:"
        ls -la --time-style=+%H:%M:%S "$SHOT_DIR" | tail -n +4
    else
        echo "no png directory: $SHOT_DIR"
    fi

    if grep -q 'isDebugBuild *: False' "$LATEST_TXT"; then
        echo
        echo "!!! RELEASE BUILD - custom markers are silent zeros. Rebuild Development."
    fi
else
    echo "No report found in $REPORT_DIR - check $LOG"
    echo
    tail -30 "$LOG" 2>/dev/null
fi
