namespace SimdImaging.Processing;

/// <summary>
/// The twelve Porter-Duff compositing operators. Each is the pair of factors applied to the source and the
/// destination: <c>out = src·Fa + dst·Fb</c>, evaluated on premultiplied colour so the alpha channel and the
/// colour channels cannot disagree.
/// </summary>
public enum PorterDuff : byte
{
    /// <summary>Both factors 0: the destination window is cleared.</summary>
    Clear,

    /// <summary>Source only, destination discarded.</summary>
    Source,

    /// <summary>Destination only, source discarded.</summary>
    Destination,

    /// <summary>Source over destination — the usual "draw on top".</summary>
    SourceOver,

    /// <summary>Destination over source.</summary>
    DestinationOver,

    /// <summary>Source clipped to the destination's alpha.</summary>
    SourceIn,

    /// <summary>Destination clipped to the source's alpha.</summary>
    DestinationIn,

    /// <summary>Source where the destination is transparent.</summary>
    SourceOut,

    /// <summary>Destination where the source is transparent — an eraser.</summary>
    DestinationOut,

    /// <summary>Source painted only inside the destination's coverage.</summary>
    SourceAtop,

    /// <summary>Destination painted only inside the source's coverage.</summary>
    DestinationAtop,

    /// <summary>One or the other, never both: overlapping coverage becomes transparent.</summary>
    Xor,
}
