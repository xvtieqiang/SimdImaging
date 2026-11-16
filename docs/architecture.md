# SimdImaging 架构设计（v0.1）

> 状态：**设计契约 + 实施记录（2026-11-15）**。M0、M1、M6–M14 已完成并验收，M15 进行中，M5 验收未启动，
> M2–M4 功能已落地但出口条件仅部分核验。§11 中带「已完成」与实测数字的行是记录，未标记的行仍是待兑现的设计。
> 底层依赖：`Sdcb.SimdCodecs` 2.0.x，**NuGet 包**，版本锁在 `Directory.Packages.props`。仓库不持有、也不构建任何外部源码。
> 算子清单见 [`operators.md`](operators.md)。

---

## 1. 目标、非目标、判定口径

### 1.1 目标

在 `Sdcb.SimdCodecs` 之上做**图片应用层**：解码之后、编码之前的所有像素工作 —— 几何、色彩、滤波、合成、绘制、量化、分析。

三条硬约束：

1. **不重写任何 codec。** 容器解析、熵解码、色彩编码标记、布局转换全部归 SimdCodecs。
2. **NativeAOT 一等公民。** 零反射、零动态代码、零 P/Invoke，`IsAotCompatible` 从第一个提交起就是 true。
3. **确定性可审计。** 同一输入在同一台机器上，四个 SIMD 档位（AVX-512 / AVX2 / Vector128 / 标量）输出**逐字节相同**；不同并行度输出相同。库里不允许存在"看运气"的算子。

> **实现状态（2026-11-15）**：AVX2 / V128 / 标量**三档全库覆盖**，逐字节一致由 CI 三档矩阵每次强制。**第四档已在 `PlanarConvolve` 落地**（水平/垂直两遍与稀疏 PSF 都有 `Vector512` 路径，gaussian / box / RL 反卷积等走它）；正确性用 `Vector512<T>` 的软件实现验证——与本机三档逐字节相同——但**性能从未在有 AVX-512 的硬件上出数**，对外只能宣称「已实现、已验正确性、未验吞吐」。

### 1.2 非目标（明确不做）

| 不做 | 原因 |
|---|---|
| 任何格式的编解码 | SimdCodecs 已覆盖 12 个格式族，重复实现只会引入两套 bug |
| 字体解析与文字排版 | 需要一个完整的 TrueType/CFF 解析器 + shaper（ImageSharp 靠 SixLabors.Fonts），是独立产品，单列 P2 卫星包 |
| GPU / OpenCL / Vulkan 后端 | 与 NativeAOT + 纯托管 + 确定性三条约束冲突，且不是当前瓶颈 |
| 图像编辑器式的撤销/图层栈 | 应用层要的是流水线，不是画布 |
| EXIF/IPTC 的语义编辑 UI | 只做透传与少量字段读写，解析归 `Sdcb.SimdCodecs.Exif` |

### 1.3 "超越 ImageSharp 4.x" 的判定口径

**基线锁定 ImageSharp 4.1.x**（2026-10 最新补丁 4.1.3，net8.0）。4.0 是一次大版本：ICC 色彩转换、色彩配置文件体系、`Convolution` 公开 API、AOT 种子（4.1 起，像素格式 + ICO/CUR/QOI）都补上了。**这些不再是差异点，只能算追赶项。**

不比 star、不比 API 数量，比七条可测的属性。每条都要在 M5 出报告：

| # | 维度 | 目标 | 测法 |
|---|---|---|---|
| 1 | 端到端吞吐 | 19 个固定场景 ≥ 4.1.3，其中 resize/模糊/合成 ≥ 1.5× | 同进程 A/B，MP/s |
| 2 | 稳态分配 | 池化 + 计划复用后 **0 B/op** | `GC.GetAllocatedBytesForCurrentThread` |
| 3 | 峰值内存 | 8K 图 ≤ 2.5× 单帧 | `GC.GetTotalMemory` + WorkingSet |
| 4 | 位深保真 | 10/16 bit 全链路不塌到 8 bit | 16-bit PNG / TIFF 往返端点误差 0。原本的 10-bit HEIF 端点**测不了**：HEIF 是独立包且需手动注册，本库不接入（`known-gaps.md`） |
| 5 | 确定性 | 四档 SIMD 逐字节一致（**设计目标**） | CI 强制三档；AVX-512 在卷积内核实现，正确性经软件模拟验证，吞吐待硬件，见 `known-gaps.md` |
| 6 | 色彩 | ICC→ICC **与 4.1.3 对拍**，不只比"有没有" | ΔE2000 ≤ 1（矩阵/TRC），≤ 2（CLUT）。**尺子已在 M13 落地**（`ColorDifference`，Sharma 34 组参考全过）；对 4.1.3 的同图对拍仍被许可密钥挡着（§10） |
| 7 | AOT | 全库 NativeAOT + 零 trim 警告作为构建门禁；4.x 仍在补 AOT 种子 | publish + `warnaserror` |

读法：第 1 条是唯一"可能输"的，所以它进基准而不是承诺。第 4、5、7 条是结构性的——4.x 的处理管线建在 `Image<TPixel>` 上，位深由像素类型决定，跨档位一致与全库 AOT 都不是它的契约。第 6 条从"人无我有"降级为"人对人更准"：4.x 的 ICC 是 v4.0 的新特性，我们必须在 ΔE 上赢，不能只靠"支持 ICC"这句话。

**一条非技术事实**：ImageSharp 4.x 是 Six Labors Split License 1.0（商业使用按官方条款购买许可，有免费门槛）；本项目按 MIT 发布（M0 落 `LICENSE`）。这不是技术优势，但是选型时的真实差异，写进 README 的对比表里，别只放在文档里自嗨。

---

## 2. 底层能力盘点（事实，非推断）

读的是已发布的 `Sdcb.SimdCodecs` **2.0.0** 包（源码提交 `3b43931`，2026-11-09；版本族 2.0）。下表"位置"列写包名与类型，**不再指向本地源码路径**——仓库里没有那份检出。

### 2.1 已有的

| 能力 | 位置 | 对我们的意义 |
|---|---|---|
| 12 个格式族（JPEG/PNG/TIFF/WebP/GIF/BMP/ICO/QOI/PNM/JP2/JBIG2/HEIF）+ EXIF / ICC / 增益图等辅助包 + 门面 | 包 `Sdcb.SimdCodecs*` | 全部编解码外包，我们一行都不写。门面引用 12 族里的 11 个；HEIF 是独立包、需手动注册，本库不接入 |
| `IDecodedImage`：原始通道数、8/16/float、真实位深、非预乘 | `Abstractions/IDecodedImage.cs` | 保真层的唯一入口 |
| `PixelConverter`：16 种 `PixelFormat` 任意互转，全部有 SIMD 内核 + 标量孪生 | `Abstractions/PixelConverter.cs` | **我们绝不复刻**，边界上一次转换到位 |
| `RasterImage.Wrap(..., IMemoryOwner<byte>)` | `Abstractions/RasterImage.cs` | 池化 buffer 可以**零拷贝**交给编码器 |
| `DecodeOptions`：`Region`、`ScaleDenominator`（任意 2 的幂）、`ApplyOrientation`、`Pool`、`CancellationToken` | `Abstractions/DecodeOptions.cs` | 区域/缩放/方向解码可**下推**到 codec |
| `TryGetClosestSize`、`TryDecodeThumbnail`、`IPlanarImageDecoder` | 同上 | 缩略图、YCbCr 直通 |
| `ImageDecoder` 会话 + 多帧 + 时长 | `Sdcb.SimdCodecs` / `ImageDecoder.cs` | 动图/多页的帧模型 |
| `ColorEncoding`（H.273 primaries/transfer/matrix） | `Abstractions/ColorEncoding.cs` | 只**标记**，从不重算像素 |
| `ImageMetadata`（Exif 原始块、XMP、ICC） | `Abstractions/ImageMetadata.cs` | 透传 |
| `Sdcb.SimdCodecs.Exif`：解析 + 写回 | 包 `Sdcb.SimdCodecs.Exif` | 方向、GPS、拍摄参数 |
| `Sdcb.SimdCodecs.Color.Icc`：**只有 profile 字节**（sRGB v4） | 包 `Sdcb.SimdCodecs.Color.Icc` | ICC **变换**是空白，归我们 |

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
  ├─ SimdImaging                    门面：Load / Save / Mutate / Pipe
  │    ├─ SimdImaging.Drawing       P1 起：形状 / 路径 / 渐变 / 抗锯齿
  │    ├─ SimdImaging.Processing    全部算子与 SIMD 内核（清单见 operators.md）
  │    └─ SimdImaging.Abstractions  Image / WorkingBuffer / IImageOp
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
  SimdImaging.Abstractions/         # 契约：WorkingBuffer / PixelView / IPixelPool / IImageOp / 选项
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
ImageCodecs.TryDecode(bytes, out IDecodedImage decoded, options, ct);
// 16-bit PNG / 16-bit TIFF 直接落到 Rgba16，精度不经过 8 位
Image image = Image.FromDecoded(decoded, WorkingFormat.Rgba16, pool);
// 内部：PixelConverter.ConvertRow(decoded.Pixels, decoded.Channels, decoded.SampleFormat,
//                                 dst, PixelFormat.Rgba64, w * h)
```

`PixelConverter` 已对 16×16 组格式做过逐字节对标，这里只调用，不重写。源是 `Rgba8` 且工作格式是 `Rgba8` 时，退化为整块 `CopyTo`。

**② 处理 → 编码（池化内存直接交给 codec）**

```csharp
// WorkingBuffer 实现 IMemoryOwner<byte>，且默认 tight（stride == w*4*bpp）
var owner = image[0].Pixels;                          // WorkingBuffer : IMemoryOwner<byte>
var raster = RasterImage.Wrap(w, h, 4, SampleFormat.UInt8, owner);
encoder.AddFrame(raster, ...);                        // 不再复制
// 注意：Wrap 接管所有权 —— 之后不要再 Dispose owner，否则双重归还池。
```

前提是 tight。`Slice()` 出来的视图 stride 可能大于行宽——此时 `ToRasterImage()` 会**显式**复制一次并记录一次分配，不做隐性降级。

**③ 元数据回流（不解析、只搬运）**

`IDecodedImage.Metadata` → `Image.Metadata` → `SaveOptions.Metadata` → 编码器写回。Exif 的字段级读写由 `Sdcb.SimdCodecs.Exif` 负责。

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

public sealed class WorkingBuffer : IDisposable, IMemoryOwner<byte>
{
    public static WorkingBuffer Rent(int width, int height, WorkingFormat format, IPixelPool? pool = null);
    public static WorkingBuffer Wrap(IMemoryOwner<byte> owner, int width, int height, int stride, WorkingFormat format);

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }          // 字节
    public WorkingFormat Format { get; }
    public bool IsTight { get; }        // Stride == Width * BytesPerPixel
    public PixelView View { get; }      // 只读/读写视图，零拷贝
    public WorkingBuffer ToTight();      // 必要时才复制
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
- **`WorkingBuffer` 直接实现 `IMemoryOwner<byte>`**，这是零拷贝交给 `RasterImage.Wrap` 的开关。
- **类型名叫 `WorkingBuffer`，不叫 `PixelBuffer`**：`Sdcb.SimdCodecs.Abstractions` 2.0 自带一个 `PixelBuffer`（codec 侧，native layout，按 `PixelFormat` 而非 `WorkingFormat`），而每个碰像素的文件都同时 `using` 两个命名空间。2026-11-11 从其 2.0.0 包切换时改名，而不是在每个文件写 `using` 别名——别名的代价是每个新文件都要记得加，改名只付一次。
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
    public Image Clone(IPixelPool? pool = null);              // 复制一张，原图不动
    public Image Mutate(Action<ImagePipeline> steps, IPixelPool? pool = null);   // 原地跑一条链
    public Image Apply(ProcessingPlan plan, IPixelPool? pool = null, PlanObserver? observe = null);
    public void Dispose();
    // Pipe() 是扩展方法（ImagePipelineExtensions）：image.Pipe().A().B().ToImage()
    // 静态工厂 Load / TryLoad、静态 Save / Encode 见 §5.5
}

public sealed class ImageFrame : IDisposable
{
    public WorkingBuffer Pixels { get; }
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

两种用法，同一个执行器：

```csharp
// 1) 返回新图
using var result = image.Pipe()
    .Resize(1920, 1080, Resampler.Lanczos3)
    .Grayscale()
    .Contrast(1.1f)
    .ToImage();

// 2) 原地生效（同一个链类型，结果写回这张图）
image.Mutate(p => p.Resize(1920, 1080, Resampler.Lanczos3).Grayscale().Contrast(1.1f));
```

链的每一步是 `ProcessingPlan` 里的一个 `ImageOp`：算子自己带着「读多远」的支撑半径，`PlanOps` 的工厂从
算子参数推导它（高斯是 `ceil(3σ)`、盒式是半径、点算子是 0），所以调用方既不用写、也写不错。
`Add(ImageOp)` 是唯一的公共入口，`PlanSteps` 与字符串版 `Add` 都是 `internal`。
逐算子的判定见 [`operators.md`](operators.md)。

**计划期做的四件事**（这是"超越"里最实的一块）：

1. **点算子融合**：连续的 `Brightness → Contrast → Gamma → Levels` 合成一张 256/4096 项 LUT，四遍内存往返变一遍。
2. **预乘合并**：滤波链入口 `Premultiply` 一次、出口 `Unpremultiply` 一次，不在每个算子内部反复乘除（8 位反复预乘会有可见漂移）。
3. **缓冲 ping-pong**：两张 scratch 交替，最后一张直接成为结果，避免收尾大拷贝。
4. **下推**：`Load + Crop + Resize(1/8)` 时把 region/scale 下推给 `DecodeOptions`；`Save` 时按 `GetClosestInputFormat` 选目标 `PixelFormat`，避免编码器内部再转一次。

`PlanReport` 是结构化对象（段、回退原因、worker 数、band 数、池租借、耗时），只在
`ExecuteOptions.Diagnostics = true` 时构造；诊断关闭时不建对象、不读 `Stopwatch`。

### 5.5 门面：静态成员挂在 `Image` 上

不另立门面类型。命名空间已经叫 `SimdImaging`，再来一个 `SimdImaging` 类会变成 `SimdImaging.SimdImaging.Load(...)`；而 `Image.Load(...)` 是 .NET 图像库的通用心智模型（ImageSharp 同形），学习成本为零。

```

**实现状态（2026-11-16）**：上面的写法就是当前实现。`PlanReport` / `PlanObserver` 存在，但**带观察者会强制退化为逐步执行**，所以它用于解释分段原因，不代表实际执行形状；需要观测真实执行时用 `benchmarks` 的 `fusion` 模式（按租用次数判定）。
- 行带循环已改为**并行**（`RowParallel.ForBands`）。因此**内核会被并发调用**，`OpKernel` 必须线程安全，
  且只能写 `destinationRect` 内的行。这是 2026-11-06 的契约变更。
- **跨步骤已融合（M10）**：连续可切带步骤（Point / Neighborhood / Separable）按段经 `FusedExecutor`
  的窗口递归执行，halo 沿链累积，每个 worker 两张 band 窗口 ping-pong，输出与逐步骤执行逐字节相同；
  `Global` / `Composite` 是段边界，整图执行。`PlanObserver` 存在时强制回退逐步骤（要看到每一步的整幅结果），
  回退原因进 `PlanReport.FallbackReason`。
- 点算子链还有一条更强的路径：`AddPointLut` 把连续 4×256 字节表折叠成一张、单遍执行（Rgba8）。
  这是构造性恒等（逐步执行本来就在字节域量化），不是近似。
- 观察者路径仍每步一张整图；融合路径每段一张整图 + 每 worker 两张窗口。两者是两条路径，不是同一件事。
- **改变尺寸的算子不是 plan step**：`Crop` / `Resize` / `SeamCarving` 都产生不同尺寸的结果，而 `Execute`
  按输入 `width×height` 租缓冲、按 `BandRows` 切带，没有「输出尺寸」这个概念。它们只能在 `Image` 层调用。
  反卷积（`RichardsonLucy`）是同尺寸的邻域算子，**可以**作为 `Neighborhood` 步进计划，但 halo 是 `iterations × PSF 半径` 而不是一个半径：k 次迭代的支撑是 k·r，声明成 r 会在每条 band 边界留 1 LSB 的差（`RunningAsAPlanStep_MatchesTheWholeImageCall` 抓到的就是这个）。

csharp
public sealed partial class Image : IDisposable
{
    public static Image Load(ReadOnlySpan<byte> data, LoadOptions? options = null, CancellationToken ct = default);
    public static Image Load(string path, LoadOptions? options = null, CancellationToken ct = default);
    public static Image Load(Stream stream, LoadOptions? options = null, CancellationToken ct = default);
    public static bool TryLoad(ReadOnlySpan<byte> data, out Image? image, LoadOptions? options = null, CancellationToken ct = default);
    public static bool TryLoad(Stream stream, out Image? image, LoadOptions? options = null, CancellationToken ct = default);
    public static Task<Image> LoadAsync(Stream stream, LoadOptions? options = null, CancellationToken ct = default);
    public static Task<Image> LoadAsync(string path, LoadOptions? options = null, CancellationToken ct = default);
    public static Task<Image?> TryLoadAsync(Stream stream, LoadOptions? options = null, CancellationToken ct = default);
    public static Task<Image?> TryLoadAsync(string path, LoadOptions? options = null, CancellationToken ct = default);
    public static void Save(Image image, string path, SaveOptions? options = null, CancellationToken ct = default);
    public static byte[] Encode(Image image, ImageFormat format, SaveOptions? options = null, CancellationToken ct = default);
}

public sealed record LoadOptions
{
    public WorkingFormat? Format { get; init; }        // null：按源位深自动选 Rgba8 / Rgba16 / Rgba32F
    public bool ApplyOrientation { get; init; }         // 下推 DecodeOptions.ApplyOrientation；默认 false：报告标签、不动像素
    public PixelRect? Region { get; init; }             // 下推给 DecodeOptions
    public int? ScaleDenominator { get; init; }         // 2 的幂；输出恒为 ⌈w/d⌉，原生没有该档时解到原生档再 box 过滤
    public bool PreferNativeScale { get; init; }        // true：只取 codec 原生档，跳过 box 过滤（结果可能比请求的大）
    public IPixelPool? Pool { get; init; }
    public long MaxPixels { get; init; } = 1L << 28;    // 268 MPix 上限，防 OOM
}
```

`ScaleDenominator` 的语义照抄 SimdCodecs 2.0：**任何 2 的幂都接受**，输出恒为 `⌈w/d⌉ × ⌈h/d⌉`；codec 能原生缩的档位（JPEG ≤8、JPEG 2000 到其分解级数）就按那一档解，差值由 `BoxDownscaler` 做掉——精确，但多一趟整图过滤。`PreferNativeScale` 是这趟过滤的开关：打开就拿 `TryGetClosestSize` 协商出的原生档，输出可能比 `⌈w/d⌉` 大，但少一趟。原生只有 1 的 codec 不参与协商——拿了等于整幅解码，每个维度都更差。

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
  decoded (codec 池)  ──PixelConverter 一次──►  WorkingBuffer (SimdImaging 池, tight)
处理:
  A ──op──► B ──op──► A ──op──► B     (ping-pong，2 张 scratch)
输出:
  B 直接成为 Image 的帧 buffer（不复制）
Save:
  WorkingBuffer(=IMemoryOwner) ──► RasterImage.Wrap ──► encoder.WriteRows
```

设计目标：稳态分配 = 0，唯一允许的分配是权重表首次构建、`PlanReport` 文本、以及用户显式调用的 `Clone/ToArray`。**2026-11-11 实测未达成**：流水线 7–39 KiB/op、Rgba16 单算子 0–15 KiB/op（见 `known-gaps.md` §「M2–M4 出口条件核验」）。**2026-11-15 在 M10 融合路径上重测仍未达成**：`AddPointLut` LUT 折叠稳态 ≈4.3 KiB/op（§1.3 字面尺）/ ≈6.0 KiB/op（进程级），`degree=1` 下限 2144 B/op；残余为 per-call 执行计划重建（详见 `known-gaps.md` §「M2『点算子融合后分配』重测与判定」）。

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

> x64 AOT 的默认基线是 SSE2：ILC 会把 `Avx2.IsSupported` 折叠为 false 并裁掉 AVX2 内核，运行时变慢且不报错。在我们这里表现为"测试通过但没测到东西"，比性能损失更难发现。

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
| **M1 几何（已完成）** | Crop / Pad / Flip / Rotate 90·180·任意角 / Affine / Perspective / Resize（5 种采样器）/ Premultiply / Unpremultiply / Composite；SIMD 内核与四档一致性、`ProcessingPlan`、PublicAPI 快照 | ✅ 黄金测试通过、三档逐字节一致（AVX-512 档预留）；对 ImageSharp 的倍数见 `docs/measurements.md` |
| **M2 色彩 + 滤波（功能完成）** | 35 个点算子 + LUT 字节域融合、Gaussian/Box/Median/Sobel/Unsharp、直方图与均衡、自适应二值化 | 模糊 ≥ 1.5×：**1080p 实测 1.61–1.86×，达标**（11-07）；「点算子融合后分配 = 0」：**❌ 已判定未达标（2026-11-15，M5 出数）**——M10 的 `AddPointLut` 折叠与窗口递归落地后重测，稳态 ≈4.3 KiB/op（§1.3 字面尺 `GC.GetAllocatedBytesForCurrentThread`）/ ≈6.0 KiB/op（进程级），两尺子、两次独立运行、两尺寸可复现；残余是 per-call 计划重建而非像素缓冲。0 B/op 未达成，与 §1.3/§6.1/README 既有口径一致；11-11 的 7 / 39 / 8 KiB/op 是融合前的旧读数，不作结论。见 `known-gaps.md` §「M2『点算子融合后分配』重测与判定」 |
| **M3 量化 + 绘制（功能完成）** | Octree/Wu/Palette/WebSafe、抖动、路径与形状、18 blend + 12 Porter-Duff、渐变、水印 | 量化 PSNR：**16.61 dB**（Wu / 64 色 / 无抖动，同参数对拍，11-11）；本行未给阈值，只能记数、不能判达标。抗锯齿覆盖率：**判据未定义**（无测法、无阈值，现有测试只有 `partial > 0` 存在性断言）→ 仍不可判定 |
| **M4 HDR/ICC/高级（功能完成）** | 传递函数、色调映射、gain map、ICC 矩阵/TRC（LUT 顺延 M13）、CLAHE、双边、行带流式 | 8K 峰值内存：**4 场景 0.74 / 2.68 / 1.01 / 1.01× 单帧**（单帧 126.56 MiB，2.5× = 316.4 MiB），gaussian-blur 339.0 MiB **超 7.1%**；10-bit HEIF → 16-bit PNG 端点：**测不了**——HEIF 未接入、也无可编解码语料，未出数（`Png_Rgba16_RoundTripsByteExact` 只证 16-bit PNG 往返，不等于 HEIF 端点） |
| **M5 验收（已完成）** | 19 场景基准报告、分配报告、AOT 报告、`known-gaps.md` 定稿 | ✅ 2026-11-15：§1.3 七条**全部出数或写明不可测原因**——吞吐 1080p 19/19 领先（2.1632×）达标；稳态分配 4.3–6.0 KiB/op **未达标**；8K 峰值 3/4 达标（`gaussian-blur` 2.68× **未达标**）；位深 16-bit PNG 逐字节达标、HEIF 端点架构下不可测；三档逐字节一致（963×3、body SHA `9bd6c8df…`），第四档未实现；ICC ΔE 尺子对 Sharma 34/34 达标、与 4.1.3 对拍缺；AOT 双档逐字节达标。**总账 2 达标 / 3 部分 / 2 未达标**，逐条见 `known-gaps.md` 的 M5 节 |
| **M6 治理与验收基建前置（已完成）** | 参考向量包 / 像素差异工具 / CC0 语料清单 + SHA-256 下载脚本 / SIMD 档位矩阵 / 版本化性能基线格式与 `gates.sh --perf` 框架 / AotSmoke 必需 hash 检查与 `warp` 条目 | ✅ 2026-11-14：`gates.sh` observed `warnings=0 errors=0`、`failed=0 passed=619`、PublicApi 快照 unchanged；`simd-tiers.sh` 三档 JIT 各 619/619，算子输出 body SHA-256 均为 `44390203…6763`；`run.sh` 的 JIT/AVX2/SSE2 三路 `diff` 无差且必需 hash 含 `warp` / `samplepixel`；语料 2/2 条 SHA-256 通过；`--perf` 如实输出 `enforced=0`（框架入库、阈值待 M15）。AOT 档无法跑 xUnit，以 smoke 的三路字节比对代替第四次全量测试，这是口径说明不是宣称跑了四套测试 |

| **M7 P0 正确性止血（已完成）** | `Execute` / `Transforms.Copy` 异常所有权；`SamplingCore` 格式原生采样 + `SampleOptions` 契约 + `SampleRaw`；Warp 精度回归；AotSmoke `warp` / `samplepixel` 条目 | ✅ 2026-11-14：计数池 5 例（Global kernel / band kernel / observer / 正常三步 / 空 plan）验证 `Rent == Dispose`、重复释放 0；`WarpPrecisionTests` 12 例覆盖三格式恒等逐字节、Rgba16 低字节、Rgba32F 的 HDR>1 / 负值 / 超范围、边界背景、非整数坐标、8-bit 兼容重载、不支持契约显式抛、map 抛异常不泄漏；`gates.sh --aot --perf` observed `warnings=0 errors=0`、`failed=0 passed=636`、快照 review 后 unchanged、AOT 三路 `diff` 无差；`simd-tiers.sh` 三档各 636/636，body SHA-256 `44390203…6763` 与 M6 相同（Rgba8 输出字节未变，旧内联断言全部未改）。`docs/public-api.txt` 只有新增条目、无现有签名改写 |

| **M8 采样三链统一与色彩域（已完成）** | `SampleTransferCurve` 镜像 enum / `SampleOptions.Curve` / `TapPlan` 唯一权重生成器 / `SampleTransfer` / 统一重建核心（含 tap-plan 注入重载）/ `WarpWithOptions` + 第二波 `Mapping.TransformWithOptions` / `PerspectiveWithOptions` / `Resample.ResizeWithOptions` / `Image.{Resize,Rotate,Affine,Perspective}WithOptions` | ✅ 2026-11-14：`gates.sh --aot --perf` observed `warnings=0 errors=0`、`failed=0 passed=668`、快照 review「只有新增、0 删除 0 改写」；**剥离 AotSmoke 全部 5 条新增行后，既有算子 body SHA-256 仍为 `44390203…6763`**（既有输出字节零变化）；三档新 body SHA `7a7e74fe…` 一致；AOT 三路 `diff` 无差；`SampleOptionsContractTests` 12 例 + `GeometryOptionsContractTests` 13 例（含 2 条新向量执行断言）+ Mapping 等价扫描 9 例；语料 2/2。**Mapping 接入 TapPlan 按等价扫描判分支 B**：legacy 逐字冻结（Rgba32F 在 2/33 矩阵差 1 ULP，根因 = JIT 对 legacy 一次性 Σw·c 表达式做 FMA 融合而循环累加不融合，见 `known-gaps.md` M8 节与 `Diagnose_DumpsBothWeightPathsForTheShearCase`），仅新 WithOptions 走 `MappingTapSampler`；零值张力（全零 record = 历史契约）已在入口 xmldoc 与 known-gaps 登记。EWA 与融合属 M9/M10 |

| **M9 几何采样突破（EWA / 自适应 AA）（已完成）** | `SampleAaMode{None,Elliptical}` + `SampleOptions.AaMode` / `SourceJacobian` / `EwaKernel`（椭圆高斯足迹 `w=exp(-0.5·dᵀ(JJᵀ)⁻¹d)` + 退化判定）/ `AdaptiveAa`（\|det J\| 分档；轴比只判退化）/ `SamplingCore` Jacobian 重载 / `Mapping.TransformWithOptions`・`PerspectiveWithOptions` 的解析 Jacobian（affine 常量、perspective 逐像素解析）/ `WarpWithOptions` 传 Elliptical 显式抛 | ✅ 2026-11-14：**足迹公式勘误后重测**（原 `Q=MᵀM`+`k²` 把缩小做成了锐化，2× 缩小 σ≈0.35 px；已改为 Heckbert 定义 `R=(JJᵀ)⁻¹`、2σ 窗口、`(maxTaps+1)²` 缓冲，见 `known-gaps.md` M9 节）。`EwaPrecisionTests` 18 例（对称中心保真 128、独立双精度高斯逐字节 3 相位、单像素白线 0.25× 缩小四行都必须保留能量且 legacy 双线性丢失该线、`(maxTaps+1)²` 预算边界不越界、`AdaptiveAa` 5 档、退化 status 可观察 + 各向同性回退逐字节、Nearest/Warp+Elliptical 显式抛、强透视远端无 NaN、HDR LinearLight 往返、2 条 ewa 向量执行断言）+ `TapPlanTests` 7 例；`gates` observed `failed=0 passed=698`；三档 body SHA `42c595fe…` 一致，**剥离 6 条新增 smoke 行后精确回到 `44390203…6763`**（legacy 字节零变化）；AOT 三路 diff 无差；快照新增 `SampleAaMode` 4 行、0 删除 0 改写；`ewa.identity`=96 / `ewa.minify`=128 独立推导逐字节一致（两者都是对称足迹，对公式修正不变）。**设计决定**：EWA 累加保持纯标量（无 SIMD 层），SIMD twin 顺延 M15——规避 M8 等价扫描暴露的「legacy 表达式 FMA 融合 vs 循环累加不融合」形状不对称在参考/加速双路径间复现；`ewa.aa.tier.*` 阈值待摩尔纹语料扫描回填（P4） |

| **M10 执行架构：真正融合（已完成）** | `ExecuteOptions`（degree / `MaxWorkingBytes` / `AllowFusion` / `Diagnostics` / 取消）/ `ProcessingPlan.ExecuteWithOptions` + `Image.ApplyWithOptions` / `PlanReport` + `PlanSegment` + `FusionFallbackReason` / `FusionPlanner` 分段 / `FusedExecutor` 窗口递归（halo 累积、同原点视图、按 worker 的两张窗口 ping-pong）/ `ProcessingPlan.AddPointLut` + 连续字节表折叠 / `CountingPool` | ✅ 2026-11-14：`gates.sh --aot --perf` observed `warnings=0 errors=0`、`failed=0 passed=722`、快照两次 review「只有新增、0 删除 0 改写」；三档 JIT 各 721/721，body SHA `d6781e6e…` 一致，**剥离 7 条 smoke 行（M8 5 条 + `ewa.rotate` + `plan.fused`）后精确回到 `44390203…6763`**；AOT 三路 `diff` 无差且 `REQUIRED` 含 `plan.fused`；`FusedExecutorEquivalenceTests` 18 例（P³ × bandRows{1,2,7,64} × degree{1,2,4}、P+N(3)、N(2)+N(3)、N(3)+P+N(1)、P+N(6)（halo>band）、N+Global+N、Separable+P、Rgba16/Rgba32F、结构化 rect 断言、预算/取消/诊断）+ `PointLutCompositionTests` 4 例（折叠表 == 逐步应用，用真实点算子的 4×256 表验证）；`ProcessingPlanLeakTests` 按新内存模型重写（1 张输出 + 2×worker 窗口，异常路径全归还）；`--perf` 如实 `enforced=0`。**口径**：融合只改内存曲线与池租借，不改字节；halo 声明小于算子真实支撑时融合与原路径本来就会不同（已写入 `AGENTS.md`） |
| **M11 高级照片算法 I（已完成）** | `NonLocalMeans`（精确无剪枝、Rgba8 整数 / Rgba16 long / Rgba32F double 距离、AVX2 SSD 标量孪生、可切带 halo=`S+P`）/ `Pyramid`（Burt–Adcock 5-tap，层原生尺寸，分析/合成同核）/ `LocalLaplacian`（按层选样、code-value LUT、原始基带残差、层原生存储、2 GiB 硬上限） | ✅ 2026-11-14：`gates.sh --aot --perf` observed `warnings=0 errors=0`、`failed=0 passed=768`、快照两次 review「只有新增、0 删除 0 改写」；三档 body SHA `3d51c3a6…` 一致，**剥离 9 条 smoke 行（M8 5 条 + `ewa.rotate` + `plan.fused` + `nlm` + `ll`）后精确回到 `44390203…6763`**（既有算子字节零变化）；AOT 三路 `diff` 无差且 `REQUIRED` 含 `nlm` / `ll` / `bilateral`（补上 bilateral 只 hash 不 gate 的欠账）；`NonLocalMeansTests` 19 例（双精度朴素参考逐字节：六组尺寸×S×P、3×3 图 S=5/P=2 专抓行环播种、degree 1/2/16、AVX2 vs 标量孪生、PSNR +3 dB、Rgba32F NaN 有界）+ `LocalLaplacianTests` 11 例（α=β=1 恒等 ≤1 LSB、LUT==直算逐字节、小图 levels 钳制、对比度 1.5×、常数图不变）+ `PyramidTests` 10 例（双精度参考）；NLM 1080p 实测 **S=3/P=1 5.8 s、S=5/P=2 16.1 s**（默认按实测下调为 S=3/P=1）；LL 1080p 实测 **1.81 s / 分配 139.7 MB**（4K 按面积外推约 560 MB，未实测）。**口径**：exp 是 NLM SIMD 之后的地板（不剪枝、不近似）；LL 的细节增益由采样间距/σr 决定（实测 1.5×），插值变体未做 |
| **M12 反卷积与内容感知缩放（已完成）** | `RichardsonLucy`（高斯 PSF，可分离）/ `RichardsonLucyMotion`（PSF 取 `MotionBlur` 同一套整数偏移 ⇒ 真逆对，稀疏 tap）/ `PlanarConvolve`（float 平面，向量轴为像素、tap 升序）/ `SeamCarving`（整数 luma 能量 + DP backpointer，删/插/`CarveTo`，水平走转置）/ FFT（评估不落地，见 `docs/known-gaps.md`） | ✅ 2026-11-14：`gates.sh --aot --perf` observed `warnings=0 errors=0`、`failed=0 passed=799`、snapshot 新增 15 行 0 删除 0 改写；三档 body SHA `cf7dc7f8…9838` 一致，**剥离 13 条 smoke 行（M8 5 条 + `ewa.rotate` + `plan.fused` + `nlm` + `ll` + `rl` + `rl.motion` + `seam`）后精确回到 `44390203…6763`**；AOT 三路 `diff` 无差且 `REQUIRED` 含 `rl` / `rl.motion` / `seam`；`RichardsonLucyTests` 14 例（0 迭代逐字节拷贝、高斯去模糊 PSNR 10.55→13.58 dB、运动去模糊 7.23→11.52 dB、alpha 逐字节不变、Rgba32F 4.0 不钳、标量/向量孪生、degree 1/2/16 一致、非法参数抛）+ `SeamCarvingTests` 14 例（平局恒删第 0 列、强边方块保宽、横向==转置竖向、插入后删除回原尺寸、三格式保留字节、确定性）+ 融合等价 +3（RL/运动 RL 作为 `Neighborhood` 步）+ halo 对拍（计划步 == 整图调用，halo = `iterations×半径`）。**实测**：1080p σ=1.5 十次迭代 254 ms / 分配 95 MiB；运动 len=15 347 ms；接缝裁剪 1080p 删 50 列 502 ms（≈10 ms/缝）、分配 4 MiB。**口径**：FFT 未落地（σ ≳ 18 px 才有算术优势，且破坏 D1，见 `docs/known-gaps.md` 的评估记录）；接缝的「增量能量更新」实测否决（影响锥每行扩散一列，整高缝到底行覆盖全宽），文档写明而不是静默省略 |
| **M13 HDR GainMap 与 ICC LUT（已完成）** | `IccProfile`（header/tag 表校验 + `mft1`/`mft2`/`mAB`/`mBA` 解析 + 方向选择）/ `IccClut3D`（u16 网格、Q15 权重、int64 累加、四面体六分支）/ `IccTransformOps` 行带并行 + PCS 缝合 / ICC 边界（见 `docs/known-gaps.md` 的评估记录） / `GainMapParameters` + `HdrOps.ReconstructGainMap`（hdrgm：min/max/gamma/offsetSdr/offsetHdr/capacity，1/4 尺寸双线性上采样，行带并行）/ `GainMappedImage`（Crop·Flip·Rotate90/180·Rotate·Resize·Affine·Perspective·WarpNormalized 的同步几何 + Load/Save/FromHdr/ToHdr）/ `ColorDifference`（ΔE2000） | ✅ 2026-11-14：`failed=0 passed=864`（+65 例：ICC 28、ΔE 7、配对几何 22、Ultra HDR 8）；ΔE2000 对 Sharma 34 组参考全部 ≤1e-4；ICC 1080p 实测 `grid=33` **40.0 ms** / `grid=2` **36.5 ms** / 同图 matrix-TRC **34.8 ms** ⇒ CLUT 只占约 9%，**per-pixel SIMD 实测否决**（AVX2 无 64 位整数乘、8 角点 gather），见 `known-gaps.md` M13 节；gainmap 重建 1080p **28.4 ms**（加行带并行前 138.6 ms）、配对 resize 1080p→720p **5.2 ms**；快照 58 行**只有新增、0 删除 0 改写**；`run.sh` 的 `REQUIRED` 含新增 `icc.lut` / `gainmap.hdr` |
| **M14 内存预算与预估 API（已完成）** | `FloatScratch` per-thread 字节预算（64 MB 保留 / 32 MB 单块上限、最久未用淘汰、命中/分配/淘汰计数、`Trim`）/ 权重表缓存从「24 条 + 全清」改为 8 MB + 64 条 + LRU + 命中率 / `MemoryBudget.Snapshot()·Trim()·SetLimits(...)` / `MemoryEstimator.Estimate(...)`（源、目标、计划 scratch、每 worker 声明、峰值；`checked` 溢出安全）/ `PlanStep.Scratch` + `Add(..., scratch)` 6 参重载 / `RichardsonLucyOps.ScratchBytes` / `CountingPool` 增加到字节峰值统计 | ✅ 2026-11-14：`gates.sh --aot` → `warnings=0 errors=0`、`failed=0 passed=884`（+20 例）、快照**只新增不改写**（5 参 `Add` 原样保留，新增 6 参重载）；三档 body SHA 仍为 `846c1c62…20c6`（**既有算子零字节变化**，与 M13 相同）；预估对拍 `CountingPool.PeakBytes` 三个尺寸全过（`PeakBytes ≥ 实测 + 源`）；融合算术锁常量：两个 point 步 / 64 行 band / degree 4 → `2×4×491520 = 3 932 160` 字节；预算压到 0 后 resize 输出与基线逐字节相同 |
| **M15 SIMD / 门禁 / 契约收口（进行中）** | 已完成：**性能回退门禁**（版本化基线 + 同机器同源码可比性 + 机器校正判定 + `--self-test`）与 `docs/contracts.md` 30 条逐条关联测试。AVX-512 已出评估（`docs/known-gaps.md` 的评估记录，本机 `avx512=False`，只出评估不出数）；AotSmoke 已全量核对（53 条 hash / 23 条设闸 / 0 悬空） | ✅ 2026-11-15：判定口径重做为**机器校正**——绝对毫秒在同一台机器上 4 小时内漂 1.2–1.8×，黑盒参照 ImageSharp 同幅漂移（resize-bicubic 12.5 → 19.3 ms），故每跑自带两个不调用本库的探针（256 MB 并行求和 / 32M xorshift），判定用 `got / max(mem, alu)` 相对基线校正；阈值由**五次**运行归一化后的偏差推出 `max(20, ceil(3×S))`，设闸条数随机器噪声浮动（本次 16/29，阈值 20–59%），规则见 `known-gaps.md` 的 M15 节。实测 `--perf` 正常跑 `enforced=16`、最差 **+1.4%**；`--self-test` **exit=1**（29 条全部纳入）；负载验证：4 核抢占下 `alu` 探针报 **x1.59**、校正后全部设闸场景**最差 −1.2%**，而未校正的原始读数均值 **+40%**。2026-11-14 的旧口径（两次一对照、15 个 15% 闸、`enforced=15`）已被替换，原因见 `known-gaps.md` 的 M15 节 |

> **状态口径**：M2–M4 的功能（算子、测试、公开 API）都已落地；上表「出口条件」是当初写进设计的验收标准。
> 2026-11-11 补测后：M2 模糊、M3 量化 PSNR（已出数但无阈值可判）、M4 的 8K 峰值内存有了实数
> （见 `known-gaps.md` §「M2–M4 出口条件核验」），其中 8K `gaussian-blur` **未达标**；M2 的「融合后分配 = 0」
> 2026-11-15 已在 M10 融合路径上重测并**判定为未达标**（≈4.3–6.0 KiB/op，见 `known-gaps.md` §「M2『点算子融合后分配』重测与判定」），
> 不再是「未判定」，但同样不是达标；M3 抗锯齿覆盖率（判据未定义）与 M4 的 HEIF 端点（HEIF 未接入，见 `known-gaps.md`）仍无法出数。
> 要宣称某个里程碑「完成」，先把它那一行的出口条件全部跑出数来，且必须达标。

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
- **工作格式 RGBA-only**。Gray 特化不立项（见 §5.1）。
- 对标基线 ImageSharp 4.1.3。

待定：

- **文字渲染**：推荐 v1 不做。若业务必须要，它会是比整个 `SimdImaging.Processing` 还大的独立工程。
