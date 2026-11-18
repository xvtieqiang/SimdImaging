using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

// Prints the tier first so run.sh can prove the AOT build actually kept its vector paths:
// ILC folds IsHardwareAccelerated to false against a lower baseline, which would make a green
// run meaningless.
Console.WriteLine($"tier avx512={Vector512.IsHardwareAccelerated} avx2={Vector256.IsHardwareAccelerated} v128={Vector128.IsHardwareAccelerated} runtime={RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"dynamic-code-supported={RuntimeFeature.IsDynamicCodeSupported}");

const int Width = 64;
const int Height = 48;

using var image = Image.Create(Width, Height, WorkingFormat.Rgba8);
var pixels = image.WritablePixels;
for (var i = 0; i < pixels.Length; i++)
    pixels[i] = (byte)((i * 37 + 11) & 0xFF);

var png = Image.Encode(image, ImageFormat.Png);
Console.WriteLine($"png.bytes={png.Length} sha={Hash(png)}");

using (var decoded = Image.Load(png))
{
    decoded.Width.ShouldBe(Width);
    Console.WriteLine($"png.pixels sha={Hash(decoded.WritablePixels)}");
}

var jpeg = Image.Encode(image, ImageFormat.Jpeg, new SaveOptions { JpegQuality = 90 });
Console.WriteLine($"jpeg.bytes={jpeg.Length} sha={Hash(jpeg)}");

using (var decoded = Image.Load(jpeg))
    Console.WriteLine($"jpeg.pixels sha={Hash(decoded.WritablePixels)}");

using var wide = Image.Create(32, 24, WorkingFormat.Rgba16);
var widePixels = MemoryMarshal.Cast<byte, ushort>(wide.WritablePixels);
for (var i = 0; i < widePixels.Length; i++)
    widePixels[i] = (ushort)(i * 1031 + 13);

var png16 = Image.Encode(wide, ImageFormat.Png);
Console.WriteLine($"png16.bytes={png16.Length} sha={Hash(png16)}");

using (var decoded = Image.Load(png16))
    Console.WriteLine($"png16.pixels sha={Hash(decoded.WritablePixels)}");

// Processing operators are in the smoke on purpose: float resampling must produce the same bytes
// under ILC as under the JIT, or "AOT output == JIT output" only covers the codec boundary.
using (var cropped = image.Pipe().Crop(new PixelRect(8, 6, 32, 24)).ToImage())
    Console.WriteLine($"crop sha={Hash(cropped.WritablePixels)}");

using (var resized = image.Pipe().Resize(31, 19, Resampler.Bicubic).ToImage())
    Console.WriteLine($"resize.bicubic sha={Hash(resized.WritablePixels)}");

// Nearest's Rgba8 fast path is a 32-bit bit-copy; hash its output directly so the JIT/AOT/tier
// comparison actually covers the operator whose inner loop was optimised (AGENTS.md: keep the
// changed operator in the hash list, or a green smoke verifies nothing about it).
using (var resized = image.Pipe().Resize(23, 17, Resampler.Nearest).ToImage())
    Console.WriteLine($"resize.nearest sha={Hash(resized.WritablePixels)}");

using (var resized = image.Pipe().Resize(97, 71, Resampler.Lanczos3).ToImage())
    Console.WriteLine($"resize.lanczos3 sha={Hash(resized.WritablePixels)}");

using (var resized = wide.Pipe().Resize(13, 9, Resampler.Bilinear).ToImage())
    Console.WriteLine($"resize16 sha={Hash(resized.WritablePixels)}");

using (var rotated = image.Pipe().Rotate90().ToImage())
    Console.WriteLine($"rotate90 sha={Hash(rotated.WritablePixels)}");

using (var flipped = image.Pipe().FlipHorizontal().ToImage())
    Console.WriteLine($"flip sha={Hash(flipped.WritablePixels)}");

// 2026-11-12: Pixelate moved to an integer path and Rotate180 gained an AVX2 body. Both are hashed here or
// "AOT output == JIT output" would stop covering the two operators that changed most.
using (var pixelated = image.Pipe().Pixelate(5).ToImage())
    Console.WriteLine($"pixelate sha={Hash(pixelated.WritablePixels)}");

using (var flipped180 = image.Pipe().Rotate180().ToImage())
    Console.WriteLine($"rotate180 sha={Hash(flipped180.WritablePixels)}");

using (var padded = image.Pipe().Pad(3, new RgbaColor(1, 2, 3, 255)).ToImage())
    Console.WriteLine($"pad sha={Hash(padded.WritablePixels)}");

using (var filtered = image.Pipe().Grayscale().GaussianBlur(1.5f).Invert().ToImage())
    Console.WriteLine($"filter sha={Hash(filtered.WritablePixels)}");

using (var styled = image.Pipe().HueRotate(35f).Levels(0.1f, 0.9f, 1.2f).Sepia(0.6f).Emboss(0.8f).Normalize().ToImage())
    Console.WriteLine($"style sha={Hash(styled.WritablePixels)}");

using (var tone = image.Pipe().Vibrance(0.4f).Temperature(0.3f).Tint(new RgbaColor(255, 200, 150, 255), 0.2f).Sharpen(0.8f, 1f).ToImage())
    Console.WriteLine($"tone sha={Hash(tone.WritablePixels)}");

using (var variant = image.Pipe().ChannelSwap(2, 1, 0).MotionBlur(5, 30f).BokehBlur(2).ToImage())
    Console.WriteLine($"variant sha={Hash(variant.WritablePixels)}");

using (var graded = image.Pipe().Curves(new[] { new CurvePoint(0f, 0f), new CurvePoint(0.5f, 0.6f), new CurvePoint(1f, 1f) }).Opacity(0.8f).ToImage())
    Console.WriteLine($"curves sha={Hash(graded.WritablePixels)}");

using (var rotated = image.Pipe().Rotate(17f, new RgbaColor(0, 0, 0, 255)).ToImage())
    Console.WriteLine($"rotate17 sha={Hash(rotated.WritablePixels)}");

using (var skewed = image.Pipe().Perspective(new[] { 1f, 0.2f, 0f, 0f, 1f, 0f, 0.002f, 0f }, 64, 48, new RgbaColor(0, 0, 0, 255)).ToImage())
    Console.WriteLine($"perspective sha={Hash(skewed.WritablePixels)}");

// The delegate warp and the point-sample chain were absent from this list. Sampling.cs is the
// precision-critical path, so a green smoke that never hashed it proved nothing about it.
using (var warped = image.Pipe().Warp(64, 48,
    (float x, float y, out float sx, out float sy) => { sx = x; sy = y; },
    Resampler.Bilinear, new RgbaColor(0, 0, 0, 255)).ToImage())
    Console.WriteLine($"warp sha={Hash(warped.WritablePixels)}");

var sampled = image.SamplePixel(3.25f, 5.5f, Resampler.Bilinear);
Span<byte> sampleBytes = stackalloc byte[4];
sampleBytes[0] = sampled.R;
sampleBytes[1] = sampled.G;
sampleBytes[2] = sampled.B;
sampleBytes[3] = sampled.A;
Console.WriteLine($"samplepixel sha={Hash(sampleBytes)}");

// M8 explicit-contract path: linear-light domain, premultiplied alpha and a 5-kernel warp all
// have to produce the same bytes under ILC as under the JIT.
using (var linearWarp = image.Pipe().WarpWithOptions(64, 48,
    (float x, float y, out float sx, out float sy) => { sx = x; sy = y; },
    Resampler.Bicubic, new SimdImaging.Abstractions.SampleOptions
    {
        Domain = SimdImaging.Abstractions.SampleDomain.LinearLight,
    }).ToImage())
    Console.WriteLine($"warp.linear sha={Hash(linearWarp.WritablePixels)}");

using (var premulWarp = image.Pipe().WarpWithOptions(64, 48,
    (float x, float y, out float sx, out float sy) => { sx = x - 0.25f; sy = y; },
    Resampler.Lanczos3, new SimdImaging.Abstractions.SampleOptions
    {
        Alpha = SimdImaging.Abstractions.AlphaMode.Premultiplied,
        Edge = SimdImaging.Abstractions.EdgeMode.Background,
    }).ToImage())
    Console.WriteLine($"warp.premul sha={Hash(premulWarp.WritablePixels)}");

// M8 second wave: the Mapping/Resize WithOptions entry points (explicit contracts across the
// remaining geometry chains) must match the JIT byte for byte under ILC too.
using (var resizeOptions = image.Pipe().ResizeWithOptions(32, 24, Resampler.Bicubic,
    new SimdImaging.Abstractions.SampleOptions
    {
        Domain = SimdImaging.Abstractions.SampleDomain.LinearLight,
    }).ToImage())
    Console.WriteLine($"resize.options sha={Hash(resizeOptions.WritablePixels)}");

using (var rotateOptions = image.Pipe().RotateWithOptions(17f, Resampler.Bicubic,
    new SimdImaging.Abstractions.SampleOptions
    {
        Alpha = SimdImaging.Abstractions.AlphaMode.Premultiplied,
    }, new RgbaColor(0, 0, 0, 255)).ToImage())
    Console.WriteLine($"rotate.options sha={Hash(rotateOptions.WritablePixels)}");

using (var perspectiveOptions = image.Pipe().PerspectiveWithOptions(
    new[] { 1f, 0.2f, 0f, 0f, 1f, 0f, 0.002f, 0f }, 64, 48, Resampler.Lanczos3,
    new SimdImaging.Abstractions.SampleOptions
    {
        Edge = SimdImaging.Abstractions.EdgeMode.Background,
    }, new RgbaColor(0, 0, 0, 255)).ToImage())
    Console.WriteLine($"perspective.options sha={Hash(perspectiveOptions.WritablePixels)}");

// M9 EWA: the elliptical footprint path must produce the same bytes under ILC as under the JIT.
using (var ewaRotate = image.Pipe().RotateWithOptions(17f, Resampler.Bicubic,
    new SimdImaging.Abstractions.SampleOptions
    {
        AaMode = SimdImaging.Abstractions.SampleAaMode.Elliptical,
    }, new RgbaColor(0, 0, 0, 255)).ToImage())
    Console.WriteLine($"ewa.rotate sha={Hash(ewaRotate.WritablePixels)}");

// M10 fusion: a banded chain through ProcessingPlan must produce the same bytes under ILC as under
// the JIT. The equality to the step-by-step executor is covered by FusedExecutorEquivalenceTests;
// this entry is what proves the fused window recursion survives AOT.
// The shape a caller writes: three steps, no planning vocabulary anywhere in sight.
using (var planned = image.Pipe().BandRows(8).Brightness(0.05f).GaussianBlur(1f).Invert().ToImage())
    Console.WriteLine($"plan.fused sha={Hash(planned.WritablePixels)}");

// M11 photo algorithms: non-local means (integer distances plus Math.Exp weights) and the local
// Laplacian pyramid (Math.Pow remap table plus the Burt-Adcock kernels) must both match under ILC.
using (var nlm = image.Pipe().NonLocalMeans(2, 1, 0.15f).ToImage())
    Console.WriteLine($"nlm sha={Hash(nlm.WritablePixels)}");

using (var llf = image.Pipe().LocalLaplacian(levels: 3, samples: 8, alpha: 0.3f).ToImage())
    Console.WriteLine($"ll sha={Hash(llf.WritablePixels)}");

// Morphology with a large window (van Herk, not the 3x3 specialisation) and the Laplacian's signed kernel.
using (var morph = image.Pipe().Morph(3, Morphology.Erode).ToImage())
    Console.WriteLine($"morph sha={Hash(morph.WritablePixels)}");

// The combinations exist only by composition, so a trimming change that drops the second primitive would show up
// here and nowhere else.
using (var open = image.Pipe().Morph(2, Morphology.Open).ToImage())
    Console.WriteLine($"morph.open sha={Hash(open.WritablePixels)}");

using (var topHat = image.Pipe().Morph(2, Morphology.TopHat).ToImage())
    Console.WriteLine($"morph.tophat sha={Hash(topHat.WritablePixels)}");

using (var lap = image.Pipe().Laplacian().ToImage())
    Console.WriteLine($"laplacian sha={Hash(lap.WritablePixels)}");

var Box5 = new float[25];
for (var i = 0; i < Box5.Length; i++)
    Box5[i] = 1f / 25f;

using (var chain = image.Pipe().PointChain(PointOps.ContrastRow(1.2f), PointOps.InvertRow()).ToImage())
    Console.WriteLine($"pointchain sha={Hash(chain.WritablePixels)}");

// The general convolution and the wide median: both are new code paths (a border-mapped kernel and a sliding
// histogram) and neither is exercised by the 3x3 specialisations above.
using (var conv = image.Pipe().Convolve(Box5, 2, BorderMode.Mirror).ToImage())
    Console.WriteLine($"convolve sha={Hash(conv.WritablePixels)}");

using (var med = image.Pipe().Median(2).ToImage())
    Console.WriteLine($"median5 sha={Hash(med.WritablePixels)}");

// Dehazing and exposure fusion: both go through code paths the other operators do not - a guided filter driven by
// a luma guide, and a Laplacian pyramid whose levels are rebuilt per channel - so neither is covered by the
// operators above. Without a hash here, "AOT is a first-class citizen" would not be true of them.
using (var dh = image.Pipe().Dehaze(patchRadius: 4, omega: 0.95f, tMin: 0.1f, refineRadius: 6).ToImage())
    Console.WriteLine($"dehaze sha={Hash(dh.WritablePixels)}");

using (var fuse = Image.FuseExposures([image, image]))
    Console.WriteLine($"fusion sha={Hash(fuse.WritablePixels)}");

// M12: deconvolution (separable and sparse kernels through PlanarConvolve) and seam carving (integer DP
// path selection, then pixel moves through the lossless transpose for the horizontal axis).
using (var rl = image.Pipe().RichardsonLucy(1.2f, 3).ToImage())
    Console.WriteLine($"rl sha={Hash(rl.WritablePixels)}");

using (var rlm = image.Pipe().RichardsonLucyMotion(7, 30f, 2).ToImage())
    Console.WriteLine($"rl.motion sha={Hash(rlm.WritablePixels)}");

using (var seam = image.Pipe().RemoveSeams(SeamAxis.Vertical, 2).ToImage())
    Console.WriteLine($"seam sha={Hash(seam.WritablePixels)}");

// Insertion shares FindSeam with removal but is the only user of the blocked mask, and it had no hash entry
// at all - so nothing covered the path the mask change touches.
// Thirty-two-bit float takes the sixteen-byte transpose arm, which the Rgba8 image above cannot reach.
using (var floatImage = Image.Create(37, 23, WorkingFormat.Rgba32F))
{
    var samples = MemoryMarshal.Cast<byte, float>(floatImage.WritablePixels);
    for (var i = 0; i < samples.Length; i++)
        samples[i] = (i * 7919 % 1000) / 1000f;

    using var turned = floatImage.Pipe().Rotate90().ToImage();
    Console.WriteLine($"rotate90.f32 sha={Hash(turned.WritablePixels)}");
}

using (var inserted = image.Pipe().InsertSeams(SeamAxis.Vertical, 3).ToImage())
    Console.WriteLine($"seam.insert sha={Hash(inserted.WritablePixels)}");

// ANL-9: the exact squared EDT (float parabolas on a serial envelope sweep) goes through the facade here,
// which also proves the Rgba32F result is not relabelled as the 8-bit input.
using (var mask = Image.Create(Width, Height, WorkingFormat.Rgba8))
{
    var maskPixels = mask.WritablePixels;
    for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var offset = (y * Width + x) * 4;
            maskPixels[offset + 3] = ((x / 7) + (y / 5)) % 3 == 0 ? (byte)255 : (byte)0;
        }

    using var distance = mask.Pipe().DistanceTransform().ToImage();
    Console.WriteLine($"dt sha={Hash(distance.WritablePixels)}");
}

// M13: the ICC LUT path (integer curves, a tetrahedral CLUT, a float PCS seam) and the hdrgm gain-map
// reconstruction (gamma and exp2 over a bilinearly upsampled map). Both produce pixels, so both are
// hashed — otherwise "AOT output == JIT output" would stop covering the newest arithmetic.
var lutProfile = BuildLut16Profile(3);
using (var iccLut = image.Pipe().IccTransform(lutProfile).ToImage())
    Console.WriteLine($"icc.lut sha={Hash(iccLut.WritablePixels)}");

using (var gainMapImage = Image.Create(16, 12, WorkingFormat.Rgba8))
{
    var gainPixels = gainMapImage.WritablePixels;
    for (var i = 0; i < gainPixels.Length; i++)
        gainPixels[i] = (byte)((i * 29 + 3) & 0xFF);

    // The pair owns both images, so it gets a copy: `image` is hashed again below.
    using var pair = new GainMappedImage(image.Clone(), gainMapImage, GainMapParameters.Default);
    using var hdr = pair.ToHdr(4f);
    Console.WriteLine($"gainmap.hdr sha={Hash(hdr.WritablePixels)}");
}

using (var dithered = image.Pipe().DitherToPalette(PaletteQuantizer.WebSafe).ToImage())
    Console.WriteLine($"dither sha={Hash(dithered.WritablePixels)}");

using (var gradient = Image.Create(64, 48, WorkingFormat.Rgba8))
{
    gradient.LinearGradient(new PixelRect(0, 0, 64, 24), new RgbaColor(0, 0, 0, 255), new RgbaColor(255, 120, 0, 255), 30f);
    gradient.RadialGradient(new PixelRect(0, 24, 64, 24), 32f, 36f, 24f, new RgbaColor(255, 255, 255, 255), new RgbaColor(0, 0, 128, 255));
    Console.WriteLine($"gradient sha={Hash(gradient.WritablePixels)}");
}

using (var edges = image.Pipe().Median3x3().Sobel().ToImage())
    Console.WriteLine($"median.sobel sha={Hash(edges.WritablePixels)}");

// Sixteen-bit point operator: it now goes through a 4 x 65536 table instead of the float row path, so its
// bytes have to survive both the tier matrix and ILC.
using (var widePoint = Image.Create(160, 120, WorkingFormat.Rgba16))
{
    // 19200 pixels, above PointOps.Lut16PixelFloor, so this hash covers the 4 x 65536 table rather than the
    // float path the small `wide` image would take.
    var samples = MemoryMarshal.Cast<byte, ushort>(widePoint.WritablePixels);
    for (var i = 0; i < samples.Length; i++)
        samples[i] = (ushort)((i * 7919 + 3) & 0xFFFF);

    using var inverted16 = widePoint.Pipe().Invert().ToImage();
    Console.WriteLine($"invert16 sha={Hash(inverted16.WritablePixels)}");
}

using (var quantized = image.Pipe().Quantize(8).ToImage())
    Console.WriteLine($"quantize sha={Hash(quantized.WritablePixels)}");

using (var equalized = image.Pipe().EqualizeHistogram().ToImage())
    Console.WriteLine($"equalize sha={Hash(equalized.WritablePixels)}");

using (var wu = image.Pipe().Quantize(8, Quantizer.Wu).ToImage())
    Console.WriteLine($"quantize.wu sha={Hash(wu.WritablePixels)}");

using (var opened = image.Pipe().Open3x3().ToImage())
    Console.WriteLine($"morph sha={Hash(opened.WritablePixels)}");

using (var bilateral = image.Pipe().GaussianBlur(2f).Bilateral(2, 0f, 0.2f).ToImage())
    Console.WriteLine($"bilateral sha={Hash(bilateral.WritablePixels)}");

// Oil paint is the sliding-window histogram: the second call uses a window wider than the row, which is the
// clamped-slide path rather than the interior one, and the two are different code.
// Self-guided has its own inner loop (it must keep its bytes), and it had no hash entry at all until the
// cross path was reworked - only the cross kernel was covered.
using (var selfGuided = image.Pipe().GuidedFilter(3, 1e-4f).ToImage())
    Console.WriteLine($"guided sha={Hash(selfGuided.WritablePixels)}");

// Octree had no hash at all: the other two quantisers were covered, this one never was.
using (var octree = image.Pipe().Quantize(8, Quantizer.Octree).ToImage())
    Console.WriteLine($"quantize.octree sha={Hash(octree.WritablePixels)}");

using (var painted = image.Pipe().OilPaint(2, 20).ToImage())
    Console.WriteLine($"oilpaint sha={Hash(painted.WritablePixels)}");

using (var paintedWide = image.Pipe().OilPaint(40, 5).ToImage())
    Console.WriteLine($"oilpaint.clamp sha={Hash(paintedWide.WritablePixels)}");

// Cross-guided filter: cov(I, G) and var(G) from a guide that is not the source, so the second box-pass
// layout is hashed too. The self-guided path is already covered through the one-argument call.
using (var guide = image.Pipe().GaussianBlur(1f).ToImage())
using (var crossGuided = image.Pipe().GuidedFilter(guide, 2, 1e-4f).ToImage())
    Console.WriteLine($"guided.cross sha={Hash(crossGuided.WritablePixels)}");

using (var canvas = Image.Create(64, 48, WorkingFormat.Rgba8))
{
    canvas.Fill(new RgbaColor(7, 8, 9, 255));
    canvas.FillRect(new PixelRect(4, 3, 20, 10), new RgbaColor(200, 100, 50, 128));
    canvas.DrawRect(new PixelRect(2, 2, 40, 30), new RgbaColor(0, 255, 0, 255), 2);
    canvas.DrawLine(0, 47, 63, 0, new RgbaColor(255, 255, 255, 255));
    canvas.FillEllipse(new PixelRect(10, 10, 20, 14), new RgbaColor(90, 20, 200, 255));
    canvas.FillPolygon(new[] { new PixelPoint(40, 30), new PixelPoint(60, 34), new PixelPoint(44, 46) },
        new RgbaColor(250, 240, 10, 255));
    Console.WriteLine($"draw sha={Hash(canvas.WritablePixels)}");
}

using (var overlay = image.Pipe().Crop(new PixelRect(2, 3, 9, 7)).Resize(9, 7, Resampler.Nearest).ToImage())
using (var baseImage = image.Pipe().Resize(64, 48, Resampler.Nearest).ToImage())
{
    using (var composed = baseImage.Pipe().Composite(overlay, 5, 5).ToImage())
        Console.WriteLine($"composite sha={Hash(composed.WritablePixels)}");

    using (var xored = baseImage.Pipe().Composite(overlay, 5, 5, PorterDuff.Xor).ToImage())
        Console.WriteLine($"composite.xor sha={Hash(xored.WritablePixels)}");

    using (var multiplied = baseImage.Pipe().Blend(overlay, 5, 5, BlendMode.Multiply).ToImage())
        Console.WriteLine($"blend.multiply sha={Hash(multiplied.WritablePixels)}");
}

static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..16];


// A minimal lut16 profile with an identity A2B0 CLUT, hand-written here so the smoke needs no fixture:
// what it exercises is the parser and the tetrahedral evaluation, not a particular real-world profile.
static byte[] BuildLut16Profile(byte grid)
{
    const int headerSize = 128 + 16;
    var samples = grid * grid * grid * 3;
    var tagSize = 48 + 3 * 256 * 2 + samples * 2 + 3 * 256 * 2;
    var profile = new byte[headerSize + tagSize];

    BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(0, 4), profile.Length);
    BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8, 4), 0x02300000);
    "scnr"u8.CopyTo(profile.AsSpan(12));
    "RGB "u8.CopyTo(profile.AsSpan(16));
    "XYZ "u8.CopyTo(profile.AsSpan(20));
    "acsp"u8.CopyTo(profile.AsSpan(36));
    BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(128, 4), 1);
    "A2B0"u8.CopyTo(profile.AsSpan(132));
    BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(136, 4), headerSize);
    BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(140, 4), tagSize);

    var tag = profile.AsSpan(headerSize);
    "mft2"u8.CopyTo(tag);
    tag[8] = 3;
    tag[9] = 3;
    tag[10] = grid;
    for (var i = 0; i < 9; i++)
        BinaryPrimitives.WriteInt32BigEndian(tag.Slice(12 + i * 4, 4), i % 4 == 0 ? 65536 : 0);

    var offset = 48;
    for (var channel = 0; channel < 3; channel++)
        for (var i = 0; i < 256; i++, offset += 2)
            BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(i * 257));

    for (var r = 0; r < grid; r++)
        for (var g = 0; g < grid; g++)
            for (var b = 0; b < grid; b++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(r * 32768 / (grid - 1)));
                offset += 2;
                BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(g * 32768 / (grid - 1)));
                offset += 2;
                BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(b * 32768 / (grid - 1)));
                offset += 2;
            }

    for (var channel = 0; channel < 3; channel++)
        for (var i = 0; i < 256; i++, offset += 2)
            BinaryPrimitives.WriteUInt16BigEndian(tag.Slice(offset, 2), (ushort)(i * 257));

    return profile;
}

internal static class SmokeAssert
{
    public static void ShouldBe(this int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
