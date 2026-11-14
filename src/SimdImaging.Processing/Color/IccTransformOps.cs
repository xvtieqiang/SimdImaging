using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// ICC colour transforms. Two profile models are evaluated, chosen per direction:
/// <list type="bullet">
/// <item>a 3×3 matrix into D50 XYZ plus a per-channel tone curve — the shape every RGB working space
/// takes (sRGB, Display P3, Adobe RGB, ProPhoto);</item>
/// <item>a lookup table (<c>A2B0</c>/<c>B2A0</c> holding <c>mft1</c>, <c>mft2</c>, <c>mAB</c> or
/// <c>mBA</c>): 1-D pre-shaping, a 3-D CLUT evaluated tetrahedrally, output 1-D, and for the
/// <c>mAB</c> family a matrix between the two curve sets. The CLUT is integer (Q15 weights over a
/// 16-bit grid), so that stage is bit-exact across SIMD tiers; the PCS seam around it is float.</item>
/// </list>
/// <para>
/// Only RGB profiles are evaluated: a CMYK, grey or Lab device space is refused with
/// <see cref="NotSupportedException"/>, as is an 8-bit LUT with a PCSXYZ connection space (which the
/// specification leaves undefined). Three-in, three-out only. Rendering intent 0 is used; intents 1
/// and 2 are ignored when 0 is present and refused when it is not.
/// </para>
/// <para>
/// An empty profile means sRGB: the destination defaults to it, so <c>Transform(view, profile)</c>
/// converts a file's colours into sRGB, and <c>Transform(view, default, profile)</c> goes the other way.
/// </para>
/// <para>
/// Determinism: the transform is D1 — the float order is frozen and every SIMD tier produces the same
/// bytes. The CLUT stage by itself is integer and therefore D0. See
/// <c>docs/icc-boundary-decision.md</c> for the numeric contract and the parsing boundary.
/// </para>
/// </summary>
public static class IccTransformOps
{
    /// <summary>Converts pixels from <paramref name="sourceProfile"/> into <paramref name="destinationProfile"/>.</summary>
    /// <param name="source">The pixels, encoded as <paramref name="sourceProfile"/> says.</param>
    /// <param name="sourceProfile">The profile the pixels are in; empty means sRGB.</param>
    /// <param name="destinationProfile">The profile to convert to; empty means sRGB.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="InvalidDataException">A profile is malformed or a tag points outside it.</exception>
    /// <exception cref="NotSupportedException">A profile is not an RGB colour model with a usable
    /// device-to-PCS or PCS-to-device direction.</exception>
    public static WorkingBuffer Transform(PixelView source, ReadOnlySpan<byte> sourceProfile,
        ReadOnlySpan<byte> destinationProfile = default, IPixelPool? pool = null)
    {
        var from = sourceProfile.IsEmpty ? null : IccProfile.Parse(sourceProfile);
        var to = destinationProfile.IsEmpty ? null : IccProfile.Parse(destinationProfile);
        if (from is null && to is null)
            return Geometry.Transforms.Copy(source, pool);

        var width = source.Width;
        var destination = WorkingBuffer.Rent(width, source.Height, source.Format, pool);

        try
        {
            RowParallel.For(width, source.Height, (start, end) =>
            {
                var row = FloatScratch.Rent(width * 4);
                try
                {
                    for (var y = start; y < end; y++)
                    {
                        Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

                        for (var x = 0; x < width; x++)
                        {
                            var offset = x * 4;

                            // Source → PCS (D50 XYZ).
                            float px, py, pz;
                            if (from is null)
                            {
                                var (x65, y65, z65) = ColorMath.ToXyz(row[offset], row[offset + 1], row[offset + 2]);
                                (px, py, pz) = ColorMath.Adapt(x65, y65, z65, WhitePoint.D65, WhitePoint.D50);
                            }
                            else
                            {
                                (px, py, pz) = from.ToXyz(row[offset], row[offset + 1], row[offset + 2]);
                            }

                            // PCS → destination.
                            if (to is null)
                            {
                                var (x65, y65, z65) = ColorMath.Adapt(px, py, pz, WhitePoint.D50, WhitePoint.D65);
                                var (r, g, b) = ColorMath.FromXyz(x65, y65, z65);
                                row[offset] = r;
                                row[offset + 1] = g;
                                row[offset + 2] = b;
                            }
                            else
                            {
                                var (r, g, b) = to.FromXyz(px, py, pz);
                                row[offset] = r;
                                row[offset + 1] = g;
                                row[offset + 2] = b;
                            }
                        }

                        Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
                    }
                }
                finally
                {
                    FloatScratch.Return(row);
                }
            });
        }
        catch
        {
            destination.Dispose();
            throw;
        }

        return destination;
    }
}
