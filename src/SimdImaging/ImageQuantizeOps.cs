using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Palette reduction.</summary>
public sealed partial class Image
{
    /// <summary>Reduces the image to at most <paramref name="maxColors"/> colours.</summary>
    /// <param name="maxColors">2..256.</param>
    /// <param name="quantizer">Median cut (default), Wu or octree.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxColors"/> is outside 2..256.</exception>
    public Image Quantize(int maxColors, Quantizer quantizer = Quantizer.MedianCut, IPixelPool? pool = null)
    {
        var palette = BuildPaletteCore(maxColors, quantizer);
        return ReplaceBuffer(PaletteQuantizer.Apply(this[0].Pixels.View, palette, pool));
    }

    /// <summary>
    /// Reduces the image to at most <paramref name="maxColors"/> colours in one step: builds the palette with
    /// <paramref name="quantizer"/> and maps onto it with <paramref name="kernel"/> error diffusion.
    /// </summary>
    /// <param name="maxColors">2..256.</param>
    /// <param name="quantizer">How the palette is built.</param>
    /// <param name="kernel">Diffusion kernel, or <see cref="DitherKernel.None"/> to map each pixel alone.</param>
    /// <param name="maxError">Largest error carried to a neighbour, in sample units.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxColors"/> is outside 2..256, or
    /// <paramref name="maxError"/> is negative.</exception>
    /// <exception cref="NotSupportedException">An ordered (Bayer) kernel: a palette has no uniform step.</exception>
    public Image ReduceColors(int maxColors, Quantizer quantizer = Quantizer.MedianCut,
        DitherKernel kernel = DitherKernel.FloydSteinberg, float maxError = 16f / 255f, IPixelPool? pool = null)
    {
        var palette = BuildPaletteCore(maxColors, quantizer);
        return ReplaceBuffer(PaletteQuantizer.Dither(this[0].Pixels.View, palette, kernel, maxError, pool));
    }

    /// <summary>Maps the image onto a caller-supplied palette.</summary>
    public Image MapToPalette(ColorPalette palette, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return ReplaceBuffer(PaletteQuantizer.Apply(this[0].Pixels.View, palette, pool));
    }

    /// <summary>Maps to a palette with error diffusion.</summary>
    /// <param name="palette">The target palette.</param>
    /// <param name="kernel">Diffusion kernel, or <see cref="DitherKernel.None"/> to map each pixel alone.</param>
    /// <param name="maxError">Largest error carried to a neighbour, in sample units.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="NotSupportedException">An ordered (Bayer) kernel: a palette has no uniform step.</exception>
    public Image DitherToPalette(ColorPalette palette, DitherKernel kernel = DitherKernel.FloydSteinberg,
        float maxError = 16f / 255f, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return ReplaceBuffer(PaletteQuantizer.Dither(this[0].Pixels.View, palette, kernel, maxError, pool));
    }

    /// <summary>Reduces each colour channel to <paramref name="bits"/> bits, with an ordered or diffused dither.</summary>
    /// <param name="bits">Bits per channel, 1..8.</param>
    /// <param name="kernel">The dither, or <see cref="DitherKernel.None"/> to round.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> is outside 1..8.</exception>
    public Image Dither(int bits, DitherKernel kernel = DitherKernel.FloydSteinberg, IPixelPool? pool = null) =>
        ReplaceBuffer(DitherOps.Dither(this[0].Pixels.View, bits, kernel, pool));

    /// <summary>Maps to the fixed 216-colour web-safe palette.</summary>
    public Image QuantizeWebSafe(IPixelPool? pool = null) =>
        ReplaceBuffer(PaletteQuantizer.Apply(this[0].Pixels.View, PaletteQuantizer.WebSafe, pool));

    /// <summary>Builds a palette from this image without applying it.</summary>
    public ColorPalette BuildPalette(int maxColors, Quantizer quantizer = Quantizer.MedianCut) =>
        BuildPaletteCore(maxColors, quantizer);

    private ColorPalette BuildPaletteCore(int maxColors, Quantizer quantizer) => quantizer switch
    {
        Quantizer.MedianCut => PaletteQuantizer.Build(this[0].Pixels.View, maxColors),
        Quantizer.Wu => WuQuantizer.Build(this[0].Pixels.View, maxColors),
        Quantizer.Octree => OctreeQuantizer.Build(this[0].Pixels.View, maxColors),
        _ => throw new ArgumentOutOfRangeException(nameof(quantizer), quantizer, "Unknown quantizer."),
    };
}
