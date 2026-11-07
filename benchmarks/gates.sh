#!/usr/bin/env bash
# Quality gates for SimdImaging, in the order that fails fastest.
#
#   bash benchmarks/gates.sh          # build + tests + public-API snapshot (~1 min)
#   bash benchmarks/gates.sh --aot    # also the NativeAOT smoke (needs the C++ workload, several minutes)
#
# Every step prints the criterion it enforces and the value it observed. That is deliberate: most of the
# measurement mistakes in this repo came from reading a green summary without checking what it summarised,
# so each gate states its own number instead of just exiting 0.

set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

# Git Bash in this environment often starts with an empty PATH; fall back to the usual install location
# rather than failing with a bare "dotnet: command not found".
if ! command -v dotnet >/dev/null 2>&1; then
    if [ -x "/c/Program Files/dotnet/dotnet.exe" ]; then
        PATH="/c/Program Files/dotnet:$PATH"
    else
        echo "gates: dotnet not found on PATH" >&2
        exit 2
    fi
fi
export DOTNET_CLI_UI_LANGUAGE=en     # English assertions, so the greps below cannot miss on a localised SDK

fail=0
report() { printf '\n=== %s ===\n' "$1"; }
pass()   { printf 'PASS  %s\n' "$1"; }
nope()   { printf 'FAIL  %s\n' "$1"; fail=1; }

# ---------------------------------------------------------------- 1. build, zero warnings
# The repo rule is "no new warnings", and there is no suppression budget. A warning count above zero is a
# gate failure here, not a note.
report "1/3 build (Release, zero warnings required)"
build_log="$(dotnet build SimdImaging.slnx -c Release --nologo 2>&1)"
warn=$(printf '%s' "$build_log" | grep -oE '[0-9]+ Warning\(s\)' | grep -oE '[0-9]+' | tail -1)
err=$(printf '%s' "$build_log"  | grep -oE '[0-9]+ Error\(s\)'   | grep -oE '[0-9]+' | tail -1)
printf 'observed: warnings=%s errors=%s\n' "${warn:-?}" "${err:-?}"
if [ "${err:-1}" = "0" ] && [ "${warn:-1}" = "0" ]; then
    pass "build clean"
else
    printf '%s\n' "$build_log" | grep -E 'warning |error ' | head -20
    nope "build has warnings or errors"
fi

# ---------------------------------------------------------------- 2. full test suite
# Fails on any failed test, and also on a suspiciously small total — a suite that silently stopped
# discovering tests would otherwise look like a pass.
report "2/3 tests (all, none failed)"
test_log="$(dotnet test tests/SimdImaging.Tests/SimdImaging.Tests.csproj -c Release --no-build --nologo 2>&1)"
failed=$(printf '%s' "$test_log" | grep -oE 'Failed: +[0-9]+' | grep -oE '[0-9]+' | tail -1)
passed=$(printf '%s' "$test_log" | grep -oE 'Passed: +[0-9]+' | grep -oE '[0-9]+' | tail -1)
printf 'observed: failed=%s passed=%s\n' "${failed:-?}" "${passed:-?}"
if [ "${failed:-1}" = "0" ] && [ "${passed:-0}" -ge 500 ]; then
    pass "$passed tests green"
else
    printf '%s\n' "$test_log" | grep -E '^\s+Failed |Error Message' | head -10
    nope "tests failed, or the total is below the 500 floor"
fi

# ---------------------------------------------------------------- 3. public API snapshot
# docs/public-api.txt is the lock. This step must never be "fixed" by regenerating the file inside the gate:
# the regeneration is a reviewed diff, done by hand with SIMDIMAGING_UPDATE_PUBLIC_API=1.
report "3/3 public API snapshot (no drift)"
api_log="$(dotnet test tests/SimdImaging.Tests/SimdImaging.Tests.csproj -c Release --no-build --nologo \
    --filter "FullyQualifiedName~PublicApi" 2>&1)"
if printf '%s' "$api_log" | grep -qE 'Passed: +1|Failed: +0'; then
    pass "snapshot matches docs/public-api.txt"
else
    printf 'observed:\n%s\n' "$(printf '%s' "$api_log" | tail -15)"
    nope "public surface drifted — review the diff, then regenerate with SIMDIMAGING_UPDATE_PUBLIC_API=1"
fi

# ---------------------------------------------------------------- 4. optional AOT smoke
# Two publishes (avx2 and the SSE2 baseline), each compared byte-for-byte against the JIT run. Slow, and
# needs the C++ workload, so it is opt-in rather than part of the default gate.
if [ "${1:-}" = "--aot" ]; then
    report "4/4 NativeAOT smoke (publish twice, compare bytes)"
    if bash tests/SimdImaging.AotSmoke/run.sh; then
        pass "AOT output matches the JIT baseline"
    else
        nope "AOT smoke failed"
    fi
else
    printf '\n(skipping AOT smoke; pass --aot to include it)\n'
fi

printf '\n---------------------------------------------\n'
if [ "$fail" = "0" ]; then
    echo "GATES: all green"
else
    echo "GATES: FAILED"
fi
exit "$fail"
