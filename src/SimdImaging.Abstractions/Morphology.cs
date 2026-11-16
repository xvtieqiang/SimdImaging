namespace SimdImaging.Abstractions;

/// <summary>Which half of morphology a call wants.</summary>
public enum Morphology
{
    /// <summary>Minimum: shrinks bright regions, removes bright specks.</summary>
    Erode = 0,

    /// <summary>Maximum: grows bright regions, fills dark specks.</summary>
    Dilate = 1,

    /// <summary>Erode then dilate: removes bright specks smaller than the window, keeps the rest.</summary>
    Open = 2,

    /// <summary>Dilate then erode: fills dark specks smaller than the window, keeps the rest.</summary>
    Close = 3,

    /// <summary>The source minus its opening: the bright detail the opening removed.</summary>
    TopHat = 4,

    /// <summary>The closing minus the source: the dark detail the closing filled in.</summary>
    BlackHat = 5,
}
