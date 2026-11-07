namespace SimdImaging.Processing;

/// <summary>
/// Separable and non-separable blend modes (W3C compositing). The separable ones act per colour channel; Hue,
/// Saturation, Color and Luminosity move between HSL components and need the whole pixel.
/// </summary>
public enum BlendMode : byte
{
    /// <summary>Source over destination — the same result as <see cref="PorterDuff.SourceOver"/>.</summary>
    Normal,

    /// <summary>Darkens: <c>b·s</c>.</summary>
    Multiply,

    /// <summary>Lightens: <c>1 - (1-b)(1-s)</c>.</summary>
    Screen,

    /// <summary>Multiply on dark backdrop, screen on light.</summary>
    Overlay,

    /// <summary>Channel-wise minimum.</summary>
    Darken,

    /// <summary>Channel-wise maximum.</summary>
    Lighten,

    /// <summary>Brightens the backdrop in proportion to the source.</summary>
    ColorDodge,

    /// <summary>Darkens the backdrop in proportion to the source.</summary>
    ColorBurn,

    /// <summary>Overlay with the operands swapped.</summary>
    HardLight,

    /// <summary>A softer HardLight using the W3C pegtop curve.</summary>
    SoftLight,

    /// <summary>Absolute difference.</summary>
    Difference,

    /// <summary><c>b + s - 2bs</c>.</summary>
    Exclusion,

    /// <summary>Additive, clamped.</summary>
    Add,

    /// <summary>Subtractive, clamped.</summary>
    Subtract,

    /// <summary>Takes the source hue with the backdrop's saturation and luminosity.</summary>
    Hue,

    /// <summary>Takes the source saturation with the backdrop's hue and luminosity.</summary>
    Saturation,

    /// <summary>Takes the source hue and saturation with the backdrop's luminosity.</summary>
    Color,

    /// <summary>Takes the source luminosity with the backdrop's hue and saturation.</summary>
    Luminosity,
}
