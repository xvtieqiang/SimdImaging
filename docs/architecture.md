# SimdImaging 架构设计（v0.1 设计稿）

> 状态：**设计稿，待确认后进入 M0**。本文只定义形状与契约，不含实现。
> 底层依赖：`Sdcb.SimdCodecs` 2.0.x（已克隆到 `SimdCodecs/`，仅作设计与对标参考，产品依赖走 NuGet）。
> 算子清单见 [`operators.md`](operators.md)。

---

## 1. 目标、非目标、判定口径

### 1.1 目标

在 `Sdcb.SimdCodecs` 之上做**图片应用层**：解码之后、编码之前的所有像素工作 —— 几何、色彩、滤波、合成、绘制、量化、分析。

三条硬约束：

1. **不重写任何 codec。** 容器解析、熵解码、色彩编码标记、布局转换全部归 SimdCodecs。
2. **NativeAOT 一等公民。** 零反射、零动态代码、零 P/Invoke，`IsAotCompatible` 从第一个提交起就是 true。
3. **确定性可审计。** 同一输入在同一台机器上，四个 SIMD 档位（AVX-512 / AVX2 / Vector128 / 标量）输出**逐字节相同**；不同并行度输出相同。库里不允许存在"看运气"的算子。

### 1.2 非目标（明确不做）

| 不做 | 原因 |
|---|---|
| 任何格式的编解码 | SimdCodecs 已覆盖 12 个格式族，重复实现只会引入两套 bug |
| 字体解析与文字排版 | 需要一个完整的 TrueType/CFF 解析器 + shaper（ImageSharp 靠 SixLabors.Fonts），是独立产品，单列 P2 卫星包 |
| 文档 / OCR 垂直场景（版面分析、去斜流水线、文档二值化配方、文字识别） | 识别链路归 [`Sdcb.SimdPaddleOCR`](https://github.com/sdcb/SimdPaddleOCR)（纯托管 PP-OCRv6，输入交错像素、不做解码）。通用**原语**我们照给（旋转、自适应阈值、透视矫正），但**不做面向文档的流水线**。它也不做去斜与二值化，这块明确留白，需要时单独立项 |
| GPU / OpenCL / Vulkan 后端 | 与 NativeAOT + 纯托管 + 确定性三条约束冲突，且不是当前瓶颈 |
| 图像编辑器式的撤销/图层栈 | 应用层要的是流水线，不是画布 |
| EXIF/IPTC 的语义编辑 UI | 只做透传与少量字段读写，解析归 `Sdcb.SimdCodecs.Exif` |

### 1.3 "超越 ImageSharp 4.x" 的判定口径

**基线锁定 ImageSharp 4.1.x**（2026-10 最新补丁 4.1.3，net8.0）。4.0 是一次大版本：ICC 色彩转换、色彩配置文件体系、`Convolution` 公开 API、AOT 种子（4.1 起，像素格式 + ICO/CUR/QOI）都补上了。**这些不再是差异点，只能算追赶项。**

不比 star、不比 API 数量，比七条可测的属性。每条都要在 M5 出报告：

| # | 维度 | 目标 | 测法 |
|---|---|---|---|
| 1 | 端到端吞吐 | 12 个固定场景 ≥ 4.1.3，其中 resize/模糊/合成 ≥ 1.5× | 同进程 A/B，MP/s |
| 2 | 稳态分配 | 池化 + 计划复用后 **0 B/op** | `GC.GetAllocatedBytesForCurrentThread` |
| 3 | 峰值内存 | 8K 图 ≤ 2.5× 单帧 | `GC.GetTotalMemory` + WorkingSet |
| 4 | 位深保真 | 10/16 bit 全链路不塌到 8 bit | 10-bit HEIF → 16-bit 处理 → 16-bit PNG，端点误差 0 |
| 5 | 确定性 | 四档 SIMD 逐字节一致 | CI 矩阵强制 |
| 6 | 色彩 | ICC→ICC **与 4.1.3 对拍**，不只比"有没有" | ΔE2000 ≤ 1（矩阵/TRC），≤ 2（CLUT） |
| 7 | AOT | 全库 NativeAOT + 零 trim 警告作为构建门禁；4.x 仍在补 AOT 种子 | publish + `warnaserror` |

读法：第 1 条是唯一"可能输"的，所以它进基准而不是承诺。第 4、5、7 条是结构性的——4.x 的处理管线建在 `Image<TPixel>` 上，位深由像素类型决定，跨档位一致与全库 AOT 都不是它的契约。第 6 条从"人无我有"降级为"人对人更准"：4.x 的 ICC 是 v4.0 的新特性，我们必须在 ΔE 上赢，不能只靠"支持 ICC"这句话。

**一条非技术事实**：ImageSharp 4.x 是 Six Labors Split License 1.0（商业使用按官方条款购买许可，有免费门槛）；本项目按 MIT 发布（M0 落 `LICENSE`）。这不是技术优势，但是选型时的真实差异，写进 README 的对比表里，别只放在文档里自嗨。

---

## 2. 底层能力盘点（事实，非推断）

读的是 `SimdCodecs/` 的 `main` 分支（HEAD `715dd99`，2026-11-05；版本族 2.0）。

### 2.1 已有的

| 能力 | 位置 | 对我们的意义 |
|---|---|---|
| 12 个格式族（JPEG/PNG/TIFF/WebP/GIF/BMP/ICO/QOI/PNM/JP2/JBIG2/HEIF）+ EXIF / ICC / 增益图等辅助包 + 门面 | `src/Sdcb.SimdCodecs*` | 全部编解码外包，我们一行都不写 |
| `IDecodedImage`：原始通道数、8/16/float、真实位深、非预乘 | `Abstractions/IDecodedImage.cs` | 保真层的唯一入口 |
| `PixelConverter`：16 种 `PixelFormat` 任意互转，全部有 SIMD 内核 + 标量孪生 | `Abstractions/PixelConverter.cs` | **我们绝不复刻**，边界上一次转换到位 |
| `RasterImage.Wrap(..., IMemoryOwner<byte>)` | `Abstractions/RasterImage.cs` | 池化 buffer 可以**零拷贝**交给编码器 |
| `DecodeOptions`：`Region`、`ScaleDenominator`(1/2–1/8)、`Pool`、`CancellationToken` | `Abstractions/DecodeOptions.cs` | 区域/缩放解码可**下推**到 codec |
| `TryGetClosestSize`、`TryDecodeThumbnail`、`IPlanarImageDecoder` | 同上 | 缩略图、YCbCr 直通 |
| `ImageDecoder` 会话 + 多帧 + 时长 | `src/Sdcb.SimdCodecs/ImageDecoder.cs` | 动图/多页的帧模型 |
| `ColorEncoding`（H.273 primaries/transfer/matrix） | `Abstractions/ColorEncoding.cs` | 只**标记**，从不重算像素 |
| `ImageMetadata`（Exif 原始块、XMP、ICC） | `Abstractions/ImageMetadata.cs` | 透传 |
| `Sdcb.SimdCodecs.Exif`：解析 + 写回 | `src/Sdcb.SimdCodecs.Exif` | 方向、GPS、拍摄参数 |
| `Sdcb.SimdCodecs.Color.Icc`：**只有 profile 字节**（sRGB v4） | `src/Sdcb.SimdCodecs.Color.Icc` | ICC **变换**是空白，归我们 |

### 2.2 没有的（= SimdImaging 的存在理由）

1. 任何几何变换（resize / rotate / crop / warp）。
2. 任何滤波与卷积。
3. 任何逐像素色彩调整。
4. 任何合成、绘制、路径光栅化。
5. 色彩空间**变换**：`ColorEncoding` 只是标签，`IDecodedImage` 的文档明确写 "never rescales pixels"；`ToFloats()` 是容器加宽，不是线性化。
6. ICC 的 A2B/B2A 变换（包里只有 profile 数据）。
7. 处理期的内存管理（block 级池、分块、行带并行）—— `RasterImage` 只管一张整图。

### 2.3 性能现状（影响我们的基准口径）

AGENTS.md 写明：JPEG 编解码、WebP 解码、PNG 编解码**已调到世界级**；其余格式"correct and tested but not yet optimised"。
→ SimdImaging 的基准报告必须**按格式分别给结论**，不能用一个平均值掩盖 PNM/JPEG2000 的慢。

---

## 3. 分层与包结构

```
业务代码
  │
  ├─ SimdImaging                    门面：Load / Save / Mutate / Clone / Plan
  │    ├─ SimdImaging.Drawing       P1 起：形状 / 路径 / 渐变 / 抗锯齿
  │    ├─ SimdImaging.Processing    112 个算子 + 全部 SIMD 内核
  │    └─ SimdImaging.Abstractions  Image / PixelBuffer / IImageOp
  │
  ├─ 边界：一次 PixelConverter + RasterImage
  │
  ├─ Sdcb.SimdCodecs 2.0.x          编解码 / 容器 / 元数据块 / 布局转换
  │
  └─ BCL：ZLibStream / Brotli / ArrayPool / Vector128 / 256 / 512
```

### 3.1 仓库结构（镜像 SimdCodecs 的决策）

```
SimdImaging.slnx
Directory.Build.props          # TFM net10.0, IsAotCompatible, PublicAPI 分析器, 统一版本
Directory.Packages.props       # 中央包版本；Sdcb.SimdCodecs 锁 2.0.x
global.json                    # SDK 10.0.100, rollForward latestFeature
docs/
  architecture.md     # 本文
  operators.md        # 算子清单
  known-gaps.md       # 有意不做 + 已知偏差（对标 SimdCodecs 同名文档）
src/
  Shared/                           # 源码共享，编译进各包自己的命名空间（不跨包引用 internal）
    Kernels/    # 向量内核 + 标量孪生
    Simd/       # 档位探测与分派
    Memory/     # 池、行带、块
  SimdImaging.Abstractions/         # 契约：PixelBuffer / PixelView / IPixelPool / IImageOp / 选项
  SimdImaging.Processing/           # M1 起：算子（Geometry/Color/Filters/Composite/Quantize/Analysis）
  SimdImaging.Drawing/              # P1 起：形状/路径；描边与路径填充 P2
  SimdImaging/                      # Image / ImageFrame / Load / Save / Encode（M0 已实现）
tests/
  SimdImaging.Tests/                # 唯一的测试项目（xunit v3 + Shouldly）
    Oracle/     # 外部黑盒与语料（README 固定版本 + URL + SHA-256）
    Fixtures/   # *-golden.tsv
  SimdImaging.AotSmoke/             # 不在 slnx；NativeAOT 产物 vs JIT 产物逐字节比对
benchmarks/
  SimdImaging.Resize.Benchmarks/
  SimdImaging.Filters.Benchmarks/
  SimdImaging.Pipeline.Benchmarks/
```

### 3.2 SimdCodecs 的决策 → SimdImaging 的对应决策

| SimdCodecs 的决策 | SimdImaging 照搬的做法 |
|---|---|
| 一个领域一个包，`PackageId = Assembly = RootNamespace`，同步发版 | 同左；`SimdImaging.*` 同版本号发布 |
| 不跨包用 internal；两包都要的代码走 `src/Shared` **源码**编译 | 同左（`Directory.Build.props` 里一行 `<Compile Include>` 决定哪些内核进哪个包） |
| `PublicAPI.Shipped.txt` / `Unshipped.txt` + RS0016/RS0017 | **M1 接入**：M0 的 API 还会动，现在锁只会制造 churn；M0 期间靠 diff 评审 |
| 一个测试项目，xunit v3 + Shouldly，`REQUIRE_ORACLES=1` 让跳过变失败 | 同左 |
| 每个向量内核必须有标量孪生，CI 强制各档位 | 同左，比较的是**输出字节** |
| 基准在 slnx 里只编译不跑；结果是"同一进程 A/B + 记录机器" | 同左，另有 `--ablate` 阶段归因 |
| `NoInlining \| AggressiveOptimization` 标注内核 | 同左 |
| 错误约定：`Try*` / `InvalidDataException` / 不静默回退 | 同左（见 §5.6） |
| 不提交第三方素材，语料 URL + SHA-256 下载 | 同左（只用 PD / CC0 / 自己生成） |

---

## 4. 与 SimdCodecs 的边界

### 4.1 职责矩阵

| 事项 | SimdCodecs | SimdImaging |
|---|---|---|
| 魔数嗅探、容器、chunk/box | ✅ | ❌ |
| 熵解码、IDCT、预测器、反量化 | ✅ | ❌ |
| `PixelFormat`（16 种布局）互转 | ✅ `PixelConverter` | ❌ 不复制 |
| 解码到 8/16/float、真实位深 | ✅ | ❌ 只选工作格式 |
| 区域解码、1/2–1/8 缩放解码 | ✅ 能力协商 | ✅ 调用方（下推决策） |
| 缩略图、多帧、平面 YCbCr | ✅ | ✅ 帧级/平面级处理 |
| Exif/XMP/ICC **原始块** | ✅ | ✅ 语义使用与回填 |
| Exif 字段解析/写回 | ✅ `Exif` 包 | ❌ |
| `ColorEncoding` 标记 | ✅ 透传 | ✅ 消费（决定要不要线性化/色调映射） |
| 色彩空间**变换**、ICC A2B/B2A、色调映射 | ❌ | ✅ |
| 几何、滤波、合成、绘制、量化、分析 | ❌ | ✅ |
| 块级内存池、行带并行、分块执行 | ❌ | ✅ |

**一句话边界**：SimdCodecs 负责"这张图是什么"，SimdImaging 负责"这张图要变成什么"。字节层到像素层的翻译由 SimdCodecs 完成一次；像素层到像素层的所有事都归 SimdImaging。

### 4.2 三条零拷贝通道

**① 解码 → 处理（单次转换，不产生中间 RGBA8）**

```csharp
SimdCodecs.TryDecode(bytes, out IDecodedImage decoded, options, ct);
// 16-bit PNG / 10-bit HEIF 直接落到 Rgba16，精度不经过 8 位
Image image = Image.FromDecoded(decoded, WorkingFormat.Rgba16, pool);
// 内部：PixelConverter.ConvertRow(decoded.Pixels, decoded.Channels, decoded.SampleFormat,
//                                 dst, PixelFormat.Rgba64, w * h)
```

`PixelConverter` 已对 16×16 组格式做过逐字节对标，这里只调用，不重写。源是 `Rgba8` 且工作格式是 `Rgba8` 时，退化为整块 `CopyTo`。

**② 处理 → 编码（池化内存直接交给 codec）**

```csharp
// PixelBuffer 实现 IMemoryOwner<byte>，且默认 tight（stride == w*4*bpp）
var owner = image[0].Pixels;                          // PixelBuffer : IMemoryOwner<byte>
var raster = RasterImage.Wrap(w, h, 4, SampleFormat.UInt8, owner);
encoder.AddFrame(raster, ...);                        // 不再复制
// 注意：Wrap 接管所有权 —— 之后不要再 Dispose owner，否则双重归还池。
```

前提是 tight。`Slice()` 出来的视图 stride 可能大于行宽——此时 `ToRasterImage()` 会**显式**复制一次并记录一次分配，不做隐性降级。

**③ 元数据回流（不解析、只搬运）**

`IDecodedImage.Metadata` → `Image.Metadata` → `SaveOptions.Metadata` → 编码器写回。Exif 的字段级读写由 `Sdcb.SimdCodecs.Exif` 负责。

**④ 交给推理引擎（BGR24 / RGB24 / BGRA32 / RGBA32）**

```csharp
// Sdcb.SimdPaddleOCR 的输入契约就是交错像素 + stride，不做解码
byte[] bgr = new byte[image.Width * image.Height * 3];
if (!image[0].Pixels.TryCopyTo(bgr, image.Width * 3, PixelFormat.Bgr24))
    throw new InvalidOperationException("目标缓冲不够大");
```

换算仍走 `PixelConverter`，我们不写第二套 swizzle。正因为下游要的是交错四通道，工作格式选 RGBA 才顺——这条通道不是附赠，是设计前提之一。

### 4.3 禁止事项（写进 AGENTS.md）

1. 不定义第二套 `PixelFormat`。工作格式只有三个值，边界翻译只用 `PixelConverter`。
2. 不在 SimdImaging 里解析任何容器字节。
3. 不把 `IDecodedImage` / `RasterImage` 泄漏进算子签名——它们只出现在 facade 边界。
4. 不改 SimdCodecs 的内部 API。确实需要的新能力（比如 ICC 变换）在自己这边实现；若属于 codec 层，向上游提 PR。
5. 不复制 SimdCodecs 的数值约定之外的任何代码；数值约定（`v×257`、`v>>8`、`(t+(t>>8))>>8`）**必须**一致，否则边界两侧的黄金测试会互相打架。

---

## 5. 核心抽象

### 5.1 工作格式：只有三个

```csharp
public enum WorkingFormat : byte
{
    Rgba8,    // 4 B/px，非线性编码（保持 ColorEncoding）
    Rgba16,   // 8 B/px
    Rgba32F,  // 16 B/px，容器语义：不隐式线性化
}
```

**为什么统一成 RGBA 四通道**：内核数量 = 工作格式数 × 算法数，而不是布局数（6）× 位深（2）× 预乘（2）× 算法数。一个 resize 写 3 份而不是 96 份。代价是 RGB24 输入内存 +33%、Gray8 +300%。

**为什么不让 float 隐式线性化**：与 SimdCodecs 的 `ToFloats()` 保持同一语义——容器加宽，不改含义。要线性光，显式调用 `Linearize()`。这条不妥协，否则 resize 的默认结果会和所有人的预期都不一样。

**Gray 特化：不做。** 文档/扫描场景不在范围内，灰度素材直接摊成 RGBA，代价是内存 ×4。用这个代价换掉一整族内核分支，值。

### 5.2 缓冲

```csharp
public interface IPixelPool
{
    IMemoryOwner<byte> Rent(int length);   // 释放 owner 即归还：一个 owner 一条释放路径
}

public sealed class PixelBuffer : IDisposable, IMemoryOwner<byte>
{
    public static PixelBuffer Rent(int width, int height, WorkingFormat format, IPixelPool? pool = null);
    public static PixelBuffer Wrap(IMemoryOwner<byte> owner, int width, int height, int stride, WorkingFormat format);

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }          // 字节
    public WorkingFormat Format { get; }
    public bool IsTight { get; }        // Stride == Width * BytesPerPixel
    public PixelView View { get; }      // 只读/读写视图，零拷贝
    public PixelBuffer ToTight();       // 必要时才复制
    public bool TryCopyTo(Span<byte> destination, int stride, PixelFormat format); // 布局/位深转换，走 PixelConverter
    Memory<byte> IMemoryOwner<byte>.Memory { get; }  // 仅 tight 时可用；非 tight 抛
}
```

```csharp
public readonly struct PixelView   // 算子唯一认识的形态
{
    public Memory<byte> Memory { get; }
    public int Offset { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public WorkingFormat Format { get; }
    public Span<byte> Span { get; }                 // 整块
    public Span<byte> Row(int y);                   // 单行，长度 Width*Bpp
    public PixelView Slice(PixelRect r);            // 零拷贝子视图
}
```

设计要点：

- **按行给 stride，不按整块连续假设。** 所有内核签名都是 `(src, srcStride, dst, dstStride, widthPx)`，天然支持子视图与分块。
- **`PixelBuffer` 直接实现 `IMemoryOwner<byte>`**，这是零拷贝交给 `RasterImage.Wrap` 的开关。
- 池：M0 只有 `ArrayPixelPool`（默认，`ArrayPool<byte>.Shared` 分桶）。`NativePixelPool`（`NativeMemory.AlignedAlloc`，64B 对齐）与 `NullPixelPool` 到 M1 再加——先让接口稳定，再补实现。测试里用一个计数池断言租/还各一次。
- 池参数：`MaxRetainedBytes`（默认 256 MB）、`ClearOnReturn`（默认 false）、`PreferNativeAbove`（默认 8 MB）。

### 5.3 图像与帧

```csharp
public sealed partial class Image : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public WorkingFormat Format { get; }
    public int FrameCount { get; }
    public ImageFrame this[int index] { get; }
    public ColorEncoding ColorEncoding { get; }     // 直接复用 Abstractions 的类型
    public ImageMetadata Metadata { get; }
    public Image Clone(IPixelPool? pool = null);
    public void Dispose();
    // 静态工厂 Load / TryLoad、静态 Save / Encode 见 §5.5
}

public sealed class ImageFrame : IDisposable
{
    public PixelBuffer Pixels { get; }
    public TimeSpan? Duration { get; }
}
```

`WorkingFormat` 是**整图统一**的：多帧动图不允许帧间位深不一致（解码器会给一致的结果，不一致说明上游有 bug，直接抛）。

### 5.4 算子与计划

```csharp
public enum OpCategory : byte
{
    Point,          // 逐像素，无邻域
    Neighborhood,   // 有 (HaloX, HaloY)
    Separable,      // 可拆成水平+垂直两遍
    Geometric,      // 需要源坐标逆映射
    Global,         // 需要全图统计（直方图、量化、CLAHE）
    Composite,      // 双输入
}

public interface IImageOp
{
    string Name { get; }
    OpCategory Category { get; }
    WorkingFormatMask Formats { get; }        // 支持哪些工作格式
    int HaloX { get; }                        // Point=0；Gaussian=r
    int HaloY { get; }
    PixelRect MapSourceRect(PixelRect dest, int srcWidth, int srcHeight);
}
```

`MapSourceRect` 是分块执行的关键：执行器据此算出每个输出块要读的源矩形（含 halo），从而支持超大图的行带/分块处理。**v1 所有算子都实现它，但执行器先只跑整图**；P1 打开行带执行，P2 打开二维分块。

```csharp
public sealed class ProcessingPlan
{
    public static ProcessingPlanBuilder Create();
    public PlanReport Analyze(Image image);                 // 融合、格式、缓冲水位、预计分配
    public Image Execute(Image image, ExecuteOptions? options = null);
}

public sealed record ExecuteOptions
{
    public IPixelPool? Pool { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = -1;  // -1 = 处理器数
    public long MaxWorkingBytes { get; init; } = 512L << 20;
    public TilePolicy Tiling { get; init; } = TilePolicy.Whole;
    public CancellationToken CancellationToken { get; init; }
}
```

两种用法，同一份计划：

```csharp
// 1) 顺手（对标 ImageSharp 的 Mutate 心智模型），内部建计划 + ping-pong，原地生效
image.Mutate(x => x.Resize(1920, 1080, Lanczos3).Grayscale().Contrast(1.1f));

// 2) 显式（批处理、需要复用权重表、需要审计）
var plan = ProcessingPlan.Create()
    .Resize(1920, 1080, Lanczos3)
    .Premultiply()
    .GaussianBlur(8f)
    .Unpremultiply()
    .Build();
PlanReport report = plan.Analyze(image);   // 文本可导出，做黄金比对
Image result = plan.Execute(image);
```

**计划期做的四件事**（这是"超越"里最实的一块）：

1. **点算子融合**：连续的 `Brightness → Contrast → Gamma → Levels` 合成一张 256/4096 项 LUT，四遍内存往返变一遍。
2. **预乘合并**：滤波链入口 `Premultiply` 一次、出口 `Unpremultiply` 一次，不在每个算子内部反复乘除（8 位反复预乘会有可见漂移）。
3. **缓冲 ping-pong**：两张 scratch 交替，最后一张直接成为结果，避免收尾大拷贝。
4. **下推**：`Load + Crop + Resize(1/8)` 时把 region/scale 下推给 `DecodeOptions`；`Save` 时按 `GetClosestInputFormat` 选目标 `PixelFormat`，避免编码器内部再转一次。

`PlanReport` 是纯文本（`StringBuilder`），测试里对计划做黄金比对，防止融合规则被悄悄改掉。

### 5.5 门面：静态成员挂在 `Image` 上

不另立门面类型。命名空间已经叫 `SimdImaging`，再来一个 `SimdImaging` 类会变成 `SimdImaging.SimdImaging.Load(...)`；而 `Image.Load(...)` 是 .NET 图像库的通用心智模型（ImageSharp 同形），学习成本为零。

```

**实现状态（2026-11-06）**

- 实际 API 是 `new ProcessingPlan { BandRows = … }.Add(name, category, kernel, haloX, haloY)` + `Image.Apply(plan)`；
  草图中的 `Create()` builder 与 `Analyze()` / `PlanReport` **尚未实现**，不要按草图写代码。
- 行带循环已改为**并行**（`RowParallel.ForBands`）。因此**内核会被并发调用**，`OpKernel` 必须线程安全，
  且只能写 `destinationRect` 内的行。这是 2026-11-06 的契约变更。
- **跨步骤没有融合**：`Execute` 仍按步骤整图循环，每步租一张整图 ping-pong（类注释里已如实写明）。
  现在赢得的是「band 并行 + 算子内联」，实测流水线 3/3、几何平均 1.9954×（`docs/audit-response-2026-11-06.md` §5b）。
- 跨步骤融合（一条 band 内跑完所有同尺寸步骤、两张 band 缓冲 ping-pong）是独立改动，路线见
  `docs/roadmap-plan-fusion.md`。
csharp
public sealed partial class Image : IDisposable
{
    public static Image Load(ReadOnlySpan<byte> data, LoadOptions? options = null, CancellationToken ct = default);
    public static Image Load(string path, LoadOptions? options = null, CancellationToken ct = default);
    public static bool TryLoad(ReadOnlySpan<byte> data, out Image? image, LoadOptions? options = null, CancellationToken ct = default);
    public static void Save(Image image, string path, SaveOptions? options = null, CancellationToken ct = default);
    public static byte[] Encode(Image image, ImageFormat format, SaveOptions? options = null, CancellationToken ct = default);
}

public sealed record LoadOptions
{
    public WorkingFormat? Format { get; init; }        // null：按源位深自动选 Rgba8 / Rgba16 / Rgba32F
    public bool ApplyOrientation { get; init; }         // EXIF 方向，默认 false（与 SimdCodecs 一致：报告但不应用）
    public PixelRect? Region { get; init; }             // 下推给 DecodeOptions
    public int? ScaleDenominator { get; init; }         // 1/2、1/4、1/8；不支持则 NotSupportedException，不偷偷整幅解码
    public bool PreferNativeScale { get; init; }        // true：用 TryGetClosestSize 协商最接近的
    public IPixelPool? Pool { get; init; }
    public long MaxPixels { get; init; } = 1L << 28;    // 268 MPix 上限，防 OOM
}
```

`ScaleDenominator` 的语义严格照抄 SimdCodecs：**做不到就抛 `NotSupportedException`**，绝不静默整幅解码再缩放（那会让 1/8 快 8 倍的承诺变成谎话）。

### 5.6 错误与取消契约

与 SimdCodecs 对齐，逐条：

| 情形 | 行为 |
|---|---|
| 损坏的输入字节 | 由 codec 抛 `InvalidDataException` / 返回 false，SimdImaging 不吞 |
| 调用方参数非法（负数尺寸、半径 < 0、σ = NaN） | `ArgumentOutOfRangeException` / `ArgumentException` |
| 能力不支持（该 codec 没有 1/8 解码、该算子不支持 Rgba16） | `NotSupportedException`，**不降级** |
| 超过 `MaxPixels` / `MaxWorkingBytes` | `InvalidOperationException`，消息带实际值与上限 |
| 取消 | `OperationCanceledException`；检查点放在**行带边界**，不进内层循环 |
| 已释放对象 | `ObjectDisposedException` |

库内不出现 `try { } catch { }` 吞异常，不出现 `Debug.Assert` 当契约。

---

## 6. 内存与执行模型

### 6.1 一张图的稳态

```
Load:
  decoded (codec 池)  ──PixelConverter 一次──►  PixelBuffer (SimdImaging 池, tight)
处理:
  A ──op──► B ──op──► A ──op──► B     (ping-pong，2 张 scratch)
输出:
  B 直接成为 Image 的帧 buffer（不复制）
Save:
  PixelBuffer(=IMemoryOwner) ──► RasterImage.Wrap ──► encoder.WriteRows
```

稳态分配 = 0。唯一允许的分配是：权重表首次构建、`PlanReport` 文本、以及用户显式调用的 `Clone/ToArray`。

### 6.2 并行

- **行带切分，不用 work-stealing**：`band = ceil(height / workers)`，第 i 个线程处理固定行区间。每像素只依赖输入 → 并行度不影响结果。
- 每个算子声明 `IsRowParallelSafe`（默认 true）；误差扩散抖动、串行 K-means 标注 false，单线程跑，且扫描序写死。
- `MaxDegreeOfParallelism` 之外不读 `Environment.ProcessorCount`（否则同一台机器换容器结果就变）。

### 6.3 分块

| 阶段 | 能力 |
|---|---|
| v1（M1–M3） | 整图执行；`MapSourceRect` 契约已在，算子全实现 |
| P1（M4） | 行带流式：只对 `Neighborhood` / `Separable` 打开，halo = 核半径；内存从 O(H) 降到 O(band + halo) |
| P2 | 二维分块：`Geometric` 也支持；`Global` 算子强制两遍（先统计后处理），仍受 `MaxWorkingBytes` 约束 |

分块的验收不是"能跑"，是**输出与整图执行逐字节相同**。这一条会让不少 op 在 P1 阶段被判定为"不能分块"，那是正确结果，不是失败。

### 6.4 超限行为

- `MaxWorkingBytes` 不够 → 先尝试降档（Rgba32F → Rgba16，若算子是精确整数语义）→ 仍不够则抛，**不静默改精度**。
- `MaxPixels` 在 Load 的头信息阶段就检查，避免先分配再失败。

---

## 7. SIMD 策略

### 7.1 四个档位

```csharp
internal static class SimdTier
{
    public static readonly bool Avx512 = Vector512.IsHardwareAccelerated && Avx512F.IsSupported && Avx512BW.IsSupported;
    public static readonly bool Avx2   = Vector256.IsHardwareAccelerated && Avx2.IsSupported;
    public static readonly bool V128   = Vector128.IsHardwareAccelerated;
    public static readonly bool Shuffle = BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);
}
```

| 工作格式 | 1 px | Vector128 | Vector256 | Vector512 |
|---|---|---|---|---|
| Rgba8 | 4 B | 4 px | 8 px | 16 px |
| Rgba16 | 8 B | 2 px | 4 px | 8 px |
| Rgba32F | 16 B | 1 px | 2 px | 4 px |

四通道对齐到 128 位边界是这套 lane 模型成立的前提——这也是选 RGBA 而非 RGB 的第二个理由。

### 7.2 内核契约

```csharp
internal delegate void RowKernel(ReadOnlySpan<byte> src, int srcStride, Span<byte> dst, int dstStride, int widthPx);

internal static class BlurRowKernels
{
    public static readonly RowKernel Rgba8 = SimdTier.Select(
        Avx512: Rgba8Avx512,
        Avx2:   Rgba8Avx2,
        V128:    Rgba8V128,
        Scalar: Rgba8Scalar);   // 具体方法组，不经过泛型静态抽象成员（Mono AOT 会 trap）
}
```

规则（全部来自 SimdCodecs 的实践教训）：

1. **一个内核 = 一行**。边界、halo、并行、分块由上层负责，内核不持有状态。
2. **每个向量内核必须有同文件、同算法的标量孪生**，输出逐字节一致。
3. `[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]`。防止动态 PGO 把热调用链 inline 爆预算，反而让向量变成走内存传参。
4. **尾部读整像素、写整像素**。SimdCodecs 有过教训：按 3 字节像素写 4 字节，下一个像素的 4 字节读踩进上一次 store，store-to-load forwarding 失败，RGB 反滤波比标量还慢。
5. 不用 `Vector256.Sum` 之类的水平归约产出像素值（结果依赖 lane 分组，跨宽度不一致）。
6. `Min/Max` 用 `MinNative/MaxNative`（输入保证非 NaN 时），否则 .NET 9+ 的 IEEE 754-2019 语义会生成 compare/blend 序列。

### 7.3 三种算术路径

| 路径 | 用于 | 精度 | 确定性 |
|---|---|---|---|
| **整数定点** | Rgba8 / Rgba16 的几何、卷积、合成 | Q14 权重，u32/u64 累加 | D0：逐字节、跨平台 |
| **浮点 + 单次舍入 FMA** | Rgba32F、ICC、色调映射、双三次 | IEEE-754 binary32 | D1：固定运算序 → 跨档位逐字节；FMA 单次舍入 → 跨 ISA 一致 |
| **查表** | 任意逐像素曲线（γ、levels、curves、posterize） | 256 / 4096 项 | D0 |

**定点约定（冻结，改动即输出变化）**：

- 权重 Q14，归一化和恒等于 `1 << 14`，余数补给中心 tap → DC 增益精确为 1，常量图缩放后仍是常量。
- 8 → 16：`v * 257`；16 → 8：`v >> 8`（与 `PixelConverter` 一致，边界两侧的黄金可复用）。
- N 位真位深 → 16 位容器：满量程映射（1023 → 65535），位深记在 `ImageInfo.BitDepth`。
- 预乘：`(t + (t >> 8)) >> 8`，其中 `t = c * a`（等价 `round(c·a/255)`，0..65025 全域精确）。
- 反预乘：`a == 0` 时颜色 0；否则 `min(max, round(c·max/a))`。
- 浮点 → 整数的舍入统一为 `(int)(x + 0.5f)`，只对非负域定义；单测覆盖 0.5 / 1.5 / 2.5。

**浮点确定性**：禁止重组（`a+b+c` 的结合顺序写死）、禁止用 `Vector.Sum` 做归约、tap 顺序固定。融合乘加用 `Math.FusedMultiplyAdd` / `Fma.MultiplyAdd` / `AdvSimd.FusedMultiplyAdd`——IEEE 定义为单次舍入，x86 与 ARM 结果一致。

### 7.4 逐类算子的策略

| 算子类 | 主力手段 | 备注 |
|---|---|---|
| 点算子 | 8 位走 u16 加/乘后 clamp；或合并成 LUT | 3 个点算子融合后内存往返 ÷3 |
| 3×3 卷积 | u16 中间量 + 移位代替除法；`MultiplyAddAdjacent` | Sobel/Scharr/Emboss 同一内核族 |
| 可分离卷积 | 水平 4–8 px/iter，垂直按列累加；f32 用 FMA | 垂直遍是 cache 敏感的，按行带处理 |
| 重采样 | 权重表复用 + 水平/垂直两遍；u8/u16 定点 | 权重表按 (srcLen, dstLen, filter) 缓存，动图只算一次 |
| 90° 旋转 | 8×8 字节块转置 / 4×4 u16 块 | `Ssse3.Shuffle` + `Unpack`，或 `AdvSimd` 表查 |
| 中值 | 排序网络（min/max 交换），`Vector128<byte>` 16 lane 并行 | 3×3 = 19 次 compare-exchange |
| 形态学 | van Herk / Gil-Werman 1D min-max，O(1)/px | 先横后纵，与核大小无关 |
| 盒模糊 | 积分图（u32/u64 前缀和）或滑动窗口 | 与半径无关 |
| 直方图 | 多累加器打破依赖链（4–8 份） | SIMD 无冲突检测时，标量多累加器更快 |
| 误差扩散抖动 | 串行，固定扫描序 | 显式标注不可并行 |
| 合成 | 预乘空间整数；`ConditionalSelect` 做 alpha 比较 | Porter-Duff 12 式同一族 |

### 7.5 档位验证

CI 跑四遍同一测试矩阵：

```bash
dotnet test tests/SimdImaging.Tests -c Release
DOTNET_EnableAVX512F=0   dotnet test tests/SimdImaging.Tests -c Release --filter "FullyQualifiedName~Kernel"
DOTNET_EnableAVX2=0      dotnet test tests/SimdImaging.Tests -c Release --filter "FullyQualifiedName~Kernel"
DOTNET_EnableHWIntrinsic=0 dotnet test tests/SimdImaging.Tests -c Release --filter "FullyQualifiedName~Kernel"
```

比较的是**输出字节的 SHA-256**，不是"看起来一样"。

---

## 8. NativeAOT 与裁剪

### 8.1 硬性配置

```xml
<TargetFramework>net10.0</TargetFramework>
<IsAotCompatible>true</IsAotCompatible>     <!-- 同时打开 AOT / Trim / SingleFile 分析器 -->
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<Nullable>enable</Nullable>
<InvariantGlobalization>true</InvariantGlobalization>
```

### 8.2 禁用清单

| 禁用 | 原因 / 替代 |
|---|---|
| `System.Drawing.Common` | Windows-only、非托管、不 AOT |
| `Type.GetType` / `Activator.CreateInstance` | 格式注册改成静态表（照抄 SimdCodecs 的 `ImageDecoderRegistration`） |
| `Expression.Compile` / `Reflection.Emit` | 同上；需要动态行为时用显式 `switch` |
| `dynamic` | 同上 |
| P/Invoke | 纯托管是产品定位；性能靠 SIMD 不靠 native |
| `JsonSerializer` 默认反射 | `PlanReport` 用文本；确有 JSON 需求走 source generator |
| 泛型静态抽象成员上建委托 | Mono AOT（wasm/iOS/Android）会 trap；用具体方法组 |

### 8.3 验证

`tests/SimdImaging.AotSmoke/run.sh`（不进 slnx，CI 单独跑）。**必须 publish 两次**：

```bash
# A. AVX2 基线：让 ILC 保留 AVX2 内核
dotnet publish -c Release -r win-x64 -p:PublishAot=true -p:IlcInstructionSet=avx2
# B. 默认基线（SSE2）：验证 Vector128 / 标量降级路径
dotnet publish -c Release -r win-x64 -p:PublishAot=true
```

三个已经踩到的前提：

1. **先装 C++ 工作负载。** 缺链接器时 publish 直接报 `Platform linker not found`；JIT 侧不受影响，所以很容易误以为 AOT 已经通过。
2. **JIT 基线要显式 `-p:PublishAot=false`。** 工程里写 `PublishAot=true` 会让**普通构建**也把 `RuntimeFeature.IsDynamicCodeSupported` 置为 false（实测 .NET 10.0.12）。拿它当 JIT 基线，`IsDynamicCodeSupported == false` 这条断言就永远成立、永远没意义。
3. **Git Bash 下用 `-p:`，不要用 `/p:`。** MSYS 会把 `/p:IlcInstructionSet=avx2` 改写成路径，MSBuild 于是报 “Only one project can be specified”。

两次都跑同一份语料（load → resize → blur → composite → quantize → save），每步输出算 SHA-256，分别与 JIT 的 **AVX2 档**、**V128/标量档**比对。

**为什么必须两次**：ILC 在编译期把 `Avx2.IsSupported` 常量折叠成 `false`（x64 默认基线是 SSE2），AVX2 内核会被整段裁掉。只跑一次默认 publish 的 AOT smoke 会"绿"得毫无意义——它压根没执行 AVX2 路径。所以还要断言实际档位：

```csharp
// 冒烟程序启动时打印并校验，不允许静默降级
Console.WriteLine($"Tier: Avx512={SimdTier.Avx512} Avx2={SimdTier.Avx2} V128={SimdTier.V128}");
```

另外两个断言：`RuntimeFeature.IsDynamicCodeSupported == false` 时全路径可跑；publish 输出零 trim/AOT 警告（`-warnaserror`）。

> 这条经验来自 SimdPaddleOCR 的 README：它在 x64 AOT 时要求显式设置 `IlcInstructionSet=avx2`，否则 AVX2 内核被裁、推理明显变慢且不报错。同类问题在我们这里表现为"测试通过但没测到东西"，比性能损失更难发现。

---

## 9. 测试策略（对标 SimdCodecs）

### 9.1 三层

| 层 | 内容 | 判定 |
|---|---|---|
| 内核级 | 每个内核 × 每个档位 × 边缘宽度（0, 1, 3, 4, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65） | 与标量孪生**逐字节**相等 |
| 算子级 | 每种算子 × 工作格式 × 参数矩阵（σ=0/0.1/1/8/64，半径 0/1/3/9，矩形退化/越界） | 黄金 TSV 的 SHA-256 |
| 计划级 | 融合结果、`PlanReport` 文本、ping-pong 次数、分配统计 | 黄金文本 + `TrackingPixelPool` 计数 |

### 9.2 不变量（比黄金更值钱）

- 常量图 resize/blur 后仍是同一常量（DC 增益 = 1）。
- `Rotate90` 四次 = 原图逐字节。
- `Premultiply → Unpremultiply` 误差 ≤ 1 LSB。
- `alpha == 0` 的像素颜色不参与任何滤波输出（无颜色渗漏）——这条抓的是"没预乘就去卷积"这个最常见 bug。
- `Crop(a) + Crop(b)` 与一次 `Crop(a∪b)` 相同。
- 加噪/量化的固定种子重跑结果相同。

### 9.3 黄金与 oracle

- 黄金 `Fixtures/<op>-golden.tsv`：**永远不为让测试通过而重生成**。变更必须在评审 diff 里可见。
- 外部 oracle 只用黑盒二进制 + 公开 API，版本锁在 `tests/SimdImaging.Tests/Oracle/README.md`：
  - ImageSharp **4.1.3**（锁定补丁版）：功能对标、PSNR/SSIM 参考（不要求逐字节，因为重采样核不同）；
    4.0 起自带 ICC，色彩必须按 ΔE2000 对拍，不能只比"支持/不支持"
  - SkiaSharp / Magick.NET：重采样与模糊的视觉参考（benchmark + test only）
  - 算法类（box、bilinear、nearest）：要求 ≤ 1 LSB，偏差写进 `known-gaps.md`
- 语料只用 PD / CC0，URL + SHA-256 下载，不提交仓库。

### 9.4 鲁棒性

- 恶意尺寸：`0`、`int.MaxValue`、`w*h` 溢出、stride 小于行宽、halo 大于图。
- 浮点脏值：NaN / ±Inf / 负数进 `sigma`、`gamma`、矩阵。
- 全部要求：抛明确异常，5 秒内返回，不挂起，不无界分配。
- 随机模糊：`SIMDIMAGING_FUZZ_ITERATIONS=300000` 跑算子参数组合。

---

## 10. 基准与对比

完全照 SimdCodecs 的规矩：

- **同进程 A/B**：`baseline` 命令用 `git worktree` 编出上一提交的库，和当前代码在同一进程里跑。跨会话的数字不算基线（机器状态能差 10%+）。
- **端到端才算数**：单独内核快 5× 不代表算子快——SimdCodecs 记录过一个"向量化色彩转换在真实管线里慢 42%"的案例。
- **按消融归因**：`--ablate` 关掉某一阶段看差额，不用采样 profiler。
- **先正确后计时**：输出不对的基准拒绝计时。
- **记录机器**：CPU / OS / .NET 版本 / `Vector256/512.IsHardwareAccelerated` 每次写进结果文件。

三个基准项目，入口命令一致（`matrix` / `ab` / `baseline` / `--ablate`）：

| 项目 | 场景 | 参照实现 |
|---|---|---|
| `SimdImaging.Resize.Benchmarks` | 16 种重采样器 × 上/下采样 × 8/16 bit；4K→1080p、1080p→4K、1/8 缩略图 | ImageSharp 4.1.3、SkiaSharp、Magick.NET |
| `SimdImaging.Filters.Benchmarks` | Gaussian/Box/Median/Morphology/Convolve × 半径矩阵；合成 16 种 blend | ImageSharp 4.1.3、SkiaSharp |
| `SimdImaging.Pipeline.Benchmarks` | 真实链路：load→resize→blur→sharpen→composite→quantize→save；8K 峰值内存；稳态分配 | ImageSharp 4.1.3（同链路） |

报告模板：`benchmarks/<项目>/results/<日期>-<机器>.md`，含正确性门、耗时、MP/s、alloc/op、峰值内存、消融表。

**许可证前提（必须先确认）**：据 SimdCodecs 的依赖注释（2026-11），ImageSharp **3.2.0 与 4.x 在构建期校验 Six Labors 许可密钥**；3.1.12 是最后一个免密钥可构建的版本。所以"和 4.1.3 同进程 A/B"这条在拿到合法密钥之前跑不起来。三条路，按优先级：
1. 拿密钥，直接对 4.1.3（唯一能兑现 §1.3 承诺的路径）；
2. 无密钥时退到 3.1.12，报告里明确写"基线是 3.1.12，不是 4.x"，不冒充；
3. 用其官方工具/预编译二进制做黑盒参考，只出趋势不出同比。

---

## 11. 里程碑

| 里程碑 | 内容 | 出口条件 |
|---|---|---|
| **M0 骨架（已完成）** | slnx、props、`Abstractions`（格式 / 池 / 缓冲 / 算子契约 / 选项）、`Image` + Load/Save/Encode、AOT 冒烟工程与脚本 | ✅ `Release` 构建 0 警告；两种基线的 AOT 产物与 JIT 逐字节一致，`dynamic-code=False` 已断言 |
| **M1 几何（已完成）** | Crop / Pad / Flip / Rotate 90·180·任意角 / Affine / Perspective / Resize（5 种采样器）/ Premultiply / Unpremultiply / Composite；SIMD 内核与四档一致性、`ProcessingPlan`、PublicAPI 快照 | ✅ 黄金测试通过、四档逐字节一致；对 ImageSharp 的倍数见 `compare-4.1.3` |
| **M2 色彩 + 滤波（功能完成）** | 35 个点算子 + LUT 字节域融合、Gaussian/Box/Median/Sobel/Unsharp、直方图与均衡、自适应二值化 | 模糊 ≥ 1.5×：**1080p 实测 1.61–1.86×，达标**；"点算子融合后分配 = 0"：**未逐条核**（实测 8–100 KiB） |
| **M3 量化 + 绘制（功能完成）** | Octree/Wu/Palette/WebSafe、抖动、路径与形状、18 blend + 12 Porter-Duff、渐变、水印 | ❌ 未验：量化 PSNR 对 ImageSharp 同参数、抗锯齿覆盖率 |
| **M4 HDR/ICC/高级（功能完成）** | 传递函数、色调映射、gain map、ICC 矩阵/TRC 与 LUT、CLAHE、双边、行带流式 | ❌ 未验：10-bit HEIF → 16-bit PNG 端点精确、8K 峰值内存 ≤ 2.5× |
| **M5 验收** | 12 场景基准报告、分配报告、AOT 报告、`known-gaps.md` 定稿 | §1.3 六条全部出数，输的条目写清原因 |

> **状态口径**：M2–M4 的功能（算子、测试、公开 API）都已落地；上表"出口条件"是当初写进设计的验收标准，
> 目前只有 M0/M1 与 M2 的模糊一项逐条核过。要宣称某个里程碑"完成"，先把它那一行的出口条件跑出数来。

每个里程碑结束跑一次 `bash tests/SimdImaging.AotSmoke/run.sh`，不只是 M5。

---

## 12. 风险与待决

### 12.1 风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| RGBA-only 让灰度素材内存 ×4 | 灰度大图多占 3 倍内存 | 文档场景已排除，接受；大图靠行带流式把峰值压回去 |
| ILC 把 `Avx2.IsSupported` 折叠为 false | AOT 产物只走 SSE2 档，性能掉档且**测试全绿** | publish 必须 `IlcInstructionSet=avx2`；AOT smoke 断言实际档位（§8.3） |
| 浮点跨档位逐字节很难 | 可能导致大量算子被迫走整数 | 已定：点/几何/卷积全走整数；float 只用于 HDR/ICC，且用固定序 + 单次舍入 FMA |
| ICC LUT（mAB/mBA）实现量大 | M4 可能延期 | 先做 matrix/TRC（覆盖 95% 的 sRGB/Display P3/AdobeRGB），LUT 单列 P2 |
| 文字渲染缺失 | 与 ImageSharp 功能对比时是明显空白 | 明确写进非目标；需要时单列 `SimdImaging.Fonts` 卫星包 |
| AVX-512 降频 | 某些 SKU 反而更慢 | 档位可配置（`SIMDIMAGING_DISABLE_AVX512=1`）；以实测决定默认 |
| 分块后输出不一致 | 隐性正确性 bug | 分块验收 = 与整图执行逐字节相同，不一致就判定该算子不可分块 |
| 4.x 功能清单凭公开文档整理 | 对标表可能不准（4.0 新增的 ICC / 色彩配置文件 / Convolution API 面很大） | 锁 4.1.3；`Oracle/README.md` 记录版本与文档快照日期，M5 前逐项复核 |
| 框架基线 net10.0（与 SimdCodecs 一致） | 无法被 net8.0 应用引用，而 ImageSharp 4.x 是 net8.0 | 接受：本项目的定位是 net10 + AOT；若必须支持 net8，需要多目标并放弃部分 `Vector512` 优化 |
| 格式面缺口：EXR / TGA / CgBI PNG / ANI | 对标表上不是完全覆盖，而这四项在 SimdCodecs 侧没有 | 写进 `known-gaps.md`；要补属 codec 层，不在 SimdImaging 范围，也不假装"后续支持" |
| ImageSharp 4.x 构建期校验许可密钥 | 4.1.3 的同进程 A/B 在拿到密钥前跑不起来 | 按 §10 的三条路走；报告里写明基线版本，不拿 3.1.12 冒充 4.x |

### 12.2 已定与待定

已定：

- 包族与根命名空间 `SimdImaging`（NuGet 上 `simdimaging` 未占用，2026-11-05 核实）；门面不另立类型，静态工厂挂在 `Image` 上。
- **工作格式 RGBA-only**。文档/OCR 场景不在范围，Gray 特化不立项（见 §5.1）。
- 对标基线 ImageSharp 4.1.3。

待定：

- **文字渲染**：推荐 v1 不做。若业务必须要，它会是比整个 `SimdImaging.Processing` 还大的独立工程。
