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
            (i, _) => i.Pipe().Invert().ToImage(), (i, _) => i.Clone(x => x.Invert())),

        new("grayscale", "点算子", "灰度", "BT.601 亮度权重", [],
            (i, _) => i.Pipe().Grayscale().ToImage(), (i, _) => i.Clone(x => x.Grayscale(GrayscaleMode.Bt601))),

        new("sepia", "点算子", "棕褐", "全强度 3×3 颜色矩阵", [],
            (i, _) => i.Pipe().Sepia(1f).ToImage(), (i, _) => i.Clone(x => x.Sepia())),

        new("contrast", "点算子", "对比度", "(v − 0.5)·amount + 0.5",
            [new("amount", "强度", 0.2, 3, 0.05, 1.2)],
            (i, p) => i.Pipe().Contrast((float)p["amount"]).ToImage(),
            (i, p) => i.Clone(x => x.Contrast((float)p["amount"]))),

        new("saturation", "点算子", "饱和度", "luma + (v − luma)·amount；0 灰度、1 原样",
            [new("amount", "强度", 0, 2, 0.05, 1.6)],
            (i, p) => i.Pipe().Saturation((float)p["amount"]).ToImage(),
            (i, p) => i.Clone(x => x.Saturate((float)p["amount"]))),

        new("hue", "点算子", "色相旋转", "绕 RGB 轴旋转，度", [new("degrees", "角度", -180, 180, 5, 90)],
            (i, p) => i.Pipe().HueRotate((float)p["degrees"]).ToImage(), null,
            "对手 4.1.3 没有 HueRotate 扩展方法（编译期确认）"),

        new("gamma", "点算子", "伽马", "v^(1/γ)；1 为原样，2.2 近似显示解码",
            [new("gamma", "γ", 0.2, 4, 0.05, 2.2)],
            (i, p) => i.Pipe().Gamma((float)p["gamma"]).ToImage(), null,
            "对手 4.1.3 没有 GammaCorrection 扩展方法（编译期确认）"),

        new("posterize", "点算子", "色阶量化", "每通道压到 n 级", [new("levels", "级数", 2, 32, 1, 6)],
            (i, p) => i.Pipe().Posterize((int)Math.Round(p["levels"])).ToImage(), null,
            "对手 4.1.3 没有 Posterize 扩展方法（编译期确认）"),

        new("brightness", "点算子", "亮度", "加性偏移：0.2 = +0.2×满量程", [new("amount", "偏移", -1, 1, 0.05, 0.2)],
            (i, p) => i.Pipe().Brightness((float)p["amount"]).ToImage(), null,
            "对手同名 Brightness 是乘性，与加性偏移不是一回事（对拍 PSNR 仅 4.23 dB），不设倍数"),

        new("normalize", "点算子", "归一化", "各通道拉伸到满量程", [],
            (i, _) => i.Pipe().Normalize().ToImage(), null, "对手无同名算子"),

        new("tint", "点算子", "着色", "向目标色混合", [new("weight", "权重", 0, 1, 0.05, 0.45)],
            (i, p) => i.Pipe().Tint(new RgbaColor(255, 170, 90, 255), (float)p["weight"]).ToImage(), null, "对手无同名算子"),

        // ---- 第二批点算子：库里早就实现了，实验台一直没有入口 ----
        // None of these have a fair oracle counterpart, so they show the effect and say why rather than
        // inventing a ratio. `levels` and `temperature` are the two the chain view actually leans on.
        new("levels", "点算子", "色阶", "输入黑白点 + γ；抬黑压白就是褪色",
            [new("black", "输入黑点", 0, 0.4, 0.01, 0.08), new("white", "输入白点", 0.6, 1, 0.01, 0.92),
             new("gamma", "γ", 0.4, 2.5, 0.05, 1)],
            (i, p) => i.Pipe().Levels((float)p["black"], (float)p["white"], (float)p["gamma"]).ToImage(), null,
            "对手无同名算子"),

        new("curves", "点算子", "曲线 · S 形", "单调三次插值，强度正=提对比、负=降对比",
            [new("strength", "强度", -1, 1, 0.05, 0.5)],
            (i, p) => i.Pipe().Curves(SCurve(p["strength"])).ToImage(), null, "对手无同名算子"),

        new("temperature", "点算子", "色温", "白平衡增益：正=暖（红升蓝降），负=冷",
            [new("amount", "偏移", -1, 1, 0.05, 0.3)],
            (i, p) => i.Pipe().Temperature((float)p["amount"]).ToImage(), null, "对手无同名算子"),

        new("vibrance", "点算子", "自然饱和度", "低饱和优先增益，饱和处收手",
            [new("amount", "强度", -1, 1, 0.05, 0.5)],
            (i, p) => i.Pipe().Vibrance((float)p["amount"]).ToImage(), null, "对手 Vibrance 参数语义未对齐"),

        new("color-balance", "点算子", "色彩平衡", "阴影/中间调/高光三段各自 RGB 偏移",
            [new("warm", "冷暖", -1, 1, 0.05, 0.3)],
            (i, p) =>
            {
                var w = (float)p["warm"] * 0.08f;
                return i.Pipe().ColorBalance(
                    new ColorBalanceShifts(-w, 0, w), new ColorBalanceShifts(0, 0, 0),
                    new ColorBalanceShifts(w, 0, -w)).ToImage();
            }, null, "对手无同名算子"),

        new("opacity", "点算子", "不透明度", "整图乘 alpha",
            [new("value", "不透明度", 0, 1, 0.05, 0.6)],
            (i, p) => i.Pipe().Opacity((float)p["value"]).ToImage(), null, "对手无同名算子"),

        new("channel-swap", "点算子", "通道重排", "输出 R/G/B 各自取自哪个输入通道",
            [new("mode", "排列", 0, 5, 1, 1)],
            (i, p) => (int)Math.Round(p["mode"]) switch
            {
                1 => i.Pipe().ChannelSwap(2, 1, 0).ToImage(),   // BGR
                2 => i.Pipe().ChannelSwap(1, 2, 0).ToImage(),   // GRB
                3 => i.Pipe().ChannelSwap(0, 2, 1).ToImage(),   // RBG
                4 => i.Pipe().ChannelSwap(2, 0, 1).ToImage(),   // BRG
                5 => i.Pipe().ChannelSwap(1, 0, 2).ToImage(),   // GBR
                _ => i.Pipe().ChannelSwap(0, 1, 2).ToImage(),   // RGB
            }, null, "对手无同名算子"),

        new("alpha-threshold", "点算子", "Alpha 抠图", "alpha 低于阈值置 0，其余保留",
            [new("threshold", "阈值", 0, 1, 0.05, 0.5)],
            (i, p) => i.Pipe().AlphaThreshold((float)p["threshold"]).ToImage(), null, "对手无同名算子"),

        new("colorblind", "点算子", "色觉模拟", "红绿色盲（deuteranopia）的 LMS 矩阵模拟",
            [new("severity", "程度", 0, 1, 0.05, 1)],
            (i, p) => i.Pipe().ColorBlindness(ColorVisionDeficiency.Deuteranopia, (float)p["severity"]).ToImage(), null,
            "对手无同名算子"),

        // ---------- 滤波 ----------
        new("gaussian", "滤波", "高斯模糊", "真实高斯核，σ 参数（非三次 box 近似）",
            [new("sigma", "σ", 0.3, 15, 0.1, 3)],
            (i, p) => i.Pipe().GaussianBlur((float)p["sigma"]).ToImage(),
            (i, p) => i.Clone(x => x.GaussianBlur((float)p["sigma"]))),

        new("box", "滤波", "方框模糊", "半径 r，核 1/(2r+1)", [new("radius", "半径", 1, 40, 1, 3)],
            (i, p) => i.Pipe().BoxBlur((int)Math.Round(p["radius"])).ToImage(),
            (i, p) => i.Clone(x => x.BoxBlur((int)Math.Round(p["radius"])))),

        new("median", "滤波", "中值 3×3", "保边去噪，直方图选第 5 大", [],
            (i, _) => i.Pipe().Median3x3().ToImage(), null, "对手 4.1.3 的 MedianBlur 需要参数且半径语义不同"),

        new("sobel", "滤波", "Sobel 边缘", "梯度幅值，灰度输出", [],
            (i, _) => i.Pipe().Sobel().ToImage(), null, "对手 4.1.3 没有 Sobel 扩展方法（编译期确认）"),

        new("sharpen", "滤波", "USM 锐化", "高斯差分回加",
            [new("amount", "强度", 0.1, 2, 0.05, 0.7), new("sigma", "σ", 0.4, 4, 0.1, 1.2)],
            (i, p) => i.Pipe().GaussianSharpen((float)p["amount"], (float)p["sigma"]).ToImage(), null,
            "对手 GaussianSharpen 的参数含义与名字不对齐，不设倍数"),

        new("motion", "滤波", "运动模糊", "沿角度方向的线积分",
            [new("length", "长度", 3, 41, 2, 15), new("angle", "角度", 0, 180, 5, 0)],
            (i, p) => i.Pipe().MotionBlur((int)Math.Round(p["length"]), (float)p["angle"]).ToImage(), null,
            "对手参数顺序与量纲未对齐，不设倍数"),

        new("bilateral", "滤波", "双边滤波", "保边平滑，空间/值域双 sigma",
            [new("radius", "半径", 1, 6, 1, 3)],
            (i, p) => i.Pipe().Bilateral((int)Math.Round(p["radius"])).ToImage(), null, "对手 sigma 语义与默认值未对齐"),

        new("oilpaint", "滤波", "油画", "邻域直方图取众数桶",
            [new("radius", "半径", 1, 6, 1, 2), new("levels", "色阶桶", 4, 40, 2, 20)],
            (i, p) => i.Pipe().OilPaint((int)Math.Round(p["radius"]), (int)Math.Round(p["levels"])).ToImage(), null,
            "对手参数为 (levels, brushSize)，顺序不同，不设倍数"),

        new("pixelate", "滤波", "马赛克", "块内取均值", [new("block", "块边长", 2, 64, 2, 12)],
            (i, p) => i.Pipe().Pixelate((int)Math.Round(p["block"])).ToImage(),
            (i, p) => i.Clone(x => x.Pixelate((int)Math.Round(p["block"])))),

        new("vignette", "滤波", "暗角", "径向衰减",
            [new("radius", "半径", 0.2, 1.2, 0.05, 0.75), new("strength", "强度", 0.1, 1, 0.05, 0.8)],
            (i, p) => i.Pipe().Vignette((float)p["radius"], (float)p["strength"]).ToImage(), null,
            "对手 Vignette 需要颜色参数，语义不同"),

        new("clahe", "滤波", "CLAHE", "分块直方图均衡，带限幅",
            [new("clip", "限幅", 1, 10, 0.5, 3)],
            (i, p) => i.Pipe().Clahe(8, 8, (float)p["clip"]).ToImage(), null, "对手无 CLAHE"),

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
            (i, _) => i.Pipe().Rotate90().ToImage(), (i, _) => i.Clone(x => x.Rotate(RotateMode.Rotate90))),

        new("rotate180", "几何", "旋转 180°", "整像素搬运，无插值", [],
            (i, _) => i.Pipe().Rotate180().ToImage(), (i, _) => i.Clone(x => x.Rotate(RotateMode.Rotate180))),

        new("flip-h", "几何", "水平翻转", "整像素搬运", [],
            (i, _) => i.Pipe().FlipHorizontal().ToImage(), (i, _) => i.Clone(x => x.Flip(FlipMode.Horizontal))),

        new("flip-v", "几何", "垂直翻转", "整像素搬运", [],
            (i, _) => i.Pipe().FlipVertical().ToImage(), (i, _) => i.Clone(x => x.Flip(FlipMode.Vertical))),

        new("crop", "几何", "居中裁剪", "按百分比保留中间",
            [new("percent", "保留比例", 20, 100, 5, 60)],
            (i, p) => i.Pipe().Crop(CenterRect(i.Width, i.Height, p)).ToImage(),
            (i, p) => i.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(
                CenterRect(i.Width, i.Height, p).X, CenterRect(i.Width, i.Height, p).Y,
                CenterRect(i.Width, i.Height, p).Width, CenterRect(i.Width, i.Height, p).Height)))),

        new("entropy-crop", "几何", "熵裁剪", "按信息量找内容边界",
            [new("threshold", "阈值", 0.05, 0.9, 0.05, 0.25)],
            (i, p) => i.Pipe().EntropyCrop((float)p["threshold"]).ToImage(),
            (i, p) => i.Clone(x => x.EntropyCrop((float)p["threshold"]))),

        new("round-corners", "几何", "圆角", "按半径抹圆角，外圈透明",
            [new("radius", "半径", 0, 200, 4, 48)],
            (i, p) => i.Pipe().RoundCorners((float)p["radius"]).ToImage(), null, "对手无同名算子"),

        new("trim", "几何", "裁剪留白", "去掉四边接近纯色的边",
            [new("tolerance", "容差", 0, 0.3, 0.01, 0.03)],
            (i, p) => i.Pipe().Trim((float)p["tolerance"]).ToImage(), null, "对手无同名算子"),

        // ---------- 色调 / 分析 ----------
        new("equalize", "色调", "直方图均衡", "按亮度累计分布重映射", [],
            (i, _) => i.Pipe().EqualizeHistogram().ToImage(), (i, _) => i.Clone(x => x.HistogramEqualization())),

        new("threshold", "色调", "二值阈值", "按 luma 二值化",
            [new("threshold", "阈值", 0.05, 0.95, 0.05, 0.5)],
            (i, p) => i.Pipe().Threshold((float)p["threshold"]).ToImage(), null, "对手无同语义阈值算子"),

        new("black-white", "色调", "黑白", "硬阈值纯黑白", [new("threshold", "阈值", 0.05, 0.95, 0.05, 0.5)],
            (i, p) => i.Pipe().BlackWhite((float)p["threshold"]).ToImage(), null, "对手无同名算子"),

        // ---- 第二批：不常演示但会被真实负载用到，全部没有对等实现，只做单侧展示 ----
        new("bokeh", "滤波", "散景模糊", "圆盘核，半径 r", [new("radius", "半径", 2, 16, 1, 6)],
            (i, p) => i.Pipe().BokehBlur((int)Math.Round(p["radius"])).ToImage(), null, "对手无同名算子"),

        new("emboss", "滤波", "浮雕", "3×3 方向核", [new("strength", "强度", 0.2, 3, 0.1, 1)],
            (i, p) => i.Pipe().Emboss((float)p["strength"]).ToImage(), null, "对手 4.1.3 无 Emboss 扩展方法（编译期确认）"),

        new("glow", "滤波", "辉光", "亮部抽出后模糊回加",
            [new("threshold", "阈值", 0.4, 0.95, 0.05, 0.7)],
            (i, p) => i.Pipe().Glow((float)p["threshold"]).ToImage(), null, "对手 Glow 参数语义不同"),

        new("guided", "滤波", "导向滤波", "保边平滑，O(1)/像素的分块矩",
            [new("radius", "半径", 2, 16, 1, 6)],
            (i, p) => i.Pipe().GuidedFilter((int)Math.Round(p["radius"])).ToImage(), null, "对手无同名算子"),

        new("adaptive", "滤波", "自适应阈值", "局部均值减偏移后二值化",
            [new("radius", "半径", 2, 32, 2, 8)],
            (i, p) => i.Pipe().AdaptiveThreshold((int)Math.Round(p["radius"])).ToImage(), null, "对手无同名算子"),

        new("erode", "滤波", "腐蚀 3×3", "邻域取最小", [],
            (i, _) => i.Pipe().Erode3x3().ToImage(), null, "对手无同名算子"),

        new("dilate", "滤波", "膨胀 3×3", "邻域取最大", [],
            (i, _) => i.Pipe().Dilate3x3().ToImage(), null, "对手无同名算子"),

        // Radii above 3 go through van Herk rather than the 3x3 specialisation, so the bench can see the difference
        // between the two code paths that now exist.
        new("erode-n", "滤波", "腐蚀 N×N", "任意半径（>3 走 van Herk）",
            [new("radius", "半径", 2, 16, 1, 5)],
            (i, p) => i.Pipe().Morph((int)Math.Round(p["radius"]), Morphology.Erode).ToImage(), null, "对手无同名算子"),

        new("dilate-n", "滤波", "膨胀 N×N", "任意半径（>3 走 van Herk）",
            [new("radius", "半径", 2, 16, 1, 5)],
            (i, p) => i.Pipe().Morph((int)Math.Round(p["radius"]), Morphology.Dilate).ToImage(), null, "对手无同名算子"),

        new("open", "滤波", "开运算", "先腐蚀后膨胀：去亮斑",
            [new("radius", "半径", 2, 16, 1, 5)],
            (i, p) => i.Pipe().Morph((int)Math.Round(p["radius"]), Morphology.Open).ToImage(), null, "对手无同名算子"),

        new("close", "滤波", "闭运算", "先膨胀后腐蚀：填暗斑",
            [new("radius", "半径", 2, 16, 1, 5)],
            (i, p) => i.Pipe().Morph((int)Math.Round(p["radius"]), Morphology.Close).ToImage(), null, "对手无同名算子"),

        new("tophat", "滤波", "顶帽", "原图减开运算：亮细节",
            [new("radius", "半径", 2, 16, 1, 5)],
            (i, p) => i.Pipe().Morph((int)Math.Round(p["radius"]), Morphology.TopHat).ToImage(), null, "对手无同名算子"),

        new("blackhat", "滤波", "黑帽", "闭运算减原图：暗细节",
            [new("radius", "半径", 2, 16, 1, 5)],
            (i, p) => i.Pipe().Morph((int)Math.Round(p["radius"]), Morphology.BlackHat).ToImage(), null, "对手无同名算子"),

        new("laplacian", "滤波", "拉普拉斯", "luma 二阶导，取绝对值",
            [],
            (i, _) => i.Pipe().Laplacian().ToImage(), null, "对手无同名算子"),

        new("prewitt", "滤波", "Prewitt 梯度", "等权重梯度幅值",
            [],
            (i, _) => i.Pipe().Prewitt().ToImage(), null, "对手无同名算子"),

        new("scharr", "滤波", "Scharr 梯度", "3/10/3 权重，旋转对称性更好",
            [],
            (i, _) => i.Pipe().Scharr().ToImage(), null, "对手无同名算子"),

        new("convolve-box", "滤波", "任意核卷积", "盒核，走通用 2D 路径",
            [new("radius", "半径", 1, 8, 1, 3)],
            (i, p) =>
            {
                var radius = (int)Math.Round(p["radius"]);
                var side = (radius * 2) + 1;
                var kernel = new float[side * side];
                Array.Fill(kernel, 1f / (side * side));
                return i.Pipe().Convolve(kernel, radius).ToImage();
            },
            null, "对手无同名算子"),

        new("median-n", "滤波", "中值 N×N", "滑动直方图，任意半径",
            [new("radius", "半径", 2, 8, 1, 4)],
            (i, p) => i.Pipe().Median((int)Math.Round(p["radius"])).ToImage(), null, "对手无同名算子"),

        new("dehaze", "色调", "去雾", "暗通道先验",
            [new("radius", "窗口", 3, 15, 1, 7), new("strength", "强度", 0.1, 2, 0.1, 0.95)],
            (i, p) => i.Pipe().Dehaze((int)Math.Round(p["radius"]), (float)p["strength"]).ToImage(), null, "对手无同名算子"),

        new("pointchain", "色调", "点算子链 · 单遍", "五个 per-channel 变换塌缩成一张表",
            [],
            (i, _) => i.Pipe().PointChain(
                PointOps.ContrastRow(1.1f),
                PointOps.InvertRow(),
                PointOps.ContrastRow(0.9f),
                PointOps.BrightnessRow(0.02f),
                PointOps.InvertRow()).ToImage(),
            null, "对手无同名算子"),

        new("noise", "色调", "加噪", "高斯噪声，固定种子",
            [new("amount", "强度", 0.01, 0.3, 0.01, 0.05)],
            (i, p) => i.Pipe().Noise((float)p["amount"]).ToImage(), null, "对手无同名算子"),

        new("quantize-median", "色调", "量化 · 中位切分", "256 色调色板",
            [new("colors", "色数", 2, 256, 2, 64)],
            (i, p) => i.Pipe().Quantize((int)Math.Round(p["colors"])).ToImage(), null, "对手 Quantize 需自定义量化器，口径不可比"),

        new("quantize-wu", "色调", "量化 · Wu", "256 色调色板",
            [new("colors", "色数", 2, 256, 2, 64)],
            (i, p) => i.Pipe().Quantize((int)Math.Round(p["colors"]), Quantizer.Wu).ToImage(), null, "同上"),

        new("tonemap", "色调", "色调映射", "ACES 曲线",
            (ParamSpec[])[new("exposure", "曝光", -2, 2, 0.1, 0)],
            (i, p) => i.Pipe().ToneMap(ToneMapCurve.Aces, (float)p["exposure"]).ToImage(), null, "对手无同名算子"),

        new("linearize", "色调", "线性化", "sRGB 解码到线性光", [],
            (i, _) => i.Pipe().Linearize(TransferCurve.Srgb).ToImage(), null, "对手无同名算子"),

        new("delinearize", "色调", "反线性化", "线性光编码回 sRGB", [],
            (i, _) => i.Pipe().Delinearize(TransferCurve.Srgb).ToImage(), null, "对手无同名算子"),

        new("dither", "色调", "抖动量化", "Floyd–Steinberg，按位深",
            [new("bits", "位深", 1, 7, 1, 3)],
            (i, p) => i.Pipe().Dither((int)Math.Round(p["bits"])).ToImage(), null, "对手 Dither 参数含义不同"),

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
        image.Pipe().Resize(Target(image.Width, p), Target(image.Height, p), resampler).ToImage();

    internal static PixelRect CenterRect(int width, int height, IReadOnlyDictionary<string, double> p) =>
        CenterRect(width, height, p["percent"]);

    internal static PixelRect CenterRect(int width, int height, double percent)
    {
        var w = Math.Max(1, (int)Math.Round(width * percent / 100));
        var h = Math.Max(1, (int)Math.Round(height * percent / 100));
        return new PixelRect((width - w) / 2, (height - h) / 2, w, h);
    }
}
