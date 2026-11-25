using System.ComponentModel;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;

namespace SimdImaging;

/// <summary>Planned execution: a chain of kernels run band by band.</summary>
public sealed partial class Image
{
    /// <summary>
    /// Fuses several exposures of the same scene into one frame.
    /// </summary>
    /// <param name="frames">At least two frames, all the same size. The first one supplies the format and header.</param>
    /// <param name="contrast">Exponent on local contrast in the per-pixel score.</param>
    /// <param name="saturation">Exponent on colour saturation.</param>
    /// <param name="exposure">Exponent on well-exposedness.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <returns>A new image; the frames are not modified.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frames"/> is null.</exception>
    /// <exception cref="ArgumentException">Fewer than two frames, or their sizes differ.</exception>
    public static Image FuseExposures(IReadOnlyList<Image> frames,
        float contrast = 1f, float saturation = 1f, float exposure = 1f, IPixelPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count < 2)
            throw new ArgumentException("Fusion needs at least two frames.", nameof(frames));

        var views = new PixelView[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(frames[i]);
            views[i] = frames[i][0].Pixels.View;
        }

        return frames[0].ReplaceBuffer(
            ExposureFusionOps.Fuse(views, pool: pool, contrast: contrast, saturation: saturation, exposure: exposure));
    }

    /// <summary>
    /// Runs <paramref name="plan"/> over this image and returns the last step's result.
    /// <para>Advanced entry: <c>Image.Pipe()</c> is the user-facing chain, and every operator it exposes is also a
    /// plan step; use this only for a compiled recipe or per-step observation. It is hidden from IntelliSense for
    /// the same reason.</para> Same-size steps are
    /// split into row bands; global steps run whole.
    /// </summary>
    /// <param name="plan">The steps, in order.</param>
    /// <param name="pool">Where the intermediate and output buffers are rented.</param>
    /// <param name="observe">Optional per-step hook, called while the step's output is still alive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public Image Apply(ProcessingPlan plan, IPixelPool? pool = null, PlanObserver? observe = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfMultiFrame();
        return ApplyCompiled(plan, new ExecuteOptions(), pool, observe);
    }

    /// <summary>
    /// Runs <paramref name="plan"/> with explicit execution knobs (degree, working-set ceiling, fusion
    /// switch, cancellation). Output bytes are identical to <see cref="Apply"/>; only the memory profile
    /// and the failure modes (cancellation, <c>MaxWorkingBytes</c>) differ.
    /// </summary>
    /// <param name="plan">The steps, in order.</param>
    /// <param name="options">Execution knobs.</param>
    /// <param name="pool">Where the intermediate and output buffers are rented.</param>
    /// <param name="observe">Optional per-step hook; present forces the step-by-step path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public Image ApplyWithOptions(ProcessingPlan plan, ExecuteOptions options, IPixelPool? pool = null,
        PlanObserver? observe = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfMultiFrame();
        return ApplyCompiled(plan, options, pool, observe);
    }

    /// <summary>
    /// The compiler's per-step explanation for this image and plan: output size/format, the source rectangles each
    /// input port must read, and the read amplification. It compiles without executing.
    /// </summary>
    public IReadOnlyList<PlanStepExplanation> Explain(ProcessingPlan plan, PixelRect? region = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfMultiFrame();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plan.BandRows);

        var compiled = PipelineCompiler.Compile(BuildRecipe(plan), new CompileOptions
        {
            SourceWidth = Width,
            SourceHeight = Height,
            SourceFormat = Format,
            Sink = SinkCapabilities.None,
            BandRows = plan.BandRows,
            Region = region,
            Execute = new ExecuteOptions(),
        });

        return compiled.StepInfo.Select(info => new PlanStepExplanation(
            info.Name,
            info.IsGeometry,
            info.Width,
            info.Height,
            info.Format,
            info.SourceRegions.Select(port => new PlanSourceRegion(port.Port, port.Region)).ToArray(),
            info.SourceArea,
            info.OutputArea)).ToArray();
    }

    private PipelineRecipe BuildRecipe(ProcessingPlan plan)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var step in plan.Steps)
            builder.Append(step.Definition, step.Lut);
        if (plan.ResizeSpec is { } resize)
            builder.Append(GeometrySteps.Resize(resize.Width, resize.Height, resize.Resampler));
        return builder.Build();
    }

    /// <summary>
    /// The one compiled entry behind both Apply overloads: the plan's steps become a Recipe, a trailing resize
    /// becomes a geometry step, and the compiler produces the sections the frame executor runs. LUT steps keep their
    /// table so consecutive point tables still fuse.
    /// </summary>
    private Image ApplyCompiled(ProcessingPlan plan, ExecuteOptions options, IPixelPool? pool, PlanObserver? observe)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plan.BandRows);
        var compiled = PipelineCompiler.Compile(BuildRecipe(plan), new CompileOptions
        {
            SourceWidth = Width,
            SourceHeight = Height,
            SourceFormat = Format,
            Sink = SinkCapabilities.None,
            BandRows = plan.BandRows,
            Execute = options,
            ObserveSteps = observe is not null,
        });

        return ReplaceBuffer(CompiledPlanExecutor.ExecuteFrame(compiled, this[0].Pixels.View, pool, options, observe));
    }

    /// <summary>
    /// A plan runs one frame; an image with several would lose the rest without saying so. Encoding already refuses
    /// for the same reason, and so does <see cref="Image.Mutate"/>, so this refuses rather than being the one entry
    /// point that quietly returns a single frame.
    /// </summary>
    /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
    private void ThrowIfMultiFrame()
    {
        if (FrameCount > 1)
            throw new NotSupportedException(
                $"The image has {FrameCount} frames and a plan runs one; select a frame with KeepFrames first.");
    }
}
