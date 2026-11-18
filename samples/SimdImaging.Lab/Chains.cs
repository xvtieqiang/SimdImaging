using SimdImaging.Abstractions;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Lab;

/// <summary>
/// A recipe: a name, a note, one knob, and a method that extends a pipeline.
/// <para>Every recipe is written the way a caller writes one — a straight chain, no per-step decoration, no
/// categories or halos. Composition, banding and halo sizing are the pipeline's business; a sample that restated
/// them would be teaching a second, wrong API on top of the real one.</para>
/// </summary>
internal sealed record ChainSpec(
    string Id,
    string Group,
    string Title,
    string Note,
    ParamSpec[] Params,
    Func<ImagePipeline, float, ImagePipeline> Run,
    string? Caveat = null,
    // Set when the recipe starts by compositing a second image: the pipeline has no blend step, and inventing one
    // here would hide the fact that a composite is a whole-image operation.
    Func<OurImage, OurImage, OurImage>? Compose = null,
    // Set when the recipe composites: the layer kind to generate, sized to the backdrop by the caller.
    string? OverlayDemo = null)
{
    /// <summary>
    /// Runs the recipe and returns the finished image, which the caller owns.
    /// </summary>
    /// <param name="source">The first image; not disposed.</param>
    /// <param name="parameters">Resolved parameter values.</param>
    /// <param name="overlay">The layer, for a recipe that composites.</param>
    /// <exception cref="InvalidOperationException">The recipe composites but no overlay was supplied.</exception>
    public OurImage Apply(OurImage source, IReadOnlyDictionary<string, double> parameters, OurImage? overlay = null)
    {
        var first = Compose is null
            ? source
            : Compose(source, overlay ?? throw new InvalidOperationException(
                $"Recipe '{Id}' composites and needs an overlay image; none was supplied."));

        return Run(first.Pipe(), (float)parameters["strength"]).ToImage();
    }
}

internal static class Chains
{
    /// <summary>Every recipe is one pipeline chain, written the way a caller would write it.</summary>
    public static readonly ChainSpec[] Catalog =
    [
        new("vintage-film", "配方 · 风格", "复古胶片",
            "降对比 → 降饱和 → 暖色偏移 → 抬黑压白（褪色）→ 暗角 → 颗粒",
            [new("strength", "整体强度", 0, 1, 0.05, 0.8)],
            VintageFilm,
            "颗粒走整图：随机数按行推进，切带会让每带重播同一段序列、变成横条纹。它也是这条链的时间大头。"),

        new("pencil-sketch", "配方 · 风格", "线稿化",
            "灰度 → 轻微去噪 → Sobel 边缘 → 反相 → 提黑点，得到白底黑线的铅笔稿",
            [new("strength", "整体强度", 0.2, 1, 0.05, 0.7)],
            PencilSketch,
            "去噪放在边缘之前：Sobel 会放大它找到的任何噪点，而照片本身就有不少。"),

        new("mono-film", "配方 · 风格", "黑白胶片",
            "灰度 → S 曲线提对比 → 颗粒 → 暗角；黑白比彩色更挑对比",
            [new("strength", "整体强度", 0, 1, 0.05, 0.8)],
            MonoFilm,
            "用 S 曲线而不是 Contrast：Contrast 把所有值绕中点等比拉开，曲线只弯中间、两端不动——"
            + "「提气但不切白」就是这个意思。"),

        new("landscape-pop", "配方 · 风格", "风光增强",
            "局部直方图均衡 → 自然饱和度 → 锐化",
            [new("strength", "整体强度", 0.2, 1, 0.05, 0.7)],
            LandscapePop,
            "CLAHE 按块均衡，块边界会被放大；所以先均衡再锐化，顺序反了会把块边也锐出来。"),

        new("mist-cool", "配方 · 风格", "雾蓝冷调",
            "降对比 → 冷色偏移 → 抬黑发灰 → 降饱和 → 暗角；和复古胶片同一套积木，方向相反",
            [new("strength", "整体强度", 0, 1, 0.05, 0.7)],
            MistCool,
            "证明配方的价值不在算子新，在顺序与方向：暖变冷、加颗粒变不加，出来的就是另一种天气。"),

        new("brand-mark", "配方 · 合成", "品牌水印",
            "水印层正片叠底压印 → 提对比 → 轻微锐化；验证「合成之后继续处理」这条路",
            [new("strength", "整体强度", 0.2, 1, 0.05, 0.7)],
            BrandMark,
            "唯一需要第二张图的配方：水印层按底图尺寸生成，同尺寸全幅混合，之后继续走流水线。",
            Compose: (backdrop, layer) => backdrop.Pipe().Blend(layer, 0, 0, BlendMode.Multiply).ToImage(),
            OverlayDemo: "mark"),
    ];

    // Fixed seeds: the bench has to show the same grain twice, so reproducibility is worth more than randomness.
    private const uint FilmGrainSeed = 20261107u;
    private const uint MonoGrainSeed = 1935u;

    /// <summary>The faded film look: flat blacks, muted colour, warm, then grain on top.</summary>
    private static ImagePipeline VintageFilm(ImagePipeline pipe, float strength)
    {
        // At full strength the picture is 15% flatter and 28% duller; 1 means "unchanged" to both operators.
        var contrast = 1f - (0.15f * strength);
        var saturation = 1f - (0.28f * strength);
        var warmth = 0.26f * strength;
        var vignette = 0.55f * strength;
        var grain = 0.05f * strength;

        // The black point never reaches 0 and the white point never reaches 1 — the single most recognisable part
        // of the look; everything above it is seasoning.
        var fadedBlack = 0.05f * strength;
        var fadedWhite = 1f - (0.07f * strength);

        return pipe.Contrast(contrast)
            .Saturation(saturation)
            .Temperature(warmth)
            .Levels(inputBlack: fadedBlack, inputWhite: fadedWhite)
            .Vignette(radius: 0.75f, darkening: vignette)
            .Noise(amplitude: grain, kind: NoiseKind.Gaussian, seed: FilmGrainSeed);
    }

    /// <summary>White paper, black lines: flatten to luma, denoise, take edges, invert, ink the blacks.</summary>
    private static ImagePipeline PencilSketch(ImagePipeline pipe, float strength)
    {
        // At full strength the lines start at mid-grey (0.55) rather than black; more detail pulls it down.
        var inkBlack = 0.55f - (0.30f * strength);

        return pipe.Grayscale()
            .GaussianBlur(sigma: 0.6f)
            .Sobel()
            .Invert()
            // Levels, not Contrast: after the invert the page is almost entirely white and the lines sit in the
            // upper half of the range. Contrast stretches around the midpoint and pushed the lines towards white,
            // which is why the first attempt came out barely visible. Pulling the black point up is what inks them.
            .Levels(inputBlack: inkBlack, inputWhite: 1f);
    }

    /// <summary>Monochrome with the middle bent and the ends pinned, then grain and a vignette.</summary>
    private static ImagePipeline MonoFilm(ImagePipeline pipe, float strength)
    {
        // The curve bends between 0.6 and 1.4; 1.0 would be the identity.
        var curveAmount = 0.6 + (0.8 * strength);
        var grain = 0.04f * strength;
        var vignette = 0.45f * strength;

        return pipe.Grayscale()
            .Curves(Ops.SCurve(curveAmount))
            .Noise(amplitude: grain, kind: NoiseKind.Gaussian, seed: MonoGrainSeed)
            .Vignette(radius: 0.8f, darkening: vignette);
    }

    /// <summary>Local contrast, then saturation weighted towards what is already saturated, then sharpening.</summary>
    private static ImagePipeline LandscapePop(ImagePipeline pipe, float strength)
    {
        // CLAHE: 8x8 tiles, clipping twice as hard at full strength as at the bottom of the range.
        const int Tiles = 8;
        var clipLimit = 1.5f + (3 * strength);

        // 0.25 is a gentle lift, up to 0.70 at full strength.
        var vibrance = 0.25f + (0.45f * strength);
        var sharpen = 0.3f + (0.5f * strength);

        return pipe.Clahe(tilesX: Tiles, tilesY: Tiles, clipLimit: clipLimit)
            .Vibrance(vibrance)
            .GaussianSharpen(gain: sharpen);
    }

    /// <summary>The same blocks as the vintage film, pointing the other way: cold, grey, flat.</summary>
    private static ImagePipeline MistCool(ImagePipeline pipe, float strength)
    {
        var contrast = 1f - (0.2f * strength);
        var chill = -0.35f * strength;      // negative: cools instead of warms
        var fadedBlack = 0.08f * strength;
        var fadedWhite = 1f - (0.04f * strength);
        var saturation = 1f - (0.3f * strength);
        var vignette = 0.4f * strength;

        return pipe.Contrast(contrast)
            .Temperature(chill)
            .Levels(inputBlack: fadedBlack, inputWhite: fadedWhite)
            .Saturation(saturation)
            .Vignette(radius: 0.85f, darkening: vignette);
    }

    /// <summary>What runs after the blend: a little more contrast and a little more edge.</summary>
    private static ImagePipeline BrandMark(ImagePipeline pipe, float strength)
    {
        var contrast = 1f + (0.15f * strength);   // above 1: more contrast, unlike the film looks
        var sharpen = 0.25f + (0.35f * strength);

        return pipe.Contrast(contrast).GaussianSharpen(gain: sharpen);
    }

    public static Dictionary<string, double> Resolve(ChainSpec spec, Dictionary<string, double>? given)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in spec.Params)
        {
            var value = given is not null && given.TryGetValue(parameter.Name, out var g) ? g : parameter.Default;
            result[parameter.Name] = Math.Clamp(value, parameter.Min, parameter.Max);
        }

        return result;
    }
}
