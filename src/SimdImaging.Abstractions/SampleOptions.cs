namespace SimdImaging.Abstractions;

/// <summary>The domain in which sample weights are accumulated.</summary>
public enum SampleDomain : byte
{
    /// <summary>Weights are applied to the encoded (gamma) samples.</summary>
    Encoded = 0,

    /// <summary>Samples are linearised before weighting and re-encoded afterwards.</summary>
    LinearLight = 1,
}

/// <summary>How alpha participates in interpolation.</summary>
public enum AlphaMode : byte
{
    /// <summary>Colour samples are unassociated (straight) alpha.</summary>
    Straight = 0,

    /// <summary>Colour samples are associated (premultiplied) alpha.</summary>
    Premultiplied = 1,
}

/// <summary>What happens at the edge of the source image.</summary>
public enum EdgeMode : byte
{
    /// <summary>Repeat the edge sample.</summary>
    Clamp = 0,

    /// <summary>Leave the caller's background value in place.</summary>
    Background = 1,
}

/// <summary>What happens to samples outside the format's nominal [0,1] range.</summary>
public enum OutOfGamutPolicy : byte
{
    /// <summary>Keep HDR, negative and out-of-range values exactly as they are.</summary>
    Preserve = 0,

    /// <summary>Clamp to the nominal range.</summary>
    Clamp = 1,

    /// <summary>Reject such samples rather than silently changing them.</summary>
    Throw = 2,
}

/// <summary>How the sampler adapts its footprint to the transform (M9).</summary>
public enum SampleAaMode : byte
{
    /// <summary>Fixed kernel; no footprint adaptation.</summary>
    None = 0,

    /// <summary>
    /// Elliptical weighted averaging: the local Jacobian of the transform shapes an elliptical
    /// Gaussian footprint per destination pixel. Only resamplers with an analytic Jacobian
    /// (affine / perspective) support this — <see cref="SampleDomain"/>-style point warps with a
    /// caller lambda have no derivative and must reject the mode explicitly.
    /// </summary>
    Elliptical = 1,
}

/// <summary>
/// Sampling contract shared by Warp and Resize. The default (<c>default</c>) is the historical
/// behaviour: encoded domain, straight alpha, clamp edges, out-of-range values preserved.
/// <para>Combinations that a sampler does not implement must fail with <see cref="NotSupportedException"/>.
/// Silent fallback to another domain, alpha mode or resampler is not allowed.</para>
/// </summary>
public readonly record struct SampleOptions
{
    /// <summary>The historical default; every field at its zero value.</summary>
    public static SampleOptions Default => default;

    public SampleDomain Domain { get; init; }
    public AlphaMode Alpha { get; init; }
    public EdgeMode Edge { get; init; }
    public OutOfGamutPolicy OutOfGamut { get; init; }

    /// <summary>Curve applied per sample in <see cref="SampleDomain.LinearLight"/>. sRGB by default.</summary>
    public SampleTransferCurve Curve { get; init; }

    /// <summary>
    /// Footprint adaptation mode. <see cref="SampleAaMode.Elliptical"/> requires an analytic Jacobian:
    /// it is honoured by <c>AffineWithOptions</c> / <c>PerspectiveWithOptions</c> /
    /// <c>RotateWithOptions</c> and rejected (with <see cref="NotSupportedException"/>) by warp-style
    /// point sampling whose mapping is a caller lambda.
    /// </summary>
    public SampleAaMode AaMode { get; init; }
}
