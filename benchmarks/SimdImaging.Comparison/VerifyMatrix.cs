using System.Security.Cryptography;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>
/// Every path to the same operator must agree byte for byte: the whole-image call, a banded plan at several band
/// heights, and a fused chain. Parameters move with the case, because an operator that is only correct at its
/// default sigma is not correct.
/// </summary>
internal static class VerifyMatrix
{
    public static void Run(int width, int height)
    {
        var data = Fixture.Make(width, height);
        using var image = Fixture.Ours(data, width, height);

        var failures = 0;
        var total = 0;

        foreach (var (name, pipe, op) in Cases())
        {
            using var direct = pipe(image);
            var expected = Hash(direct);

            // Same input twice: a race in a parallel body shows up as a different hash, not as a crash.
            total++;
            using (var again = pipe(image))
            {
                if (Hash(again) != expected)
                {
                    failures++;
                    Console.WriteLine($"  FAIL {name} 重复执行不一致");
                }
            }

            foreach (var bandRows in new[] { 8, 37, 128 })
            {
                total++;
                var plan = new ProcessingPlan { BandRows = bandRows }.Add(op);
                using var banded = plan.Execute(image[0].Pixels.View);
                if (Hash(banded) == expected)
                    continue;

                failures++;
                Console.WriteLine($"  FAIL {name} 带高 {bandRows}");
            }

            // Fused: the operator and invert in one plan, against the same two operators written out.
            total++;
            using var paired = pipe(image);
            using var sequential = paired.Pipe().Invert().ToImage();
            var sequentialHash = Hash(sequential);

            var fusedPlan = new ProcessingPlan { BandRows = 37 }.Add(op).Add(PlanOps.Invert());
            using var fused = fusedPlan.Execute(image[0].Pixels.View);
            if (Hash(fused) != sequentialHash)
            {
                failures++;
                Console.WriteLine($"  FAIL {name} 融合+反相");
            }
        }

        // Resize is a geometry step, not a plan op, so it has no band or fusion arm — but it must still be
        // deterministic across the four resamplers, which is what the ring and the worker split could break.
        foreach (var resampler in new[] { Resampler.Nearest, Resampler.Bilinear, Resampler.Bicubic, Resampler.Lanczos3 })
        {
            total++;
            using var a = image.Pipe().Resize(width / 2, height / 2, resampler).ToImage();
            using var b = image.Pipe().Resize(width / 2, height / 2, resampler).ToImage();
            if (Hash(a) != Hash(b))
            {
                failures++;
                Console.WriteLine($"  FAIL resize {resampler} 重复执行不一致");
            }
        }

        Console.WriteLine($"  {width}x{height}: {total - failures}/{total} 一致" +
            (failures == 0 ? string.Empty : $"，{failures} 处不一致"));
    }

    private static string Hash(WorkingBuffer buffer) =>
        Convert.ToHexString(SHA256.HashData(buffer.View.Span))[..12];

    private static string Hash(SimdImaging.Image image) =>
        Convert.ToHexString(SHA256.HashData(image.WritablePixels))[..12];

    private static IEnumerable<(string Name, Func<SimdImaging.Image, SimdImaging.Image> Pipe, ImageOp Op)> Cases()
    {
        // Parameter sweep: the point of the matrix is that these are not all defaults.
        yield return ("emboss 0.3", i => i.Pipe().Emboss(0.3f).ToImage(), PlanOps.Emboss(0.3f));
        yield return ("emboss 1.0", i => i.Pipe().Emboss(1f).ToImage(), PlanOps.Emboss(1f));
        yield return ("erode", i => i.Pipe().Erode3x3().ToImage(), PlanOps.Erode3x3());
        yield return ("dilate", i => i.Pipe().Dilate3x3().ToImage(), PlanOps.Dilate3x3());
        yield return ("sharpen a=0.3 s=1", i => i.Pipe().Sharpen(0.3f, 1f).ToImage(), PlanOps.Sharpen(0.3f, 1f));
        yield return ("sharpen a=1.0 s=2", i => i.Pipe().Sharpen(1f, 2f).ToImage(), PlanOps.Sharpen(1f, 2f));
        yield return ("median3x3", i => i.Pipe().Median3x3().ToImage(), PlanOps.Median3x3());
        yield return ("median r=2", i => i.Pipe().Median(2).ToImage(), PlanOps.Median(2));
        yield return ("vignette 0.5/1", i => i.Pipe().Vignette(0.5f, 1f).ToImage(), PlanOps.Vignette(0.5f, 1f));
        yield return ("vignette 0.75/0.5", i => i.Pipe().Vignette(0.75f, 0.5f).ToImage(), PlanOps.Vignette(0.75f, 0.5f));
        yield return ("sobel", i => i.Pipe().Sobel().ToImage(), PlanOps.Sobel());
        yield return ("box-blur r=2", i => i.Pipe().BoxBlur(2).ToImage(), PlanOps.BoxBlur(2));
        yield return ("gaussian s=2", i => i.Pipe().GaussianBlur(2f).ToImage(), PlanOps.GaussianBlur(2f));
        yield return ("pixelate 6", i => i.Pipe().Pixelate(6).ToImage(), PlanOps.Pixelate(6));
        yield return ("pixelate 24", i => i.Pipe().Pixelate(24).ToImage(), PlanOps.Pixelate(24));
    }
}
