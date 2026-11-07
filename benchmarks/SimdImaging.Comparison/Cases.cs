using Sdcb.SimdCodecs.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace SimdImaging.Comparison;

/// <summary>One operation expressed twice, against the same input bytes and with the same output shape.
/// Both sides produce a NEW image (ImageSharp via Clone(ctx => ...)), so the API shape is comparable:
/// neither side gets a free in-place win, and the caller owns the result.</summary>
internal sealed record Case(
    string Id,
    string Family,
    string Description,
    Func<OurImage, OurImage> Ours,
    Func<TheirImage, TheirImage> Theirs,
    long OutputPixels);

internal static class Cases
{
    public static List<Case> Build(int width, int height)
    {
        var dw = Math.Max(1, width / 2);
        var dh = Math.Max(1, height / 2);
        var pixels = (long)dw * dh;
        var full = (long)width * height;

        return
        [
            new Case("resize-bicubic", "GEO-5", $"Resize {width}x{height} -> {dw}x{dh}, bicubic",
                i => i.Resize(dw, dh, Resampler.Bicubic),
                i => i.Clone(x => x.Resize(dw, dh, KnownResamplers.Bicubic)), pixels),

            new Case("resize-lanczos3", "GEO-5", $"Resize {width}x{height} -> {dw}x{dh}, Lanczos3",
                i => i.Resize(dw, dh, Resampler.Lanczos3),
                i => i.Clone(x => x.Resize(dw, dh, KnownResamplers.Lanczos3)), pixels),

            // Added after the resize pass and the byte-domain work: two more resamplers, because a single
            // kernel says nothing about the other three, and the two remaining pure permutations, which are
            // the cleanest cases in the table (a permutation either matches or it does not).
            new Case("resize-nearest", "GEO-5", $"Resize {width}x{height} -> {dw}x{dh}, nearest",
                i => i.Resize(dw, dh, Resampler.Nearest),
                i => i.Clone(x => x.Resize(dw, dh, KnownResamplers.NearestNeighbor)), pixels),

            new Case("resize-bilinear", "GEO-5", $"Resize {width}x{height} -> {dw}x{dh}, bilinear",
                i => i.Resize(dw, dh, Resampler.Bilinear),
                i => i.Clone(x => x.Resize(dw, dh, KnownResamplers.Triangle)), pixels),

            new Case("crop", "GEO-1", $"Crop to {dw}x{dh}",
                i => i.Crop(new PixelRect(0, 0, dw, dh)),
                i => i.Clone(x => x.Crop(new Rectangle(0, 0, dw, dh))), pixels),

            new Case("rotate90", "GEO-9", "Rotate 90 degrees",
                i => i.Rotate90(clockwise: true),
                i => i.Clone(x => x.Rotate(RotateMode.Rotate90)), full),

            new Case("flip-horizontal", "GEO-10", "Horizontal flip",
                i => i.FlipHorizontal(),
                i => i.Clone(x => x.Flip(FlipMode.Horizontal)), full),

            new Case("rotate180", "GEO-9", "Rotate 180 degrees",
                i => i.Rotate180(),
                i => i.Clone(x => x.Rotate(RotateMode.Rotate180)), full),

            new Case("flip-vertical", "GEO-10", "Vertical flip",
                i => i.FlipVertical(),
                i => i.Clone(x => x.Flip(FlipMode.Vertical)), full),

            new Case("gaussian-blur", "FLT-2", "Gaussian blur, sigma 3",
                i => i.GaussianBlur(3f),
                i => i.Clone(x => x.GaussianBlur(3f)), full),

            new Case("box-blur", "FLT-3", "Box blur, radius 3",
                i => i.BoxBlur(3),
                i => i.Clone(x => x.BoxBlur(3)), full),

            new Case("grayscale", "COL-13", "Grayscale (BT.601)",
                i => i.Grayscale(bt709: false),
                i => i.Clone(x => x.Grayscale(GrayscaleMode.Bt601)), full),

            new Case("invert", "COL-7", "Invert colour channels",
                i => i.Invert(),
                i => i.Clone(x => x.Invert()), full),

            new Case("brightness", "COL-1", "Brightness +0.2",
                i => i.Brightness(0.2f),
                i => i.Clone(x => x.Brightness(0.2f)), full),

            new Case("contrast", "COL-2", "Contrast 1.2",
                i => i.Contrast(1.2f),
                i => i.Clone(x => x.Contrast(1.2f)), full),

            // Added after the histogram pass was rewritten: it used to call Histogram() three times, each
            // converting every row to float and back, on one thread. The lab showed 36-45 ms against
            // ImageSharp's 8-9 ms; this case is what turns that observation into an auditable number.
            new Case("equalize", "HIS-1", "Histogram equalization",
                i => i.EqualizeHistogram(),
                i => i.Clone(x => x.HistogramEqualization()), full),

            new Case("sepia", "COL-17", "Sepia, full strength",
                i => i.Sepia(1f),
                i => i.Clone(x => x.Sepia()), full),
        ];
    }
}
