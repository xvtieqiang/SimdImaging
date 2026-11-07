namespace SimdImaging.Processing;

/// <summary>
/// Whole-channel statistics, in normalised sample units: min/max/mean/standard deviation in 0..1 and entropy in
/// bits over 256 levels (0..8). The same units for every working format, so an 8-bit and a float image of the
/// same content report the same numbers.
/// </summary>
/// <param name="Min">Smallest sample.</param>
/// <param name="Max">Largest sample.</param>
/// <param name="Mean">Arithmetic mean.</param>
/// <param name="StandardDeviation">Population standard deviation.</param>
/// <param name="Entropy">Shannon entropy of the 256-level distribution, in bits.</param>
public readonly record struct ChannelStatistics(float Min, float Max, float Mean, float StandardDeviation,
    float Entropy);
