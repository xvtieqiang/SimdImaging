namespace SimdImaging.Processing;

/// <summary>
/// Per-channel offsets for one tonal range, in sample units (-1..1). A positive value pushes that channel up
/// inside the range the weight function selects.
/// </summary>
public readonly record struct ColorBalanceShifts(float R, float G, float B);
