using SimdImaging.Abstractions;
using SimdImaging.Processing;

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
    /// Runs <paramref name="plan"/> over this image and returns the last step's result. Same-size steps are
    /// split into row bands; global steps run whole.
    /// </summary>
    /// <param name="plan">The steps, in order.</param>
    /// <param name="pool">Where the intermediate and output buffers are rented.</param>
    /// <param name="observe">Optional per-step hook, called while the step's output is still alive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public Image Apply(ProcessingPlan plan, IPixelPool? pool = null, PlanObserver? observe = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ReplaceBuffer(plan.Execute(this[0].Pixels.View, pool, observe));
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
    public Image ApplyWithOptions(ProcessingPlan plan, ExecuteOptions options, IPixelPool? pool = null,
        PlanObserver? observe = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        return ReplaceBuffer(plan.ExecuteWithOptions(this[0].Pixels.View, options, pool, observe));
    }
}
