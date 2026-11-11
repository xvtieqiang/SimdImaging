using SimdImaging.Abstractions;
using SimdImaging.Processing;
using Sdcb.SimdCodecs.Abstractions;
using SixLabors.ImageSharp.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image;

namespace SimdImaging.Lab;

internal sealed record ParamSpec(string Name, string Label, double Min, double Max, double Step, double Default);

/// <summary>
/// One row of the bench: the operator, its parameters, and <b>both</b> implementations side by side.
/// <para><c>Theirs</c> is null when the oracle has no fair counterpart. That is a real outcome, not a gap to
/// paper over: showing a ratio against a differently-defined "same name" API would be worse than showing
/// nothing. <c>OracleNote</c> says why.</para>
/// </summary>
/// <summary>
/// One row of the bench: the operator, its parameters, and <b>both</b> implementations side by side.
/// <para><c>Theirs</c> is null when the oracle has no fair counterpart. That is a real outcome, not a gap to
/// paper over: showing a ratio against a differently-defined "same name" API would be worse than showing
/// nothing. <c>OracleNote</c> says why.</para>
/// <para><c>Ours</c> is the whole story for every entry: the two-input families (blend modes, Porter-Duff)
/// were pulled from this catalogue on 2026-11-12 — see the README. The run endpoint
/// branches on the flag, so neither is ever invoked against null.</para>
/// </summary>
internal sealed record OpSpec(
    string Id,
    string Group,
    string Title,
    string Note,
    ParamSpec[] Params,
    Func<OurImage, IReadOnlyDictionary<string, double>, OurImage>? Ours,
    Func<TheirImage, IReadOnlyDictionary<string, double>, TheirImage>? Theirs,
    string? OracleNote = null);

internal static class Ops
{

    public static readonly OpSpec[] Catalog =
    [
        // ---------- 点算子 ----------
        new("invert", "点算子", "反相", "1 − v，逐通道，alpha 不变", [],
            (i, _) => i.Invert(), (i, _) => i.Clone(x => x.Invert())),

        new("grayscale", "点算子", "灰度", "BT.601 亮度权重", [],
            (i, _) => i.Grayscale(), (i, _) => i.Clone(x => x.Grayscale(GrayscaleMode.Bt601))),

        new("sepia", "点算子", "棕褐", "全强度 3×3 颜色矩阵", [],
            (i, _) => i.Sepia(1f), (i, _) => i.Clone(x => x.Sepia())),

        new("contrast", "点算子", "对比度", "(v − 0.5)·amount + 0.5",
            [new("amount", "强度", 0.2, 3, 0.05, 1.2)],
            (i, p) => i.Contrast((float)p["amount"]),
            (i, p) => i.Clone(x => x.Contrast((float)p["amount"]))),

        new("saturation", "点算子", "饱和度", "luma + (v − luma)·amount；0 灰度、1 原样",
            [new("amount", "强度", 0, 2, 0.05, 1.6)],
            (i, p) => i.Saturation((float)p["amount"]),
            (i, p) => i.Clone(x => x.Saturate((float)p["amount"]))),

        new("hue", "点算子", "色相旋转", "绕 RGB 轴旋转，度", [new("degrees", "角度", -180, 180, 5, 90)],
            (i, p) => i.HueRotate((float)p["degrees"]), null,
            "对手 4.1.3 没有 HueRotate 扩展方法（编译期确认）"),

        new("gamma", "点算子", "伽马", "v^(1/γ)；1 为原样，2.2 近似显示解码",
            [new("gamma", "γ", 0.2, 4, 0.05, 2.2)],
            (i, p) => i.Gamma((float)p["gamma"]), null,
            "对手 4.1.3 没有 GammaCorrection 扩展方法（编译期确认）"),

        new("posterize", "点算子", "色阶量化", "每通道压到 n 级", [new("levels", "级数", 2, 32, 1, 6)],
            (i, p) => i.Posterize((int)Math.Round(p["levels"])), null,
            "对手 4.1.3 没有 Posterize 扩展方法（编译期确认）"),

        new("brightness", "点算子", "亮度", "加性偏移：0.2 = +0.2×满量程", [new("amount", "偏移", -1, 1, 0.05, 0.2)],
            (i, p) => i.Brightness((float)p["amount"]), null,
            "对手同名 Brightness 是乘性，与加性偏移不是一回事（对拍 PSNR 仅 4.23 dB），不设倍数"),

        new("normalize", "点算子", "归一化", "各通道拉伸到满量程", [],
            (i, _) => i.Normalize(), null, "对手无同名算子"),

        new("tint", "点算子", "着色", "向目标色混合", [new("weight", "权重", 0, 1, 0.05, 0.45)],
            (i, p) => i.Tint(new RgbaColor(255, 170, 90, 255), (float)p["weight"]), null, "对手无同名算子"),

        // ---- 第二批点算子：库里早就实现了，实验台一直没有入口 ----
        // None of these have a fair oracle counterpart, so they show the effect and say why rather than
        // inventing a ratio. `levels` and `temperature` are the two the chain view actually leans on.
        new("levels", "点算子", "色阶", "输入黑白点 + γ；抬黑压白就是褪色",
            [new("black", "输入黑点", 0, 0.4, 0.01, 0.08), new("white", "输入白点", 0.6, 1, 0.01, 0.92),
             new("gamma", "γ", 0.4, 2.5, 0.05, 1)],
            (i, p) => i.Levels((float)p["black"], (float)p["white"], (float)p["gamma"]), null,
            "对手无同名算子"),

        new("curves", "点算子", "曲线 · S 形", "单调三次插值，强度正=提对比、负=降对比",
            [new("strength", "强度", -1, 1, 0.05, 0.5)],
            (i, p) => i.Curves(SCurve(p["strength"])), null, "对手无同名算子"),

        new("temperature", "点算子", "色温", "白平衡增益：正=暖（红升蓝降），负=冷",
            [new("amount", "偏移", -1, 1, 0.05, 0.3)],
            (i, p) => i.Temperature((float)p["amount"]), null, "对手无同名算子"),

        new("vibrance", "点算子", "自然饱和度", "低饱和优先增益，饱和处收手",
            [new("amount", "强度", -1, 1, 0.05, 0.5)],
            (i, p) => i.Vibrance((float)p["amount"]), null, "对手 Vibrance 参数语义未对齐"),

        new("color-balance", "点算子", "色彩平衡", "阴影/中间调/高光三段各自 RGB 偏移",
            [new("warm", "冷暖", -1, 1, 0.05, 0.3)],
            (i, p) =>
            {
                var w = (float)p["warm"] * 0.08f;
                return i.ColorBalance(
                    new ColorBalanceShifts(-w, 0, w), new ColorBalanceShifts(0, 0, 0),
                    new ColorBalanceShifts(w, 0, -w));
            }, null, "对手无同名算子"),

        new("opacity", "点算子", "不透明度", "整图乘 alpha",
            [new("value", "不透明度", 0, 1, 0.05, 0.6)],
            (i, p) => i.Opacity((float)p["value"]), null, "对手无同名算子"),

        new("channel-swap", "点算子", "通道重排", "输出 R/G/B 各自取自哪个输入通道",
            [new("mode", "排列", 0, 5, 1, 1)],
            (i, p) => (int)Math.Round(p["mode"]) switch
            {
                1 => i.ChannelSwap(2, 1, 0),   // BGR
                2 => i.ChannelSwap(1, 2, 0),   // GRB
                3 => i.ChannelSwap(0, 2, 1),   // RBG
                4 => i.ChannelSwap(2, 0, 1),   // BRG
                5 => i.ChannelSwap(1, 0, 2),   // GBR
                _ => i.ChannelSwap(0, 1, 2),   // RGB
            }, null, "对手无同名算子"),

        new("alpha-threshold", "点算子", "Alpha 抠图", "alpha 低于阈值置 0，其余保留",
            [new("threshold", "阈值", 0, 1, 0.05, 0.5)],
            (i, p) => i.AlphaThreshold((float)p["threshold"]), null, "对手无同名算子"),

        new("colorblind", "点算子", "色觉模拟", "红绿色盲（deuteranopia）的 LMS 矩阵模拟",
            [new("severity", "程度", 0, 1, 0.05, 1)],
            (i, p) => i.ColorBlindness(ColorVisionDeficiency.Deuteranopia, (float)p["severity"]), null,
            "对手无同名算子"),

        // ---------- 滤波 ----------
        new("gaussian", "滤波", "高斯模糊", "真实高斯核，σ 参数（非三次 box 近似）",
            [new("sigma", "σ", 0.3, 15, 0.1, 3)],
            (i, p) => i.GaussianBlur((float)p["sigma"]),
            (i, p) => i.Clone(x => x.GaussianBlur((float)p["sigma"]))),

        new("box", "滤波", "方框模糊", "半径 r，核 1/(2r+1)", [new("radius", "半径", 1, 40, 1, 3)],
            (i, p) => i.BoxBlur((int)Math.Round(p["radius"])),
            (i, p) => i.Clone(x => x.BoxBlur((int)Math.Round(p["radius"])))),

        new("median", "滤波", "中值 3×3", "保边去噪，直方图选第 5 大", [],
            (i, _) => i.Median3x3(), null, "对手 4.1.3 的 MedianBlur 需要参数且半径语义不同"),

        new("sobel", "滤波", "Sobel 边缘", "梯度幅值，灰度输出", [],
            (i, _) => i.Sobel(), null, "对手 4.1.3 没有 Sobel 扩展方法（编译期确认）"),

        new("sharpen", "滤波", "USM 锐化", "高斯差分回加",
            [new("amount", "强度", 0.1, 2, 0.05, 0.7), new("sigma", "σ", 0.4, 4, 0.1, 1.2)],
            (i, p) => i.GaussianSharpen((float)p["amount"], (float)p["sigma"]), null,
            "对手 GaussianSharpen 的参数含义与名字不对齐，不设倍数"),

        new("motion", "滤波", "运动模糊", "沿角度方向的线积分",
            [new("length", "长度", 3, 41, 2, 15), new("angle", "角度", 0, 180, 5, 0)],
            (i, p) => i.MotionBlur((int)Math.Round(p["length"]), (float)p["angle"]), null,
            "对手参数顺序与量纲未对齐，不设倍数"),

        new("bilateral", "滤波", "双边滤波", "保边平滑，空间/值域双 sigma",
            [new("radius", "半径", 1, 6, 1, 3)],
            (i, p) => i.Bilateral((int)Math.Round(p["radius"])), null, "对手 sigma 语义与默认值未对齐"),

        new("oilpaint", "滤波", "油画", "邻域直方图取众数桶",
            [new("radius", "半径", 1, 6, 1, 2), new("levels", "色阶桶", 4, 40, 2, 20)],
            (i, p) => i.OilPaint((int)Math.Round(p["radius"]), (int)Math.Round(p["levels"])), null,
            "对手参数为 (levels, brushSize)，顺序不同，不设倍数"),

        new("pixelate", "滤波", "马赛克", "块内取均值", [new("block", "块边长", 2, 64, 2, 12)],
            (i, p) => i.Pixelate((int)Math.Round(p["block"])),
            (i, p) => i.Clone(x => x.Pixelate((int)Math.Round(p["block"])))),

        new("vignette", "滤波", "暗角", "径向衰减",
            [new("radius", "半径", 0.2, 1.2, 0.05, 0.75), new("strength", "强度", 0.1, 1, 0.05, 0.8)],
            (i, p) => i.Vignette((float)p["radius"], (float)p["strength"]), null,
            "对手 Vignette 需要颜色参数，语义不同"),

        new("clahe", "滤波", "CLAHE", "分块直方图均衡，带限幅",
            [new("clip", "限幅", 1, 10, 0.5, 3)],
            (i, p) => i.Clahe(8, 8, (float)p["clip"]), null, "对手无 CLAHE"),

        // ---------- 几何 ----------
        new("resize-nearest", "几何", "缩放 · 最近邻", "最快，块状",
            [new("percent", "百分比", 10, 200, 5, 50)],
            (i, p) => ResizeOurs(i, p, Resampler.Nearest),
            (i, p) => i.Clone(x => x.Resize(Target(i.Width, p), Target(i.Height, p), KnownResamplers.NearestNeighbor))),

        new("resize-bilinear", "几何", "缩放 · 双线性", "三角形核",
            [new("percent", "百分比", 10, 200, 5, 50)],
            (i, p) => ResizeOurs(i, p, Resampler.Bilinear),
            (i, p) => i.Clone(x => x.Resize(Target(i.Width, p), Target(i.Height, p), KnownResamplers.Triangle))),

        new("resize-bicubic", "几何", "缩放 · 双三次", "Catmull-Rom 族",
            [new("percent", "百分比", 10, 200, 5, 50)],
            (i, p) => ResizeOurs(i, p, Resampler.Bicubic),
            (i, p) => i.Clone(x => x.Resize(Target(i.Width, p), Target(i.Height, p), KnownResamplers.Bicubic))),

        new("resize-lanczos", "几何", "缩放 · Lanczos3", "窗化 sinc，最锐",
            [new("percent", "百分比", 10, 200, 5, 50)],
            (i, p) => ResizeOurs(i, p, Resampler.Lanczos3),
            (i, p) => i.Clone(x => x.Resize(Target(i.Width, p), Target(i.Height, p), KnownResamplers.Lanczos3))),

        new("rotate90", "几何", "旋转 90°", "整像素搬运，无插值", [],
            (i, _) => i.Rotate90(), (i, _) => i.Clone(x => x.Rotate(RotateMode.Rotate90))),

        new("rotate180", "几何", "旋转 180°", "整像素搬运，无插值", [],
            (i, _) => i.Rotate180(), (i, _) => i.Clone(x => x.Rotate(RotateMode.Rotate180))),

        new("flip-h", "几何", "水平翻转", "整像素搬运", [],
            (i, _) => i.FlipHorizontal(), (i, _) => i.Clone(x => x.Flip(FlipMode.Horizontal))),

        new("flip-v", "几何", "垂直翻转", "整像素搬运", [],
            (i, _) => i.FlipVertical(), (i, _) => i.Clone(x => x.Flip(FlipMode.Vertical))),

        new("crop", "几何", "居中裁剪", "按百分比保留中间",
            [new("percent", "保留比例", 20, 100, 5, 60)],
            (i, p) => i.Crop(CenterRect(i.Width, i.Height, p)),
            (i, p) => i.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(
                CenterRect(i.Width, i.Height, p).X, CenterRect(i.Width, i.Height, p).Y,
                CenterRect(i.Width, i.Height, p).Width, CenterRect(i.Width, i.Height, p).Height)))),

        new("entropy-crop", "几何", "熵裁剪", "按信息量找内容边界",
            [new("threshold", "阈值", 0.05, 0.9, 0.05, 0.25)],
            (i, p) => i.EntropyCrop((float)p["threshold"]),
            (i, p) => i.Clone(x => x.EntropyCrop((float)p["threshold"]))),

        new("round-corners", "几何", "圆角", "按半径抹圆角，外圈透明",
            [new("radius", "半径", 0, 200, 4, 48)],
            (i, p) => i.RoundCorners((float)p["radius"]), null, "对手无同名算子"),

        new("trim", "几何", "裁剪留白", "去掉四边接近纯色的边",
            [new("tolerance", "容差", 0, 0.3, 0.01, 0.03)],
            (i, p) => i.Trim((float)p["tolerance"]), null, "对手无同名算子"),

        // ---------- 色调 / 分析 ----------
        new("equalize", "色调", "直方图均衡", "按亮度累计分布重映射", [],
            (i, _) => i.EqualizeHistogram(), (i, _) => i.Clone(x => x.HistogramEqualization())),

        new("threshold", "色调", "二值阈值", "按 luma 二值化",
            [new("threshold", "阈值", 0.05, 0.95, 0.05, 0.5)],
            (i, p) => i.Threshold((float)p["threshold"]), null, "对手无同语义阈值算子"),

        new("black-white", "色调", "黑白", "硬阈值纯黑白", [new("threshold", "阈值", 0.05, 0.95, 0.05, 0.5)],
            (i, p) => i.BlackWhite((float)p["threshold"]), null, "对手无同名算子"),

        // ---- 第二批：不常演示但会被真实负载用到，全部没有对等实现，只做单侧展示 ----
        new("bokeh", "滤波", "散景模糊", "圆盘核，半径 r", [new("radius", "半径", 2, 16, 1, 6)],
            (i, p) => i.BokehBlur((int)Math.Round(p["radius"])), null, "对手无同名算子"),

        new("emboss", "滤波", "浮雕", "3×3 方向核", [new("strength", "强度", 0.2, 3, 0.1, 1)],
            (i, p) => i.Emboss((float)p["strength"]), null, "对手 4.1.3 无 Emboss 扩展方法（编译期确认）"),

        new("glow", "滤波", "辉光", "亮部抽出后模糊回加",
            [new("threshold", "阈值", 0.4, 0.95, 0.05, 0.7)],
            (i, p) => i.Glow((float)p["threshold"]), null, "对手 Glow 参数语义不同"),

        new("guided", "滤波", "导向滤波", "保边平滑，O(1)/像素的分块矩",
            [new("radius", "半径", 2, 16, 1, 6)],
            (i, p) => i.GuidedFilter((int)Math.Round(p["radius"])), null, "对手无同名算子"),

        new("adaptive", "滤波", "自适应阈值", "局部均值减偏移后二值化",
            [new("radius", "半径", 2, 32, 2, 8)],
            (i, p) => i.AdaptiveThreshold((int)Math.Round(p["radius"])), null, "对手无同名算子"),

        new("erode", "滤波", "腐蚀 3×3", "邻域取最小", [],
            (i, _) => i.Erode3x3(), null, "对手无同名算子"),

        new("dilate", "滤波", "膨胀 3×3", "邻域取最大", [],
            (i, _) => i.Dilate3x3(), null, "对手无同名算子"),

        new("noise", "色调", "加噪", "高斯噪声，固定种子",
            [new("amount", "强度", 0.01, 0.3, 0.01, 0.05)],
            (i, p) => i.Noise((float)p["amount"]), null, "对手无同名算子"),

        new("quantize-median", "色调", "量化 · 中位切分", "256 色调色板",
            [new("colors", "色数", 2, 256, 2, 64)],
            (i, p) => i.Quantize((int)Math.Round(p["colors"])), null, "对手 Quantize 需自定义量化器，口径不可比"),

        new("quantize-wu", "色调", "量化 · Wu", "256 色调色板",
            [new("colors", "色数", 2, 256, 2, 64)],
            (i, p) => i.Quantize((int)Math.Round(p["colors"]), Quantizer.Wu), null, "同上"),

        new("tonemap", "色调", "色调映射", "ACES 曲线",
            (ParamSpec[])[new("exposure", "曝光", -2, 2, 0.1, 0)],
            (i, p) => i.ToneMap(ToneMapCurve.Aces, (float)p["exposure"]), null, "对手无同名算子"),

        new("linearize", "色调", "线性化", "sRGB 解码到线性光", [],
            (i, _) => i.Linearize(TransferCurve.Srgb), null, "对手无同名算子"),

        new("delinearize", "色调", "反线性化", "线性光编码回 sRGB", [],
            (i, _) => i.Delinearize(TransferCurve.Srgb), null, "对手无同名算子"),

        new("dither", "色调", "抖动量化", "Floyd–Steinberg，按位深",
            [new("bits", "位深", 1, 7, 1, 3)],
            (i, p) => i.Dither((int)Math.Round(p["bits"])), null, "对手 Dither 参数含义不同"),

    ];

    public static Dictionary<string, double> Resolve(OpSpec spec, Dictionary<string, double>? given)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in spec.Params)
        {
            var value = given is not null && given.TryGetValue(parameter.Name, out var g) ? g : parameter.Default;
            result[parameter.Name] = Math.Clamp(value, parameter.Min, parameter.Max);
        }

        return result;
    }

    /// <summary>Five-point S curve: strength 0 is the identity, positive steepens, negative flattens.</summary>
    internal static CurvePoint[] SCurve(double strength)
    {
        var k = (float)Math.Clamp(strength, -1, 1) * 0.18f;
        return
        [
            new CurvePoint(0f, 0f),
            new CurvePoint(0.25f, Math.Clamp(0.25f - k, 0f, 1f)),
            new CurvePoint(0.5f, 0.5f),
            new CurvePoint(0.75f, Math.Clamp(0.75f + k, 0f, 1f)),
            new CurvePoint(1f, 1f),
        ];
    }

    private static int Target(int size, IReadOnlyDictionary<string, double> p) =>
        Math.Max(1, (int)Math.Round(size * p["percent"] / 100));

    private static OurImage ResizeOurs(OurImage image, IReadOnlyDictionary<string, double> p, Resampler resampler) =>
        image.Resize(Target(image.Width, p), Target(image.Height, p), resampler);

    internal static PixelRect CenterRect(int width, int height, IReadOnlyDictionary<string, double> p) =>
        CenterRect(width, height, p["percent"]);

    internal static PixelRect CenterRect(int width, int height, double percent)
    {
        var w = Math.Max(1, (int)Math.Round(width * percent / 100));
        var h = Math.Max(1, (int)Math.Round(height * percent / 100));
        return new PixelRect((width - w) / 2, (height - h) / 2, w, h);
    }
}
