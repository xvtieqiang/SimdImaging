namespace SimdImaging.Abstractions;

/// <summary>An 8-bit RGBA colour, for the API surface (a pad background, a clear colour). Internal pipelines
/// work in the image's own depth, so this scales to 16-bit (<c>× 257</c>) and float (<c>/ 255</c>).</summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    /// <summary>Fully transparent black.</summary>
    public static RgbaColor Transparent => new(0, 0, 0, 0);

    /// <summary>Opaque black.</summary>
    public static RgbaColor Black => new(0, 0, 0, 255);

    /// <summary>Opaque white.</summary>
    public static RgbaColor White => new(255, 255, 255, 255);
}
