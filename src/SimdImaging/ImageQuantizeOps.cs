using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Palette reduction.</summary>
public sealed partial class Image
{
    

    

    

    

    

    

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
