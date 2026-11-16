namespace SimdImaging.Abstractions;

/// <summary>Which half of morphology a call wants.</summary>
public enum Morphology
{
    /// <summary>Minimum: shrinks bright regions, removes bright specks.</summary>
    Erode = 0,

    /// <summary>Maximum: grows bright regions, fills dark specks.</summary>
    Dilate = 1,
}
