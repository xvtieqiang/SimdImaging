#!/usr/bin/env bash
# Versioned performance-baseline check (opt-in from gates.sh --perf).
#
# A run is comparable only with itself: same machine id, same binary hash, same protocol, same repeat
# count. When no stored baseline matches, this script says so and passes — a cross-machine comparison of
# stopwatch readings is noise, not a gate.
#
# Absolute milliseconds are not judged, the machine-corrected reading is: each scenario is divided by how
# much slower the calibration probes say the machine is than when the baseline was taken. The machine
# drifts 1.2-1.8x within a day and the black-box reference drifts with it, so an absolute threshold would
# either false-alarm or hide a real regression. Failures print the raw swing beside the normalised one.
#
#   bash benchmarks/check-baseline.sh               # compare against the stored baseline
#   bash benchmarks/check-baseline.sh --self-test   # prove the comparison fails on a regression
#
# Scenarios whose baseline entry has thresholdPercent = null are recorded but not enforced: the
# seconds-class operators (NLM at 5.8 s, local Laplacian at 1.8 s) are too noisy to gate on and would
# make an opt-in check cost minutes.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

if ! command -v dotnet >/dev/null 2>&1; then
    if [ -x "/c/Program Files/dotnet/dotnet.exe" ]; then
        PATH="/c/Program Files/dotnet:$PATH"
    else
        echo "check-baseline: dotnet not found on PATH" >&2
        exit 2
    fi
fi

self_test=0
for arg in "$@"; do
    case "$arg" in
        --self-test) self_test=1 ;;
    esac
done

observed="benchmarks/baselines/.observed.json"
trap 'rm -f "$observed"' EXIT

# 1. Is there anything to compare against?
count=$(ls benchmarks/baselines/*.json 2>/dev/null | wc -l | tr -d ' ')
if [ "$count" = "0" ]; then
    echo "observed: baseline-files=0 scenarios=0 enforced=0"
    echo "PASS  no baseline stored yet; nothing to regress against"
    exit 0
fi

# 2. A run of our own, with the repeat count the baseline used.
repeats=$(python - <<'PY'
import glob, json
counts = set()
for path in glob.glob("benchmarks/baselines/*.json"):
    with open(path, encoding="utf-8-sig") as handle:
        counts.add(int(json.load(handle)["repeats"]))
print(max(counts) if counts else 3)
PY
)

echo "observed: running the benchmark ($repeats repeats) for a like-for-like reading"
dotnet run -c Release --project benchmarks/SimdImaging.Benchmarks --no-build -- "$repeats" --json "$observed" >/dev/null 2>&1
if [ ! -f "$observed" ]; then
    echo "FAIL  the benchmark produced no JSON; cannot compare" >&2
    exit 1
fi

# 3. Compare.
STRICT="$self_test" python - "$observed" <<'PY'
import glob
import json
import os
import sys

observed_path = sys.argv[1]
strict = os.environ.get("STRICT") == "1"

with open(observed_path, encoding="utf-8-sig") as handle:
    run = json.load(handle)

by_scenario = {entry["scenario"]: entry for entry in run["entries"]}


def scale_of(doc):
    """Both probe readings, or None when the file predates them."""
    cal = doc.get("calibration")
    if not cal:
        return None
    memory = float(cal["memory"])
    alu = float(cal["alu"])
    if memory <= 0 or alu <= 0:
        return None
    return (memory, alu)


run_probes = scale_of(run)
if run_probes is None:
    print("FAIL  this run carried no calibration block; cannot normalise", file=sys.stderr)
    sys.exit(1)

comparable = []
for path in sorted(glob.glob("benchmarks/baselines/*.json")):
    with open(path, encoding="utf-8-sig") as handle:
        doc = json.load(handle)

    if doc["machine"] != run["machine"]:
        print(f"skip  {os.path.basename(path)}: machine differs (not comparable)")
        continue
    if doc["binarySha256"] != run["binarySha256"]:
        print(f"skip  {os.path.basename(path)}: binary differs (not comparable)")
        continue
    if doc["protocol"] != run["protocol"]:
        print(f"skip  {os.path.basename(path)}: protocol differs (not comparable)")
        continue
    if scale_of(doc) is None:
        print(f"skip  {os.path.basename(path)}: no calibration block (not comparable)")
        continue
    comparable.append((path, doc))

if not comparable:
    print("observed: comparable-baselines=0")
    print("PASS  no baseline matches this machine and binary; a cross-machine reading would be noise")
    sys.exit(0)

enforced = 0
failures = []
worst = None

for path, doc in comparable:
    base_probes = scale_of(doc)
    # How much slower is the machine than when the baseline was taken? Take the worse of the two probes:
    # whichever resource was contended, the correction covers it. Over-correcting only makes the gate more
    # forgiving on a degraded machine; it never invents a regression, because a probe that stays fast
    # leaves the ratio at 1.0 and the scenario is judged on its own movement.
    memory_ratio = run_probes[0] / base_probes[0]
    alu_ratio = run_probes[1] / base_probes[1]
    degradation = max(memory_ratio, alu_ratio)
    print(f"observed: machine vs {os.path.basename(path)}: memory x{memory_ratio:.2f} "
          f"alu x{alu_ratio:.2f} -> correcting by x{degradation:.2f}")

    for entry in doc["entries"]:
        threshold = entry["thresholdPercent"]
        if strict:
            threshold = 0.0
        if threshold is None:
            continue

        name = entry["scenario"]
        if name not in by_scenario:
            print(f"FAIL  {name}: in the baseline but not in this run", file=sys.stderr)
            sys.exit(1)

        enforced += 1
        base_raw = float(entry["value"])
        got_raw = float(by_scenario[name]["value"])

        # Judge the machine-corrected reading and print the raw swing beside it: the distance between the
        # two numbers is how much of the movement belongs to the machine rather than to the code.
        base = base_raw
        got = got_raw / degradation
        delta = (got - base) / base * 100.0 if base else 0.0
        raw_delta = (got_raw - base_raw) / base_raw * 100.0 if base_raw else 0.0

        if worst is None or delta > worst[1]:
            worst = (name, delta)

        if delta > threshold:
            failures.append(
                f"{name}: {base_raw:.1f} -> {got_raw:.1f} ms (raw {raw_delta:+.1f}%), "
                f"normalised {delta:+.1f}% (allowed {threshold:.1f}%)")

print(f"observed: comparable-baselines={len(comparable)} enforced={enforced}")
if worst is not None:
    print(f"observed: worst-delta={worst[1]:+.1f}% ({worst[0]})")

if failures:
    for line in failures:
        print(f"FAIL  {line}", file=sys.stderr)
    sys.exit(1)

print(f"PASS  no scenario regressed past its threshold ({enforced} enforced)")
PY
status=$?
exit $status
