using SimdImaging.Abstractions;

namespace SimdImaging;

/// <summary>One frame of an <see cref="Image"/>: its pixels and, for animations, how long it is shown.</summary>
public sealed class ImageFrame : IDisposable
{
    internal ImageFrame(PixelBuffer pixels, TimeSpan? duration)
    {
        Pixels = pixels;
        Duration = duration;
    }

    /// <summary>The frame's pixels. Disposing the frame releases them.</summary>
    public PixelBuffer Pixels { get; }

    /// <summary>How long an animation shows this frame; null for a still image.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>Releases the pixel buffer to its pool.</summary>
    public void Dispose() => Pixels.Dispose();
}
