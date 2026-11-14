namespace SimdImaging.Abstractions;

/// <summary>
/// Transfer curve used when <see cref="SampleDomain.LinearLight"/> sampling is requested.
/// <para>This is an Abstractions-layer mirror of <c>SimdImaging.Processing.TransferCurve</c>. The
/// dependency direction is <c>Abstractions ← Processing</c>, so Abstractions cannot reference the
/// Processing enum; moving that enum down would rewrite existing public-API signatures.</para>
/// <para>The zero value is deliberately <b>sRGB</b>, not <c>Linear</c>: a caller that asks for
/// linear-light sampling without naming a curve gets the common default, while
/// <c>default(SampleOptions)</c> still keeps every field at its historical value.</para>
/// </summary>
public enum SampleTransferCurve : byte
{
    Srgb = 0,
    Linear = 1,
    Gamma22 = 2,
    Rec709 = 3,
    Pq = 4,
    Hlg = 5,
}
