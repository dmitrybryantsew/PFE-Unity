#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------------
# run-build-cli.sh - build the Development player from the command line.
#
# RUN THIS FROM A NORMAL CONSOLE (Windows Terminal / PowerShell / cmd), NOT from an agent shell.
#
#   Why that matters, and why this script exists at all:
#
#   FAULT 1 - SOLVED, and this script handles it. The agent shell does not set PROGRAMDATA, and
#   Unity's Package Manager (UnityPackageManager.exe, a Node program) builds its config path with
#   path.join(process.env.PROGRAMDATA, "Unity", "config"). With the variable absent UPM dies before
#   writing a single line to upm.log:
#       Application terminated due to an uncaught exception:
#         TypeError [ERR_INVALID_ARG_TYPE]: The "path" argument must be of type string. Received
#         undefined    at Object.join (node:path:510:7)    at getLocalConfigFolder (app.js:221:2702)
#   Exit code 101. Unity then reports "Server process stopped with exit code 101" / "Could not
#   connect to IPC stream" and exits 1. Exporting PROGRAMDATA (below) fixes it - verified.
#   Do NOT reach for -noUpm as a workaround: it hides this and produces fault 2 instead.
#
#   FAULT 2 - STILL OPEN. With PROGRAMDATA set the Editor reaches
#       [Package Manager] Done registering packages in 0.03 seconds
#   and then hangs. A GUI launch goes straight on to
#       [ScriptCompilation] Requested script compilation because: Assetdatabase observed changes ...
#   -> ILPP host ("Now listening on: http://localhost:80") -> bee_backend.exe; a CLI launch never
#   emits that line. Unity sits idle, and 5 of its 69 threads are in WaitReason=EventPairLow, i.e.
#   blocked in ALPC calls waiting for replies from build helpers that were never spawned
#   (bee_backend / netcorerun / ILPP all absent; UnityPackageManager is present, so spawning itself
#   works). No helper crashed - the Application event log is empty of Unity entries.
#
#   It is NOT the project, NOT the environment, NOT stdout. All three were eliminated by
#   single-variable runs against a throwaway empty project: the empty project hangs at the identical
#   line; `env -i` with a clean PATH and no proxy/agent variables hangs identically; redirecting
#   stdout/stderr to a file instead of the tool's pipes hangs identically. Also ruled out: the
#   compile error (fixed + compiler-verified), -noUpm, the proxy vars, NO_PROXY, -nographics, the
#   sandbox, -executeMethod removed entirely, port-80 conflicts, orphan holders, and stale
#   Library/*-lock files (those are dated 2026-01-30). Full table:
#   .workbuddy-ai/memory/TOPIC_headless_harness_and_profiling.md
#
#   The one variable left is the PARENT PROCESS. Every failing attempt has an agent shell as parent.
#   The agent cannot test this - schtasks.exe is on the sandbox program blacklist - so it needs a
#   human. Running this script from your own console IS that test: if it succeeds, the parent is
#   confirmed as the cause and you have a working headless build path.
#
#   Fallback that is known to work: the Editor menu item Tools/PFE/Build Development Player.
#
# ---------------------------------------------------------------------------------------------

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT" || exit 1

UNITY="C:/Program Files/Unity/Hub/Editor/6000.3.10f1/Editor/Unity.exe"
LOG="$ROOT/Logs/build-dev.log"

echo "=== PFE headless build (Development) ==="
echo "project : $ROOT"
echo

if [ ! -x "$UNITY" ]; then
    echo "ERROR: Unity not found at: $UNITY"
    exit 1
fi

# --- Warn if this looks like an agent shell rather than a normal console -----------------------
# The agent shell is the one configuration known to hang, so say so up front rather than letting
# the user wait five minutes for a hang.
AGENT_SHELL=0
if [ -n "${HTTP_PROXY:-}" ] && [ -z "${NO_PROXY:-}" ]; then
    AGENT_SHELL=1
fi
if printf '%s' "${PATH:-}" | grep -q 'WorkBuddyAI/resources/app.asar.unpacked/cli/vendor/shim'; then
    AGENT_SHELL=1
fi
if [ "$AGENT_SHELL" = "1" ]; then
    echo "WARNING: this looks like the AGENT shell (proxy vars and/or the WorkBuddy shim on PATH)."
    echo "         Every CLI build attempted from that shell has hung at 'Done registering"
    echo "         packages' (fault 2 above). Run this script from a normal console instead."
    echo
fi

# --- Fault 1: UPM needs PROGRAMDATA ------------------------------------------------------------
export PROGRAMDATA='C:\ProgramData'
export ProgramData='C:\ProgramData'
export APPDATA='C:\Users\User\AppData\Roaming'
export ALLUSERSPROFILE='C:\ProgramData'
echo "PROGRAMDATA=[$PROGRAMDATA]  (fault 1 guard)"

# --- Cheap pre-flight: a compile error would otherwise cost a full Unity launch -----------------
# `dotnet build <Unity-generated>.csproj` is a TYPE CHECK, not a build: it writes Temp/bin/Debug,
# compiles with the Editor define set, and produces no player. It is still the fastest way to catch
# a CS error before spending minutes in Unity. The static checkers cannot do this - they are
# structural and have passed files that did not compile.
if command -v dotnet >/dev/null 2>&1 && [ -f "$ROOT/PFE.Core.csproj" ]; then
    echo
    echo "--- pre-flight type check (PFE.Core) ---"
    if dotnet build "$ROOT/PFE.Core.csproj" -nologo -v:q 2>&1 | grep -E ": error " | head -20; then
        echo "ERROR: PFE.Core does not compile - fix the errors above before launching Unity."
        exit 1
    fi
    echo "PFE.Core type-checks clean."
fi

# --- Build -------------------------------------------------------------------------------------
echo
echo "--- launching Unity (no -noUpm) ---"
rm -f "$LOG"
"$UNITY" -batchmode -quit \
    -projectPath "$ROOT" \
    -logFile "$LOG" \
    -executeMethod HeadlessBuilder.BuildDevelopment
CODE=$?
echo "--- unity exit code: $CODE ---"
echo

# --- Report ------------------------------------------------------------------------------------
if [ "$CODE" = "0" ] && [ -x "$ROOT/Builds/PFE_Dev/PFE_Unity.exe" ]; then
    echo "OK - player built: Builds/PFE_Dev/PFE_Unity.exe"
    echo "Next: ./run-scenario.sh move+shots   (or ./run-profile-smoke.sh)"
    exit 0
fi

echo "Build did not produce a player. Diagnose from the log:"
echo "  log            : $LOG"
echo "  size           : $(wc -c < "$LOG" 2>/dev/null || echo '?') bytes"
echo
echo "Look for these, in this order:"
echo "  1. 'Server process stopped with exit code 101'  -> fault 1; PROGRAMDATA is not reaching"
echo "     Unity's child processes. Re-check the exports above."
echo "  2. 'Done registering packages' as the LAST line  -> fault 2 (the open one). If this script"
echo "     was run from a normal console and still stops there, the parent-process theory is wrong;"
echo "     see the topic file for what has already been eliminated."
echo "  3. ': error CS'                                  -> a compile error. Note that -batchmode"
echo "     swallows compile errors: the GUI shows a dialog, batchmode just stops."
echo
echo "Workaround that works today: open the project in the Editor and run"
echo "  Tools/PFE/Build Development Player"
exit "$CODE"
