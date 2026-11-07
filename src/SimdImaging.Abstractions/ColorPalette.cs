namespace SimdImaging.Abstractions;

/// <summary>
/// An ordered set of colours an image can be reduced to — the palette of an indexed format. Immutable: the
/// array is copied in and never handed out writable.
/// </summary>
public sealed class ColorPalette
{
    private readonly RgbaColor[] _colors;

    /// <summary>Copies <paramref name="colors"/> into a new palette.</summary>
    /// <exception cref="ArgumentException"><paramref name="colors"/> is empty or longer than 256.</exception>
    public ColorPalette(ReadOnlySpan<RgbaColor> colors)
    {
        if (colors.IsEmpty)
            throw new ArgumentException("A palette needs at least one colour.", nameof(colors));
        if (colors.Length > 256)
            throw new ArgumentException("A palette holds at most 256 colours.", nameof(colors));
        _colors = colors.ToArray();
    }

    /// <summary>The colours, in palette order.</summary>
    public ReadOnlySpan<RgbaColor> Colors => _colors;

    /// <summary>Number of colours.</summary>
    public int Count => _colors.Length;

    /// <summary>One colour.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the palette.</exception>
    public RgbaColor this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_colors.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Palette index must be 0..{_colors.Length - 1}.");
            return _colors[index];
        }
    }
}
