using SimdImaging.Abstractions;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;

namespace SimdImaging.Lab;

/// <summary>
/// One step of a recipe: a name, how it reads its source, and the operator itself.
/// <para>The operator takes the resolved parameters so a whole chain is rebuilt per request. Rebuilding is
/// cheap — <see cref="ProcessingPlan"/> is a list of delegates, not compiled code.</para>
/// </summary>
internal sealed record ChainStep(
    string Name,
    OpCategory Category,
    int HaloX,
    int HaloY,
    // Single-input body. Null when the step composites, in which case OverlayOp is the body instead.
    Func<PixelView, IReadOnlyDictionary<string, double>, PixelBuffer>? Op,
    // Composite body: backdrop view, a demo layer sized to match it, parameters. Full-frame, no resampling.
    // Views, not Images: the plan hands kernels a view, and Blend.Apply takes views directly.
    Func<PixelView, PixelView, IReadOnlyDictionary<string, double>, PixelBuffer>? OverlayOp = null,
    // Which synthetic layer to generate when the recipe is composited ("mark" / "glow" / "alpha").
    string? OverlayDemo = null);

/// <summary>
/// A recipe: several operators that together produce one recognisable look, plus the one or two knobs that
/// matter. The point of the bench view is that these are the same primitives as the single-operator tab —
/// nothing here is a special case, and the output must be identical to calling the operators by hand.
/// </summary>
internal sealed record ChainSpec(
    string Id,
    string Group,
    string Title,
    string Note,
    ParamSpec[] Params,
    ChainStep[] Steps,
    string? Caveat = null,
    // Set when the recipe composites: the layer kind to generate, sized to the backdrop by the caller.
    string? OverlayDemo = null)
{
    /// <summary>Turns the recipe into a runnable plan for the given parameters.</summary>
    /// <param name="parameters">Resolved parameter values.</param>
    /// <param name="overlay">The demo layer, required when <see cref="OverlayDemo"/> is set. Must outlive
    /// every execution of the returned plan: composite steps close over its pixels.</param>
    /// <exception cref="InvalidOperationException">The recipe composites but no overlay was supplied.</exception>
    public ProcessingPlan Build(IReadOnlyDictionary<string, double> parameters, OurImage? overlay = null)
    {
        var plan = new ProcessingPlan();

        foreach (var step in Steps)
        {
            OpKernel kernel;

            if (step.OverlayOp is not null)
            {
                if (overlay is null)
                    throw new InvalidOperationException(
                        $"Recipe '{Id}' composites and needs an overlay image; none was supplied.");

                var overlayOp = step.OverlayOp;
                var overlayView = overlay[0].Pixels.View;
                kernel = PlanSteps.Global(view => overlayOp(view, overlayView, parameters));
            }
            else
            {
                var op = step.Op!;
                kernel = step.Category switch
                {
                    OpCategory.Point => PlanSteps.Point(view => op(view, parameters)),
                    OpCategory.Neighborhood => PlanSteps.Neighborhood(
                        view => op(view, parameters), step.HaloX, step.HaloY),
                    _ => PlanSteps.Global(view => op(view, parameters)),
                };
            }

            plan.Add(step.Name, step.Category, kernel, step.HaloX, step.HaloY);
        }

        return plan;
    }
}

internal static class Chains
{
    /// <summary>Gaussian halo for a sigma, matching the kernel builder's <c>ceil(3σ)</c>.</summary>
    private static int HaloFor(float sigma) => (int)Math.Ceiling(3 * sigma);

    public static readonly ChainSpec[] Catalog =
    [
        new("vintage-film", "配方 · 风格", "复古胶片",
            "降对比 → 降饱和 → 暖色偏移 → 抬黑压白（褪色）→ 暗角 → 颗粒",
            [new("strength", "整体强度", 0, 1, 0.05, 0.8)],
            [
                new("contrast", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Contrast(v, (float)(1 - 0.15 * p["strength"]))),

                new("desaturate", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Saturation(v, (float)(1 - 0.28 * p["strength"]))),

                new("warm", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Temperature(v, (float)(0.26 * p["strength"]))),

                // Faded film: the black point never reaches 0 and the white point never reaches 1, which is
                // the single most recognisable part of the look. Everything above it is only seasoning.
                new("fade", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Levels(v,
                        inputBlack: (float)(0.05 * p["strength"]),
                        inputWhite: (float)(1 - 0.07 * p["strength"]),
                        gamma: 1f)),

                // Global, not Point or Neighborhood: Vignette derives its falloff from the centre of the view
                // it is handed, so a band would get its own centre and the picture would end up with one
                // ellipse per band instead of one overall. Caught by looking at the output, not by a test.
                new("vignette", OpCategory.Global, 0, 0,
                    (v, p) => FilterOps.Vignette(v, 0.75f, (float)(0.55 * p["strength"]))),

                // Global, not Point: the seeded RNG advances per row, so slicing it into bands would repeat
                // the same sequence in every band and the grain would turn into horizontal stripes.
                new("grain", OpCategory.Global, 0, 0,
                    (v, p) => NoiseOps.Noise(v, (float)(0.05 * p["strength"]), NoiseKind.Gaussian, seed: 20261107u)),
            ],
            "颗粒走 Global（整图），因为它按行推进固定种子，切带会让每带重播同一段序列、变成横条纹。"
            + "它也是这条链的时间大头：1080p 约 40 ms，其余五步合计约 12 ms。"),

        new("pencil-sketch", "配方 · 风格", "线稿化",
            "灰度 → 轻微去噪 → Sobel 边缘 → 反相 → 提对比，得到白底黑线的铅笔稿",
            [new("detail", "细节强度", 0.2, 1, 0.05, 0.7)],
            [
                new("gray", OpCategory.Point, 0, 0,
                    (v, _) => PointOps.Grayscale(v)),

                // Before the edge pass, not after: Sobel amplifies whatever noise it finds, and a photograph
                // carries plenty. σ is fixed low so the line stays thin.
                new("denoise", OpCategory.Neighborhood, HaloFor(0.6f), HaloFor(0.6f),
                    (v, _) => FilterOps.GaussianBlur(v, 0.6f)),

                new("edges", OpCategory.Neighborhood, 1, 1,
                    (v, _) => FilterOps.Sobel(v)),

                new("invert", OpCategory.Point, 0, 0,
                    (v, _) => PointOps.Invert(v)),

                // Levels, not Contrast. After the invert the page is almost entirely white and the lines sit
                // in the upper half of the range; Contrast stretches around the midpoint, so it pushed the
                // lines *towards* white and the first attempt came out barely visible. Pulling the black
                // point up is what actually inks them.
                new("ink", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Levels(v,
                        inputBlack: (float)(0.55 - 0.30 * p["detail"]),
                        inputWhite: 1f,
                        gamma: 1f)),
            ],
            "Sobel 的 halo 是 1（3×3 邻域），去噪那步的 halo 按 ceil(3σ) 给 2。两处都必须给足，否则带边界会出现接缝。"),

        new("mono-film", "配方 · 风格", "黑白胶片",
            "灰度 → S 曲线提对比 → 颗粒 → 暗角；黑白比彩色更挑对比，S 曲线在这里就是主角",
            [new("strength", "整体强度", 0, 1, 0.05, 0.8)],
            [
                new("gray", OpCategory.Point, 0, 0,
                    (v, _) => PointOps.Grayscale(v)),

                // An S curve, not Contrast: Contrast pivots every value around the midpoint equally, while
                // the curve keeps the extremes pinned and bends only the middle — that is what "punchy but
                // not clipped" means for monochrome.
                new("curve", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Curves(v, Ops.SCurve(0.6 + 0.8 * p["strength"]))),

                new("grain", OpCategory.Global, 0, 0,
                    (v, p) => NoiseOps.Noise(v, (float)(0.04 * p["strength"]), NoiseKind.Gaussian, seed: 1935u)),

                new("vignette", OpCategory.Global, 0, 0,
                    (v, p) => FilterOps.Vignette(v, 0.8f, (float)(0.45 * p["strength"]))),
            ],
            "颗粒与暗角都走 Global：前者按行推进固定种子，后者从视图中心算衰减。"),

        new("landscape-pop", "配方 · 风格", "风光增强",
            "CLAHE 拉开局部层次 → 自然饱和度 → 轻微 USM 锐化；每个算子各管一件事",
            [new("strength", "整体强度", 0.2, 1, 0.05, 0.7)],
            [
                // CLAHE first: it works on the luminance distribution, so boosting colour before it would
                // just be amplified twice. Equalising the local contrast is the step that actually makes a
                // hazy landscape read.
                new("clahe", OpCategory.Global, 0, 0,
                    (v, p) => ClaheOps.Clahe(v, 8, 8, (float)(1.5 + 3 * p["strength"]))),

                new("vibrance", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Vibrance(v, (float)(0.25 + 0.45 * p["strength"]))),

                // σ 1 halo 3; sharpening after the contrast work so the edge gain is applied to the final
                // tonality, not to something CLAHE is about to redistribute.
                new("sharpen", OpCategory.Neighborhood, 3, 3,
                    (v, p) => FilterOps.GaussianSharpen(v, (float)(0.3 + 0.5 * p["strength"]), 1f)),
            ],
            "CLAHE 是 Global（tile 直方图依赖全图分布），不能切带；锐化走 Neighborhood，halo 按 ceil(3σ) 给 3。"),

        new("mist-cool", "配方 · 风格", "雾蓝冷调",
            "降对比 → 冷色偏移 → 抬黑发灰 → 降饱和 → 暗角；和复古胶片同一套积木，方向相反",
            [new("strength", "整体强度", 0, 1, 0.05, 0.7)],
            [
                new("contrast", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Contrast(v, (float)(1 - 0.2 * p["strength"]))),

                new("cool", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Temperature(v, (float)(-0.35 * p["strength"]))),

                new("fade", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Levels(v,
                        inputBlack: (float)(0.08 * p["strength"]),
                        inputWhite: (float)(1 - 0.04 * p["strength"]),
                        gamma: 1f)),

                new("desaturate", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Saturation(v, (float)(1 - 0.3 * p["strength"]))),

                new("vignette", OpCategory.Global, 0, 0,
                    (v, p) => FilterOps.Vignette(v, 0.85f, (float)(0.4 * p["strength"]))),
            ],
            "证明配方的价值不在算子新，在顺序与方向：同样五类积木，暖变冷、颗粒去掉、灰度不去，出来的就是另一种天气。"),

        new("brand-mark", "配方 · 合成", "品牌水印",
            "水印层正片叠底压印 → 提对比 → 轻微锐化；验证「合成之后继续处理」这条路能不能走通",
            [new("strength", "整体强度", 0.2, 1, 0.05, 0.7)],
            [
                // The white card drops out under Multiply and only the strokes print — the same thing a logo
                // layer does in any editor. Full-frame by construction: the caller generates the layer at the
                // backdrop's size, so there is no resampling step hiding inside the blend.
                new("mark", OpCategory.Global, 0, 0, null,
                    (bg, ov, _) => Blend.Apply(bg, ov, 0, 0, BlendMode.Multiply)),

                new("contrast", OpCategory.Point, 0, 0,
                    (v, p) => PointOps.Contrast(v, (float)(1 + 0.15 * p["strength"]))),

                new("sharpen", OpCategory.Neighborhood, 3, 3,
                    (v, p) => FilterOps.GaussianSharpen(v, (float)(0.25 + 0.35 * p["strength"]), 1f)),
            ],
            "唯一需要第二张图的配方：水印层按底图尺寸生成，同尺寸全幅混合。它也标出了 ProcessingPlan 的一条边界"
            + "——合成步骤只能以 Global 整图执行，因为 Blend 要读整幅叠加层，切带没有意义。",
            OverlayDemo: "mark"),
    ];

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
