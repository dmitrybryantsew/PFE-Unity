#!/usr/bin/env bash
# Tier-0 smoke test runner: headless per-frame profiling harness.
#
# WHAT IT PROVES
#   1. ProfilerRecorder can read a CUSTOM ProfilerMarker per frame in a Development Build with
#      NO Profiler session attached. This is the one assumption the whole harness rests on.
#   2. It can detect a deliberate injection (a 50 ms sleep in a known window), so a broken setup
#      cannot masquerade as success.
#   3. Per-frame CALL COUNTS are available (the "log calls and their change" requirement).
#   4. FrameTimingManager gives CPU/GPU frame times headless.
#   5. Whether CurrentValue or LastValue carries a SumAllSamplesInFrame aggregate mid-frame.
#
# Background: docs/Perf_HeadlessProfileHarness_Feasibility.md
#
# ---------------------------------------------------------------------------------------------
# STEP 1 - BUILD A DEVELOPMENT PLAYER (this step needs Unity, so it is NOT run by this script)
# ---------------------------------------------------------------------------------------------
#   The Development flag is mandatory. A Release build compiles ProfilerMarker.Begin/End away
#   ([Conditional]) and every custom marker reads as ZERO, silently.
#
#   PREFERRED, when the Unity Editor is open (which is the normal case on this machine):
#       menu bar -> Tools/PFE/Build Development Player
#
#   DO NOT use the -batchmode command below while the Editor has this project open. Unity holds a
#   project lock, so the batchmode instance exits with code 1 BEFORE the engine initialises: the
#   log stops right after "Successfully changed project path to", never reaches "Initialize engine
#   version", prints no compile errors and creates no build. It looks like nothing happened.
#   Confirmed twice in this repo - headless_build.log (6000.3.2f1, 2026-01-30) and
#   Logs/build-dev.log (6000.3.10f1, 2026-09-28) fail identically. Close the Editor first, or use
#   the menu item. The CLI path is for CI, where nothing holds the lock.
#
#   THAT LOCK FAILURE IS NOT THE ONLY ONE, and the second one has TWO parts. Both were diagnosed
#   2026-09-28; read this before re-deriving anything.
#
#   PART 1 - SOLVED. The agent shell does not set PROGRAMDATA, and Unity's Package Manager
#   (UnityPackageManager.exe, a Node program) builds its config path with
#   path.join(process.env.PROGRAMDATA, "Unity", "config"). With the variable absent UPM dies before
#   it writes a single line to upm.log:
#       Application terminated due to an uncaught exception:
#         TypeError [ERR_INVALID_ARG_TYPE]: The "path" argument must be of type string. Received
#         undefined    at Object.join (node:path:510:7)    at getLocalConfigFolder (app.js:221:2702)
#   Exit code 101. Unity then reports "Server process stopped with exit code 101" / "Could not
#   connect to IPC stream" and exits 1. EXPORT IT and UPM starts normally:
#
#     export PROGRAMDATA='C:\ProgramData' APPDATA='C:\Users\User\AppData\Roaming' \
#            ALLUSERSPROFILE='C:\ProgramData'
#
#   Do NOT reach for -noUpm to get past this: it hides the crash and replaces it with PART 2.
#   Verify the fix in ~5 seconds without launching Unity at all:
#     UPM=".../PackageManager/Server/UnityPackageManager.exe"
#     timeout 12 env -u PROGRAMDATA              "$UPM" -s $$ -ipc -ipc-path "Unity-Upm-$$" -l 2  # 101
#     timeout 12 env PROGRAMDATA='C:\ProgramData' "$UPM" -s $$ -ipc -ipc-path "Unity-Upm-$$" -l 2  # ok
#
#   PART 2 - STILL OPEN. With PROGRAMDATA set the Editor now reaches
#       [Package Manager] Done registering packages in 0.03 seconds
#   and then stops forever. A GUI launch goes straight on to
#       [ScriptCompilation] Requested script compilation because: Assetdatabase observed changes ...
#   -> ILPP host ("Now listening on: http://localhost:80") -> bee_backend.exe; a CLI launch never
#   emits that line at all. Unity is idle (CPU frozen, disk idle, all threads in Wait) and spawns no
#   AssetImportWorker / bee_backend / netcorerun.
#
#   Eliminated, each by a run that changed ONE variable: the compile error in PfeScenarioTest.cs
#   (fixed and compiler-verified), -noUpm, the proxy vars, NO_PROXY, -nographics, the agent sandbox,
#   the full Windows environment, and -executeMethod removed entirely (still hangs - so it is the
#   refresh, not the build method). Not a port-80 conflict; no orphan holders; "ImportWorker Server
#   TCP listen port: 0" is normal (the GUI logs it too); Library/*-lock mtimes are a red herring
#   (dated 2026-01-30). Full table: .workbuddy-ai/memory/TOPIC_headless_harness_and_profiling.md
#
#   CONCLUSION: produce the player with the Editor menu item, then run this script - the run half is
#   fully automatic. Do not burn time re-deriving the above.
#
#   Expect Builds/PFE_Dev/PFE_Unity.exe.
#
#   Do NOT try to identify a Development build from boot.config. `profiler-enable` there means the
#   **Autoconnect Profiler** build setting (Unity Manual, "Profiler command line arguments"), NOT
#   the Development flag - a genuine Development build can legitimately have no such key, and this
#   one does not. The authoritative check is runtime: the probe records Debug.isDebugBuild and
#   shouts if it is false, and this script re-checks that line in the report below.
#
# ---------------------------------------------------------------------------------------------
# STEP 2 - RUN THE PROBE (this script)
# ---------------------------------------------------------------------------------------------
#   ./run-profile-smoke.sh [tag] [frames] [poisonMs]
#
# Output lands in the player's persistentDataPath, NOT the repo:
#   %USERPROFILE%\AppData\LocalLow\DefaultCompany\PFE_Unity\ProfilerCaptures\
#     smoke_<tag>_<stamp>.csv   one row per frame, both CurrentValue and LastValue columns
#     smoke_<tag>_<stamp>.txt   the verdict summary (Q1..Q5)
#
# NOTE: the probe calls Application.Quit() itself. A player has no command-line way to exit
# (-quit is Editor-only), so without that the run would have to be killed and could never be
# trusted to flush its own output. The timeout below is only a safety net.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE="$ROOT/Builds/PFE_Dev/PFE_Unity.exe"
OUT="$ROOT/ProfilerCaptures/smoke"

TAG="${1:-run1}"
FRAMES="${2:-120}"
POISON_MS="${3:-50}"
WARMUP=30
SAFETY_SECONDS=180

if [ ! -f "$EXE" ]; then
    echo "MISSING: $EXE"
    echo
    echo "Build the Development player first - see STEP 1 in the header of this script."
    echo "If the Unity Editor is open: menu bar -> Tools/PFE/Build Development Player"
    echo "A Release build will run but report all-zero markers; that is not a valid result."
    exit 2
fi

mkdir -p "$OUT"
LOG="$OUT/player_${TAG}.log"
rm -f "$LOG"

# The player is a NATIVE Windows process and cannot resolve a Git-Bash path, so
# `-logFile /e/Games/...` silently wrote nowhere: runs 1-3 produced no player log at all, which
# is why a hung run had nothing to read. Convert explicitly rather than relying on MSYS guessing.
LOG_WIN="$(cygpath -w "$LOG")"

echo "running: $EXE"
echo "  tag=$TAG frames=$FRAMES warmup=$WARMUP poisonMs=$POISON_MS"
echo "  log:   $LOG"

MSYS_NO_PATHCONV=1 "$EXE" -batchmode \
    -logFile "$LOG_WIN" \
    -pfe-profilesmoke \
    -pfe-profilesmoke-tag "$TAG" \
    -pfe-profilesmoke-frames "$FRAMES" \
    -pfe-profilesmoke-warmup "$WARMUP" \
    -pfe-profilesmoke-poison-ms "$POISON_MS" \
    -pfe-profilesmoke-poison-count 5 &
CHILD=$!

# Safety net only. The probe exits itself via Application.Quit().
for _ in $(seq 1 "$SAFETY_SECONDS"); do
    if ! kill -0 "$CHILD" 2>/dev/null; then break; fi
    sleep 1
done

if kill -0 "$CHILD" 2>/dev/null; then
    echo "WARNING: still running after ${SAFETY_SECONDS}s - killing. Output may be truncated."
    MSYS_NO_PATHCONV=1 taskkill /F /IM PFE_Unity.exe >/dev/null 2>&1
    sleep 2
fi
wait "$CHILD" 2>/dev/null

echo
echo "=== result ==="
REPORT_DIR="C:/Users/User/AppData/LocalLow/DefaultCompany/PFE_Unity/ProfilerCaptures"
LATEST_TXT="$(ls -t "$REPORT_DIR"/smoke_"$TAG"_*.txt 2>/dev/null | head -1)"
LATEST_CSV="$(ls -t "$REPORT_DIR"/smoke_"$TAG"_*.csv 2>/dev/null | head -1)"

if [ -n "${LATEST_TXT:-}" ]; then
    cat "$LATEST_TXT"
    echo
    # The only trustworthy Development-build check is the probe's own runtime reading - not
    # boot.config, where profiler-enable means Autoconnect Profiler.
    if grep -q 'isDebugBuild *: False' "$LATEST_TXT"; then
        echo "!!! RELEASE BUILD - every custom-marker column above is a silent zero."
        echo "!!! Rebuild via Tools/PFE/Build Development Player and re-run."
        echo
    fi
    echo "csv: $LATEST_CSV"
else
    echo "No report found in $REPORT_DIR - check $LOG"
    echo
    tail -30 "$LOG" 2>/dev/null
fi
