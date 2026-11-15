#!/usr/bin/env bash
# Runs one comparison mode several times and reports, per case, the median ratio, the range across runs, and
# how many runs fell below the tie band. One run cannot tell a 5% regression from machine drift; six can.
#
#   benchmarks/repeat.sh perf 640x360 6
#   benchmarks/repeat.sh perf16 7680x4320 4
#
# The point is not the median - the per-case ratio already has one. It is the "behind" column: a case that is
# under 0.95 in every run is a real loss, and a case that swings from 0.93 to 2.74 is the sampler talking.
set -euo pipefail
cd "$(dirname "$0")/.."

mode=${1:?usage: repeat.sh <perf|perf16|matrix> <WxH> [runs]}
size=${2:?usage: repeat.sh <mode> <WxH> [runs]}
runs=${3:-6}

tag="repeat$(date +%s)"
echo "running $mode at $size, $runs times (tag $tag)..."

for i in $(seq 1 "$runs"); do
    SIMDIMAGING_COMPARE_TAG="$tag.$i" \
        dotnet run -c Release --project benchmarks/SimdImaging.Comparison --no-build -- "$mode" "$size" >/dev/null
done

results="benchmarks/SimdImaging.Comparison/results"
suffix=$([ "$mode" = "perf" ] && echo "perf" || echo "$mode")

python - "$results" "$tag" "$suffix" "$mode@$size" <<'PY'
import glob, re, statistics, sys

results, tag, suffix, label = sys.argv[1:5]
files = sorted(glob.glob(f"{results}/*.{tag}.*.{suffix}.md"))
runs = []
for path in files:
    rows, header, ratio_col = {}, None, None
    for line in open(path, encoding="utf-8"):
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if any("加速比" in c for c in cells):
            header = cells
            ratio_col = next(i for i, c in enumerate(cells) if "加速比" in c)
            continue
        if header and len(cells) == len(header):
            m = re.match(r"^([\d.,]+)×$", cells[ratio_col])
            if m:
                rows[cells[0]] = float(m.group(1).replace(",", ""))
    if rows:
        runs.append(rows)

if not runs:
    print("no results collected")
    raise SystemExit(1)

print(f"\n{label}: {len(runs)} 轮")
print(f"{'场景':26}{'中位':>8}{'最小':>8}{'最大':>8}{'落后':>8}")
behind = []
for case in sorted(set().union(*[set(r) for r in runs])):
    values = [r[case] for r in runs if case in r]
    low = sum(1 for v in values if v < 0.95)
    if low == len(values):
        behind.append(case)
    print(f"{case:26}{statistics.median(values):8.3f}{min(values):8.3f}{max(values):8.3f}{low:>5}/{len(values)}")

print(f"\n每一轮都低于 0.95（真实落后）：{len(behind)} {behind}")
print("同一场景 min 与 max 相差超过 1.5 倍（采样噪声主导，不可引用单次）："
      + str([c for c in sorted(set().union(*[set(r) for r in runs]))
             if (lambda v: max(v) / min(v) > 1.5)([r[c] for r in runs if c in r])]))
PY
