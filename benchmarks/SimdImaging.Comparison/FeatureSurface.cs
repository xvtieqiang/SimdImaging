using System.Reflection;
using System.Text;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Comparison;

/// <summary>A1–A4. Everything here is read from the *public metadata* of the pinned black-box assembly
/// and our own assemblies. No source, no IL decompilation, no implementation inspection: this is the
/// same information an API reference exposes. A missing method NAME is not proof of a missing FEATURE
/// (a different name may implement it); the report says so explicitly.</summary>
internal static class FeatureSurface
{
    private static readonly (string Domain, string Name, string TheirNames, string OurNames)[] Capabilities =
    [
        // Geometry
        ("几何", "Resize", "Resize", "Resize"),
        ("几何", "按一边缩放", "Resize|ResizeToWidth", "ResizeToWidth|ResizeToHeight"),
        ("几何", "ResizeMode 五种模式", "Resize", "Resize"),
        ("几何", "Crop", "Crop", "Crop"),
        ("几何", "Pad", "Pad|Resize", "Pad"),
        ("几何", "Trim", "Trim|EntropyCrop", "Trim"),
        ("几何", "EntropyCrop", "EntropyCrop", "EntropyCrop"),
        ("几何", "Rotate 90/180", "Rotate|RotateFlip", "Rotate90|Rotate180"),
        ("几何", "任意角旋转", "Rotate", "Rotate"),
        ("几何", "Flip H/V", "Flip", "FlipHorizontal|FlipVertical"),
        ("几何", "Skew", "Skew", "Affine"),
        ("几何", "Affine", "Transform|AffineTransform", "Affine"),
        ("几何", "Perspective", "Transform|ProjectiveTransform", "Perspective"),
        ("几何", "AutoOrient", "AutoOrient", "AutoOrient"),
        ("几何", "ResizeCanvas", "Resize", "ResizeCanvas"),
        ("几何", "Tile", "Tile", "Tile"),
        ("几何", "RoundCorners", "RoundCorners", "RoundCorners"),
        ("几何", "Transpose / RotateFlip", "RotateFlip", "Transpose|RotateFlip"),
        ("几何", "Warp / 委托变换", "Transform", "Warp"),
        ("几何", "点采样", "Sample|ProcessPixelRows", "SamplePixel"),
        // Colour / point
        ("色彩", "Brightness", "Brightness", "Brightness"),
        ("色彩", "Contrast", "Contrast", "Contrast"),
        ("色彩", "Saturation", "Saturate", "Saturation"),
        ("色彩", "Hue", "Hue", "HueRotate"),
        ("色彩", "Lightness", "Lightness", "Lightness"),
        ("色彩", "Opacity", "Opacity", "Opacity"),
        ("色彩", "Invert", "Invert", "Invert"),
        ("色彩", "Gamma", "Gamma", "Gamma"),
        ("色彩", "Levels", "Levels", "Levels"),
        ("色彩", "Curves", "Curves", "Curves"),
        ("色彩", "ColorMatrix", "Filter", "ColorMatrix"),
        ("色彩", "ChannelSwap", "Swizzle", "ChannelSwap"),
        ("色彩", "Grayscale", "Grayscale", "Grayscale"),
        ("色彩", "BlackWhite / 二值化", "BinaryThreshold", "BlackWhite|Threshold"),
        ("色彩", "Posterize", "Posterize", "Posterize"),
        ("色彩", "Sepia", "Sepia", "Sepia"),
        ("色彩", "Tint", "Tint", "Tint"),
        ("色彩", "ColorBalance", "ColorBalance", "ColorBalance"),
        ("色彩", "Temperature", "Temperature", "Temperature"),
        ("色彩", "Vibrance", "Vibrance", "Vibrance"),
        ("色彩", "Normalize", "Normalize", "Normalize"),
        ("色彩", "Clamp", "Clamp", "Clamp"),
        ("色彩", "Premultiply/Unpremultiply", "Premultiply|Unpremultiply", "Premultiply|Unpremultiply"),
        ("色彩", "HistogramEqualization", "HistogramEqualization", "EqualizeHistogram"),
        ("色彩", "AdaptiveThreshold", "AdaptiveThreshold", "AdaptiveThreshold"),
        ("色彩", "ColorBlindness", "ColorBlindness", "ColorBlindness"),
        ("色彩", "BackgroundColor", "BackgroundColor", "ReplaceBackground"),
        ("色彩", "Linearize / 传递曲线", "Linearize|Delinearize", "Linearize|Delinearize"),
        ("色彩", "ToneMap HDR", "ToneMap", "ToneMap"),
        ("色彩", "GainMap", "ApplyGainMap", "ApplyGainMap"),
        ("色彩", "ICC 转换", "ColorProfileConverter", "IccTransform"),
        ("色彩", "色彩空间转换", "ColorSpaceConvert", "ConvertColorSpace"),
        ("色彩", "色度重采样", "ChromaResample", "ChromaResample"),
        // Filters
        ("滤波", "GaussianBlur", "GaussianBlur", "GaussianBlur"),
        ("滤波", "BoxBlur", "BoxBlur", "BoxBlur"),
        ("滤波", "Median", "MedianBlur", "Median3x3"),
        ("滤波", "Bilateral", "Bilateral", "Bilateral"),
        ("滤波", "BokehBlur", "BokehBlur", "BokehBlur"),
        ("滤波", "MotionBlur", "MotionBlur", "MotionBlur"),
        ("滤波", "Sharpen / unsharp", "Sharpen", "Sharpen"),
        ("滤波", "GaussianSharpen", "GaussianSharpen", "GaussianSharpen"),
        ("滤波", "DetectEdges", "DetectEdges", "Sobel"),
        ("滤波", "Emboss", "Emboss", "Emboss"),
        ("滤波", "OilPaint", "OilPaint", "OilPaint"),
        ("滤波", "Pixelate", "Pixelate", "Pixelate"),
        ("滤波", "Vignette", "Vignette", "Vignette"),
        ("滤波", "Glow", "Glow", "Glow"),
        ("滤波", "Dither", "Dither|BinaryDither", "Dither"),
        ("滤波", "Morphology", "Erode|Dilate|Open|Close", "Erode3x3|Dilate3x3|Open3x3|Close3x3"),
        ("滤波", "CLAHE", "Clahe", "Clahe"),
        ("滤波", "Noise", "Noise", "Noise"),
        ("滤波", "GuidedFilter", "GuidedFilter", "GuidedFilter"),
        ("滤波", "任意卷积", "Convolve|Filter", "Convolve3x3"),
        // Quantization
        ("量化", "Quantize", "Quantize", "Quantize"),
        ("量化", "Octree", "OctreeQuantizer", "Quantize"),
        ("量化", "Wu", "WuQuantizer", "Quantize"),
        ("量化", "WebSafe", "WebSafeQuantizer", "QuantizeWebSafe"),
        ("量化", "调色板 / 抖动", "PaletteQuantizer|Dither", "BuildPalette|MapToPalette|DitherToPalette"),
        // Analysis
        ("分析", "Histogram", "Histogram", "Histogram"),
        ("分析", "Statistics", "Statistics", "Statistics"),
        ("分析", "PSNR", "Psnr", "Psnr"),
        ("分析", "SSIM", "Ssim", "Ssim"),
        ("分析", "Diff", "Diff", "Diff"),
        ("分析", "AlphaBounds", "AlphaBounds", "AlphaBounds"),
        ("分析", "DominantColors", "DominantColors", "DominantColors"),
        // Composition / drawing
        ("合成绘制", "DrawImage", "DrawImage", "DrawImage"),
        ("合成绘制", "BlendModes", "Blend", "Blend"),
        ("合成绘制", "PorterDuff", "DrawImage", "Composite"),
        ("合成绘制", "Fill / Clear", "Fill|Clear", "Fill|Clear"),
        ("合成绘制", "DrawLine", "DrawLine", "DrawLine"),
        ("合成绘制", "FillEllipse / Polygon", "FillEllipse|FillPolygon", "FillEllipse|FillPolygon"),
        ("合成绘制", "FillPath / StrokePath", "FillPath|DrawPath", "FillPath|DrawPath"),
        ("合成绘制", "Clip", "Clip", "ClipPath"),
        ("合成绘制", "Gradient", "Gradient", "LinearGradient|RadialGradient|SweepGradient"),
        ("合成绘制", "FloodFill", "FloodFill", "FloodFill"),
        ("合成绘制", "Watermark", "DrawImage", "Watermark"),
        // IO / metadata
        ("容器元数据", "Load / Save / Encode", "Load|Save|Encode", "Load|Save|Encode"),
        ("容器元数据", "Identify", "Identify", "Load"),
        ("容器元数据", "Transcode", "Save", "Transcode"),
        ("容器元数据", "Frames / 动画", "Frames|CloneFrames", "Frames|KeepFrames|ComposeAnimation"),
        ("容器元数据", "MetadataTransfer", "Metadata", "WithMetadata|CopyMetadataFrom"),
        ("容器元数据", "Thumbnail", "Thumbnail", "Thumbnail"),
    ];

    private static HashSet<string> MethodNames(params Assembly[] assemblies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        foreach (var type in SafeTypes(assembly))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                              BindingFlags.Static | BindingFlags.DeclaredOnly))
            names.Add(method.Name);
        return names;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
    }

    private static bool AnyPresent(HashSet<string> names, string candidates) =>
        candidates.Split('|').Any(names.Contains);

    private static string Signature(HashSet<string> names, Assembly[] assemblies, string method)
    {
        foreach (var assembly in assemblies)
        foreach (var type in SafeTypes(assembly))
        {
            var m = type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                    BindingFlags.Static | BindingFlags.DeclaredOnly)
                .FirstOrDefault(x => x.Name == method);
            if (m is null) continue;
            var args = string.Join(", ", m.GetParameters().Select(p =>
                p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue ? "=" + (p.DefaultValue ?? "null") : "")));
            return $"{type.Name}.{m.Name}({args})";
        }

        return "—";
    }

    public static void Write(Report report)
    {
        var theirAssembly = typeof(SixLabors.ImageSharp.Image).Assembly;
        var ourAssemblies = new[]
        {
            typeof(OurImage).Assembly, typeof(PointOps).Assembly,
            typeof(SimdImaging.Abstractions.WorkingFormat).Assembly,
        };
        var theirs = MethodNames(theirAssembly);
        var ours = MethodNames(ourAssemblies);

        report.Heading(2, "A 组 · 功能面（公开 API 元数据探测）");
        report.Line($"> 对手：`{theirAssembly.GetName().Name}` {Env.OracleVersion}（NuGet 4.1.3）黑盒程序集，只读取公开类型与方法名。");
        report.Line("> 我方：`SimdImaging` / `SimdImaging.Processing` / `SimdImaging.Abstractions` 三个程序集。");
        report.Line("> **读法**：「名称未命中」只表示公开方法名不同，不等于功能缺失；需人工复核语义。这是 clean-room 下能拿到的最硬证据。");
        report.Line();

        var ourTypes = ourAssemblies.Sum(a => SafeTypes(a).Count());
        var theirTypes = SafeTypes(theirAssembly).Count();
        var ourOps = typeof(OurImage).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(m => m.ReturnType == typeof(OurImage) && !m.IsSpecialName);
        var theirProcessors = SafeTypes(theirAssembly).Count(t =>
            t.Namespace is not null && t.Namespace.Contains("Processors", StringComparison.Ordinal) &&
            t.Name.EndsWith("Processor", StringComparison.Ordinal));

        report.Table(["指标", "ImageSharp 4.1.3", "SimdImaging", "说明"],
        [
            ["公开类型数", theirTypes.ToString(), ourTypes.ToString(), "程序集导出的 public 类型"],
            ["公开方法名数（去重）", theirs.Count.ToString(), ours.Count.ToString(), "含扩展方法、属性访问器"],
            ["处理器类（*Processor）", theirProcessors.ToString(), "—", "我方不走处理器类，算子是纯静态方法"],
            ["门面上的算子方法", "—", ourOps.ToString(), "`Image` 上返回新 `Image` 的公开方法"],
        ]);

        report.Heading(3, "A1 算子能力覆盖矩阵");
        var rows = new List<string[]>();
        var both = 0; var onlyOurs = 0; var onlyTheirs = 0; var neither = 0;
        foreach (var capability in Capabilities)
        {
            var hasTheir = AnyPresent(theirs, capability.TheirNames);
            var hasOur = AnyPresent(ours, capability.OurNames);
            var status = (hasTheir, hasOur) switch
            {
                (true, true) => "双方",
                (false, true) => "仅我方命中",
                (true, false) => "仅对手命中",
                _ => "均未命中",
            };
            if (hasTheir && hasOur) both++;
            else if (hasOur) onlyOurs++;
            else if (hasTheir) onlyTheirs++;
            else neither++;
            rows.Add([capability.Domain, capability.Name, capability.TheirNames.Replace("|", " / "),
                capability.OurNames.Replace("|", " / "), status]);
        }

        report.Table(["域", "能力", "对手 API 名", "我方 API 名", "命中"], rows);
        report.Line($"**汇总**：共 {Capabilities.Length} 项能力，双方命中 {both}，仅我方 {onlyOurs}，仅对手 {onlyTheirs}，均未命中 {neither}。");
        report.Line();

        report.Heading(3, "A2 格式与像素格式面");
        var formatInterface = theirAssembly.GetType("SixLabors.ImageSharp.Formats.IImageFormat");
        var theirFormats = formatInterface is null
            ? []
            : SafeTypes(theirAssembly).Where(t => t.IsClass && !t.IsAbstract &&
                                                  formatInterface.IsAssignableFrom(t))
                .Select(t => t.Name.Replace("Format", "", StringComparison.Ordinal)).Distinct().OrderBy(x => x).ToArray();
        var theirPixels = SafeTypes(theirAssembly)
            .Where(t => t.IsValueType && t.Namespace == "SixLabors.ImageSharp.PixelFormats" &&
                        t.GetInterfaces().Any(i => i.Name == "IPixel" || i.Name.StartsWith("IPixel`", StringComparison.Ordinal)))
            .Select(t => t.Name).Distinct().OrderBy(x => x).ToArray();
        var imageFormatEnum = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return SafeTypes(a); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.IsEnum && t.Name == "ImageFormat");
        var ourFormats = imageFormatEnum is null ? [] : Enum.GetNames(imageFormatEnum);
        var workingFormats = Enum.GetNames<SimdImaging.Abstractions.WorkingFormat>();

        report.Line($"- 对手公开格式类型（{theirFormats.Length}）：{string.Join(", ", theirFormats)}");
        report.Line($"- 对手公开像素格式（{theirPixels.Length}）：{string.Join(", ", theirPixels)}");
        report.Line($"- 我方 `ImageFormat` 枚举（{ourFormats.Length}）：{string.Join(", ", ourFormats)}");
        report.Line($"- 我方工作格式（{workingFormats.Length}）：{string.Join(", ", workingFormats)}");
        report.Line("- 格式编解码由 `Sdcb.SimdCodecs` 承担，差集属 codec 层，不在本项目范围；`known-gaps.md` 已登记 EXR / TGA / CgBI PNG / ANI。");
        report.Line();

        report.Heading(3, "A3 参数与语义等价（代表算子的方法签名）");
        var pairs = new (string Capability, string Their, string Our)[]
        {
            ("Resize", "Resize", "Resize"), ("Grayscale", "Grayscale", "Grayscale"),
            ("Brightness", "Brightness", "Brightness"), ("GaussianBlur", "GaussianBlur", "GaussianBlur"),
            ("BoxBlur", "BoxBlur", "BoxBlur"), ("Quantize", "Quantize", "Quantize"),
            ("Crop", "Crop", "Crop"), ("Psnr", "Psnr", "Psnr"),
        };
        report.Table(["能力", "ImageSharp 4.1.3 签名", "SimdImaging 签名"],
            pairs.Select(p => new[] { p.Capability, Signature(theirs, [theirAssembly], p.Their),
                Signature(ours, ourAssemblies, p.Our) }));

        report.Heading(3, "A4 扩展与组合 API");
        var theirTypeNames = new HashSet<string>(SafeTypes(theirAssembly).Select(t => t.Name), StringComparer.Ordinal);
        var ourTypeNames = new HashSet<string>(ourAssemblies.SelectMany(SafeTypes).Select(t => t.Name), StringComparer.Ordinal);
        var extensionChecks = new (string Item, string Their, string Our)[]
        {
            ("原地修改 Mutate", "Mutate", "Fill"),
            ("克隆式流水线 Clone(...)", "Clone", "Clone"),
            ("自定义处理器接口", "IImageProcessor", "IImageOp"),
            ("行级访问", "ProcessPixelRows", "PixelView"),
            ("配置对象", "Configuration", "LoadOptions"),
            ("计划 / 融合", "ProcessingPlan", "ProcessingPlan"),
            ("算子 halo 契约", "MapSourceRect", "MapSourceRect"),
            ("取消令牌", "CancellationToken", "CancellationToken"),
        };
        report.Table(["扩展点", "ImageSharp 4.1.3", "SimdImaging"],
            extensionChecks.Select(c => new[]
            {
                c.Item,
                (theirTypeNames.Contains(c.Their) || theirs.Contains(c.Their)) ? "✓" : "—",
                (ourTypeNames.Contains(c.Our) || ours.Contains(c.Our)) ? "✓" : "—",
            }));

        report.Heading(3, "A1 附录 · 对手公开处理器扩展方法名（可核查）");
        report.Line("> 用来复核上面「仅我方命中」的条目：如果某个能力在这里找不到同名方法，只说明**没有同名处理器**，");
        report.Line("> 仍可能由 `Filter` / `ProcessPixelRows` / 自定义 `IImageProcessor` 组合实现。此处不替对手下缺失结论。");
        report.Line();
        var extensionNames = SafeTypes(theirAssembly)
            .Where(t => t.Namespace is not null &&
                        t.Namespace.StartsWith("SixLabors.ImageSharp.Processing", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        report.Line($"共 {extensionNames.Length} 个去重方法名：");
        report.Line();
        report.Line("```text");
        report.Line(string.Join(", ", extensionNames));
        report.Line("```");
    }
}
