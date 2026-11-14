namespace SimdImaging.Processing.Geometry;

/// <summary>
/// The 2×2 Jacobian of the destination → source mapping: <c>u</c> is the source column, <c>v</c> the
/// source row, <c>(x, y)</c> the destination. All four values are doubles because the perspective
/// path evaluates them in double precision (its frozen arithmetic order is documented at the call site).
/// </summary>
/// <param name="DuDx">∂u/∂x — how the source column moves per destination column.</param>
/// <param name="DuDy">∂u/∂y.</param>
/// <param name="DvDx">∂v/∂x.</param>
/// <param name="DvDy">∂v/∂y.</param>
internal readonly record struct SourceJacobian(double DuDx, double DuDy, double DvDx, double DvDy);

/// <summary>How the elliptical footprint plan turned out. Degenerate plans never fall back silently:
/// the status is returned to the caller and the isotropic replacement is part of the contract.</summary>
internal enum EwaStatus : byte
{
    /// <summary>A usable elliptical plan was built.</summary>
    Ok = 0,

    /// <summary>The Jacobian is singular (|det J| below the registered threshold), not finite, or its
    /// axis ratio is beyond the registered cap; the caller must rebuild isotropically.</summary>
    DegenerateIsotropic = 1,
}

/// <summary>
/// A 2-D elliptical tap window: the axis-aligned bounding box of the footprint plus a row-major
/// weight grid of exactly <c>CountX * CountY</c> normalised entries. The weight buffer is caller
/// supplied and reused per row so a whole image pass allocates nothing per pixel.
/// </summary>
internal struct EwaPlan
{
    public int StartX;
    public int StartY;
    public int CountX;
    public int CountY;
    public double[] Weights;
    public EwaStatus Status;
}

/// <summary>
/// Elliptical weighted averaging (EWA, Heckbert's footprint filtering): the local Jacobian of the
/// inverse mapping shapes a truncated elliptical Gaussian footprint per destination pixel.
/// <para>
/// Frozen mathematics (every number below is part of the contract, changes require a vector update):
/// with <c>J = ∂(u,v)/∂(x,y)</c> the destination → source Jacobian, the unit disk in destination
/// space maps to the ellipse <c>dᵀ Q⁻¹ d ≤ 1</c> with <c>Q = J·Jᵀ</c>; the tap weight is
/// <c>exp(-0.5 · dᵀ Q⁻¹ d)</c>, so minification (|J| &gt; 1) widens the footprint and magnification
/// narrows it. The window is the 2σ bounding box (±2·sqrt(λmax(Q))), clipped by the per-axis tap
/// budget, and weights are normalised in double and truncated to float once, in the accumulator —
/// the same rounding discipline as <see cref="TapPlanBuilder.FillAt"/>. <c>exp</c> is
/// <see cref="Math.Exp"/>, a BCL-managed implementation with architecture-independent bit patterns
/// (verified on x64 2026-11-14; no ARM hardware in the fleet — recorded honestly in known-gaps).
/// SIMD never evaluates <c>exp</c>: the accumulation stays scalar with a frozen tap order, and a
/// vector tier is deliberately deferred to M15 so no FMA-shape asymmetry like the one the M8
/// equivalence sweep exposed can arise between reference and accelerated paths.
/// </para>
/// </summary>
internal static class EwaKernel
{
    // Registered thresholds (docs/known-gaps.md, entries ewa.aa.*): initial values, pending the
    // quality sweep on the moiré corpus. Changing one is a semantic change, not a tuning knob.
    private const double DegenerateDetJ = 1e-6;      // below this the footprint covers no source area
    private const double DegenerateAxisRatio = 64.0; // beyond this the ellipse is a needle

    /// <summary>Builds the elliptical plan for one destination pixel. <paramref name="weights"/> must
    /// hold at least <c>(maxTapsPerAxis + 1)²</c> doubles and is reused across pixels; the stored values
    /// are the normalised weights (the accumulator truncates each one to float on read).</summary>
    public static EwaPlan Build(SourceJacobian jacobian, double centreX, double centreY,
        int sourceWidth, int sourceHeight, int maxTapsPerAxis, double[] weights)
    {
        // Q = J·Jᵀ — the source-space footprint of one destination pixel.
        var qxx = jacobian.DuDx * jacobian.DuDx + jacobian.DuDy * jacobian.DuDy;
        var qxy = jacobian.DuDx * jacobian.DvDx + jacobian.DuDy * jacobian.DvDy;
        var qyy = jacobian.DvDx * jacobian.DvDx + jacobian.DvDy * jacobian.DvDy;
        var det = jacobian.DuDx * jacobian.DvDy - jacobian.DuDy * jacobian.DvDx;
        var detQ = qxx * qyy - qxy * qxy; // == det² but computed directly: no cancellation on shears

        var half = (qxx + qyy) / 2.0;
        var root = Math.Sqrt(Math.Max(0.0, half * half - detQ));
        var lambdaMax = half + root;
        var lambdaMin = half - root;
        var axisRatio = lambdaMin > 0.0 ? Math.Sqrt(lambdaMax / lambdaMin) : double.PositiveInfinity;

        var plan = default(EwaPlan);
        plan.Weights = weights;
        // `!(detQ > 0)` also rejects NaN and infinity: a comparison-based test alone would let them
        // through, and a NaN footprint must fail into the registered isotropic fallback, not spread.
        if (!(detQ > 0.0) || !(Math.Abs(det) >= DegenerateDetJ) || !(axisRatio <= DegenerateAxisRatio))
        {
            plan.Status = EwaStatus.DegenerateIsotropic;
            plan.CountX = 0;
            plan.CountY = 0;
            return plan;
        }

        // R = Q⁻¹. The exponent is dᵀRd; the 1σ contour is dᵀRd = 1 and the window is the 2σ box.
        var invDetQ = 1.0 / detQ;
        var rxx = qyy * invDetQ;
        var rxy = -qxy * invDetQ;
        var ryy = qxx * invDetQ;

        var sigmaMax = Math.Sqrt(lambdaMax);
        var clip = maxTapsPerAxis / 2.0;
        var radius = Math.Min(2.0 * sigmaMax, clip);

        // Tap centres sit at +0.5 (areal); a box of ±radius around an areal centre covers at most
        // 2·radius + 1 taps, and the half-integer phase can reach that upper bound exactly.
        var startX = (int)Math.Ceiling(centreX - radius - 0.5);
        var endX = (int)Math.Floor(centreX + radius - 0.5);
        var startY = (int)Math.Ceiling(centreY - radius - 0.5);
        var endY = (int)Math.Floor(centreY + radius - 0.5);
        startX = Math.Max(startX, 0);
        startY = Math.Max(startY, 0);
        endX = Math.Min(endX, sourceWidth - 1);
        endY = Math.Min(endY, sourceHeight - 1);
        var countX = endX - startX + 1;
        var countY = endY - startY + 1;
        if (countX <= 0 || countY <= 0)
        {
            plan.Status = EwaStatus.DegenerateIsotropic;
            plan.CountX = 0;
            plan.CountY = 0;
            return plan;
        }

        // Row-major weights, normalised in double. The 2σ cut-off is expressed in the same quadratic
        // form as the exponent (q = 4 ⇔ 2σ), so a window clipped by the tap budget has no extra taper.
        const double qClip = 4.0;
        var sum = 0.0;
        var index = 0;
        for (var y = startY; y <= endY; y++)
        {
            var dy = y + 0.5 - centreY;
            for (var x = startX; x <= endX; x++)
            {
                var dx = x + 0.5 - centreX;
                var q = rxx * dx * dx + 2.0 * rxy * dx * dy + ryy * dy * dy;
                var weight = q <= qClip ? Math.Exp(-0.5 * q) : 0.0;
                weights[index++] = weight;
                sum += weight;
            }
        }

        // The centre tap always has weight exp(0) = 1 and sits inside the box, so sum > 0 here.
        var inverseSum = 1.0 / sum;
        for (var i = 0; i < index; i++)
            weights[i] *= inverseSum;

        plan.Status = EwaStatus.Ok;
        plan.StartX = startX;
        plan.StartY = startY;
        plan.CountX = countX;
        plan.CountY = countY;
        return plan;
    }
}

/// <summary>
/// Adaptive antialiasing: the minification factor |det J| picks the per-axis tap budget, and the
/// footprint's axis ratio decides degeneracy inside <see cref="EwaKernel.Build"/>. The boundaries
/// below are initial values pending the moiré-corpus sweep; they are registered as
/// <c>ewa.aa.tier.*</c> in docs/known-gaps.md and changing one is a semantic change, not a tuning knob.
/// </summary>
internal static class AdaptiveAa
{
    /// <summary>
    /// Returns the per-axis tap budget for one Jacobian. Minification widens the footprint by
    /// sqrt(|det J|) source pixels, so the budget grows with it; beyond 16× the window is clipped by
    /// this budget instead of the exact 2σ box (registered in known-gaps as a quality/throughput
    /// trade-off, not a silent semantic change).
    /// </summary>
    public static int Evaluate(double detJ)
    {
        var magnitude = Math.Abs(detJ);

        // Magnification (or identity): the footprint is at most one source pixel — a small kernel.
        if (magnitude <= 1.0)
            return 8;

        // Tier boundaries are measured values, not tuning knobs: see ewa.aa.tier.{1..3} in known-gaps.
        return magnitude <= 4.0 ? 16 : magnitude <= 16.0 ? 32 : 64;
    }
}
