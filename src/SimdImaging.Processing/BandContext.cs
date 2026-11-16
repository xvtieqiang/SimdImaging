namespace SimdImaging.Processing;

/// <summary>
/// Where a band's window sits inside the whole image. Operators that must distinguish an image edge from a band
/// edge — anything with a border mode other than Clamp — need this, because the window alone cannot tell them apart.
/// </summary>
/// <param name="ImageWidth">Width of the image the band belongs to.</param>
/// <param name="ImageHeight">Height of the image the band belongs to.</param>
/// <param name="OriginX">Column in the image where the window's column 0 sits.</param>
/// <param name="OriginY">Row in the image where the window's row 0 sits.</param>
public readonly record struct BandContext(int ImageWidth, int ImageHeight, int OriginX, int OriginY);
