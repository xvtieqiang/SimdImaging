using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

public class ProcessingPlanTests
{
    private static Image Ramp(int width, int height)
    {
        var image = Image.Create(width, height, WorkingFormat.Rgba8);
        var span = image.WritablePixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                span[o] = (byte)(x * 7 + y * 3);
                span[o + 1] = (byte)(y * 11);
                span[o + 2] = (byte)(x * 5);
                span[o + 3] = 255;
            }
        }

        return image;
    }

    private static OpKernel AddBrightness(float amount) => (source, destination, destinationRect, _) =>
    {
        for (var y = destinationRect.Y; y < destinationRect.Y + destinationRect.Height; y++)
        {
            var sourceRow = source.Row(y);
            var destinationRow = destination.Row(y);

            for (var x = destinationRect.X; x < destinationRect.X + destinationRect.Width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var value = sourceRow[x * 4 + c] + amount * 255f;
                    destinationRow[x * 4 + c] = (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
                }

                // A rented buffer's contents are undefined: a kernel must write every byte it owns,
                // including alpha, or the result depends on what the pool handed out.
                destinationRow[x * 4 + 3] = sourceRow[x * 4 + 3];
            }
        }
    };

    private static void VerticalThree(PixelView source, PixelView destination, PixelRect destinationRect, PixelRect _)
    {
        for (var y = destinationRect.Y; y < destinationRect.Y + destinationRect.Height; y++)
        {
            for (var x = 0; x < destinationRect.Width; x++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var k = -1; k <= 1; k++)
                        sum += source.Row(Math.Clamp(y + k, 0, source.Height - 1))[x * 4 + c];

                    destination.Row(y)[x * 4 + c] = (byte)(sum / 3);
                }
            }
        }
    }

    [Fact]
    public void Plan_RunsStepsInOrder()
    {
        using var source = Ramp(8, 6);

        using var twice = source.Apply(new ProcessingPlan()
            .Add("brighten", OpCategory.Point, AddBrightness(0.1f))
            .Add("brighten again", OpCategory.Point, AddBrightness(0.1f)));

        using var once = source.Apply(new ProcessingPlan()
            .Add("brighten more", OpCategory.Point, AddBrightness(0.2f)));

        var a = twice.WritablePixels;
        var b = once.WritablePixels;
        for (var i = 0; i < a.Length; i++)
            Math.Abs(a[i] - b[i]).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Plan_BandedRunMatchesTheWholeRun()
    {
        using var source = Ramp(9, 17);

        using var whole = source.Apply(new ProcessingPlan { BandRows = 64 }
            .Add("v3", OpCategory.Neighborhood, VerticalThree, haloY: 1));

        using var banded = source.Apply(new ProcessingPlan { BandRows = 2 }
            .Add("v3", OpCategory.Neighborhood, VerticalThree, haloY: 1));

        banded.WritablePixels.ToArray().ShouldBe(whole.WritablePixels.ToArray());
    }

    [Fact]
    public void Plan_ReportsTheSourceRectWithHaloClampedToTheImage()
    {
        using var source = Ramp(10, 10);
        // Bands run in parallel, so a kernel may be invoked from several workers at once: the recorder has to
        // be thread-safe and the assertions have to be order-independent. Order was never part of the
        // contract; which rects are produced is.
        var seen = new System.Collections.Concurrent.ConcurrentBag<PixelRect>();

        using var result = source.Apply(new ProcessingPlan { BandRows = 4 }
            .Add("record", OpCategory.Neighborhood, (sourceView, destination, destinationRect, sourceRect) =>
            {
                seen.Add(sourceRect);
                destination.Row(destinationRect.Y).Slice(destinationRect.X * 4, destinationRect.Width * 4)
                    .Clear();
            }, haloX: 2, haloY: 3));

        var ordered = seen.OrderBy(r => r.Y).ToList();
        ordered.Count.ShouldBe(3);
        ordered[0].ShouldBe(new PixelRect(0, 0, 10, 7));    // first band: halo clamped at the top
        ordered[1].ShouldBe(new PixelRect(0, 1, 10, 9));    // middle band reaches both ways, bottom clamped
        ordered[2].ShouldBe(new PixelRect(0, 5, 10, 5));    // last band: halo clamped at the bottom
    }

    [Fact]
    public void Plan_RunsGlobalStepsOnce()
    {
        using var source = Ramp(8, 8);
        var calls = 0;

        using var result = source.Apply(new ProcessingPlan { BandRows = 4 }
            .Add("histogram", OpCategory.Global, (sourceView, destination, destinationRect, _) =>
            {
                calls++;
                sourceView.Span.CopyTo(destination.Span);
            }));

        calls.ShouldBe(1);
    }

    [Fact]
    public void Plan_EmptyIsACopy()
    {
        using var source = Ramp(5, 5);
        using var result = source.Apply(new ProcessingPlan());

        result.WritablePixels.ToArray().ShouldBe(source.WritablePixels.ToArray());
    }

    [Fact]
    public void Plan_RejectsGeometricStepsAndBadHalos()
    {
        var plan = new ProcessingPlan();

        Should.Throw<NotSupportedException>(() =>
            plan.Add("rotate", OpCategory.Geometric, (_, _, _, _) => { }));

        Should.Throw<ArgumentOutOfRangeException>(() =>
            plan.Add("neg", OpCategory.Neighborhood, (_, _, _, _) => { }, haloX: -1));

        Should.Throw<ArgumentException>(() =>
            plan.Add(string.Empty, OpCategory.Point, (_, _, _, _) => { }));
    }

    [Fact]
    public void PlanSteps_PointMatchesTheDirectOperator()
    {
        using var source = Ramp(9, 7);

        using var planned = source.Apply(new ProcessingPlan { BandRows = 3 }
            .Add("brightness", OpCategory.Point, PlanSteps.Point(view => PointOps.Brightness(view, 0.1f))));

        using var direct = PointOps.Brightness(source[0].Pixels.View, 0.1f);

        AnalysisOps.Diff(planned[0].Pixels.View, direct.View).MaxDifference.ShouldBe(0f);
    }

    [Fact]
    public void PlanSteps_NeighborhoodMatchesTheDirectBlurAcrossBandBoundaries()
    {
        using var source = Ramp(11, 13);
        const int Halo = 3;

        using var planned = source.Apply(new ProcessingPlan { BandRows = 3 }
            .Add("blur", OpCategory.Neighborhood,
                PlanSteps.Neighborhood(view => FilterOps.GaussianBlur(view, 1f), Halo, Halo), Halo, Halo));

        using var direct = FilterOps.GaussianBlur(source[0].Pixels.View, 1f);

        AnalysisOps.Diff(planned[0].Pixels.View, direct.View).MaxDifference.ShouldBe(0f);
    }

    [Fact]
    public void PlanSteps_GlobalMatchesTheDirectOperator()
    {
        using var source = Ramp(8, 8);

        using var planned = source.Apply(new ProcessingPlan { BandRows = 2 }
            .Add("equalize", OpCategory.Global, PlanSteps.Global(view => HistogramOps.Equalize(view))));

        using var direct = HistogramOps.Equalize(source[0].Pixels.View);

        AnalysisOps.Diff(planned[0].Pixels.View, direct.View).MaxDifference.ShouldBe(0f);
    }

    [Fact]
    public void Plan_RejectsNonPositiveBandRows()
    {
        using var source = Ramp(4, 4);
        Should.Throw<ArgumentOutOfRangeException>(() =>
            source.Apply(new ProcessingPlan { BandRows = 0 }));
    }
}
