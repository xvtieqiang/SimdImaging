# Benchmarks

A stopwatch harness, not BenchmarkDotNet: it answers one question — **did the end-to-end operation move?**
Kernel-in-isolation numbers are not recorded here on purpose (a faster kernel once made the real pipeline
42% slower; see SimdCodecs' benchmark lessons).

```bash
dotnet run -c Release --project benchmarks/SimdImaging.Benchmarks [iterations]
```

## Recorded results

Machine: Windows 10.0.19045, X64, .NET 10.0.14, `avx512=False avx2=True v128=True`.
Source: 3840×2160 Rgba8 (8.3 MPix). Best of 3 after one warm-up run, **two sessions**.

| Operation | Baseline (scalar) | Current | Change |
|---|---:|---:|---:|
| Resize 4K→1080p, Bicubic | 200.1 ms / 10.4 MPix/s | **97–111 ms / 18.6–21.4 MPix/s** | **≈1.8–2.1×** |
| Resize 4K→1080p, Lanczos3 | 243.0 ms / 8.5 MPix/s | **107–123 ms / 16.9–19.4 MPix/s** | **≈2.0–2.3×** |
| Crop 1000×1000 | 0.4 ms | 0.5 ms | noise (memcpy-bound) |
| Quantize 4K→64 colours (median cut) | 213 ms / 38.9 MPix/s, 1 069 KiB | **59–69 ms / 120–140 MPix/s, 949 KiB** | **3.6×** |
| Quantize 4K→64 colours (Wu) | 132 ms / 62.9 MPix/s | **110 ms / 75.4 MPix/s, 1 440 KiB** | ≈1.5× slower than median cut **in the same run** |
| Quantize 4K→64 colours (octree) | — | 227 ms / 36.6 MPix/s, 85 KiB | new; per-pixel tree pruned to a 4 096-leaf budget |
| Reduce colors 4K→64, dithered | — | 488 ms / 17.0 MPix/s, 265 KiB | new; octree + Floyd–Steinberg |
| Fill 4K ellipse path (AA) | — | 53 ms / 156 MPix/s, 47 KiB | new; 16 sub-scanlines, interior runs replace |
| Stroke 4K ellipse path (AA, w=6) | — | 19 ms / 443 MPix/s, 231 KiB | new |
| Clip 4K ellipse path (AA) | — | 79 ms / 105 MPix/s, 47 KiB | new; every row is visited, alpha scaled per pixel |
| Chroma 4:2:0 4K | 216 ms / 38.5 MPix/s, 56 760 KiB | **213 ms / 38.9 MPix/s, 1 088 KiB** | row-banded: chroma rows for the band plus its neighbours, down 52× |
| Gaussian blur 4K, σ=3 | 549 ms / 15.1 MPix/s, 162 489 KiB | **473 ms / 17.5 MPix/s, 8 880 KiB** | row-banded scratch: 18× less memory and 14% faster (the plane no longer blows cache) |
| Oil paint 4K, r=2, 20 levels | — | 2 015 ms / 4.1 MPix/s, 361 KiB | new; 25-tap window + 20-bin scan per pixel, scalar |
| Guided filter 4K, r=4 | 893–990 ms / 8.4–9.3 MPix/s, 162 121 KiB | **523–540 ms / 15.4–15.8 MPix/s, 10 891 KiB** | row-banded (162 MB → 11 MB) and the window sum vectorised over a float prefix; matches a naive 2-D reference within 1 LSB |
| Composite 1080p overlay | 35.7 ms | 28.8–46.7 ms | noise (unchanged code) |
| Bilateral 4K, r=2 | — | 1 160 ms / 7.1 MPix/s, 363 KiB | new; 25 taps/px, scalar + 512-entry range LUT |
| Resize allocation | 70 450 KiB | **17 020 KiB** | **4.1× lower**: row bands bound the scratch to 16 MB |

**Read the ranges, not the best case.** Run-to-run spread on this machine reached 40% during this session (other load), so absolute numbers move between sessions; comparisons are only trustworthy when both sides are measured in the same run. And the baseline was
recorded *before* the change in an earlier session — by this repository's own rule that is not a valid
baseline. Treat this as a directional result; a same-process A/B (`baseline <git-ref>`) is part of M5.

### What produced the change

1. **Clamp-free interior in both passes.** The old inner loop called `Math.Clamp` per tap per pixel and
   computed `taps > sourceWidth` edge cases through a wrap-around `uint` cast — the wrapped case read past
   the row. The check is now `start >= 0 && start + taps <= sourceWidth`, with a separate clamped path for
   the first and last few pixels.
2. **Horizontal pass: one `Vector128<float>` per destination pixel** (the four channels of a pixel in one
   vector), taps accumulated in the same order as the scalar twin.
3. **Vertical pass: `Vector256<float>` across the row** (eight floats per iteration).
4. **Premultiplied row read vectorized for Rgba8** (four pixels per iteration: 16 bytes → ushort → uint →
   float, alpha broadcast over RGB, alpha lane stored unmultiplied).
5. **Quantizer byte path.** Build and apply were converting every row to floats only to rebuild a 5-bit bin
   from 8-bit values; the Rgba8 path now indexes the bytes directly (213 → 59 ms).
6. **Row-band resizing.** The horizontal pass now runs per band of destination rows, sized so its float
   scratch stays within a 16 MB budget, instead of materialising `dstW × srcH` floats (4K: 66 MB).
7. **Premultiplied row write vectorized for Rgba8** (reciprocal, clamp, truncate, pack 4×4 bytes). No
   measurable end-to-end change on this machine — kept because it is proven byte-identical and the reference
   is needed on other hardware.

The write kernel found a real bug on the way in: the scalar twin guards `alpha > 0`, the vector version
guarded only `alpha == 0`. A Lanczos overshoot makes filtered alpha **negative**, and dividing a negative
colour by a negative alpha flips it into a bright pixel. The smoke-pattern test caught it (`scalar=0
vector=245` at one channel); the fix is `LessThanOrEqual(alpha, 0)` and the AOT hash returned to its previous
value.

Output is unchanged: every AOT smoke hash (crop / bicubic / Lanczos3 / 16-bit resize / rotate / flip / pad /
composite) is byte-identical before and after the vector work, and `RowMathTests` compares the vector kernel
against its scalar twin for widths 1..65 exactly.

## Gates, tiers and baselines (M6, 2026-11-14)

Four scripts now share one ruler:

- `bash benchmarks/gates.sh` — build (0 warnings), full tests, public-API snapshot.
- `bash benchmarks/gates.sh --aot --perf` — adds the NativeAOT smoke and the versioned baseline check.
- `bash benchmarks/simd-tiers.sh [--aot]` — runs the **full** suite under `DOTNET_EnableAVX512F=0`,
  `AVX2=0` and `HWIntrinsic=0`, then diffs the AOT smoke program's operator-output hash bodies.
  It deliberately does not use `--filter "FullyQualifiedName~Kernel"`: that filter only matched tests
  whose name happened to contain "Kernel" and never covered the scalar-twin tests.
- `bash tests/Fixtures/download-corpus.sh --verify` — verifies the CC0 corpus by SHA-256.

Observed 2026-11-14 on the machine line above: `gates.sh` reported `warnings=0 errors=0`,
`failed=0`, passed above the 500 floor, snapshot unchanged. All three JIT tiers passed the whole
suite and produced the same operator-body SHA-256
`4439020325bc2f27a251abd9106e0b07cb181676dc6441da03330428e9067663`, unchanged after the M7 warp
precision rewrite (the Rgba8 output bytes did not move). The exact pass count is deliberately not
pinned here — it grows with every milestone; read it from the gate's `observed:` line instead.
`tests/SimdImaging.AotSmoke/run.sh` additionally diffed JIT / avx2 / SSE2 bodies with no difference
and now requires `warp` and `samplepixel` hashes. An AOT publish cannot run xUnit, so the smoke's
byte-for-byte diff stands in for re-running the suite under AOT — a different axis (build product),
not a fourth SIMD tier, and not a claim that four full suites were executed.

M8 update (2026-11-14): `AotSmoke/Program.cs` gained two explicit-contract operators
(`warp.linear`, `warp.premul`), so the operator-body SHA moved to
`7a7e74fe860e76ebe8c2895dfe80f5d2e730a8778c8cfd140931a26538b10e1f` and all three JIT tiers still
agree. **Stripping those two new lines reproduces the M7 body SHA
`4439020325bc2f27a251abd9106e0b07cb181676dc6441da03330428e9067663` exactly** — that is the check
that no EXISTING operator output byte changed when `WeightTable.BuildCore` moved to the shared
`TapPlanBuilder`. A body hash moving because new operators were added is not a byte regression;
do not confuse the two.

M8 second wave (2026-11-14): three more explicit-contract operators joined the smoke
(`resize.options`, `rotate.options`, `perspective.options`); the body SHA is unchanged at
`7a7e74fe…` because the new Console lines are part of the same hash they define — **stripping all
five new lines (`warp.linear`, `warp.premul`, `resize.options`, `rotate.options`,
`perspective.options`) still reproduces `44390203…6763` exactly**, which is the check that the
Mapping/Resize WithOptions additions, the `RotationCanvas` extraction and the
`SamplingCore` plan-injecting refactor changed no existing operator byte.

M9 update (2026-11-14): the `ewa.rotate` operator joined the smoke, so the body SHA moved to
`2a66d31a2eec905b32bf7b74086456f48413f9c303684654fe88e9d6585da902` across all three JIT tiers.
**Stripping all six M8+M9 lines (`warp.linear`, `warp.premul`, `resize.options`, `rotate.options`,
`perspective.options`, `ewa.rotate`) still reproduces `44390203…6763` exactly** — the EWA additions
(`SampleOptions.AaMode`, `EwaKernel`, the `SamplingCore` Jacobian overload, the analytic Jacobians in
`Mapping`) changed no existing operator byte.

M9 erratum (2026-11-14): the EWA footprint formula was inverted (`Q = MᵀM` with an extra `k²` made
minification sharpen instead of blur). After the fix the body SHA is
`42c595fe324c0af52ec126f3987ce790e612db495de2e2c7a8384852d5594992`; `ewa.rotate` changed because the
footprint changed, and stripping the same six lines still returns `44390203…6763`. The two `ewa.*`
reference vectors are unchanged (both are symmetric footprints, where equal weights make the formula
direction irrelevant).

M10 update (2026-11-14): the `plan.fused` chain (point → 3×3 blur → point through `ProcessingPlan`)
joined the smoke, so the body SHA is now
`d6781e6e4b946a69b0b83763d36f2e6b9f30a85c901eda7f1d98d1bee641370b` across all three JIT tiers.
**Stripping all seven new lines (the six above plus `plan.fused`) still reproduces `44390203…6763`
exactly** — the fused executor changed no operator byte. `run.sh`'s `REQUIRED` list grew with
`plan.fused` in the same change (AGENTS.md: every operator whose implementation changes must be in it).

M11 update (2026-11-14): `nlm` (non-local means) and `ll` (local Laplacian) joined the smoke, so the body
SHA is `3d51c3a6bfd5a9385483b9873b2908e1aff2035a759dc6783b4aee038a54f1c6` across all three JIT tiers.
**Stripping all nine lines (the seven above plus `nlm` and `ll`) reproduces `44390203…6763` exactly** —
both new operators sit outside every existing path. `bilateral` had been hashed but missing from
`REQUIRED`; it is listed there now, which is the first time that hash has actually gated anything.

M12 update (2026-11-14): `rl`, `rl.motion` and `seam` joined the smoke, so the body SHA is
`cf7dc7f8d060e3d5dd3a039e437367879245e87cbc2db1d6460309f594389838` across all three JIT tiers.
**Stripping all thirteen lines (the ten above plus `rl`, `rl.motion` and `seam`) reproduces
`44390203…6763` exactly** — deconvolution and seam carving touch no existing operator.

M12 measured on this machine at 1080p Rgba8: `RichardsonLucy(1.5, 10)` **254 ms**, alloc 95 MiB;
`RichardsonLucyMotion(15, 25°, 10)` **347 ms**; `RemoveSeams(Vertical, 50)` **502 ms** (≈10 ms per seam),
alloc 4 MiB. Deconvolution cost is `2 · taps · iterations` per pixel; FFT would need σ ≳ 18 px to win and
would break cross-tier byte equality — see `docs/fft-evaluation.md`.

M15 (2026-11-14): `gates.sh --perf` stopped being a placeholder. `benchmarks/baselines/windows-10.0.19045-x64-16-m15.json`
records 29 scenarios from two `best-of-3` runs of the same binary, **15 of them with `thresholdPercent: 15`**,
and `check-baseline.sh` compares a fresh run against them: `PASS` at worst **+1.9%** and **+3.5%** on two
validation runs (`enforced=15`), and
`--self-test` (thresholds forced to 0) exits **1** with all 29 over — a comparison that only ever passes is
not a gate. A scenario is gated only if **every** recorded pair agreed within 10%, so the 15% threshold keeps
at least five points of margin: that keeps stroke (23% between runs), gaussian (15.6%), bilateral (21.8%),
ICC (19.6%), crop (20.5%), `dt` (21.7% in one pair) and resize-Lanczos (25.9%) unenforced rather than turning
the gate into a coin flip, and it is why RL and seam stay out despite a quiet pair today (29% and 25% earlier).
Comparability needs the same machine id, binary hash, protocol and repeat count; anything else prints
"not comparable" and is skipped, because a cross-machine stopwatch difference is noise.

M15 re-record (2026-11-14, same machine and protocol): the file was rewritten twice — first after
`DistanceTransformOps` and the cross-guided filter landed, then again after the distance transform's column
pass moved to 64-column tiles — so `binarySha256` matches the current Processing/Abstractions pair and
`--perf` reports `enforced=15` instead of `comparable-baselines=0`. Membership moved with the rule above
against the original 14: **+** `resize 4K->1080p bicubic`, `composite 1080p overlay`, `guided.cross 4K r=4`;
**−** `stroke 4K ellipse`, `gaussian blur 4K` (`clip` stayed in, 4.9% on the defining pair).

M15 gate rework (2026-11-15): absolute milliseconds are no longer what gets judged. The same source measured
1.2–1.8× apart four hours apart, and the black-box ImageSharp reference drifted by the same factor
(resize-bicubic 12.5 → 19.3 ms at 1920×1080), so that drift belongs to the machine — and an absolute baseline
recorded in one state either false-alarms or hides a real regression in another.
`SimdImaging.Benchmarks` now carries two probes that never call this library (a 256 MB parallel streaming sum
and 32M xorshift rounds, both best-of-7, measured at the start and end of the run), and `check-baseline.sh`
divides each scenario by `max(run/baseline)` of the two — whichever resource was contended, the correction
covers it. Thresholds are derived from **three** runs rather than one pair: each run is normalised by its own
calibration, the largest relative deviation `S` across the three sets `threshold = max(15, ceil(2.5·S))`, and
`S > 20%` stays `null`. Result: **24 of 29 gated, at 15–48%**. Proven under deliberate load: four busy cores
drove the `alu` probe to ×1.59, the gate corrected by ×1.59, and every gated scenario passed with a worst
delta of **−1.2%** while the raw readings averaged **+40%**. `--self-test` still exits 1.

M15 smoke update (2026-11-14): `dt` (the exact squared EDT through the facade) and `guided.cross` (the
cross-guided filter) joined the smoke, so the body SHA is
`ff856b8bf81f45fbf910a4ea638b7a56b5685dffbbbf33275e516d6c5bfabd4e` across all three JIT tiers.
**Stripping those two lines reproduces the M13 body SHA `846c1c62…20c6` exactly** — both are new operator
paths and changed no existing operator byte. `REQUIRED` grew to 49 hashes / 22 gated in the same change.

M13 update (2026-11-14): `icc.lut` (a hand-built lut16 profile through `Image.IccTransform`) and
`gainmap.hdr` (hdrgm reconstruction of a quarter-size map) joined the smoke, so the body SHA is
`846c1c62280b8cb6b61fd486356b3f373bb077ab28459c7737355b98038620c6` across all three JIT tiers.
**Stripping those two lines reproduces the M12 body SHA
`cf7dc7f8d060e3d5dd3a039e437367879245e87cbc2db1d6460309f594389838` exactly**, and stripping the M12
list as well reproduces `44390203…6763` — the LUT parser, the CLUT and the gain-map reconstruction
changed no existing operator byte.

M13 measured on this machine at 1080p Rgba8 (`SimdImaging.Benchmarks`, best of five warm runs):
`icc 1080p lut16 grid=33` **40.0 ms** / 51.8 MPix/s, `icc 1080p lut16 grid=2` **36.5 ms**,
`icc 1080p matrix/trc` **34.8 ms** — so the whole 215 KB CLUT (data plus address arithmetic) is worth
about 3.5 ms, 9% of the call; the rest is format conversion and the sRGB/PCS maths both paths share.
That is why the LUT path ships scalar with row-band parallelism instead of a vector body: AVX2 has no
64-bit integer multiply (`vpmullq` is AVX-512DQ) and the accumulator needs 34 bits, so a `Vector256<long>`
product would be expanded to scalar anyway, on top of the eight-corner gather. `gainmap.reconstruct 1080p`
**28.4 ms** — 138.6 ms before the row-band split, i.e. 4.9× from parallelism; `gainmap resize 1080p→720p`
**5.2 ms**.

NLM measured on this machine at 1080p Rgba8 (best of one warm run, `SimdImaging.Benchmarks`):
`S=3 P=1` (the default) 5.8 s, `S=5 P=2` (quality) 16.1 s. The operator's cost is
`(2S+1)²·(2P+1)²` distance terms per pixel; after the AVX2 distance the exponential dominates, so lowering
`h`/radii is the only lever that does not change the result.

`benchmarks/baselines/` is versioned (unlike the git-ignored `results/`). `thresholdPercent` is set for the
24 of 29 scenarios whose normalised spread over three runs stayed under 20%, at `max(15, 2.5×spread)` — always
above the measured noise, never a round number chosen to make today green. `check-baseline.sh` prints
`enforced=24` plus the machine correction it applied; the five that stay `null` (crop, both path fills,
two ICC entries) are below the resolution this box can offer. Default `gates.sh` stays at ~1 min; `--aot`,
`--perf` and `simd-tiers.sh` are opt-in.

## What is still slow

97 ms for 4K→1080p is still behind ImageSharp's published class of result. The remaining costs, in order:

| Cost | Why it is still there |
|---|---|
| `WriteRowUnpremultiplied` (scalar, per-pixel division) | Not vectorized yet; float→byte packing must match the scalar rounding exactly |
| Horizontal tap loop | Per-pixel `Vector128.Create(weight)` and a zero-weight branch per tap |
| No same-run comparison | An ImageSharp number is only valid measured in the same process (M5, needs the licence present at build time) |

Next measurement target: after vectorizing the write path, re-run this harness — if the end-to-end number
does not move, the change does not land.
