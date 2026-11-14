using SimdImaging.Abstractions;

namespace SimdImaging.Processing.Geometry;

/// <summary>
/// Per-sample encoded ↔ linear-light transfer for reconstruction. This deliberately does NOT reuse
/// <see cref="TransferOps"/>' whole-image LUT pipeline: <c>Lookup</c> clamps to [0,1] and writes back
/// in the source format, which would destroy HDR, negative and out-of-range samples (M7 contract).
/// <para>Only the transfer mathematics is shared (<c>Decode</c> / <c>Encode</c>). Values for which the
/// EOTF is undefined (negative, NaN, infinity) are preserved verbatim rather than clamped or turned
/// into NaN; <see cref="OutOfGamutPolicy.Throw"/> reports them at the write stage.</para>
/// </summary>
internal static class SampleTransfer
{
    public static float ToLinear(float value, SampleTransferCurve curve)
    {
        if (curve == SampleTransferCurve.Linear) return value;
        if (value < 0f || float.IsNaN(value) || float.IsInfinity(value)) return value;
        return TransferOps.Decode(ToProcessingCurve(curve), value);
    }

    public static float FromLinear(float value, SampleTransferCurve curve)
    {
        if (curve == SampleTransferCurve.Linear) return value;
        if (value < 0f || float.IsNaN(value) || float.IsInfinity(value)) return value;
        return TransferOps.Encode(ToProcessingCurve(curve), value);
    }

    private static TransferCurve ToProcessingCurve(SampleTransferCurve curve) => curve switch
    {
        SampleTransferCurve.Srgb => TransferCurve.Srgb,
        SampleTransferCurve.Linear => TransferCurve.Linear,
        SampleTransferCurve.Gamma22 => TransferCurve.Gamma22,
        SampleTransferCurve.Rec709 => TransferCurve.Rec709,
        SampleTransferCurve.Pq => TransferCurve.Pq,
        SampleTransferCurve.Hlg => TransferCurve.Hlg,
        _ => throw new ArgumentOutOfRangeException(nameof(curve), curve, "Not a defined transfer curve."),
    };
}
