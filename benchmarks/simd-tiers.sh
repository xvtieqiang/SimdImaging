#!/usr/bin/env bash
# SIMD tier matrix. The gate script does NOT set these variables, so a green gates.sh never proved
# that the vector and scalar tiers produce the same bytes. This script does.
#
#   bash benchmarks/simd-tiers.sh          # three JIT tiers
#   bash benchmarks/simd-tiers.sh --aot    # also the NativeAOT avx2 publish (needs the C++ workload)
#
# Each tier runs the FULL test suite (not `--filter FullyQualifiedName~Kernel`, which matches only
# tests whose name happens to contain "Kernel" and misses the actual scalar-twin tests), then runs
# the AOT smoke program under the JIT to capture its operator output hashes. Those bodies are
# compared byte for byte across tiers; that is the cross-tier determinism contract.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

if ! command -v dotnet >/dev/null 2>&1; then
    if [ -x "/c/Program Files/dotnet/dotnet.exe" ]; then
        PATH="/c/Program Files/dotnet:$PATH"
    else
        echo "simd-tiers: dotnet not found on PATH" >&2
        exit 2
    fi
fi
export DOTNET_CLI_UI_LANGUAGE=en

work="$(mktemp -d)"
trap 'rm -rf "$work" >/dev/null 2>&1 || true' EXIT
fail=0

echo "=== build (Release, once; all tiers share this binary) ==="
if ! dotnet build SimdImaging.slnx -c Release --nologo > "$work/build.log" 2>&1; then
    tail -25 "$work/build.log" >&2
    echo "FAIL: build" >&2
    exit 1
fi
warn=$(grep -oE '[0-9]+ Warning\(s\)' "$work/build.log" | grep -oE '[0-9]+' | tail -1)
echo "observed: warnings=${warn:-?}"

# AotSmoke is deliberately NOT in SimdImaging.slnx, so build its JIT host once here. Without
# --no-build below, each tier's stdout would be polluted by MSBuild chatter and the body diff
# would compare build logs instead of operator output bytes.
echo "=== build AOT smoke host (JIT, once) ==="
if ! dotnet build tests/SimdImaging.AotSmoke/SimdImaging.AotSmoke.csproj -c Release \
    -p:PublishAot=false --nologo > "$work/smoke-build.log" 2>&1; then
    tail -25 "$work/smoke-build.log" >&2
    echo "FAIL: smoke host build" >&2
    exit 1
fi

run_tier() {
    local name="$1"; shift
    echo
    echo "=== tier $name ==="

    local test_log="$work/$name.test.log"
    ( export "$@"; dotnet test tests/SimdImaging.Tests/SimdImaging.Tests.csproj -c Release --no-build --nologo ) > "$test_log" 2>&1
    local failed passed
    failed=$(grep -oE 'Failed: +[0-9]+' "$test_log" | grep -oE '[0-9]+' | tail -1)
    passed=$(grep -oE 'Passed: +[0-9]+' "$test_log" | grep -oE '[0-9]+' | tail -1)
    echo "observed: failed=${failed:-?} passed=${passed:-?}"
    if [ "${failed:-1}" != "0" ] || [ "${passed:-0}" -lt 500 ]; then
        echo "FAIL: tier $name test suite" >&2
        grep -E '^\s+Failed |Error Message' "$test_log" | head -10 >&2
        fail=1
    fi

    local smoke="$work/$name.smoke.txt"
    if ! ( export "$@"; dotnet run --no-build -c Release -p:PublishAot=false --project tests/SimdImaging.AotSmoke/SimdImaging.AotSmoke.csproj ) > "$smoke" 2>&1; then
        echo "FAIL: tier $name smoke run" >&2
        tail -20 "$smoke" >&2
        fail=1
        return
    fi
    grep -v -e '^tier' -e '^dynamic-code-supported' "$smoke" > "$work/$name.body.txt"
    local body_sha
    # Feed via stdin: on Windows coreutils escapes the hash with a leading backslash when the
    # temporary path contains one, which makes the observed number noisy for no reason.
    body_sha=$(sha256sum < "$work/$name.body.txt" | cut -d' ' -f1)
    echo "observed: smoke-body-sha256=$body_sha"
    printf '%s\n' "$body_sha" > "$work/$name.sha"
}

# Tier assignments are passed as plain NAME=value arguments and applied with the shell's own
# `export` inside a subshell (see run_tier). Do not use the `env` binary as a prefix: on some
# developer setups PATH holds a shim named `env` that only adjusts PATH and never execs the
# command, which silently turns every tier into a no-op (empty output, misleading green diff).
# Tier 1: the machine default with AVX-512 disabled (the repo's recorded baseline).
run_tier default DOTNET_EnableAVX512F=0
# Tier 2: AVX2 off -> Vector128 paths.
run_tier no-avx2 DOTNET_EnableAVX512F=0 DOTNET_EnableAVX2=0
# Tier 3: all hardware intrinsics off -> scalar twins.
run_tier scalar DOTNET_EnableHWIntrinsic=0

echo
echo "=== cross-tier byte comparison ==="
base=$(cat "$work/default.sha")
for tier in no-avx2 scalar; do
    sha=$(cat "$work/$tier.sha")
    if [ "$sha" = "$base" ]; then
        echo "PASS  default == $tier  ($sha)"
    else
        echo "FAIL  default != $tier" >&2
        diff -u "$work/default.body.txt" "$work/$tier.body.txt" | head -40 >&2
        fail=1
    fi
done

if [ "${1:-}" = "--aot" ]; then
    echo
    echo "=== tier aot-avx2 (NativeAOT publish) ==="
    if bash tests/SimdImaging.AotSmoke/run.sh > "$work/aot.log" 2>&1; then
        echo "PASS  AOT avx2 + SSE2 bodies match the JIT baseline"
        tail -3 "$work/aot.log"
    else
        echo "FAIL  AOT smoke" >&2
        tail -30 "$work/aot.log" >&2
        fail=1
    fi
else
    echo "(skipping the AOT tier; pass --aot to include it)"
fi

echo
if [ "$fail" = "0" ]; then
    echo "TIERS: all byte-identical"
else
    echo "TIERS: FAILED"
fi
exit "$fail"
