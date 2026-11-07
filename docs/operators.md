# SimdImaging 算子清单（v0.1 设计稿）

> 配套 [`architecture.md`](architecture.md)。共 **112 个算子**，按域分组。  
> 这张表是 M0 之后所有实现的唯一来源：编号进代码、进测试名、进基准用例名。

## 图例

**阶段**：`P0` = M1（骨架 + 几何核心）｜`P1` = M2–M3（色彩、滤波、量化、绘制）｜`P2` = M4+（HDR、ICC、高级）

**确定性**：

- `D0` —— 整数定点路径，跨 SIMD 档位、跨平台逐字节一致
- `D1` —— 浮点路径，固定运算序 + 单次舍入 FMA，跨档位逐字节一致，跨 ISA 由 IEEE 保证
- `D0/D1` —— 8/16 位走整数（D0），float 工作格式走浮点（D1）
- `D2` —— 算法本身有顺序依赖（误差扩散、串行聚类），但扫描序写死，与并行度无关
- `D3` —— 不确定。**库内不允许出现**

**格式**：未标注即 `Rgba8 / Rgba16 / Rgba32F` 全支持。

---

## A. 容器与帧（8）

| 编号   | 算子               | 语义要点                                              | 实现策略（算法 + SIMD）                                                                                              | 确定性 | 阶段 |
| ---- | ---------------- | ------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ | --- | -- |
| IO-1 | Load             | 路径/字节/流；嗅探与解码全交 SimdCodecs                        | 无内核。`Region` / `ScaleDenominator` / `Pool` 直接下推 `DecodeOptions`；结果经 `PixelConverter.ConvertRow` **一次**落到工作格式 | D0  | P0 |
| IO-2 | Save             | 目标格式协商：`GetClosestInputFormat` → 目标 `PixelFormat` | 无内核。tight 缓冲直接 `RasterImage.Wrap` 零拷贝；非 tight 显式复制并计数                                                        | D0  | P0 |
| IO-3 | Transcode        | 同格式重写，只换元数据                                       | 字节搬运 + 元数据块替换，不解像素                                                                                           | D0  | P1 |
| IO-4 | AutoOrient       | EXIF Orientation 1–8 → 8 种转置组合                    | 委托 GEO-9/10/11/12，一次查表                                                                                       | D0  | P0 |
| IO-5 | Frames           | 枚举/选取/丢弃帧，保留 `Duration`                           | 帧数组操作，无像素拷贝                                                                                                  | D0  | P1 |
| IO-6 | AnimationCompose | GIF disposal 0–3 / WebP blend → 画布合成              | 逐帧 SourceOver，复用 CMP-3                                                                                       | D0  | P2 |
| IO-7 | MetadataTransfer | Exif / XMP / ICC / cICP / mDCV / cLLI 透传与覆盖       | 无内核。写回走各格式 writer 的选项参数                                                                                      | D0  | P0 |
| IO-8 | Thumbnail        | 原生缩略图 → 1/2–1/8 下推 → 残余缩放 → 轻锐化                   | IO-1 + GEO-5 + FLT-8 组合；JPEG 走 DCT 域可省 5–8×                                                                  | D0  | P1 |

## B. 几何（20）

| 编号     | 算子               | 语义要点                                            | 实现策略（算法 + SIMD）                                                                                  | 确定性   | 阶段                                         |
| ------ | ---------------- | ----------------------------------------------- | ------------------------------------------------------------------------------------------------ | ----- | ------------------------------------------ |
| GEO-1  | Crop             | 矩形/相对/锚点                                        | 无重采样。tight 时整行 `Buffer.MemoryCopy`；非 tight 出视图零拷贝                                                | D0    | P0                                         |
| GEO-2  | Pad              | 背景色 / edge / mirror / tile 四种模式                 | 先分配目标，四边填充；edge 整行复制，mirror 反向行复制                                                                | D0    | P0                                         |
| GEO-3  | Trim             | 剔除与角像素同色的均匀边框                                   | 四边逐行向量比较 + `SequenceEqual`，O(周长)                                                                 | D0    | P1                                         |
| GEO-4  | EntropyCrop      | 按行/列熵阈值裁掉低信息边界                                  | 两遍：行列表的 256-bin 直方图 → 熵 → 阈值定位                                                                   | D1    | P1                                         |
| GEO-5  | Resize           | **已实现 5 种**（设计目标 16 种，见下表）                       | 可分离两遍：水平行内核 + 垂直列内核。当前实现走 f32 两遍 + 字节域 LUT，权重表按 (srcLen, dstLen, kernel) 缓存（24 条有界）；行带 scratch 预算 4 MiB；下采样按 1/scale 缩放支撑 | D1    | P0 已实现 5 种，其余 P1 未做 |
| GEO-6  | ResizeMode       | Stretch / BoxPad / CropPad / Max / Min + 9 宫格锚点 | 先算目标矩形，再对子区重采样，避免二次拷贝                                                                            | D0    | P1                                         |
| GEO-7  | ResizeByAxis     | 只给宽或高，保持比例                                      | 算术 + 委托 GEO-5                                                                                    | D0    | P0                                         |
| GEO-8  | ResizeCanvas     | 扩到目标宽高比（不缩放内容）                                  | 计算 pad 偏移 + GEO-2                                                                                | D0    | P1                                         |
| GEO-9  | Rotate90/180/270 | 无损转置                                            | 8×8 字节块转置 / 4×4 u16 块；`Ssse3.Shuffle` + `Unpack`，或 `AdvSimd` 表查；270 复用 90 的镜像                    | D0    | P0                                         |
| GEO-10 | FlipH / FlipV    | 水平/垂直镜像                                         | 水平：`Avx2.Shuffle` 反转 16 字节块；垂直：交换行引用或整行复制                                                        | D0    | P0                                         |
| GEO-11 | Transpose        | 转置 / 副转置                                        | 同 GEO-9 的块转置                                                                                     | D0    | P1                                         |
| GEO-12 | RotateFlip       | 旋转 + 翻转的组合                                      | 组合 GEO-9/10/11，合并为一次遍历                                                                           | D0    | P1                                         |
| GEO-13 | Rotate           | 任意角，Nearest/Bilinear/Bicubic 采样                 | 逆映射 + 采样；cos/sin 预计算为 Q16 定点，按输出行扫描                                                              | D1    | P1                                         |
| GEO-14 | Skew             | X/Y 错切                                          | 等价仿射；水平错切按行整数偏移，垂直错切按行带重采样                                                                       | D1    | P1                                         |
| GEO-15 | Affine           | `Matrix3x2` + 采样器 + 边界模式                        | 通用逆矩阵；可选 2×2 超采样抗锯齿                                                                              | D1    | P1                                         |
| GEO-16 | Projective       | 3×3 单应                                          | 按行递推 1/w（避免逐像素除法），再乘回                                                                            | D1    | P2                                         |
| GEO-17 | Tile             | 平铺 / 镜像平铺                                       | 目标行按周期复制源行块，行宽整数倍时整块 `memcpy`                                                                    | D0    | P1                                         |
| GEO-18 | CustomWarp       | 用户提供 `ISampler`                                 | 逐像素接口调用（非热路径）；另提供非泛型委托重载，AOT 安全                                                                  | D1    | P2                                         |
| GEO-19 | RoundCorners     | 圆角/圆形裁剪，带抗锯齿                                    | 解析覆盖率或 4×4 超采样生成 mask，乘 alpha；覆盖率按行 SIMD                                                         | D0    | P1                                         |
| GEO-20 | Sample           | 单点/多点坐标采样（最近/双线性）                               | 抽出的公共采样器，供 GEO-13/15/16/18 复用                                                                    | D1    | P1                                         |

### GEO-5 的 16 种重采样器（设计目标）

> **实现状态（2026-11-06 核实）**：`Resampler` 枚举当前只有 **5 种** —— `Nearest` / `Box` / `Bilinear` /
> `Bicubic`（Catmull-Rom, B=0 C=0.5）/ `Lanczos3`。下表其余 11 种是**设计目标，尚未实现**。
> 对标报告与 README 不得按 16 种宣称功能覆盖；ImageSharp 4.1.3 公开了 16 个 `KnownResamplers` 名称。

| 家族                       | 核            | 用途             |
| ------------------------ | ------------ | -------------- |
| NearestNeighbor          | 1 邻域         | 像素画、掩膜、索引图     |
| Box                      | 面积平均         | 大比例下采样（无振铃）    |
| Bilinear / Triangle      | 三角           | 通用快速           |
| Bicubic (B,C 可调)         | 三次           | 通用高质量默认        |
| CatmullRom               | B=0, C=0.5   | 锐利放大           |
| MitchellNetravali        | B=1/3, C=1/3 | 无振铃平衡          |
| CubicBSpline             | B=1, C=0     | 最平滑            |
| Lanczos2 / 3 / 5 / 8     | sinc 窗       | 高质量缩放（3 为默认推荐） |
| Spline36                 | 三次 B 样条近似    | 与 Lanczos3 竞争  |
| Hermite                  | 三次 Hermite   | 快速平滑           |
| Robidoux / RobidouxSharp | 优化 B,C       | 下采样锐度          |
| Welch                    | 抛物线窗         | 轻量             |

## C. 色彩与逐像素（35）

| 编号     | 算子                      | 语义要点                                                                                                                     | 实现策略（算法 + SIMD）                                                               | 确定性   | 阶段 |
| ------ | ----------------------- | ------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------- | ----- | -- |
| COL-1  | Brightness              | 加性偏移（**自有语义**：0.2 = +0.2×满量程；不与对手同名 API 对齐，也不提供兼容模式）                                                  | u8 加宽到 u16 → 加 → clamp → 饱和窄化；4–16 px/iter                                    | D0    | P0 |
| COL-2  | Contrast                | `(v-128)·k+128`                                                                                                          | Q8 定点乘加；与 COL-1 融合成单 LUT                                                      | D0    | P0 |
| COL-3  | Saturation              | `luma + (v-luma)·k`                                                                                                      | 需三通道重排：u16 定点或 f32；BT.601/709 权重可选                                            | D0/D1 | P1 |
| COL-4  | HueRotate               | RGB 轴旋转矩阵                                                                                                                | 3×3 矩阵，f32 四通道一组；u8 走 Q12                                                     | D1    | P1 |
| COL-5  | Lightness               | 亮度偏移/增益                                                                                                                  | 同 COL-1，融合                                                                    | D0    | P1 |
| COL-6  | Alpha / Opacity         | 乘 alpha（Q8）                                                                                                              | 4 通道独立乘，`(t+(t>>8))>>8`                                                       | D0    | P0 |
| COL-7  | Invert                  | `255-v` / `65535-v`                                                                                                      | 异或全 1 掩码                                                                      | D0    | P0 |
| COL-8  | Gamma                   | `v^(1/γ)`                                                                                                                | u8：256 项 LUT；u16：4096 段分段线性 LUT；f32 直算                                        | D0/D1 | P1 |
| COL-9  | Levels                  | 输入黑/白/γ + 输出黑/白                                                                                                          | 合成单张 LUT，一次应用                                                                 | D0    | P1 |
| COL-10 | Curves                  | 每通道控制点，单调三次插值                                                                                                            | 预计算 256/4096 项 LUT；插值用 Fritsch–Carlson 保单调                                    | D0    | P1 |
| COL-11 | ColorMatrix             | 5×4 矩阵（含偏移）                                                                                                              | f32：`Vector128<float>` 一个像素一次乘加；u8 走 Q12                                      | D1    | P1 |
| COL-12 | ChannelSwap / Extract   | 通道重排/提取                                                                                                                  | `Ssse3.Shuffle` 单指令完成 RGBA→BGRA/RGBX 等                                        | D0    | P1 |
| COL-13 | Grayscale               | BT.601 / BT.709 亮度                                                                                                       | `(77R+150G+29B+128)>>8`（与 SimdCodecs 同式）；u16 用 `MultiplyAddAdjacent`          | D0    | P0 |
| COL-14 | BlackWhite              | 阈值二值化                                                                                                                    | 单 LUT 或比较 + 掩码                                                                | D0    | P1 |
| COL-15 | Threshold               | 逐通道/亮度阈值                                                                                                                 | 比较 + `ConditionalSelect`                                                      | D0    | P1 |
| COL-16 | Posterize               | 每通道保留 n 位                                                                                                                | 位掩码或 LUT                                                                      | D0    | P1 |
| COL-17 | Sepia                   | 固定 3×3 色调矩阵                                                                                                              | 走 COL-11 的定点路径                                                                | D0    | P1 |
| COL-18 | Tint                    | 与目标色混合                                                                                                                   | 每像素 lerp，Q8 定点                                                                | D0    | P1 |
| COL-19 | ColorBalance            | 阴影/中间调/高光三段                                                                                                              | 亮度权重曲线（Q8 查表）× 三通道偏移；可融合                                                      | D0    | P2 |
| COL-20 | Temperature / Tint      | 白平衡增益（R/B 增益 + G 微调）                                                                                                     | 每通道乘性增益，Q12；可融合                                                               | D0    | P1 |
| COL-21 | Vibrance                | 低饱和优先增益                                                                                                                  | 先算当前饱和度（max-min），增益按 (1-s) 加权；f32                                             | D1    | P1 |
| COL-22 | Premultiply             | `round(c·a/max)`                                                                                                         | `(t+(t>>8))>>8`，8 位全域精确                                                       | D0    | P0 |
| COL-23 | Unpremultiply           | `min(max, round(c·max/a))`                                                                                               | 需除法：f32 倒数或逐像素整数除；a=0 → 0                                                     | D0/D1 | P0 |
| COL-24 | AlphaThreshold / Matte  | alpha < t 置 0，可铺开颜色                                                                                                      | 比较 + 掩码；边缘可选 1px 羽化                                                           | D0    | P1 |
| COL-25 | Normalize               | min–max 拉伸                                                                                                               | 两遍：直方图求 min/max → LUT                                                         | D0    | P1 |
| COL-26 | Clamp / QuantizeStep    | 钳制或量化到步长                                                                                                                 | LUT 或位运算                                                                      | D0    | P1 |
| COL-27 | Linearize / Delinearize | sRGB、γ2.2、Rec.709、PQ、HLG                                                                                                 | u8/u16：Q16 LUT；f32：`MathF.Pow` 太慢，用 4096 段 LUT + 线性插值（误差 < 0.5 LSB16）         | D0/D1 | P2 |
| COL-28 | ToneMap                 | Reinhard / Hable / ACES / BT.2390                                                                                        | f32 逐像素曲线；可选按帧统计做自动曝光                                                         | D1    | P2 |
| COL-29 | GainMapApply            | Ultra HDR / Adobe gain map 合成 HDR                                                                                        | 基础图 × gain^w，按 `ColorEncoding` 决定输出空间                                         | D1    | P2 |
| COL-30 | IccTransform            | ICC → ICC（A2B/B2A）                                                                                                       | 快路径：3×3 矩阵 + 1D TRC；完整路径：mAB/mBA 的 1D 预整形 + 3D CLUT 三线性 + 输出 1D。CLUT 用 Q15 定点 | D1    | P2 |
| COL-31 | ColorSpaceConvert       | RGB ↔ YCbCr / XYZ / xyY / Lab / LCh / Luv / LChuv / HunterLab / LMS / HSL / HSV / CMYK / YccK，含 Von Kries / Bradford 色适应 | 矩阵路径 f32；YCbCr / YccK 的 8/16 位走整数定点（BT.601/709/2020）；色适应是三阶矩阵乘                | D0/D1 | P2 |
| COL-32 | ChromaResample          | 4:2:0 / 4:2:2 / 4:4:4 上下采样                                                                                               | 下采样 2×2 box 平均；上采样双线性；整数                                                      | D0    | P2 |
| COL-33 | AdaptiveThreshold       | 局部均值自适应二值化（mean-C / Bradley）                                                                                             | 积分图求窗口均值，O(1)/px；阈值 `mean·(1-k)`；全整数，无浮点。仅为对标 4.x 保留，文档二值化流水线不在本项目范围          | D0    | P1 |
| COL-34 | ColorBlindness          | 四型色觉模拟/校正（protan / deutan / tritan / achromatopsia）                                                                      | LMS 空间 3×3 矩阵，f32；可逆模式做矩阵求逆                                                   | D1    | P2 |
| COL-35 | BackgroundColor         | 把指定背景色替换为目标色（容差 + 边缘羽化），证件照/商品图常用                                                                                        | 颜色距离（RGB 欧氏或 Lab ΔE）→ 掩码 → 替换；容差边缘 1px 线性羽化                                   | D0    | P1 |

## D. 滤波（21）

| 编号     | 算子                    | 语义要点                                                                       | 实现策略（算法 + SIMD）                                                                | 确定性               | 阶段 |
| ------ | --------------------- | -------------------------------------------------------------------------- | ------------------------------------------------------------------------------ | ----------------- | -- |
| FLT-1  | Convolve              | 任意 2D 核；自动检测可分离                                                            | 可分离则横+纵两遍；3×3 特化为 `MultiplyAddAdjacent`。边界：Clamp（默认）/ Mirror / Wrap / Constant | D0/D1             | P1 |
| FLT-2  | GaussianBlur          | σ，半径 `ceil(3σ)`；Exact / Fast 两模式                                           | Exact：可分离定点核（u8/u16 Q14，f32 FMA）；Fast：三次 box 近似，复杂度与 σ 无关。4–8 px/iter          | D0/D1             | P1 |
| FLT-3  | BoxBlur               | 方框/矩形核                                                                     | 积分图 u32/u64 前缀和，O(1)/px；或水平滑动窗口                                                | D0                | P1 |
| FLT-4  | Median                | 3/5/7/9 方形核                                                                | 排序网络（min/max 交换），`Vector128<byte>` 16 lane 并行；大核改直方图 O(r)                      | D0                | P1 |
| FLT-5  | Bilateral             | 空间核 × 值域核                                                                  | 值域核预计算 256 项 LUT；空间核可分离近似（快速模式）；精确模式为 2D，成本 O(r²)                              | D0/D1             | P2 |
| FLT-6  | BokehBlur             | 圆盘/多边形光圈核                                                                  | 直接 2D 卷积成本高，用可分离六边形近似 + 能量归一化                                                  | D1                | P2 |
| FLT-7  | MotionBlur            | 线段核，角度 + 长度                                                                | 按角度生成采样点表，双线性采样后累加                                                             | D1                | P2 |
| FLT-8  | Sharpen               | unsharp mask：`src + amount·(src - blur)`                                   | 与高斯共用模糊结果；定点 Q8                                                                | D0/D1             | P1 |
| FLT-9  | GaussianSharpen       | 负高斯核 + 中心冲激                                                                | 组合成单核，一次卷积                                                                     | D0/D1             | P1 |
| FLT-10 | DetectEdges           | Sobel / Scharr / Prewitt / Kayyali / Laplacian                             | 3×3 两方向 → 幅值（`sqrt` 或 `abs(gx)+abs(gy)`）；u16 中间量                               | D0                | P1 |
| FLT-11 | Emboss                | 固定 3×3 核 + 128 偏移                                                          | 同 FLT-1 的 3×3 特化                                                               | D0                | P1 |
| FLT-12 | OilPaint              | 邻域直方图取众数                                                                   | 逐像素 256-bin 局部直方图；标量，分块并行                                                      | D2                | P2 |
| FLT-13 | Pixelate              | 块平均后最近邻放大                                                                  | 先 box 下采样再最近邻上采样，整数                                                            | D0                | P1 |
| FLT-14 | Vignette              | 径向衰减                                                                       | 距离 → 1D LUT（按行预计算），乘 alpha/亮度；SIMD 广播                                          | D0                | P1 |
| FLT-15 | Glow                  | 高光提取 → 模糊 → 加法合成                                                           | 阈值 LUT + FLT-2 + CMP-2(Add)                                                    | D0/D1             | P2 |
| FLT-16 | Dither                | Floyd–Steinberg / Atkinson / Burkes / Sierra / Stucki / Bayer 2·4·8 / None | 误差扩散串行（固定扫描序，sbyte 误差累加）；Bayer 为阈值矩阵，可 SIMD                                    | D2（扩散）/ D0（Bayer） | P1 |
| FLT-17 | Morphology            | Erode / Dilate / Open / Close                                              | 3×3：滚动三行 + 标量 min/max，内存 O(行)；更大半径再上 van Herk–Gil-Werman（O(1)/px）              | D0                | P1 |
| FLT-18 | HistogramEqualization | 全局直方图 → CDF                                                                | 两遍：直方图 → 256 项 LUT → 应用                                                        | D0                | P1 |
| FLT-19 | Clahe                 | 分块自适应均衡，clip limit                                                         | 分块直方图 → 裁剪重分布 → CDF LUT → tile 间双线性插值                                          | D0                | P2 |
| FLT-20 | Noise                 | 高斯/均匀噪声                                                                    | xorshift128+ 定种子；高斯用 Box–Muller 或 12 次均匀和近似                                    | D2（固定种子）          | P2 |
| FLT-21 | GuidedFilter          | 保边平滑（可选）                                                                   | 积分图求均值/方差，O(1)/px                                                              | D1                | P2 |

## E. 合成与绘制（13）

| 编号     | 算子                           | 语义要点                                                                                                                                                                       | 实现策略（算法 + SIMD）                             | 确定性   | 阶段 |
| ------ | ---------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- | ----- | -- |
| CMP-1  | DrawImage                    | 目标矩形 + 采样器 + 不透明度 + blend                                                                                                                                                  | 拆成"采样 + 合成"两段；同尺寸同位置时退化为纯合成                 | D0/D1 | P0 |
| CMP-2  | BlendModes                   | 16 种：Normal / Multiply / Screen / Overlay / Darken / Lighten / ColorDodge / ColorBurn / HardLight / SoftLight / Difference / Exclusion / Add / Subtract / Hue / Luminosity | 逐通道可分离的走整数 Q8；Hue/Luminosity 需 HSL 往返，走 f32 | D0/D1 | P1 |
| CMP-3  | PorterDuff                   | 12 式：SourceOver / Source / DestOver / DestOut / SrcIn / SrcOut / Xor / Clear / …                                                                                           | 预乘空间整数运算；`ConditionalSelect` 做 alpha 分支     | D0    | P0 |
| CMP-4  | Fill                         | 纯色填充（整图/矩形/掩膜）                                                                                                                                                             | 广播 + `StoreUnsafe`，行宽整数倍时接近 memset 带宽       | D0    | P1 |
| CMP-5  | FloodFill                    | 扫描线种子填充，容差                                                                                                                                                                 | 队列 + 行段扩展；固定顺序                              | D2    | P1 |
| CMP-6  | DrawLine                     | 带宽度与端点样式                                                                                                                                                                   | 距离场覆盖率（抗锯齿）或 Bresenham（无 AA）；按行扫描           | D0    | P1 |
| CMP-7  | DrawRect / Ellipse / Polygon | 填充与描边                                                                                                                                                                      | 解析覆盖率（椭圆用二次型），扫描线填充                         | D0    | P1 |
| CMP-8  | FillPath                     | 贝塞尔扁平化 → 扫描线，非零/奇偶规则                                                                                                                                                       | 自适应细分（容差 0.25px）→ 边表 → 跨度填充 + 覆盖率           | D0    | P2 |
| CMP-9  | StrokePath                   | 宽度、连接（miter/round/bevel）、端点、虚线                                                                                                                                             | 路径偏移 + 圆角连接；虚线按弧长切分                         | D0    | P2 |
| CMP-10 | Gradient                     | 线性 / 径向 / 扫描，含 stops 与 spread                                                                                                                                              | 参数 t → 1024 项颜色 LUT；径向提前算 1/√               | D0    | P2 |
| CMP-11 | Clip / Mask                  | 路径裁剪、alpha mask                                                                                                                                                            | 路径 → 覆盖率 mask（CMP-8）→ 乘 alpha               | D0    | P2 |
| CMP-12 | Clear                        | 清空为透明/指定色                                                                                                                                                                  | `Span.Clear` 或广播填充                          | D0    | P1 |
| CMP-13 | Watermark                    | 图片水印，九宫格位置 + 边距 + 不透明度                                                                                                                                                     | CMP-1 组合，位置表预计算                             | D0    | P1 |


## F. 量化（8）

| 编号    | 算子               | 语义要点                    | 实现策略（算法 + SIMD）                | 确定性   | 阶段 |
| ----- | ---------------- | ----------------------- | ------------------------------ | ----- | -- |
| QNT-1 | OctreeQuantizer  | 8 层八叉树，叶合并              | 逐像素插入 + 按像素数剪枝；无随机             | D2    | P1 |
| QNT-2 | WuQuantizer      | 33³ 直方图 + 三阶矩，最小方差切分    | 直方图累加用 u64；切分用前缀和查表            | D0    | P1 |
| QNT-3 | PaletteQuantizer | 给定调色板或中位切分初始化 + k-means | 固定迭代次数与初始中心（无 RNG）             | D2    | P1 |
| QNT-4 | WebSafeQuantizer | 216 色固定表                | 直接映射                           | D0    | P1 |
| QNT-5 | BuildPalette     | 从图生成 N 色调色板             | 组合 QNT-1/2/3                   | D2    | P1 |
| QNT-6 | MapToPalette     | 最近色映射                   | 32³ 查找表（构建一次，O(1)/像素）或 kd-tree | D0    | P1 |
| QNT-7 | ReduceColors     | 量化 + 抖动                 | QNT-* + FLT-16                 | D0/D2 | P1 |
| QNT-8 | DitherToPalette  | 有界误差扩散到索引色              | 误差限制在 ±16，避免雪花；固定扫描序           | D2    | P1 |

## G. 分析（7）

| 编号    | 算子             | 语义要点                        | 实现策略（算法 + SIMD）                     | 确定性 | 阶段 |
| ----- | -------------- | --------------------------- | ----------------------------------- | --- | -- |
| ANL-1 | Histogram      | 每通道 256/65536 bins          | 4–8 个独立累加器打破依赖链；SIMD 无冲突检测，标量多累加器更快 | D0  | P1 |
| ANL-2 | Statistics     | min/max/mean/stddev/entropy | 与直方图一趟出；u64 累加防溢出                   | D0  | P1 |
| ANL-3 | DominantColors | 前 N 主色                      | 量化后取 top-N + 原图均值回代                 | D2  | P2 |
| ANL-4 | AlphaBounds    | 非透明内容包围盒                    | 逐行比较 + `MoveMask` 找首末非零字节           | D0  | P1 |
| ANL-5 | Psnr           | MSE → PSNR                  | u32/u64 差平方累加；SIMD 乘加               | D0  | P1 |
| ANL-6 | Ssim / MsSsim  | 11×11 高斯窗                   | 可分离高斯 + 局部均值/方差/协方差；f32             | D1  | P2 |
| ANL-7 | Diff           | 逐像素绝对差、最大差、超阈值计数            | 无符号差 + `SAD` 思路；一次遍历三个指标            | D0  | P1 |

---

## 与 ImageSharp 4.x 的功能对标

> 基线：**ImageSharp 4.1.3**（2026-10 补丁版，net8.0）。4.0 补了 ICC 色彩转换、色彩配置文件体系、`Convolution` 公开 API；4.1 补了 AOT 种子与 plus/扩展混合模式。  
> **只对标功能，不参考源码**；命名、参数、默认值按我们自己的设计。落地前按公开文档逐项复核并锁版本（见 `tests/SimdImaging.Tests/Oracle/README.md`）。

| 能力族       | ImageSharp 4.1.x（代表）                                                                                                                                       | SimdImaging 侧            | 差异说明                                                                     |
| --------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------ | ------------------------------------------------------------------------ |
| 加载/保存/格式  | `Image.Load/Save`；JPEG/PNG/GIF/BMP/TGA/TIFF/WebP/PBM/QOI/ICO/CUR/EXR。4.0 加 ICO/CUR 解码、渐进式 JPEG 编码、JPEG COM 标记；4.1 加 ANI、CgBI PNG                           | IO-1/2                   | 格式面由 SimdCodecs 决定：多出 HEIF、JPEG2000、JBIG2、PNM 全族（PAM/PFM）；少 EXR、TGA、CgBI |
| 缩放        | `Resize` + `KnownResamplers`（4.1.3 公开 16 个名称）                                                                                                             | GEO-5                    | **我方仅 5 种已实现**（设计 16 种未齐），此项为功能缺口；另有 1/8 缩放解码下推 |
| 裁剪/画布     | `Crop` `Pad` `EntropyCrop` `BackgroundColor` `ResizeMode`                                                                                                  | GEO-1/2/4/6/8 + COL-35   | 覆盖                                                                       |
| 旋转/翻转     | `Rotate` `RotateFlip` `Flip` `AutoOrient`                                                                                                                  | GEO-9/10/11/12 + IO-4    | 覆盖                                                                       |
| 仿射/投影     | `AffineTransformBuilder` `ProjectiveTransformBuilder`（含 `QuadDistortion`、`TaperSide/TaperCorner`） `Transform` `Skew`                                       | GEO-14/15/16             | 功能对等；taper 作为单应的一种参数化                                                    |
| 模糊/锐化     | `GaussianBlur` `BoxBlur` `BokehBlur` `GaussianSharpen` `MedianBlur`                                                                                        | FLT-2/3/4/6/9            | 覆盖；多 `Exact/Fast` 两档与形态学                                                 |
| 卷积/边缘     | `Filter` + `KnownFilterMatrices`（可组合色矩阵） `DetectEdges` `KnownEdgeDetectorKernels`                                                                          | FLT-1/10/11 + COL-11     | `Filter` 类归入色矩阵；我们的卷积是任意核 + 四种边界模式                                       |
| 中值/像素化/油画 | `MedianBlur` `Pixelate` `OilPaint`                                                                                                                         | FLT-4/12/13              | 覆盖                                                                       |
| 色彩调整      | `Brightness` `Contrast` `Saturate` `Hue` `Lightness` `Opacity` `Invert` `Sepia` `Kodachrome` `Lomograph` `Polaroid` `BlackWhite` `GrayscaleMode` `Swizzle` | COL-1…COL-21 + COL-12    | 覆盖；胶片预设走 COL-11 配方（P2）                                                   |
| 色觉模拟      | `ColorBlindness`（P/D/T/achromatopsia）                                                                                                                      | COL-34                   | 覆盖                                                                       |
| 自适应二值化    | `AdaptiveThreshold`（mean / Bradley） `BinaryThreshold` `BinaryDither`                                                                                       | COL-33 / COL-14 + FLT-16 | 覆盖                                                                       |
| 直方图/均衡    | `HistogramEqualization`                                                                                                                                    | FLT-18/19 + ANL-1/2      | 多 CLAHE                                                                  |
| 阈值/色调分离   | `BinaryThreshold` `Posterize` `Dither` `KnownDitherings`                                                                                                   | COL-14/15/16 + FLT-16    | 覆盖                                                                       |
| 量化        | `Quantize` + `KnownQuantizers`（Octree/Wu/Palette/WebSafe）                                                                                                  | QNT-1…QNT-8              | 覆盖                                                                       |
| 合成/绘制     | `DrawImage` `Fill` `FillRegion` `DrawPath` `DrawLine` `Clear`、`PixelAlphaCompositionMode`；4.1 加 plus/扩展混合                                                  | CMP-1…CMP-13             | 覆盖 16 blend + 12 Porter-Duff；**文字不覆盖**                                   |
| 抗锯齿       | `GraphicsOptions.Antialias`（4.1 移除 `AntialiasThreshold`）                                                                                                   | GEO-19 + CMP-6/7/8       | 覆盖率模型不同，功能等价                                                             |
| 元数据       | Exif / IPTC / XMP / ICC；4.0 加格式间元数据转换 API                                                                                                                  | IO-7                     | 只做透传 + 少量字段；不提供 IPTC 编辑                                                  |
| 多帧/动画     | `ImageFrameCollection`、GIF/WebP/ANI；4.0 规范化动画编码与 alpha 透明处理                                                                                                | IO-5/6                   | GIF/WebP 覆盖；ANI 在 SimdCodecs 侧暂不支持，属 codec 缺口                            |
| 位深        | 由像素类型决定（`Rgba32`/`Rgba64`/`HalfVector4`…）                                                                                                                  | 全链路 Rgba16               | 16 位不被 8 位中间层吞掉                                                          |
| 色彩管理      | 4.0 起 `ColorProfileConverter` + CieLab/LCh/Luv/XYZ/xyY/HunterLab/LMS/CMYK/YCbCr/YccK + Von Kries 色适应                                                       | COL-30/31/32             | **追赶项**：ΔE2000 对拍 4.1.3，不是"有没有"                                          |
| HDR       | 公开处理器集合中未见 PQ/HLG 语义级处理（待复核）                                                                                                                               | COL-27/28/29             | PQ/HLG 色调映射 + Ultra HDR gain map                                         |
| 扩展性       | `PixelRowOperation` 行委托、`Filter` 组合、`IImageProcessor`                                                                                                      | `IImageOp` + 非泛型行委托      | 我们多"算子声明 halo 与源矩形"，因此可分块                                                |
| AOT       | 4.1 起为像素格式与 ICO/CUR/QOI 加 AOT 种子；**实测 PNG+Rgba32 完整编解码链路可 NativeAOT publish（0 警告、运行正常）**，其余格式/像素格式组合待逐项核实 | 构建门禁 + 双基线档位断言 | 从第一个提交起零 trim/AOT 警告；对手此链路同样可用，此项不宣称领先 |
| 内存        | `MemoryAllocator` 整图分配（4.1 起可配置受限）                                                                                                                         | PixelBuffer + 池 + 行带     | 稳态 0 分配与 8K 内存上限                                                         |
| 许可        | Six Labors Split License 1.0（商用按条款购买）                                                                                                                      | MIT（M0 落 LICENSE）        | 选型事实，不是技术优势                                                              |

**不覆盖项**（写进 `known-gaps.md`）：文字渲染与排版、IPTC 语义编辑、GPU 后端、EXIF 编辑器 UI、文档/OCR 垂直流水线（版面分析、去斜配方、文字识别——识别归 `Sdcb.SimdPaddleOCR`；我们只出旋转/阈值/透视这类原语）。

**继承自底层的格式缺口**：EXR、TGA、CgBI PNG、ANI 在 SimdCodecs 侧不存在，SimdImaging 无法凭空补——这是 codec 层的缺口，不在本项目范围。对标表里如实写"少"，不写"后续支持"。

---

## 实现进度（2026-11-05）

已实现：

| 组  | 编号                                                                                                                                                                                                                                                                                                              |
| -- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 容器 | IO-1 Load（含多帧）、IO-2 Save（单帧）、IO-3 Transcode（改元数据重编码）、IO-4 AutoOrient（EXIF 1–8 + 复位标签）、IO-5 Frames / KeepFrames、IO-6 AnimationCompose（逐帧 SourceOver）、IO-7 MetadataTransfer、IO-8 Thumbnail |
| 几何 | GEO-1 Crop、GEO-2 Pad、GEO-5 Resize（Nearest / Box / Bilinear / Bicubic / Lanczos3，5 / 16 种采样器）、GEO-9 Rotate 90 · 180 · 270、GEO-13 Rotate（任意角）、GEO-14 Affine、GEO-15 Perspective、GEO-10 Flip H / V、GEO-3 Trim（容差）、GEO-6 ResizeMode（Stretch / BoxPad / CropPad / Max / Min + 9 锚点）、GEO-8 ResizeCanvas、GEO-17 Tile / 镜像平铺、GEO-19 RoundCorners（抗锯齿 alpha 掩膜）、GEO-4 EntropyCrop、GEO-11 Transpose / 副转置、GEO-12 RotateFlip（单遍）、GEO-18 Warp（委托映射）、GEO-20 Sample（单点 / 批量）、GEO-7 ResizeToWidth / ResizeToHeight                                                                                                                                                                      |
| 色彩 | COL-1 Brightness、COL-2 Contrast、COL-3 Saturation、COL-4 HueRotate、COL-6 Opacity、COL-7 Invert、COL-8 Gamma、COL-9 Levels、COL-10 Curves、COL-11 ColorMatrix、COL-12 ChannelSwap、COL-13 Grayscale、COL-14 BlackWhite、COL-15 Threshold、COL-16 Posterize、COL-17 Sepia、COL-18 Tint、COL-19 ColorBalance、COL-20 Temperature、COL-21 Vibrance、COL-22 Premultiply、COL-23 Unpremultiply、COL-24 AlphaThreshold、COL-25 Normalize、COL-26 Clamp、COL-27 Linearize / Delinearize（sRGB / γ2.2 / Rec.709 / PQ / HLG）、COL-33 AdaptiveThreshold（局部均值）、COL-34 ColorBlindness（四种 + Daltonize 校正）、COL-35 BackgroundColor（容差 + 羽化）、COL-5 Lightness（乘性增益）、COL-31 ColorSpaceConvert（XYZ / xyY / Lab / LCh / Luv / LChuv / HunterLab / LMS / HSL / HSV / YCbCr + 色适应）、COL-32 ChromaResample（4:2:2 / 4:2:0）、COL-30 IccTransform（matrix/TRC 型 ICC）、COL-28 ToneMap（Reinhard / Reinhard 扩展 / Hable / ACES / BT.2390 + 自动曝光）、COL-29 GainMap                                                                                                                                            |
| 滤波 | FLT-2 GaussianBlur（真高斯）、FLT-5 Bilateral、FLT-3 BoxBlur、FLT-4 Median3x3、FLT-10 Sobel、FLT-1 Convolve3x3、FLT-6 BokehBlur、FLT-7 MotionBlur、FLT-8 Sharpen（unsharp mask）、FLT-9 GaussianSharpen（负高斯单核）、FLT-11 Emboss、FLT-17 Morphology（Erode / Dilate / Open / Close 3×3）、FLT-13 Pixelate、FLT-14 Vignette、FLT-15 Glow、FLT-16 Dither（None / Floyd–Steinberg / Atkinson / Burkes / Sierra / Stucki / Bayer 2·4·8）、FLT-12 OilPaint、FLT-18 HistogramEqualization、FLT-19 Clahe、FLT-20 Noise（固定种子）、FLT-21 GuidedFilter                                                                                                                                            |
| 合成 | CMP-1 DrawImage、CMP-10 Linear / Radial / SweepGradient、CMP-13 Watermark、CMP-5 FloodFill、CMP-4 Fill / CMP-12 Clear（原地写入）、CMP-6 DrawLine（Bresenham + Liang-Barsky 裁剪）、CMP-7 DrawRect / FillEllipse / FillPolygon、CMP-2 BlendModes（18 种，含 Hue / Saturation / Color / Luminosity）、CMP-3 PorterDuff（12 式全：Clear / Src / Dst / SrcOver / DstOver / SrcIn / DstIn / SrcOut / DstOut / SrcAtop / DstAtop / Xor）、CMP-8 FillPath（贝塞尔扁平化 + even-odd / non-zero 扫描线）、CMP-9 StrokePath（miter / round / bevel 连接、butt / round / square 端点、虚线）、CMP-11 Clip（路径覆盖率乘 alpha） |
| 量化 | QNT-2 Wu（矩 + 方差最小化切分）、QNT-4 WebSafe、QNT-8 DitherToPalette、QNT-3 中位切分调色板（无 k-means 精修）、QNT-1 Octree（8 层 + 插入期剪枝）、QNT-7 ReduceColors（量化 + 可选抖动）、QNT-5 BuildPalette、QNT-6 MapToPalette                                                                                                                                                                                                                         |
| 分析 | ANL-1 Histogram（每通道 n 桶）                                                                                                                                                                                                                                                                                        |

共 112 / 112。几何与滤波的 resize/blur 已向量化（Rgba8 行读写 + 两遍卷积），点算子仍是标量参考实现。  
逐项状态见 [`known-gaps.md`](known-gaps.md)。

---

## 统计

| 组      |      数量 |     P0 |     P1 |     P2 |
| ------ | ------: | -----: | -----: | -----: |
| A 容器与帧 |       8 |      4 |      3 |      1 |
| B 几何   |      20 |      6 |     12 |      2 |
| C 色彩   |      35 |      7 |     20 |      8 |
| D 滤波   |      21 |      0 |     13 |      8 |
| E 合成绘制 |      13 |      2 |      7 |      4 |
| F 量化   |       8 |      0 |      8 |      0 |
| G 分析   |       7 |      0 |      5 |      2 |
| **合计** | **112** | **19** | **68** | **25** |
