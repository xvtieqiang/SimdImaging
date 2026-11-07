using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Tests;

/// <summary>
/// Output contract: untrusted/caller-mistake float arguments must fail with ArgumentOutOfRangeException,
/// never silently produce a black or clamped image. Guards apply to the whole point-operator family,
/// not just the one operator the oracle comparison happened to exercise.
/// </summary>
public class FiniteContractTests
{
    [Fact]
    public void NonFinitePointOperatorArgumentsMustThrow()
    {
        using var image = Image.Create(8, 8, WorkingFormat.Rgba8);
        var view = image[0].Pixels.View;
        var nan = float.NaN;
        var inf = float.PositiveInfinity;
        var matrixWithNaN = new float[12];
        matrixWithNaN[5] = nan;

        var cases = new (string Name, Action Run)[]
        {
            ("Brightness NaN", () => { using var r = PointOps.Brightness(view, nan); }),
            ("Brightness +Inf", () => { using var r = PointOps.Brightness(view, inf); }),
            ("Lightness NaN", () => { using var r = PointOps.Lightness(view, nan); }),
            ("Contrast NaN", () => { using var r = PointOps.Contrast(view, nan); }),
            ("Contrast +Inf", () => { using var r = PointOps.Contrast(view, inf); }),
            ("Saturation NaN", () => { using var r = PointOps.Saturation(view, nan); }),
            ("HueRotate NaN", () => { using var r = PointOps.HueRotate(view, nan); }),
            ("Sepia NaN", () => { using var r = PointOps.Sepia(view, nan); }),
            ("Levels inputBlack NaN", () => { using var r = PointOps.Levels(view, nan, 1f, 1f); }),
            ("Levels gamma NaN", () => { using var r = PointOps.Levels(view, 0f, 1f, nan); }),
            ("Clamp low NaN", () => { using var r = PointOps.Clamp(view, nan, 1f); }),
            ("Vibrance NaN", () => { using var r = PointOps.Vibrance(view, nan); }),
            ("Temperature NaN", () => { using var r = PointOps.Temperature(view, nan); }),
            ("Tint NaN", () => { using var r = PointOps.Tint(view, new RgbaColor(1, 2, 3, 255), nan); }),
            ("BlackWhite NaN", () => { using var r = PointOps.BlackWhite(view, nan); }),
            ("ColorMatrix NaN element", () => { using var r = PointOps.ColorMatrix(view, matrixWithNaN); }),
            ("Gamma NaN", () => { using var r = PointOps.Gamma(view, nan); }),
            ("Threshold NaN", () => { using var r = PointOps.Threshold(view, nan); }),
            ("AlphaThreshold NaN", () => { using var r = PointOps.AlphaThreshold(view, nan); }),
            ("Opacity NaN", () => { using var r = PointOps.Opacity(view, nan); }),
            ("GaussianBlur NaN", () => { using var r = FilterOps.GaussianBlur(view, nan); }),
        };

        foreach (var (name, run) in cases)
            Should.Throw<ArgumentOutOfRangeException>(run, name);
    }
}
