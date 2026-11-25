using SimdImaging.Processing.Internal;
using SimdImaging.Processing.Contracts;
using SimdImaging.Abstractions;

namespace SimdImaging.Processing;

/// <summary>
/// The operators a <see cref="ProcessingPlan"/> can run, each carrying its own support radius.
/// </summary>
/// <remarks>
/// <para>
/// Every factory here knows how far its operator reads, because that follows from the operator's own parameters:
/// a Gaussian's support is <c>ceil(3σ)</c>, a box blur's is its radius, a point operator reads one pixel. None of
/// that is the caller's business, and none of it can be got wrong.
/// </para>
/// <para>
/// Coverage is explicit rather than reflective: an operator appears here once its support has been measured.
/// <c>HaloDeclarationTests</c> derives the smallest halo that reproduces the whole-image result and compares it
/// with what this class claims. Operators not listed here cannot be planned yet, which is honest — nobody has
/// established what they read.
/// </para>
/// </remarks>
public static class PlanOps
{
    private static ImageOp Create(string name, OpCategory category, IInputFootprint footprint,
        WorkspaceModel workspace, KernelImplementations kernels,
        WorkingFormatMask formats = WorkingFormatMask.All, ExecutionRequirements? requirements = null,
        PassModel pass = PassModel.Single, PixelSemantics? semantics = null,
        int? haloX = null, int? haloY = null) =>
        new(OperatorContract.Create(name, category, formats, footprint, workspace, requirements, pass, semantics,
                haloX: haloX, haloY: haloY),
            kernels);

    /// <summary>
    /// Point steps need no per-pixel scratch beyond a row buffer that already exists in the executor, so they
    /// declare zero rather than nothing: zero is a claim, null is an admission.
    /// </summary>
    private static ImageOp Point(string name, Func<PixelView, WorkingBuffer> op,
        WorkingFormatMask formats = WorkingFormatMask.All, PixelSemantics? semantics = null,
        bool wholeImage = true) =>
        Create(name, OpCategory.Point, IdentityFootprint.Instance, WorkspaceModel.Of(PointScratch),
            new KernelImplementations(PlanSteps.Point(op), wholeImage ? op : null), formats,
            semantics: semantics);

    /// <summary>
    /// A point step that writes its destination directly on Rgba8: the operator is run once over a 256-pixel probe to
    /// build its byte table, and the table is what the kernel applies. The probe is the operator itself, so the table
    /// cannot drift from the arithmetic it replaces — there is no second definition of the transform to keep in step.
    /// </summary>
    private static ImageOp PointTable(string name, Func<PixelView, WorkingBuffer> op,
        WorkingFormatMask formats = WorkingFormatMask.All, PixelSemantics? semantics = null) =>
        ForceAdapterPoint
            ? Point(name, op, formats, semantics)
            : Create(name, OpCategory.Point, IdentityFootprint.Instance, WorkspaceModel.Of(PointScratch),
                new KernelImplementations(
                    PlanSteps.PointTable(
                        new Lazy<byte[]>(() => BuildByteTable(op), LazyThreadSafetyMode.ExecutionAndPublication),
                        op,
                        TablePixelFloor),
                    op),
                formats, semantics: semantics);

    /// <summary>
    /// Pixels below which the table is not worth building. Measured at 27 us per table against a 4K step's 5 ms and a
    /// 320x240 step's 40 us: at 4K the table pays for itself forty-five times over, at 320x240 it barely does. The
    /// floor sits between the two, and it is the same device as <see cref="PointOps.Lut16PixelFloor"/> for the same
    /// reason - a table whose build costs more than the work it replaces is not an optimisation.
    /// </summary>
    private const long TablePixelFloor = 128 * 1024;

    /// <summary>
    /// Routes table-backed point steps through the general adapter instead. A measurement switch with the same job as
    /// <c>PointOps.ForceScalarPoint</c>: without it the old path disappears with the change that removed it, and the
    /// only number anyone can quote afterwards is the one from before the change.
    /// </summary>
    internal static bool ForceAdapterPoint;

    /// <summary>Runs the operator over one row of every possible byte value and packs the answer as 4 x 256.</summary>
    private static byte[] BuildByteTable(Func<PixelView, WorkingBuffer> op)
    {
        var table = new byte[4 * 256];
        using var probe = WorkingBuffer.Rent(256, 1, WorkingFormat.Rgba8, pool: null);
        var row = probe.View.Row(0);
        for (var value = 0; value < 256; value++)
        {
            row[value * 4] = (byte)value;
            row[(value * 4) + 1] = (byte)value;
            row[(value * 4) + 2] = (byte)value;
            row[(value * 4) + 3] = (byte)value;
        }

        // One row of 256 pixels is not parallel work: without this the operator inside the probe enters RowParallel
        // and overwrites the worker count the band split just reported for the image actually being processed.
        RowParallel.EnterNested();
        WorkingBuffer result;
        try
        {
            result = op(probe.View);
        }
        finally
        {
            RowParallel.ExitNested();
        }

        using var owned = result;
        var output = result.View.Row(0);
        for (var value = 0; value < 256; value++)
        {
            table[value] = output[value * 4];
            table[256 + value] = output[(value * 4) + 1];
            table[512 + value] = output[(value * 4) + 2];
            table[768 + value] = output[(value * 4) + 3];
        }

        return table;
    }

    /// <summary>
    /// What a point step actually allocates, which is not always nothing: a 16-bit image above
    /// <see cref="PointOps.Lut16PixelFloor"/> builds a 4 × 65536-entry table (512 KiB) because building it beats the
    /// float path. Declaring zero there made the estimator promise memory the step would then take.
    /// </summary>
    private static long PointScratch(int width, int height, int rows, WorkingFormat format) =>
        format == WorkingFormat.Rgba16 && (long)width * height >= PointOps.Lut16PixelFloor
            ? 4L * 65536 * sizeof(ushort)
            : 0L;

    /// <summary>
    /// A neighbourhood step that keeps a number of float planes proportional to its halo: the separable blurs
    /// and the 3x3 family both do, at sixteen bytes per pixel per row. The estimator's <c>rows</c> argument
    /// already includes the halo.
    /// </summary>
    private static ImageOp Neighborhood(string name, int halo, Func<PixelView, WorkingBuffer> op,
        Func<int, int, int, WorkingFormat, long>? scratch = null, IntoKernel? into = null,
        PassModel pass = PassModel.Single, PixelSemantics? semantics = null)
    {
        var footprint = halo == 0
            ? (IInputFootprint)IdentityFootprint.Instance
            : new HaloFootprint(halo, halo);
        var workspace = WorkspaceModel.Of(scratch ?? ((width, _, rows, _) => (long)rows * width * 16));
        return Create(name, OpCategory.Neighborhood, footprint, workspace,
            new KernelImplementations(PlanSteps.Neighborhood(op, halo, halo, into), op, into),
            requirements: ExecutionRequirements.RowBands, pass: pass, semantics: semantics);
    }

    /// <summary>
    /// Whole-image steps run once on the calling thread. A step that has not declared its workspace is reported as
    /// undeclared rather than as zero — the budget refuses to pretend it can enforce a ceiling it cannot see.
    /// </summary>
    private static ImageOp Global(string name, Func<PixelView, WorkingBuffer> op,
        Func<int, int, int, WorkingFormat, long>? scratch = null, IInputFootprint? footprint = null,
        PassModel pass = PassModel.Single, StateScope state = StateScope.None,
        ExecutionOrder order = ExecutionOrder.Any, Determinism determinism = Determinism.CoordinateDeterministic,
        PixelSemantics? semantics = null)
    {
        var requirements = ExecutionRequirements.WholeImage with
        {
            State = state,
            Order = order,
            Determinism = determinism,
        };

        return Create(name, OpCategory.Global, footprint ?? WholeSourceFootprint.Instance,
            scratch is null ? WorkspaceModel.Undeclared : WorkspaceModel.Of(scratch),
            new KernelImplementations(PlanSteps.Global(op), op),
            requirements: requirements, pass: pass, semantics: semantics);
    }

    /// <summary>Brightness by <paramref name="amount"/>.</summary>
    public static ImageOp Brightness(float amount) => PointTable(nameof(PointOps.Brightness), v => PointOps.Brightness(v, amount));

    /// <summary>Contrast about the mid-point.</summary>
    public static ImageOp Contrast(float amount) => PointTable(nameof(PointOps.Contrast), v => PointOps.Contrast(v, amount));

    /// <summary>Gamma correction; above one darkens.</summary>
    public static ImageOp Gamma(float gamma) => PointTable(nameof(PointOps.Gamma), v => PointOps.Gamma(v, gamma));

    /// <summary>Inverts the colour channels, leaving alpha.</summary>
    public static ImageOp Invert() => PointTable(nameof(PointOps.Invert), v => PointOps.Invert(v));

    /// <summary>BT.601 luma, alpha untouched.</summary>
    public static ImageOp Grayscale(bool bt709 = false) => Point(nameof(PointOps.Grayscale), v => PointOps.Grayscale(v, bt709));

    /// <summary>Blends towards a sepia tone; zero is identity.</summary>
    public static ImageOp Sepia(float amount = 1f) => Point(nameof(PointOps.Sepia), v => PointOps.Sepia(v, amount));

    /// <summary>Scales saturation about luma.</summary>
    public static ImageOp Saturation(float amount, bool bt709 = false) =>
        Point(nameof(PointOps.Saturation), v => PointOps.Saturation(v, amount, bt709));

    /// <summary>Quantizes each channel to <paramref name="levels"/> steps.</summary>
    public static ImageOp Posterize(int levels) => PointTable(nameof(PointOps.Posterize), v => PointOps.Posterize(v, levels));

    /// <summary>Remaps the tone curve between a black and a white point.</summary>
    public static ImageOp Levels(float inputBlack, float inputWhite, float gamma = 1f,
        float outputBlack = 0f, float outputWhite = 1f) =>
        Point(nameof(PointOps.Levels), v => PointOps.Levels(v, inputBlack, inputWhite, gamma, outputBlack, outputWhite));

    /// <summary>Gaussian blur; support is <c>ceil(3σ)</c>, which is where the halo comes from.</summary>
    public static ImageOp GaussianBlur(float sigma) =>
        Neighborhood(nameof(FilterOps.GaussianBlur), Math.Max(1, (int)MathF.Ceiling(sigma * 3f)),
            v => FilterOps.GaussianBlur(v, sigma), pass: PassModel.SeparableRows);

    /// <summary>Box blur of the given radius; support is the radius.</summary>
    public static ImageOp BoxBlur(int radius) =>
        Neighborhood(nameof(FilterOps.BoxBlur), radius, v => FilterOps.BoxBlur(v, radius),
            pass: PassModel.SeparableRows);

    /// <summary>
    /// Unsharp mask. It blurs internally, so its support follows the Gaussian (<c>ceil(3σ)</c>), not the single
    /// row a 3x3 kernel would need — the mistake this type exists to prevent.
    /// </summary>
    public static ImageOp Sharpen(float amount = 0.5f, float sigma = 1f) =>
        Neighborhood(nameof(FilterOps.Sharpen), Math.Max(1, (int)MathF.Ceiling(sigma * 3f)),
            v => FilterOps.Sharpen(v, amount, sigma), pass: PassModel.SeparableRows);

    /// <summary>3x3 emboss; one row and one column.</summary>
    public static ImageOp Emboss(float strength = 1f) =>
        Neighborhood(nameof(FilterOps.Emboss), 1, v => FilterOps.Emboss(v, strength),
            into: (window, destination, destinationY, rows, offset) =>
                FilterOps.EmbossInto(window, destination, destinationY, rows, offset, strength));

    /// <summary>3x3 Sobel magnitude; one row and one column.</summary>
    public static ImageOp Sobel() => Neighborhood(nameof(FilterOps.Sobel), 1, v => FilterOps.Sobel(v),
        into: (window, destination, destinationY, rows, offset) =>
            FilterOps.SobelInto(window, destination, destinationY, rows, offset));

    /// <summary>3x3 median; one row and one column of context. Alpha is filtered like the colour channels.</summary>
    public static ImageOp Median3x3() => Neighborhood(nameof(FilterOps.Median3x3), 1, v => FilterOps.Median3x3(v),
        into: (window, destination, destinationY, rows, offset) =>
            FilterOps.Median3x3Into(window, destination, destinationY, rows, offset),
        semantics: PixelSemantics.EncodedFloatAlphaComputed);

    /// <summary>
    /// Median with radius <paramref name="radius"/>; the support is the window, so the halo is the radius.
    /// </summary>
    /// <remarks>
    /// Band-safe for the same reason <see cref="Median3x3"/> is: every read is clamped to the view it was handed, and
    /// the executor widens that view by the halo, so an interior pixel sees its real neighbours and a pixel on the
    /// image edge sees the edge. The 8-bit path's histogram is per call, not shared between bands.
    /// </remarks>
    /// <param name="radius">Window radius; 1 is 3×3, up to 8.</param>
    public static ImageOp Median(int radius) =>
        Neighborhood(nameof(FilterOps.Median), radius, v => FilterOps.Median(v, radius),
            // Four channels are medianed, read from a band of rows widened by the radius on each side.
            (width, _, rows, _) => (long)rows * width * 4,
            semantics: PixelSemantics.EncodedFloatAlphaComputed);

    /// <summary>
    /// Convolution with an arbitrary odd kernel and any border mode.
    /// </summary>
    /// <remarks>
    /// The kernel is copied, because the step outlives the call that built it. Band-safe through
    /// <see cref="BandContext"/>: the operator maps taps in image coordinates, so Mirror and Wrap reflect about the
    /// image edge rather than the band edge.
    /// </remarks>
    /// <param name="kernel">Weights, row-major, <c>(2r+1)²</c> of them.</param>
    /// <param name="radius">Kernel radius; the side is <c>2×radius+1</c>.</param>
    /// <param name="border">How to read outside the image.</param>
    /// <param name="bias">Added after the sum, in sample units.</param>
    /// <param name="constantValue">Value used by <see cref="BorderMode.Constant"/>.</param>
    /// <exception cref="ArgumentException">The kernel length is not <c>(2r+1)²</c>.</exception>
    public static ImageOp Convolve(ReadOnlySpan<float> kernel, int radius,
        BorderMode border = BorderMode.Clamp, float bias = 0f, float constantValue = 0f)
    {
        var side = (radius * 2) + 1;
        if (kernel.Length != side * side)
            throw new ArgumentException($"A {side}×{side} kernel needs {side * side} weights.", nameof(kernel));

        var weights = kernel.ToArray();
        return Create("convolve", OpCategory.Neighborhood, WholeSourceFootprint.Instance,
            // One float RGBA copy of the window, which is a band plus the halo on both sides.
            WorkspaceModel.Of((width, _, rows, _) => (long)rows * width * 16),
            new KernelImplementations(PlanSteps.Neighborhood(
                (window, context, full) =>
                    FilterOps.ConvolveBanded(window, context, full, weights, radius, border, bias, constantValue),
                radius, radius)),
            // Mirror and Wrap map taps in image coordinates; the kernel is handed the full source for that. A
            // streaming executor has only a band, so it must refuse this step rather than seam at the band edge.
            // The band window is still widened by the radius: the taps it reads locally need their neighbours.
            requirements: ExecutionRequirements.RowBands,
            haloX: radius,
            haloY: radius);
    }

    /// <summary>
    /// Several per-channel point transforms collapsed into one table and one pass.
    /// </summary>
    /// <summary>A pre-built point chain table.</summary>
    public static ImageOp PointChain(PointChainTable table) =>
        Point(nameof(FilterOps.Apply), v => FilterOps.Apply(v, table),
            WorkingFormatMask.Rgba8, PixelSemantics.EncodedInteger);

    /// <summary>
    /// Several per-channel point transforms collapsed into one table and one pass (8-bit).
    /// </summary>
    /// <param name="transforms">The transforms, applied in order.</param>
    /// <exception cref="ArgumentException">No transforms were given.</exception>
    public static ImageOp PointChain(params PointOps.RowTransform[] transforms)
    {
        if (transforms.Length == 0)
            throw new ArgumentException("A chain needs at least one transform.", nameof(transforms));

        var chain = transforms.ToArray();
        return Create("point-chain", OpCategory.Point, IdentityFootprint.Instance, WorkspaceModel.None,
            new KernelImplementations(PlanSteps.Point(v => FilterOps.PointChain(v, chain))),
            WorkingFormatMask.Rgba8, semantics: PixelSemantics.EncodedInteger);
    }

    /// <summary>
    /// Erosion or dilation with a square window of odd side <c>2r+1</c>.
    /// </summary>
    /// <param name="radius">Window radius; 1 is 3×3.</param>
    /// <param name="kind">Erode or dilate.</param>
    public static ImageOp Morph(int radius, Abstractions.Morphology kind = Abstractions.Morphology.Erode) =>
        kind is Abstractions.Morphology.Open or Abstractions.Morphology.Close
            or Abstractions.Morphology.TopHat or Abstractions.Morphology.BlackHat
            ? Neighborhood(nameof(FilterOps.MorphCombine), radius, v => FilterOps.MorphCombine(v, radius, kind), 
                (width, height, _, _) => 4L * width * height * 4)
            : Neighborhood(nameof(FilterOps.Morph), radius, v => FilterOps.Morph(v, radius, kind == Abstractions.Morphology.Erode),
            // The separable passes hold the horizontal result plus its transpose: two extra frames.
            (width, height, _, _) => 2L * width * height * 4);

    /// <summary>Warm or cool shift.</summary>
    /// <param name="amount">Positive warms, negative cools.</param>
    public static ImageOp Temperature(float amount) =>
        Point(nameof(PointOps.Temperature), v => PointOps.Temperature(v, amount));

    /// <summary>Tone curve through the given points.</summary>
    /// <param name="points">Control points, at least two.</param>
    public static ImageOp Curves(ReadOnlySpan<CurvePoint> points)
    {
        var copy = points.ToArray();
        return Point(nameof(PointOps.Curves), v => PointOps.Curves(v, copy));
    }

    /// <summary>Saturation weighted towards the colours that are already saturated.</summary>
    /// <param name="amount">0 is greyscale, 1 is identity.</param>
    /// <param name="bt709">Use BT.709 luma instead of BT.601.</param>
    public static ImageOp Vibrance(float amount, bool bt709 = false) =>
        Point(nameof(PointOps.Vibrance), v => PointOps.Vibrance(v, amount, bt709));

    /// <summary>
    /// Seeded grain. Global: the generator advances once per pixel across the whole image, so a band would replay
    /// the same sequence and the grain would turn into stripes.
    /// </summary>
    /// <param name="amount">Amplitude in normalised units.</param>
    /// <param name="kind">Uniform or Gaussian.</param>
    /// <param name="seed">Sequence seed; the same seed always yields the same image.</param>
    public static ImageOp Noise(float amount = 0.05f, NoiseKind kind = NoiseKind.Gaussian, uint seed = 1) =>
        Global("noise", v => NoiseOps.Noise(v, amount, kind, seed), (_, _, _, _) => 0L,
            footprint: IdentityFootprint.Instance,
            state: StateScope.PerCall,
            order: ExecutionOrder.SourceOrder,
            determinism: Determinism.SeededSequence);

    /// <summary>Unsharp mask built from a negative Gaussian.</summary>
    /// <param name="amount">Strength.</param>
    /// <param name="sigma">Gaussian sigma; the halo follows it.</param>
    public static ImageOp GaussianSharpen(float amount = 0.5f, float sigma = 1f) =>
        Neighborhood(nameof(FilterOps.GaussianSharpen), (int)Math.Ceiling(3 * sigma),
            v => FilterOps.GaussianSharpen(v, amount, sigma), pass: PassModel.SeparableRows);

    /// <summary>3x3 erosion; one row and one column.</summary>
    public static ImageOp Erode3x3() => Neighborhood(nameof(FilterOps.Erode3x3), 1, v => FilterOps.Erode3x3(v),
        into: (window, destination, destinationY, rows, offset) =>
            FilterOps.Erode3x3Into(window, destination, destinationY, rows, offset));

    /// <summary>3x3 dilation; one row and one column.</summary>
    public static ImageOp Dilate3x3() => Neighborhood(nameof(FilterOps.Dilate3x3), 1, v => FilterOps.Dilate3x3(v),
        into: (window, destination, destinationY, rows, offset) =>
            FilterOps.Dilate3x3Into(window, destination, destinationY, rows, offset));

    /// <summary>Erosion then dilation, so two rows and two columns.</summary>
    public static ImageOp Open3x3() => Neighborhood(nameof(FilterOps.Open3x3), 2, v => FilterOps.Open3x3(v));

    /// <summary>Dilation then erosion, so two rows and two columns.</summary>
    public static ImageOp Close3x3() => Neighborhood(nameof(FilterOps.Close3x3), 2, v => FilterOps.Close3x3(v));

    /// <summary>Edge-preserving bilateral filter; its window is the radius.</summary>
    public static ImageOp Bilateral(int radius, float sigmaRange = 0.1f) =>
        Neighborhood(nameof(FilterOps.Bilateral), radius, v => FilterOps.Bilateral(v, radius, 0f, sigmaRange));

    /// <summary>Directional blur; its taps span half the length either side.</summary>
    public static ImageOp MotionBlur(int length, float angleDegrees = 0f) =>
        Neighborhood(nameof(FilterOps.MotionBlur), Math.Max(1, (length + 1) / 2), v => FilterOps.MotionBlur(v, length, angleDegrees));

    /// <summary>Disc-shaped bokeh; its offsets stay inside the radius.</summary>
    public static ImageOp BokehBlur(int radius) => Neighborhood(nameof(FilterOps.BokehBlur), radius, v => FilterOps.BokehBlur(v, radius));

    /// <summary>Guided filter; its window is the radius.</summary>
    public static ImageOp GuidedFilter(int radius, float epsilon = 1e-4f) =>
        Neighborhood("guided", radius, v => GuidedFilterOps.GuidedFilter(v, radius, epsilon),
            // Five float planes over bandRows + 4·radius rows (the operator widens the band by 2·radius on each
            // side, so its row count is the estimator's `rows` plus another 2·radius), plus the full-image copy
            // it makes up front. Both terms are read off GuidedFilterOps, not assumed.
            (width, height, rows, format) =>
                ((long)(rows + (2 * radius)) * width * 20) + ((long)width * height * format.BytesPerPixel()));

    /// <summary>
    /// Non-local means. Each output compares patches of radius <paramref name="patchRadius"/> inside a search
    /// window of <paramref name="searchRadius"/>, so it reads the sum of the two — not either one alone.
    /// </summary>
    public static ImageOp NonLocalMeans(int searchRadius, int patchRadius = 1, float h = 0.1f) =>
        Neighborhood(nameof(NonLocalMeansOps.NonLocalMeans), searchRadius + patchRadius,
            v => NonLocalMeansOps.NonLocalMeans(v, searchRadius, patchRadius, h),
            // Per worker: the decoded ring (rows × one pixel per row) plus six scratch arrays of width entries —
            // one long[] and five double[] — plus the output row of four floats. `rows` already carries the
            // search + patch halo. ArrayPool may hand back a longer array, which this does not model.
            (width, _, rows, format) =>
                ((long)rows * width * format.BytesPerPixel()) + ((long)width * (8 + 40 + 16)));

    /// <summary>Oil paint; its histogram window is the radius.</summary>
    public static ImageOp OilPaint(int radius, int levels = 20) =>
        Neighborhood(nameof(OilPaintOps.OilPaint), radius, v => OilPaintOps.OilPaint(v, radius, levels));

    /// <summary>
    /// Vignette. It darkens by distance from the image centre, so a band would see its own centre instead — the
    /// whole picture is the only correct context. It costs one intermediate image, like calling it directly.
    /// </summary>
    public static ImageOp Vignette(float radius = 0.75f, float strength = 1f) =>
        Global(nameof(FilterOps.Vignette), v => FilterOps.Vignette(v, radius, strength), (_, _, _, _) => 0L,
            footprint: IdentityFootprint.Instance);

    /// <summary>CLAHE: it builds per-tile histograms, so it reads the whole picture.</summary>
    public static ImageOp Clahe(int tilesX = 8, int tilesY = 8, float clipLimit = 3f) =>
        // One int histogram per tile per channel, plus the destination row.
        Global(nameof(ClaheOps.Clahe), v => ClaheOps.Clahe(v, tilesX, tilesY, clipLimit),
            (width, _, _, _) => ((long)tilesX * tilesY * 3 * 256 * 4) + ((long)width * 16),
            pass: PassModel.GatherThenApply, state: StateScope.PerImage);

    /// <summary>Local Laplacian: a pyramid, so every band would need the whole image anyway.</summary>
    public static ImageOp LocalLaplacian(int levels = 5, int samples = 32) =>
        Global(nameof(LocalLaplacianOps.LocalLaplacian), v => LocalLaplacianOps.LocalLaplacian(v, levels, samples),
            (width, height, _, _) => LocalLaplacianOps.ScratchBytes(width, height, levels, samples),
            pass: PassModel.GatherThenApply, state: StateScope.PerImage);

    /// <summary>Per-channel histogram stretch; whole-image statistics.</summary>
    public static ImageOp Normalize() =>
        // One 256-bin histogram plus the destination row the executor already provides.
        Global(nameof(PointOps.Normalize), v => PointOps.Normalize(v), (width, _, _, _) => 1024L + ((long)width * 16),
            pass: PassModel.GatherThenApply, state: StateScope.PerImage);

    /// <summary>Adaptive threshold: a local mean, but its window is wider than any band worth carrying.</summary>
    public static ImageOp AdaptiveThreshold(int radius = 8, float offset = 0.05f) =>
        // A (2r+1)-row float window of the full width, plus the working row.
        Global(nameof(ThresholdOps.AdaptiveThreshold), v => ThresholdOps.AdaptiveThreshold(v, radius, offset),
            (width, _, _, _) => ((long)((radius * 2) + 1) * width * 4) + ((long)width * 16),
            pass: PassModel.GatherThenApply, state: StateScope.PerImage);

    /// <summary>
    /// Block-aligned mosaic. A block spans rows that a band boundary can cut anywhere, so no halo is correct —
    /// whole-image is the only category that reproduces the single-image result.
    /// </summary>
    public static ImageOp Pixelate(int blockSize) =>
        // Writes blocks straight into the destination; no plane of its own. The footprint is the block grid,
        // not a constant halo: the radius a band needs depends on where the band starts.
        Global(nameof(FilterOps.Pixelate), v => FilterOps.Pixelate(v, blockSize), (_, _, _, _) => 0L,
            footprint: new BlockAlignedFootprint(blockSize));

    /// <summary>Dehaze: the dark channel and the atmospheric light both need the whole picture.</summary>
    public static ImageOp Dehaze(int patchRadius = 7, float omega = 0.95f, float tMin = 0.1f, int refineRadius = 20) =>
        Global(nameof(DehazeOps.Dehaze), v => DehazeOps.Dehaze(v, patchRadius, omega, tMin, refineRadius),
            // Counted from DehazeOps' rentals, not from the algorithm's description: dark + transmission, then —
            // when the transmission is refined — the luma guide, the guided filter's six planes and the box
            // mean's temporary, all alive at once. Ten planes, not three: the earlier declaration charged the
            // whole refinement as "one more plane" and under-reported the 1080p peak by 58 MiB.
            (width, _, rows, _) =>
            {
                var planes = refineRadius > 0 ? 10 : 2;
                var ringRows = Math.Min(rows, (2 * patchRadius) + 1);
                return (planes * (long)width * rows * sizeof(float))
                    + ((long)ringRows * width * sizeof(float))
                    + ((long)width * sizeof(float))
                    + (2L * width * 4 * sizeof(float));
            },
            pass: PassModel.GatherThenApply, state: StateScope.PerImage);

    /// <summary>Histogram equalisation needs the whole image to build its transfer curve.</summary>
    public static ImageOp Equalize() =>
        // Three 256-bin histograms turned into three LUTs, plus the destination row.
        Global(nameof(HistogramOps.Equalize), v => HistogramOps.Equalize(v),
            (width, _, _, _) => (3L * 256 * 4) + ((long)width * 16),
            pass: PassModel.GatherThenApply, state: StateScope.PerImage);
}
