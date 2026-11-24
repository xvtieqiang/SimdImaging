#!/bin/sh
# NativeAOT smoke: the published binary must produce byte-identical PNG/JPEG output to the JIT build.
#
# Two publishes, on purpose. x64 defaults to an SSE2 baseline, and against it ILC folds
# Vector256.IsHardwareAccelerated to false and trims the AVX2 kernels — the smoke would pass while
# never running them. So:
#   A. /p:IlcInstructionSet=avx2  -> keeps and exercises the AVX2 paths
#   B. default baseline           -> exercises the Vector128 / scalar fallback
set -eu
cd "$(dirname "$0")"

DOTNET="${DOTNET:-dotnet}"
RID="${RID:-win-x64}"
PROJECT="SimdImaging.AotSmoke.csproj"

publish() {
  out="$1"; shift
  # -p:, not /p:: Git Bash rewrites /p:... into a path, and MSBuild then sees two projects.
  if ! "$DOTNET" publish "$PROJECT" -c Release -r "$RID" -o "$out" "$@" > "$out.log" 2>&1; then
    echo "FAIL: publish to $out failed; last lines of $out.log:" >&2
    tail -25 "$out.log" >&2
    exit 1
  fi
  exe="$out/SimdImaging.AotSmoke"
  if [ -f "$exe.exe" ]; then exe="$exe.exe"; fi
  echo "$exe"
}

echo "== JIT =="
# PublishAot=true also flips the IsDynamicCodeSupported feature switch for ordinary builds,
# so the JIT baseline is built with it off.
"$DOTNET" run -c Release -p:PublishAot=false --project "$PROJECT" > jit.txt
cat jit.txt

echo "== NativeAOT (IlcInstructionSet=avx2) =="
avx2_exe=$(publish out-avx2 -p:IlcInstructionSet=avx2)
"$avx2_exe" > avx2.txt
cat avx2.txt

echo "== NativeAOT (default SSE2 baseline) =="
base_exe=$(publish out-base)
"$base_exe" > base.txt
cat base.txt

# The AVX2 publish must actually report AVX2, or the run below proves nothing.
if ! grep -q 'avx2=True' avx2.txt; then
  echo "FAIL: the AVX2 AOT publish does not report avx2=True; the kernels were trimmed." >&2
  exit 1
fi

grep -q 'dynamic-code-supported=True' jit.txt || { echo "FAIL: the JIT baseline reports no dynamic code." >&2; exit 1; }
grep -q 'dynamic-code-supported=False' avx2.txt || { echo "FAIL: the AOT build does not look AOT-compiled." >&2; exit 1; }

# An operator whose implementation changed but is missing from this list makes a green smoke
# meaningless. Extend REQUIRED in the same commit that changes an operator (AGENTS.md:156-160).
REQUIRED="icc.lut gainmap.hdr rl rl.motion seam seam.insert dt nlm ll dehaze fusion convolve median5 pointchain morph morph.open morph.tophat laplacian bilateral guided guided.cross invert16 quantize quantize.wu quantize.octree warp samplepixel warp.linear warp.premul resize.options rotate.options perspective.options ewa.rotate plan.fused resize.bicubic resize.nearest rotate90.f32 rotate17 perspective stream.plan region.plan region.tiff resize.stream resize.plan region.resize region.resizeplan resize.after plan.resize file.builder file.geometry"
for key in $REQUIRED; do
  for f in jit avx2 base; do
    grep -q "^${key} sha=" "$f.txt" || {
      echo "FAIL: '$key' hash absent from $f.txt — the AOT smoke would not cover it." >&2
      exit 1
    }
  done
done
echo "PASS: required operator hashes present in jit / avx2 / base"

for f in jit avx2 base; do
  grep -v -e '^tier' -e '^dynamic-code-supported' "$f.txt" > "$f.body.txt"
done

diff -u jit.body.txt avx2.body.txt
diff -u jit.body.txt base.body.txt

echo "OK: both AOT builds match the JIT output byte for byte."
