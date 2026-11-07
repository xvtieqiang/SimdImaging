using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging;

/// <summary>Histogram inspection and equalization.</summary>
public sealed partial class Image
{
    /// <summary>Counts one channel's code values into <paramref name="bins"/> buckets (256 by default).</summary>
    /// <param name="channel">0 = R, 1 = G, 2 = B, 3 = A.</param>
    /// <param name="bins">Bucket count.</param>
    public int[] Histogram(int channel, int bins = 256) =>
        HistogramOps.Histogram(this[0].Pixels.View, channel, bins);

    /// <summary>Min, max, mean, standard deviation and entropy of all four channels.</summary>
    public (ChannelStatistics R, ChannelStatistics G, ChannelStatistics B, ChannelStatistics A) Statistics() =>
        HistogramOps.Statistics(this[0].Pixels.View);

    /// <summary>The colours that cover most of the image, most used first.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1..256.</exception>
    public RgbaColor[] DominantColors(int count = 8) =>
        AnalysisOps.DominantColors(this[0].Pixels.View, count);

    /// <summary>Minimum and maximum of each colour channel.</summary>
    public (ChannelRange R, ChannelRange G, ChannelRange B) MinMax() =>
        HistogramOps.MinMax(this[0].Pixels.View);

    /// <summary>Equalizes each colour channel through its cumulative distribution; alpha is untouched.</summary>
    public Image EqualizeHistogram(IPixelPool? pool = null) =>
        ReplaceBuffer(HistogramOps.Equalize(this[0].Pixels.View, pool));

    /// <summary>Tight box around pixels whose alpha exceeds <paramref name="threshold"/>; zero-sized when
    /// nothing qualifies.</summary>
    public PixelRect AlphaBounds(float threshold = 0f) =>
        AnalysisOps.AlphaBounds(this[0].Pixels.View, threshold);

    /// <summary>PSNR in dB against <paramref name="other"/>, peak fixed at 1; infinite when identical.</summary>
    /// <exception cref="ArgumentException">The images have different dimensions.</exception>
    public double Psnr(Image other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return AnalysisOps.Psnr(this[0].Pixels.View, other[0].Pixels.View);
    }

    /// <summary>Structural similarity against <paramref name="other"/>; 1 means identical.</summary>
    /// <exception cref="ArgumentException">The images have different dimensions.</exception>
    public double Ssim(Image other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return AnalysisOps.Ssim(this[0].Pixels.View, other[0].Pixels.View);
    }

    /// <summary>Absolute differences against <paramref name="other"/>.</summary>
    /// <exception cref="ArgumentException">The images have different dimensions.</exception>
    public DiffResult Diff(Image other, float threshold = 1f / 255f)
    {
        ArgumentNullException.ThrowIfNull(other);
        return AnalysisOps.Diff(this[0].Pixels.View, other[0].Pixels.View, threshold);
    }
}
