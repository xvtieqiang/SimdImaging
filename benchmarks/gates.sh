#!/usr/bin/env bash
# Quality gates for SimdImaging, in the order that fails fastest.
#
#   bash benchmarks/gates.sh            # build + tests + public-API snapshot (~1 min)
#   bash benchmarks/gates.sh --aot      # also the NativeAOT smoke (needs the C++ workload, several minutes)
#   bash benchmarks/gates.sh --perf     # also the versioned performance-baseline check (opt-in)
#   bash benchmarks/gates.sh --realworld # also the real-photograph operator sweep (opt-in)
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
report "1/6 build (Release, zero warnings required)"
# An up-to-date build prints no "N Warning(s)" line at all, so a missing count means "nothing was rebuilt", not
# "the count is unknown". The exit status is the authority on whether the build worked; the counts only refine it.
build_status=0
build_log="$(dotnet build SimdImaging.slnx -c Release --nologo 2>&1)" || build_status=$?
warn=$(printf '%s' "$build_log" | grep -oE '[0-9]+ Warning\(s\)' | grep -oE '[0-9]+' | tail -1)
err=$(printf '%s' "$build_log"  | grep -oE '[0-9]+ Error\(s\)'   | grep -oE '[0-9]+' | tail -1)
warn=${warn:-0}
err=${err:-0}
[ "$build_status" -ne 0 ] && err=$(( err > 0 ? err : 1 ))
printf 'observed: warnings=%s errors=%s exit=%s\n' "$warn" "$err" "$build_status"
if [ "$err" = "0" ] && [ "$warn" = "0" ]; then
    pass "build clean"
else
    printf '%s\n' "$build_log" | grep -E 'warning |error ' | head -20
    nope "build has warnings or errors"
fi

# ---------------------------------------------------------------- 2. full test suite
# Fails on any failed test, and also on a suspiciously small total — a suite that silently stopped
# discovering tests would otherwise look like a pass.
report "2/6 tests (all, none failed)"
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
report "3/6 public API snapshot (no drift)"
api_log="$(dotnet test tests/SimdImaging.Tests/SimdImaging.Tests.csproj -c Release --no-build --nologo \
    --filter "FullyQualifiedName~PublicApi" 2>&1)"
if printf '%s' "$api_log" | grep -qE 'Passed: +1|Failed: +0'; then
    pass "snapshot matches docs/public-api.txt"
else
    printf 'observed:\n%s\n' "$(printf '%s' "$api_log" | tail -15)"
    nope "public surface drifted — review the diff, then regenerate with SIMDIMAGING_UPDATE_PUBLIC_API=1"
fi

want_aot=0
want_perf=0
want_real=0
want_cores=0
for arg in "$@"; do
    case "$arg" in
        --aot)       want_aot=1 ;;
        --perf)      want_perf=1 ;;
        --realworld) want_real=1 ;;
        --cores)     want_cores=1 ;;
    esac
done

# ---------------------------------------------------------------- 4. optional AOT smoke
# Two publishes (avx2 and the SSE2 baseline), each compared byte-for-byte against the JIT run. Slow, and
# needs the C++ workload, so it is opt-in rather than part of the default gate.
if [ "$want_aot" = "1" ]; then
    report "4/6 NativeAOT smoke (publish twice, compare bytes)"
    if bash tests/SimdImaging.AotSmoke/run.sh; then
        pass "AOT output matches the JIT baseline"
    else
        nope "AOT smoke failed"
    fi
else
    printf '\n(skipping AOT smoke; pass --aot to include it)\n'
fi

# ---------------------------------------------------------------- 5. optional performance regression
# Since M15 this compares against benchmarks/baselines/*.json and fails on a regression past the
# scenario's threshold. It only compares runs it can defend: same machine id, same binary hash, same
# protocol. Anything else is reported as "not comparable" and skipped, because a cross-machine stopwatch
# difference is noise. The script prints how many scenarios were actually enforced.
# ---------------------------------------------------------------- parallel efficiency
# Every operator claims to use the machine. This runs the same operators through the same code path in a fresh
# process and reports the effective core count, so a change that quietly serialises them shows up as a number
# rather than as a vague feeling that things got slower. The floor is deliberately low: this is a regression
# tripwire, not a performance target.
if [ "$want_cores" = "1" ]; then
    report "parallel efficiency (fresh process, 640x360)"
    SIMDIMAGING_COMPARE_TAG=gates-cores \
        dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- cores 640x360 >/dev/null 2>&1
    latest=$(ls -t benchmarks/SimdImaging.Comparison/results/*gates-cores.cores.md 2>/dev/null | head -1)
    if [ -z "$latest" ]; then
        nope "the core-count run produced no report"
    else
        # Column 6 is "我方核数" (the header is 场景 | 我方 ms | 对手 ms | 直接比 | 我方核数 | ...). The ratio columns also
        # look like decimals, which is how the first two versions of this check read a slowdown ratio instead and
        # claimed an operator used an eighth of a core.
        worst=$(awk -F'|' 'NF > 6 && $6 ~ /[0-9]/ {gsub(/ /,"",$6); sub(/[^0-9.].*/,"",$6); print $6}' "$latest" \
            | sort -n | head -1)
        echo "observed: lowest effective-core reading=${worst:-?}"
        if [ -n "${worst:-}" ] && awk "BEGIN{exit !($worst < 2.0)}"; then
            nope "an operator stayed below 2 effective cores (report: $latest)"
        else
            pass "every operator used at least two cores"
        fi
    fi
fi

if [ "$want_perf" = "1" ]; then
    report "5/6 performance baseline (opt-in; same machine + same binary only)"

    # Let the machine settle before timing. Run straight after the AOT publishes, `gainmap resize 1080p->720p` read
    # +42.6% against a 38% threshold and failed the gate; twenty seconds later the same run read +1.3%. The publish
    # had been competing for the cores the measurement was about to use, and the calibration probes cannot correct
    # for a load that is still finishing when the run starts.
    sleep 20
    if bash benchmarks/check-baseline.sh; then
        pass "performance baseline check"
    else
        nope "performance baseline check failed"
    fi
else
    printf '\n(skipping performance baseline; pass --perf to include it)\n'
fi

# ---------------------------------------------------------------- 6. optional real photographs
# Synthetic fixtures cover the code paths the authors thought of. The two defects in the dehaze operator - a halo
# around every silhouette and speckle in an overcast sky - were found by running real files and looking at the
# output, and no test in the suite would have reported either.
#
# A unique per-run tag is essential: with a fixed tag, a crashed run still finds yesterday's report via
# "ls -t | head -1" and reports a green gate. The exit status is read as well — the harness sets ExitCode=1
# on an unhandled exception, which leaves a partial report that may contain no failure row at all.
if [ "$want_real" = "1" ]; then
    report "6/6 real photographs (every operator, every photograph found)"
    if [ ! -d tests/Fixtures/.cache ] || ! ls tests/Fixtures/.cache/*.jpg >/dev/null 2>&1; then
        echo "SKIP  no photographs in tests/Fixtures/.cache (run tests/Fixtures/download-corpus.sh)"
    else
        run_tag="gates-real-$$-$(date +%s)"
        run_log="$(SIMDIMAGING_COMPARE_TAG="$run_tag" \
            dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- realworld 2>&1)"
        run_status=$?
        latest=$(printf '%s\n' "$run_log" | sed -n 's/^report: //p' | tail -1)
        printf 'observed: exit=%s report=%s\n' "$run_status" "${latest:-none}"
        if [ "$run_status" -ne 0 ]; then
            printf '%s\n' "$run_log" | tail -15 >&2
            nope "the real-photo sweep exited $run_status"
        elif [ -z "$latest" ] || [ ! -f "$latest" ]; then
            nope "the real-photo sweep produced no report"
        else
            # RealWorld.Write marks a caught operator exception with "**失败**" and a NaN-corrupted frame
            # with "**异常**". Both must fail the gate; checking only one of them lets a broken operator pass.
            bad=$(grep -E '算子异常|\*\*异常\*\*|\*\*失败\*\*|\*\*运行中断\*\*' "$latest")
            if [ -n "$bad" ]; then
                printf '%s\n' "$bad" >&2
                nope "a real photograph broke an operator (report: $latest)"
            else
                # The two CC0 corpus images are downloaded; the three sample photos are committed. Read the
                # count the harness actually saw instead of asserting a hard-coded five.
                count=$(sed -n 's/^## J 组 · 真实图片跑通（\([0-9]\+\) 张）.*/\1/p' "$latest" | head -1)
                count=${count:-0}
                if [ "$count" -lt 1 ]; then
                    nope "the real-photo sweep ran on zero photographs (report: $latest)"
                else
                    pass "every operator survived $count real photographs"
                fi
            fi
        fi
    fi
fi

printf '\n---------------------------------------------\n'
if [ "$fail" = "0" ]; then
    echo "GATES: all green"
else
    echo "GATES: FAILED"
fi
exit "$fail"
