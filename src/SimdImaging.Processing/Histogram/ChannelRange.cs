namespace SimdImaging.Processing;

/// <summary>The minimum and maximum of one colour channel, in sample units.</summary>
public readonly record struct ChannelRange(float Min, float Max)
{
    /// <summary>The span between the two, or 0 for a flat channel.</summary>
    public float Span => Max - Min;
}
