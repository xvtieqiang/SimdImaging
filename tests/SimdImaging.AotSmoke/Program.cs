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
using (var cropped = image.Crop(new PixelRect(8, 6, 32, 24)))
    Console.WriteLine($"crop sha={Hash(cropped.WritablePixels)}");

using (var resized = image.Resize(31, 19, Resampler.Bicubic))
    Console.WriteLine($"resize.bicubic sha={Hash(resized.WritablePixels)}");

using (var resized = image.Resize(97, 71, Resampler.Lanczos3))
    Console.WriteLine($"resize.lanczos3 sha={Hash(resized.WritablePixels)}");

using (var resized = wide.Resize(13, 9, Resampler.Bilinear))
    Console.WriteLine($"resize16 sha={Hash(resized.WritablePixels)}");

using (var rotated = image.Rotate90())
    Console.WriteLine($"rotate90 sha={Hash(rotated.WritablePixels)}");

using (var flipped = image.FlipHorizontal())
    Console.WriteLine($"flip sha={Hash(flipped.WritablePixels)}");

using (var padded = image.Pad(3, new RgbaColor(1, 2, 3, 255)))
    Console.WriteLine($"pad sha={Hash(padded.WritablePixels)}");

using (var filtered = image.Grayscale().GaussianBlur(1.5f).Invert())
    Console.WriteLine($"filter sha={Hash(filtered.WritablePixels)}");

using (var styled = image.HueRotate(35f).Levels(0.1f, 0.9f, 1.2f).Sepia(0.6f).Emboss(0.8f).Normalize())
    Console.WriteLine($"style sha={Hash(styled.WritablePixels)}");

using (var tone = image.Vibrance(0.4f).Temperature(0.3f).Tint(new RgbaColor(255, 200, 150, 255), 0.2f).Sharpen(0.8f, 1f))
    Console.WriteLine($"tone sha={Hash(tone.WritablePixels)}");

using (var variant = image.ChannelSwap(2, 1, 0).MotionBlur(5, 30f).BokehBlur(2))
    Console.WriteLine($"variant sha={Hash(variant.WritablePixels)}");

using (var graded = image.Curves(new[] { new CurvePoint(0f, 0f), new CurvePoint(0.5f, 0.6f), new CurvePoint(1f, 1f) }).Opacity(0.8f))
    Console.WriteLine($"curves sha={Hash(graded.WritablePixels)}");

using (var rotated = image.Rotate(17f, new RgbaColor(0, 0, 0, 255)))
    Console.WriteLine($"rotate17 sha={Hash(rotated.WritablePixels)}");

using (var skewed = image.Perspective(new[] { 1f, 0.2f, 0f, 0f, 1f, 0f, 0.002f, 0f }, 64, 48, new RgbaColor(0, 0, 0, 255)))
    Console.WriteLine($"perspective sha={Hash(skewed.WritablePixels)}");

using (var dithered = image.DitherToPalette(PaletteQuantizer.WebSafe))
    Console.WriteLine($"dither sha={Hash(dithered.WritablePixels)}");

using (var gradient = Image.Create(64, 48, WorkingFormat.Rgba8))
{
    gradient.LinearGradient(new PixelRect(0, 0, 64, 24), new RgbaColor(0, 0, 0, 255), new RgbaColor(255, 120, 0, 255), 30f);
    gradient.RadialGradient(new PixelRect(0, 24, 64, 24), 32f, 36f, 24f, new RgbaColor(255, 255, 255, 255), new RgbaColor(0, 0, 128, 255));
    Console.WriteLine($"gradient sha={Hash(gradient.WritablePixels)}");
}

using (var edges = image.Median3x3().Sobel())
    Console.WriteLine($"median.sobel sha={Hash(edges.WritablePixels)}");

using (var quantized = image.Quantize(8))
    Console.WriteLine($"quantize sha={Hash(quantized.WritablePixels)}");

using (var equalized = image.EqualizeHistogram())
    Console.WriteLine($"equalize sha={Hash(equalized.WritablePixels)}");

using (var wu = image.Quantize(8, Quantizer.Wu))
    Console.WriteLine($"quantize.wu sha={Hash(wu.WritablePixels)}");

using (var opened = image.Open3x3())
    Console.WriteLine($"morph sha={Hash(opened.WritablePixels)}");

using (var bilateral = image.GaussianBlur(2f).Bilateral(2, 0f, 0.2f))
    Console.WriteLine($"bilateral sha={Hash(bilateral.WritablePixels)}");

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

using (var overlay = image.Crop(new PixelRect(2, 3, 9, 7)).Resize(9, 7, Resampler.Nearest))
using (var baseImage = image.Resize(64, 48, Resampler.Nearest))
{
    using (var composed = baseImage.Composite(overlay, 5, 5))
        Console.WriteLine($"composite sha={Hash(composed.WritablePixels)}");

    using (var xored = baseImage.Composite(overlay, 5, 5, PorterDuff.Xor))
        Console.WriteLine($"composite.xor sha={Hash(xored.WritablePixels)}");

    using (var multiplied = baseImage.Blend(overlay, 5, 5, BlendMode.Multiply))
        Console.WriteLine($"blend.multiply sha={Hash(multiplied.WritablePixels)}");
}

static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..16];

internal static class SmokeAssert
{
    public static void ShouldBe(this int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
