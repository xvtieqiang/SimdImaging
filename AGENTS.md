# AGENTS.md

Decisions already made for this repository. The design is in [`docs/architecture.md`](docs/architecture.md);
the operator list is in [`docs/operators.md`](docs/operators.md).

## Goal

A managed .NET image **processing** layer over `Sdcb.SimdCodecs`: everything between a decoded raster and an
encoded file. Feature parity with ImageSharp 4.1.x and beyond; NativeAOT from the first commit; every vector
kernel has a scalar twin that produces identical bytes.

Not in scope: codecs (SimdCodecs owns them), text rendering, GPU backends.

## Clean-room rules (non-negotiable)

- Never read, decompile or paraphrase the source of ImageSharp, SkiaSharp, ImageMagick, OpenCV or any
  GPL/AGPL image library. They may be used as **black-box binaries** through documented APIs — to benchmark,
  compare output, generate test data.
- Algorithms come from specs, papers and CPU manuals (BT.601/709/2020, sRGB, ICC, PNG/T.81 where relevant),
  not from another library's code.
- Test images are Public Domain / CC0, downloaded by URL + SHA-256, never committed.
- **Licence keys never enter the repository, a report or a log.** The ImageSharp key sits at
  `docs/sixlabors.lic` and is git-ignored; benchmarks reference it by path. If it ever shows up in a diff,
  that is a leak, not a convenience.

## Repository decisions

- One package per layer: `SimdImaging.Abstractions`, `SimdImaging`, later `SimdImaging.Processing` and
  `SimdImaging.Drawing`. `PackageId = AssemblyName = RootNamespace`. Packages ship in lockstep.
- `Image` and `ImageFrame` live in **`SimdImaging`** (the facade), not in Abstractions: `Load` needs the
  codecs, and Abstractions must stay dependency-light. Operators take `PixelView`, never `Image`, so
  Processing does not need the facade.
- Dependency direction: `Abstractions ← Processing ← SimdImaging`. Never the reverse.
- The codec layer is a **NuGet dependency**, never a source checkout: `Sdcb.SimdCodecs` 2.0.x, version pinned
  in `Directory.Packages.props` and kept equal to `Sdcb.SimdCodecs.Abstractions`. No `ProjectReference` into
  another repository.
- Our owned pixel buffer is **`WorkingBuffer`**, deliberately not `PixelBuffer`: `Sdcb.SimdCodecs.Abstractions`
  2.0 ships a `PixelBuffer` of its own (native layout, codec side) and every file that touches pixels imports
  both namespaces. The names differ, so no alias directives and no qualification noise.
- No shipped package uses another's internals; shared code is source-shared under `src/Shared`.
- **PublicAPI analyzers land in M1**, not M0: the surface still moves, and locking it now would only produce
  churn. Until then, public API changes are reviewed by diff.

## Language

- **English**: code, code comments, XML docs, this file, commit messages.
- **Chinese**: `README.md`, `docs/*.md` except this file, the lab's UI and its README.
- Chinese prose keeps identifiers, API names and measured units in English (`RowParallel.Degree`,
  `Avx2.PermuteVar8x32`, `1.20×`) — that is quoting a term, not mixing languages.

## Build and test

```bash
bash benchmarks/gates.sh                          # all gates: build + tests + public-API snapshot (~1 min)
bash benchmarks/gates.sh --aot                    # also the NativeAOT smoke (~1.5 min with the C++ workload)
bash benchmarks/gates.sh --aot --perf             # also the versioned performance-baseline check
bash benchmarks/simd-tiers.sh [--aot]             # SIMD tier matrix: full suite + operator-output SHA-256 diff
```

`--perf` judges only runs whose `machine`, `binarySha256` and `protocol` all match a stored baseline.
It does not compare raw milliseconds: each scenario is divided by how much slower the two calibration
probes say the machine is than when the baseline was taken (`max` of the memory and ALU ratios), because
the same source measured 1.2–1.8× apart within four hours on this box and the black-box reference drifted
with it. A different binary prints "not comparable" and passes — the design, not a pass that proved
anything. Re-record whenever the binary changes.
`tests/Fixtures/` holds the CC0 corpus manifest (URL + measured SHA-256, cache never committed),
the independent reference vectors and the deterministic synthetic offline substitute.

Run the gate script, not the individual commands, unless you are debugging one of them: it checks the
**observed** numbers (0 warnings, 0 failed, passed ≥ 500, snapshot unchanged) rather than relying on an exit
code. That matters here — an incremental build does not re-emit warnings for projects it did not recompile, so
"0 Warning(s)" from an incremental `dotnet build` means "nothing was compiled", not "nothing is wrong". Four
warnings introduced on 2026-11-07 sat invisible for a whole session for exactly that reason and were caught by
the first full run of this script.

Underlying commands, for reference:

```bash
dotnet build SimdImaging.slnx -c Release          # must stay at zero warnings
dotnet test  SimdImaging.slnx -c Release
bash tests/SimdImaging.AotSmoke/run.sh            # needs the C++ workload (see below)
```

- **No new warnings.** Fix what you introduce; never suppress without a comment saying why.
- Every vector kernel must have a scalar twin, and CI runs the suite with
  `DOTNET_EnableAVX512F=0`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0`. Comparison is the SHA-256
  of the output bytes.
- NativeAOT is a build gate. `PublishAot=true` also flips the `IsDynamicCodeSupported` feature switch for
  **ordinary** builds, so a JIT baseline must be run with `-p:PublishAot=false`.
- `IlcInstructionSet=avx2` is required for the x64 AOT publish, or ILC folds `Avx2.IsSupported` to false and
  trims the AVX2 kernels — a green smoke that never ran them.

## Output contracts

- Integer pipelines are byte-exact across SIMD tiers and platforms. Float pipelines freeze the operation
  order and use single-rounding FMA; no reassociation, no `Vector.Sum` for pixel values.
- Never regenerate a golden to make a test pass.
- Resident memory is budgeted, never unbounded: the thread-local float scratch keeps at most 64 MB idle
  (32 MB per buffer) and the weight-table cache 8 MB, both reported by `MemoryBudget.Snapshot()`. Limits
  bound what is **kept**, never what is handed out — an 8K scratch is allocated and then dropped, not
  refused. Any operator that allocates more than a row buffer must say so through `PlanStep.Scratch`, or
  `MemoryEstimator` reports a working set that is short by exactly that much.
- A gain-map pair derives the gain map's geometry from the **base's** parameters — the same normalised
  rectangle, the conjugated transform, the scaled target size — never by re-running the operator's canvas
  or scale heuristic on the gain map's own dimensions. Two renditions that make their own decisions stop
  describing one scene, and the byte-equality tests are the only thing that notices.
- Untrusted input fails with `InvalidDataException`, caller mistakes with `ArgumentException` /
  `NotSupportedException`; no silent fallback to another format, depth or sampler.

## Status (2026-11-15)

**Milestone status: M0, M1 and M6–M14 complete and verified. M15 in progress. M5 (the acceptance report)
not started. M2–M4 implemented, exit criteria only partly verified.**
Each milestone has acceptance criteria in `docs/architecture.md` §11; M2's blur criterion (1.61–1.86× at
1080p), M3's quantisation PSNR, M4's 8K memory, and the M6–M14 rows carry measured numbers. M2's
"zero allocation after point fusion" was re-measured on the M10 fused path on 2026-11-15 and is now
**judged: not met** (steady state ~4.3 KiB/op by §1.3's literal `GC.GetAllocatedBytesForCurrentThread`
ruler, ~6.0 KiB/op process-wide; two independent runs, two sizes, both rulers — see
`docs/known-gaps.md` §「M2『点算子融合后分配』重测与判定」). The residual is per-call plan rebuild, not
pixel buffers, and the §1.3 per-thread ruler under-reports under band parallelism. M3's antialiasing
coverage and M4's HEIF endpoint remain unjudged for the reasons recorded in `docs/known-gaps.md`.
Do not call a milestone done until its row has numbers **and they meet the criterion**.

M2–M4 in code: `SimdImaging.Processing` has geometry (Crop, Pad, Trim, Flip, Rotate 90/180/arbitrary, affine, perspective, Resize with
five resamplers and the five resize modes, canvas pad/crop, tiling, rounded corners, entropy crop, transposes, combined rotate-flip, delegate warping, point sampling, aspect-preserving resize by one axis), point operators (Brightness, Contrast, Saturation, Grayscale, Invert, Gamma, Threshold, Posterize,
Levels, colour matrices, hue rotation, sepia, normalize,
vibrance, temperature, tint, colour balance, channel swap,
black/white, curves, opacity), blurs
(Gaussian, box — both separable and row-banded, 4K scratch 8.9 MB; median) a Sobel edge map,
3×3 morphology (erode / dilate / open / close)
a bilateral filter, 3×3 convolution, emboss, unsharp mask, Gaussian sharpen, motion blur, bokeh, pixelate, vignette, glow, bit-depth dither (five diffusion kernels, Bayer 2/4/8), oil paint, CLAHE, seeded noise, guided filter (row-banded and vectorised: 4K 523–540 ms / 11 MB, was 893–990 ms / 162 MB), non-local means (exact, unpruned: 1080p 5.8 s at S=3/P=1, 16.1 s at S=5/P=2), the local Laplacian pyramid (layer-native, per-level sample selection), Richardson–Lucy deconvolution (Gaussian and motion PSFs, 1080p 254 ms at σ=1.5 / ten iterations), seam carving (content-aware, 1080p 10 ms per seam), transfer curves (sRGB/γ2.2/Rec.709/PQ/HLG), adaptive threshold, background replacement, colour-vision simulation, colour-space conversion (XYZ/Lab/Luv/LCh/HSL/HSV/YCbCr with chromatic adaptation), chroma subsampling (row-banded, 4K working set 1.1 MB), tone mapping (Reinhard/Hable/ACES/BT.2390 with auto exposure), gain maps, SSIM, gradients, image placement and watermarking and median-cut, Wu, octree, web-safe and dithered palette quantization (one-step `ReduceColors` included),
transcode with metadata replacement, EXIF auto-orient, metadata transfer, multi-frame loading with kept durations, animation composition, embedded thumbnails, ICC transforms (matrix/TRC and LUT: `mft1`/`mft2`/`mAB`/`mBA` with a tetrahedral CLUT, 1080p 40 ms at a 33-point grid), CIEDE2000 colour difference, gain-map pairs (base + gain map kept in step through crop, flip, rotate, resize, affine, perspective and normalised warp, with Ultra HDR read/write), channel statistics, histogram equalization, Premultiply / Unpremultiply and the twelve Porter-Duff operators
18 W3C blend modes,
in-place fills, line/rect drawing,
ellipse and polygon fills, and Bézier paths — coverage-antialiased fill (even-odd / non-zero) and stroke (miter / round / bevel joins, butt / round / square caps, dashes), plus path clipping.
The full suite is green — read the count from the gate's `observed:` line rather than pinning it here,
because it grows with every milestone. The resize passes and the Rgba8 premultiplied row read are vectorized (≈1.8–2.1× / ≈2.0–2.3×
end to end, ranges and caveats in `benchmarks/README.md`). Since 2026-11-07 the hot paths also include
byte-domain SIMD for `Median3x3` (16 lanes, 575 ms → 0.8 ms at 1080p), shifted-row accumulation for
`BokehBlur` and `Convolve2D` (169/81 taps), and row-band parallelism behind `OilPaint`, `Bilateral`,
`Sobel`, `Clahe` and `EqualizeHistogram`. Point operators are still scalar; at equal core counts we are
1.1–1.3× behind the oracle overall, and the headline lead comes from the same number of cores doing
more work per pass, not from more cores. Numbers and protocol: `docs/compare-4.1.3-2026-11-07.md`. `ProcessingPlan` runs same-size steps in row bands with halo-aware source rects, and `PlanSteps` adapts the
existing operators into steps. Since M10 consecutive band-able steps are **fused** by default: each segment
runs through band windows (two per worker) with the halo accumulated along the chain, and the output is
byte-identical to the step-by-step path — `ExecuteOptions.AllowFusion = false` restores it, and a
`PlanObserver` forces it because the observer has to see every full-size intermediate. Consecutive
`AddPointLut` steps fold into a single 4×256 table and run in one pass (Rgba8 only). Band-safety is per
operator and not guessable: `Vignette` derives its falloff from the centre of the view it is given, so
slicing it into bands yields one ellipse per band. It has to run `Global` — and an operator that reads its
own view's height is not bandable at all, which is why the fused executor hands every step a view anchored
at the same `winTop`.

**A step's declared halo must equal the operator's real support.** Fusion changes the failure mode of an
understated halo from "seam at the band edge" to "the neighbouring real rows are read instead of clamped
replicas", so a chain that looked acceptable step-by-step can change bytes once fused. The equivalence tests
compare fused against step-by-step on correctly declared halos only; they cannot make a wrong declaration
right.

**The public surface is locked by a snapshot, not an analyzer.** `docs/public-api.txt` lists every public
type and member with its nullable annotations; `PublicApiTests` reflects it and fails on any difference.
Regenerate with `SIMDIMAGING_UPDATE_PUBLIC_API=1 dotnet test --filter PublicApi` and read the diff — that diff
is the review.

**Resize runs in row bands**, bounded to a 16 MB float scratch; the band size is `(budgetRows - taps) * (dstH / srcH)` — the scale goes the other way round or every band reallocates.

**Measure before optimizing.** `benchmarks/SimdImaging.Benchmarks` is a stopwatch harness for the end-to-end
operation; a kernel that is faster in isolation does not count until that number moves. Record the machine
with every result.

**A single-cell number that moves 2× between runs is not a number.** On 2026-11-07 four conclusions in one
day were built on one reading and later overturned: dispatch overhead at small sizes, "4× behind per core",
"grayscale is 1.75× slower", and "resize-nearest loses 4.5× at 640×360" — the last one rested on an oracle
reading of 0.031 ms that the next run reported as 0.154 ms. So: run it twice before it enters a report, quote
ranges or a geometric mean rather than per-cell ratios, and prefer two independent protocols agreeing over one
protocol repeated.

- `-- perf <size>` — throughput: each side runs 30 ms of continuous work per sample, median over rounds.
  This is the number to quote for "how fast is an operation".
- `-- cores <size>` — isolation: wall-clock ratio from both sides interleaved inside one child process and
  reported as the **minimum** of 21 rounds (interference only ever inflates), plus core counts from one side
  per child. This is the number to quote for single-shot latency, and the only place core counts are honest —
  `TotalProcessorTime` is process-wide and reading it in-process produced 1.22 and 7.86 cores for the same
  operator.
- `-- sizes` — the same table at five resolutions with the flipped cases listed. Never quote one resolution
  alone; seven of seventeen cases change sign between 640×360 and 4K.

**`RowParallel.For` versus `RowParallel.ForBands`.** `For` takes a whole image and refuses to parallelise below
64 K pixels; that floor is deliberate, but it degrades silently, so a caller that passes a *derived, smaller*
size loses parallelism with no error and no visible symptom. `ForBands` has no floor and expects the caller to
have already split the work. If an operator looks inexplicably slow on small images, check
`RowParallel.LastWorkerCount` first — that is how the `Resize` nearest path was found running on one core.

**Every operator whose implementation changes must be added to the `tests/SimdImaging.AotSmoke/Program.cs` hash
list.** That list is the only thing that actually exercises "AOT output == JIT output"; on 2026-11-12 the two
operators that had changed the most — `pixelate` and `rotate180` — were absent from it, so the gate was green and
had verified nothing about them. Adding one entry is three lines; missing one is a guarantee that silently stops
holding.

**Never band an operator whose rows depend on each other.** `Dither` (error diffusion), `FloodFill`,
`AdaptiveThreshold` (a mean window wider than the band), `GuidedFilter` and scanline `Rasterize` all read state
that crosses the band boundary, so splitting them changes bytes. They run whole, or they run with an explicit
halo plus recomputed overlap — and then only a byte-for-byte comparison proves it. The 2026-11-12 parallel audit
used exactly this filter: of 56 serial row loops it touched only the five pure geometry operators (Copy, Crop,
FlipH, FlipV, Rotate180) and left the rest on a list.

**A slow small image is a band-granularity problem, not an algorithm problem.** At 640×480 `box-blur` ran on
**3.77 cores** against the oracle's **8.95** (`cores 640x480`, CPU time ÷ wall time). Bands were pinned at 32 rows,
so 480 rows gave 15 bands and every band paid the halo again. Check the core count before reading the code: the
same operator is 1.20× at 1080p and 0.77× at 640×480 — that is not arithmetic.

**Two protocols can disagree about the same operator.** At 640×480 `grayscale` is 0.91× under `perf`
(same-process, interleaved) and **0.24×** under `cores` (subprocess-isolated, min of 21 rounds). Say which
protocol a number came from, or the number means nothing.

**NativeAOT re-verified (2026-11-07, both tiers, 31 hashes including `median.sobel`, `equalize` and `bilateral`).** **Originally verified (2026-11-05).** `run.sh` publishes twice — `-p:IlcInstructionSet=avx2` and the default
SSE2 baseline — and both binaries produce byte-identical PNG/JPEG output **and** byte-identical geometry
output (crop, bicubic/Lanczos3 resize, 16-bit resize, rotate, flip, pad, composite) to the JIT run. The tier
line proves it: the AVX2 publish reports `avx2=True`, the default one `avx2=False`, both `dynamic-code=False`.

Environment notes for reproducing it: the Windows SDK may live on another drive (this machine: `D:\Windows Kits\10`) — the AOT targets find it through the registry, no extra properties needed. `reg.exe` must not be blocked, or SDK discovery silently loses the library path and the link fails on `advapi32.lib`.
