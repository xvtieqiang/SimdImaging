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

## What is still slow

97 ms for 4K→1080p is still behind ImageSharp's published class of result. The remaining costs, in order:

| Cost | Why it is still there |
|---|---|
| `WriteRowUnpremultiplied` (scalar, per-pixel division) | Not vectorized yet; float→byte packing must match the scalar rounding exactly |
| Horizontal tap loop | Per-pixel `Vector128.Create(weight)` and a zero-weight branch per tap |
| No same-run comparison | An ImageSharp number is only valid measured in the same process (M5, needs the licence present at build time) |

Next measurement target: after vectorizing the write path, re-run this harness — if the end-to-end number
does not move, the change does not land.
