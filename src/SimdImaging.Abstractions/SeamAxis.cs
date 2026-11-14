namespace SimdImaging.Abstractions;

/// <summary>The direction a seam runs in.</summary>
public enum SeamAxis : byte
{
    /// <summary>A top-to-bottom connected path; removing one narrows the image by a column.</summary>
    Vertical,

    /// <summary>A left-to-right connected path; removing one shortens the image by a row.</summary>
    Horizontal,
}
