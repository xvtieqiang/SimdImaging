using System.Runtime.InteropServices;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Geometry;

namespace SimdImaging;

/// <summary>
/// A destination → source mapping in normalised coordinates: <c>(0,0)</c> is the top-left corner of the
/// destination and <c>(1,1)</c> the bottom-right, whatever the pixel count. This is the only warp shape
/// that can be applied to two renditions of different resolutions without guessing, which is why a
/// gain-map pair takes this and not <see cref="PixelWarp"/>.
/// </summary>
public delegate void NormalizedWarp(float u, float v, out float sourceU, out float sourceV);

/// <summary>
/// An SDR rendition and its gain map, kept together so a geometry operation can act on both.
/// <para>
/// The gain map is usually a fraction of the base's resolution, and the pair's contract is that the two
/// always describe the same scene: every operation here derives the gain map's parameters <b>from the
/// base's</b> — the same normalised rectangle, the same transform in the gain map's own pixel grid, the
/// same target size scaled by the pair's ratio. Applying the operation to the two images separately with
/// those derived parameters produces exactly the bytes this type produces; that equality is what the
/// tests assert.
/// </para>
/// <para>
/// Operations whose result is decided by content (trim, entropy crop, seam carving) or by a per-image
/// canvas rule (pad, tile, the anchored resize modes) are deliberately absent: two renditions would make
/// two different decisions, and the pair would stop describing one scene. <see cref="Crop"/>,
/// <see cref="Resize"/>, <see cref="Rotate(float, RgbaColor, RgbaColor, IPixelPool?)"/>,
/// <see cref="Rotate90"/>, <see cref="Rotate180"/>, <see cref="FlipHorizontal"/>,
/// <see cref="FlipVertical"/>, <see cref="Affine"/>, <see cref="Perspective"/> and
/// <see cref="WarpNormalized"/> are the complete supported set.
/// </para>
/// <para>
/// The pair owns both images: <see cref="Dispose"/> releases them.
/// </para>
/// </summary>
public sealed class GainMappedImage : IDisposable
{
    private bool _disposed;

    /// <summary>Pairs a base with its gain map.</summary>
    /// <param name="baseImage">The SDR rendition.</param>
    /// <param name="gainMap">The gain map, of any resolution.</param>
    /// <param name="parameters">The reconstruction metadata; null uses <see cref="GainMapParameters.Default"/>.</param>
    /// <exception cref="ArgumentNullException">Either image is null.</exception>
    /// <exception cref="ArgumentException">An image has no pixels, or the metadata cannot describe a
    /// reconstruction.</exception>
    public GainMappedImage(Image baseImage, Image gainMap, GainMapParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        ArgumentNullException.ThrowIfNull(gainMap);
        if (baseImage.Width <= 0 || baseImage.Height <= 0)
            throw new ArgumentException("The base image has no pixels.", nameof(baseImage));
        if (gainMap.Width <= 0 || gainMap.Height <= 0)
            throw new ArgumentException("The gain map has no pixels.", nameof(gainMap));

        var resolved = parameters ?? GainMapParameters.Default;
        resolved.Validate();

        Base = baseImage;
        GainMap = gainMap;
        Parameters = resolved;
    }

    /// <summary>The SDR rendition.</summary>
    public Image Base { get; }

    /// <summary>The gain map.</summary>
    public Image GainMap { get; }

    /// <summary>The reconstruction metadata that travels with the pair.</summary>
    public GainMapParameters Parameters { get; }

    /// <summary>The gain map's horizontal resolution relative to the base's.</summary>
    public double ScaleX => (double)GainMap.Width / Base.Width;

    /// <summary>The gain map's vertical resolution relative to the base's.</summary>
    public double ScaleY => (double)GainMap.Height / Base.Height;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Base.Dispose();
        GainMap.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------
    // Geometry. Each method derives the gain map's parameters and calls the very same operator, so the
    // result is byte-identical to transforming that image on its own with those parameters.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Crops the same normalised region out of both renditions. The gain rectangle is the region scaled
    /// by the pair's ratio, rounded outward (floor at the origin, ceil at the far edge) so no gain-map
    /// sample that covers the region is dropped.
    /// </summary>
    /// <param name="region">The rectangle, in base pixels.</param>
    /// <param name="pool">Where the destinations are rented.</param>
    /// <exception cref="ArgumentException">The rectangle does not lie inside the base image.</exception>
    public GainMappedImage Crop(PixelRect region, IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var baseResult = Base.Pipe(pool).Crop(region).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).Crop(DeriveCrop(region)).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    public GainMappedImage FlipHorizontal(IPixelPool? pool = null) =>
        Transform(baseImage => baseImage.Pipe(pool).FlipHorizontal().ToImage(), gainImage => gainImage.Pipe(pool).FlipHorizontal().ToImage());

    public GainMappedImage FlipVertical(IPixelPool? pool = null) =>
        Transform(baseImage => baseImage.Pipe(pool).FlipVertical().ToImage(), gainImage => gainImage.Pipe(pool).FlipVertical().ToImage());

    public GainMappedImage Rotate90(bool clockwise = true, IPixelPool? pool = null) =>
        Transform(baseImage => baseImage.Pipe(pool).Rotate90(clockwise).ToImage(), gainImage => gainImage.Pipe(pool).Rotate90(clockwise).ToImage());

    public GainMappedImage Rotate180(IPixelPool? pool = null) =>
        Transform(baseImage => baseImage.Pipe(pool).Rotate180().ToImage(), gainImage => gainImage.Pipe(pool).Rotate180().ToImage());

    /// <summary>Rotates by any angle; the gain map gets the same transform in its own pixel grid.</summary>
    public GainMappedImage Rotate(float degrees, RgbaColor baseBackground = default,
        RgbaColor gainBackground = default, IPixelPool? pool = null)
    {
        var (width, height, forward) = Image.RotationCanvas(Base.Width, Base.Height, degrees);
        return Affine(forward, width, height, baseBackground, gainBackground, pool);
    }

    /// <summary>Rotates with an explicit sampling contract.</summary>
    public GainMappedImage RotateWithOptions(float degrees, Resampler resampler, SampleOptions options,
        RgbaColor baseBackground = default, RgbaColor gainBackground = default, IPixelPool? pool = null)
    {
        var (width, height, forward) = Image.RotationCanvas(Base.Width, Base.Height, degrees);
        return AffineWithOptions(forward, width, height, resampler, options, baseBackground, gainBackground, pool);
    }

    /// <summary>Resizes both renditions, the gain map to the same ratio of the new size.</summary>
    public GainMappedImage Resize(int width, int height, Resampler resampler = Resampler.Bicubic,
        IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePositive(width, height);

        var baseResult = Base.Pipe(pool).Resize(width, height, resampler).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).Resize(Scaled(width, ScaleX), Scaled(height, ScaleY), resampler).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    /// <summary>Resizes both renditions with an explicit sampling contract.</summary>
    public GainMappedImage ResizeWithOptions(int width, int height, Resampler resampler, SampleOptions options,
        RgbaColor baseBackground = default, RgbaColor gainBackground = default, IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePositive(width, height);

        var baseResult = Base.Pipe(pool).ResizeWithOptions(width, height, resampler, options, baseBackground).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).ResizeWithOptions(Scaled(width, ScaleX), Scaled(height, ScaleY),
                resampler, options, gainBackground).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies the same geometric mapping to both renditions. <paramref name="forward"/> is in base
    /// pixels; the gain map gets the conjugated transform <c>T⁻¹·M·S</c>, which is the same mapping
    /// expressed in its own grid.
    /// </summary>
    /// <param name="forward">Source → destination transform, in base pixels.</param>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="baseBackground">Colour outside the source in the base.</param>
    /// <param name="gainBackground">Colour outside the source in the gain map; the default is "no gain".</param>
    /// <param name="pool">Where the destinations are rented.</param>
    public GainMappedImage Affine(AffineMatrix forward, int width, int height,
        RgbaColor baseBackground = default, RgbaColor gainBackground = default, IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePositive(width, height);

        var baseResult = Base.Pipe(pool).Affine(forward, width, height, baseBackground).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).Affine(Conjugate(forward, width, height), Scaled(width, ScaleX),
                Scaled(height, ScaleY), gainBackground).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    /// <summary>Applies the conjugated affine transform with an explicit sampling contract.</summary>
    public GainMappedImage AffineWithOptions(AffineMatrix forward, int width, int height, Resampler resampler,
        SampleOptions options, RgbaColor baseBackground = default, RgbaColor gainBackground = default,
        IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePositive(width, height);

        var baseResult = Base.Pipe(pool).AffineWithOptions(forward, width, height, resampler, options, baseBackground).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).AffineWithOptions(Conjugate(forward, width, height), Scaled(width, ScaleX),
                Scaled(height, ScaleY), resampler, options, gainBackground).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    /// <summary>Applies the same 8-parameter homography to both renditions.</summary>
    /// <param name="homography">Eight row-major parameters of the forward homography, in base pixels.</param>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="baseBackground">Colour outside the source in the base.</param>
    /// <param name="gainBackground">Colour outside the source in the gain map.</param>
    /// <param name="pool">Where the destinations are rented.</param>
    public GainMappedImage Perspective(ReadOnlySpan<float> homography, int width, int height,
        RgbaColor baseBackground = default, RgbaColor gainBackground = default, IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePositive(width, height);

        var baseResult = Base.Pipe(pool).Perspective(homography, width, height, baseBackground).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).Perspective(Conjugate(homography, width, height), Scaled(width, ScaleX),
                Scaled(height, ScaleY), gainBackground).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    /// <summary>Applies the conjugated homography with an explicit sampling contract.</summary>
    public GainMappedImage PerspectiveWithOptions(ReadOnlySpan<float> homography, int width, int height,
        Resampler resampler, SampleOptions options, RgbaColor baseBackground = default,
        RgbaColor gainBackground = default, IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequirePositive(width, height);

        var baseResult = Base.Pipe(pool).PerspectiveWithOptions(homography, width, height, resampler, options,
            baseBackground).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).PerspectiveWithOptions(Conjugate(homography, width, height),
                Scaled(width, ScaleX), Scaled(height, ScaleY), resampler, options, gainBackground).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Warps both renditions with the same normalised mapping — the only warp shape that means the same
    /// thing at two resolutions.
    /// </summary>
    /// <param name="width">Destination width.</param>
    /// <param name="height">Destination height.</param>
    /// <param name="map">Destination → source, in normalised coordinates.</param>
    /// <param name="resampler">Reconstruction kernel.</param>
    /// <param name="baseBackground">Colour outside the source in the base.</param>
    /// <param name="gainBackground">Colour outside the source in the gain map.</param>
    /// <param name="pool">Where the destinations are rented.</param>
    public GainMappedImage WarpNormalized(int width, int height, NormalizedWarp map,
        Resampler resampler = Resampler.Bilinear, RgbaColor baseBackground = default,
        RgbaColor gainBackground = default, IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(map);
        RequirePositive(width, height);

        var baseResult = Base.Pipe(pool).Warp(width, height, Wrap(Base.Width, Base.Height, map), resampler, baseBackground).ToImage();
        try
        {
            var gainResult = GainMap.Pipe(pool).Warp(Scaled(width, ScaleX), Scaled(height, ScaleY),
                Wrap(GainMap.Width, GainMap.Height, map), resampler, gainBackground).ToImage();
            return new GainMappedImage(baseResult, gainResult, Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // HDR
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reconstructs HDR light from the pair for a display with <paramref name="displayHeadroom"/> times
    /// SDR white. Values above one are the point and are not clamped.
    /// </summary>
    /// <param name="displayHeadroom">The display's peak in multiples of SDR white.</param>
    /// <param name="weight">Dial the gain down, 0..1.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException">The headroom or weight is out of range.</exception>
    public Image ToHdr(float displayHeadroom = 4f, float weight = 1f, IPixelPool? pool = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Base.ReplaceBuffer(HdrOps.ReconstructGainMap(Base[0].Pixels.View, GainMap[0].Pixels.View,
            Parameters, displayHeadroom, weight, pool));
    }

    // ---------------------------------------------------------------------------------------------------
    // File I/O
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Reads an Ultra HDR (gain-map) JPEG.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a gain-map JPEG.</exception>
    /// <exception cref="NotSupportedException">The options ask for a crop or a scaled decode, which cannot
    /// be applied to both renditions consistently through this entry point.</exception>
    public static GainMappedImage Load(string path, LoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Load(File.ReadAllBytes(path), options);
    }

    /// <summary>Reads an Ultra HDR (gain-map) JPEG held in memory.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a gain-map JPEG.</exception>
    /// <exception cref="NotSupportedException">The options ask for a crop or a scaled decode.</exception>
    public static GainMappedImage Load(ReadOnlySpan<byte> data, LoadOptions? options = null)
    {
        options ??= LoadOptions.Default;

        // A region or a reduced decode has to mean the same thing for both renditions; this entry point
        // decodes both at full size, so the pair's Crop and Resize are the way to ask for either.
        if (options.Region is not null || (options.ScaleDenominator ?? 1) != 1)
        {
            throw new NotSupportedException(
                "LoadOptions.Region and ScaleDenominator would have to be applied to both renditions at " +
                "once; load at full size and use Crop or Resize on the pair.");
        }

        if (!JpegGainMap.TryRead(data, out var source))
            throw new InvalidDataException("The bytes are not an Ultra HDR (gain-map) JPEG.");

        try
        {
            var baseImage = Image.FromDecodedImage(source.Base, options.Format, options.Pool);
            try
            {
                var gainImage = Image.FromDecodedImage(source.GainMap, options.Format, options.Pool);
                return new GainMappedImage(baseImage, gainImage, FromCodecMetadata(source.Metadata));
            }
            catch
            {
                baseImage.Dispose();
                throw;
            }
        }
        finally
        {
            source.Base.Dispose();
            source.GainMap.Dispose();
        }
    }

    /// <summary>
    /// Builds a pair from an HDR image: the gain map records the inverse of whatever tone map produced
    /// the SDR rendition.
    /// </summary>
    /// <param name="hdr">Display-referred linear light in <see cref="WorkingFormat.Rgba32F"/>; 1.0 is SDR white.</param>
    /// <param name="sdrBase">The SDR rendition, in <see cref="WorkingFormat.Rgba8"/>. Null derives one with
    /// the ACES curve and an sRGB encode. The pair takes ownership of a supplied image.</param>
    /// <param name="parameters">Metadata to record; null uses the fitted bounds from the gain-map computation.</param>
    /// <param name="downscale">How much smaller the gain map is than the base.</param>
    /// <exception cref="ArgumentException">The HDR image is not float, or the base differs in size or format.</exception>
    public static GainMappedImage FromHdr(Image hdr, Image? sdrBase = null, GainMapParameters? parameters = null,
        int downscale = 4)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        if (hdr.Format != WorkingFormat.Rgba32F)
            throw new ArgumentException("The HDR rendition must be Rgba32F linear light.", nameof(hdr));
        if (downscale < 1)
            throw new ArgumentOutOfRangeException(nameof(downscale), downscale, "The downscale must be at least 1.");

        var derived = sdrBase is null ? DeriveSdrBase(hdr) : null;
        var sdr = sdrBase ?? derived!;
        if (sdr.Width != hdr.Width || sdr.Height != hdr.Height)
            throw new ArgumentException("The SDR base differs in size from the HDR rendition.", nameof(sdrBase));

        var hdrPixels = PackFloats(hdr);
        var sdrPixels = PackBytes(sdr);

        var computed = JpegGainMap.Compute(hdrPixels, sdrPixels, hdr.Width, hdr.Height, downscale);

        var gainImage = Image.Create(computed.Width, computed.Height, WorkingFormat.Rgba8);
        var gainPixels = gainImage.WritablePixels;
        for (var i = 0; i < computed.GainMapGray8.Length; i++)
        {
            var offset = i * 4;
            gainPixels[offset] = computed.GainMapGray8[i];
            gainPixels[offset + 1] = computed.GainMapGray8[i];
            gainPixels[offset + 2] = computed.GainMapGray8[i];
            gainPixels[offset + 3] = 255;
        }

        try
        {
            var resolved = parameters ?? FromCodecMetadata(computed.Metadata);
            return new GainMappedImage(sdr, gainImage, resolved);
        }
        catch
        {
            gainImage.Dispose();
            derived?.Dispose();
            throw;
        }
    }
    /// <summary>Writes the pair as an Ultra HDR JPEG: the base and the gain map are encoded, then spliced.</summary>
    /// <exception cref="NotSupportedException">The base is float: encode an SDR rendition, or write the pair's
    /// <see cref="ToHdr"/> as TIFF/PNG instead.</exception>
    public byte[] Encode(SaveOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= SaveOptions.Default;

        var baseJpeg = Image.Encode(Base, ImageFormat.Jpeg, options);
        var gainJpeg = EncodeGainMap(options.JpegQuality ?? 90);
        return JpegGainMap.Assemble(baseJpeg, gainJpeg, ToCodecMetadata(Parameters));
    }

    /// <summary>Writes the pair to a file as an Ultra HDR JPEG.</summary>
    public void Save(string path, SaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, Encode(options));
    }

    // ---------------------------------------------------------------------------------------------------
    // Derivation helpers
    // ---------------------------------------------------------------------------------------------------

    private GainMappedImage Transform(Func<Image, Image> baseOperation, Func<Image, Image> gainOperation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var baseResult = baseOperation(Base);
        try
        {
            return new GainMappedImage(baseResult, gainOperation(GainMap), Parameters);
        }
        catch
        {
            baseResult.Dispose();
            throw;
        }
    }

    /// <summary>Rounds toward the outside of the region so a partly covered gain sample is kept.</summary>
    private PixelRect DeriveCrop(PixelRect region)
    {
        var width = Base.Width;
        var height = Base.Height;
        var gainWidth = GainMap.Width;
        var gainHeight = GainMap.Height;

        var x0 = (int)((long)region.X * gainWidth / width);
        var x1 = (int)(((long)(region.X + region.Width) * gainWidth + width - 1) / width);
        var y0 = (int)((long)region.Y * gainHeight / height);
        var y1 = (int)(((long)(region.Y + region.Height) * gainHeight + height - 1) / height);

        x0 = Math.Clamp(x0, 0, gainWidth - 1);
        x1 = Math.Clamp(x1, x0 + 1, gainWidth);
        y0 = Math.Clamp(y0, 0, gainHeight - 1);
        y1 = Math.Clamp(y1, y0 + 1, gainHeight);

        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// <c>T⁻¹·M·S</c> for the affine form: S takes gain pixels to base pixels on the source side, T takes
    /// them there on the destination side. Written as the closed form rather than two matrix products —
    /// a product round trip would leave <c>A·sx/sx ≠ A</c> in the last bits and break the byte equality
    /// this type promises.
    /// </summary>
    private AffineMatrix Conjugate(AffineMatrix forward, int width, int height)
    {
        var sx = ScaleX;
        var sy = ScaleY;
        var tx = Scaled(width, ScaleX) / (double)width;
        var ty = Scaled(height, ScaleY) / (double)height;

        return new AffineMatrix(
            (float)(forward.A * sx / tx),
            (float)(forward.B * sy / tx),
            (float)(forward.C / tx),
            (float)(forward.D * sx / ty),
            (float)(forward.E * sy / ty),
            (float)(forward.F / ty));
    }

    /// <summary>The same conjugation for the 8-parameter homography, whose last row stays <c>[g h 1]</c>.</summary>
    private float[] Conjugate(ReadOnlySpan<float> homography, int width, int height)
    {
        if (homography.Length != 8)
            throw new ArgumentException("A perspective homography needs eight parameters.", nameof(homography));

        var sx = (float)ScaleX;
        var sy = (float)ScaleY;
        var tx = (float)(Scaled(width, ScaleX) / (double)width);
        var ty = (float)(Scaled(height, ScaleY) / (double)height);

        return
        [
            homography[0] * sx / tx, homography[1] * sy / tx, homography[2] / tx,
            homography[3] * sx / ty, homography[4] * sy / ty, homography[5] / ty,
            homography[6] * sx, homography[7] * sy,
        ];
    }

    private static PixelWarp Wrap(int sourceWidth, int sourceHeight, NormalizedWarp map) =>
        (float x, float y, out float sourceX, out float sourceY) =>
        {
            map(x / sourceWidth, y / sourceHeight, out var u, out var v);
            sourceX = u * sourceWidth;
            sourceY = v * sourceHeight;
        };

    private static int Scaled(int value, double scale) => Math.Max(1, (int)Math.Round(value * scale));

    private static void RequirePositive(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "The destination width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "The destination height must be positive.");
    }

    private static Image DeriveSdrBase(Image hdr)
    {
        using var mapped = hdr.Pipe().ToneMap(ToneMapCurve.Aces).ToImage();
        using var encoded = mapped.Pipe().Delinearize().ToImage();

        var sdr = Image.Create(encoded.Width, encoded.Height, WorkingFormat.Rgba8);
        var source = encoded[0].Pixels.View;
        var row = new float[encoded.Width * 4];
        var bytes = new byte[encoded.Width * 4];

        for (var y = 0; y < encoded.Height; y++)
        {
            PixelsReadRow(source, y, row);
            for (var x = 0; x < row.Length; x++)
                bytes[x] = (byte)Math.Clamp((int)MathF.Round(row[x] * 255f), 0, 255);

            bytes.CopyTo(sdr.WritablePixels.Slice(y * encoded.Width * 4, bytes.Length));
        }

        return sdr;
    }

    // The facade cannot reach Processing's internal row helpers, so the two conversions it needs live here.
    private static void PixelsReadRow(PixelView view, int y, float[] destination)
    {
        var samples = MemoryMarshal.Cast<byte, float>(view.Row(y));
        for (var x = 0; x < view.Width * 4; x++)
            destination[x] = samples[x];
    }

    private static float[] PackFloats(Image image)
    {
        var width = image.Width;
        var packed = new float[width * image.Height * 4];
        var target = packed.AsSpan();
        var view = image[0].Pixels.View;

        for (var y = 0; y < image.Height; y++)
        {
            var samples = MemoryMarshal.Cast<byte, float>(view.Row(y));
            samples[..(width * 4)].CopyTo(target.Slice(y * width * 4, width * 4));
        }

        return packed;
    }

    private static byte[] PackBytes(Image image)
    {
        var width = image.Width;
        var packed = new byte[width * image.Height * 4];
        var view = image[0].Pixels.View;

        if (view.Format != WorkingFormat.Rgba8)
            throw new ArgumentException("The SDR rendition must be Rgba8 (or derived from an HDR image).", nameof(image));

        for (var y = 0; y < image.Height; y++)
            view.Row(y)[..(width * 4)].CopyTo(packed.AsSpan(y * width * 4, width * 4));

        return packed;
    }

    private byte[] EncodeGainMap(int quality)
    {
        var view = GainMap[0].Pixels.View;
        using var stream = new MemoryStream();

        if (TryPackGray(view, out var gray))
        {
            ImageCodecs.Encode(gray, view.Width, view.Height, PixelFormat.Gray8, ImageFormat.Jpeg, stream,
                view.Width, new JpegEncodeOptions { Quality = quality }, ImageMetadata.Empty);
        }
        else
        {
            // A colour gain map is legal; keep every channel instead of silently collapsing it to luma.
            ImageCodecs.Encode(view.Span, view.Width, view.Height, view.Format.ToPixelFormat(), ImageFormat.Jpeg,
                stream, view.Stride, new JpegEncodeOptions { Quality = quality }, ImageMetadata.Empty);
        }

        return stream.ToArray();
    }

    /// <summary>True when all three channels carry the same sample, which is what a computed map looks like.</summary>
    private static bool TryPackGray(PixelView view, out byte[] gray)
    {
        gray = [];
        if (view.Format != WorkingFormat.Rgba8)
            return false;

        var packed = new byte[view.Width * view.Height];
        for (var y = 0; y < view.Height; y++)
        {
            var row = view.Row(y);
            for (var x = 0; x < view.Width; x++)
            {
                var offset = x * 4;
                if (row[offset] != row[offset + 1] || row[offset] != row[offset + 2])
                    return false;

                packed[y * view.Width + x] = row[offset];
            }
        }

        gray = packed;
        return true;
    }

    private static GainMapMetadata ToCodecMetadata(GainMapParameters parameters) => new()
    {
        GainMapMin = parameters.GainMapMin,
        GainMapMax = parameters.GainMapMax,
        Gamma = parameters.Gamma,
        OffsetSdr = parameters.OffsetSdr,
        OffsetHdr = parameters.OffsetHdr,
        HdrCapacityMin = parameters.HdrCapacityMin,
        HdrCapacityMax = parameters.HdrCapacityMax,
        BaseRenditionIsHdr = parameters.BaseRenditionIsHdr,
    };

    private static GainMapParameters FromCodecMetadata(GainMapMetadata metadata) => new(
        metadata.GainMapMin,
        metadata.GainMapMax,
        metadata.Gamma,
        metadata.OffsetSdr,
        metadata.OffsetHdr,
        metadata.HdrCapacityMin,
        metadata.HdrCapacityMax,
        metadata.BaseRenditionIsHdr);
}
