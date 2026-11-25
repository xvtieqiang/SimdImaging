# SimdImaging 架构设计（v0.1）

> 状态：**设计契约 + 实施记录（2026-11-25）**。M0、M1、M6–M14 已完成并验收，M15 进行中，M16（文件级流式）、M17（区域执行）与 M18（几何进入执行模型）已完成（§6.5 / §6.6 / §6.7），M5 验收未启动，
> M2–M4 功能已落地但出口条件仅部分核验。§11 中带「已完成」与实测数字的行是记录，未标记的行仍是待兑现的设计。
> 底层依赖：`Sdcb.SimdCodecs` 2.1.x，**NuGet 包**，版本锁在 `Directory.Packages.props`。仓库不持有、也不构建任何外部源码。
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

读的是已发布的 `Sdcb.SimdCodecs` **2.1.0** 包（源码提交 `8d62810`，2026-11-24；版本族 2.1）。下表"位置"列写包名与类型，**不再指向本地源码路径**——仓库里没有那份检出。

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
| `IImageRowReader` / `StreamEncoderSession`：行级读写 + `IsStreaming` 能力位（2.1.0 起） | `Abstractions/IImageRowReader.cs`、`Sdcb.SimdCodecs/StreamEncoderSession.cs` | 文件级流式的**底层**已具备；不满足流式条件的变体（渐进式 JPEG、交错 PNG、缩放/带方向解码）内部仍整图缓冲，靠 `IsStreaming` 分辨 |
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
  ├─ Sdcb.SimdCodecs 2.1.x          编解码 / 容器 / 元数据块 / 布局转换
  │
  └─ BCL：ZLibStream / Brotli / ArrayPool / Vector128 / 256 / 512
```

### 3.1 仓库结构（镜像 SimdCodecs 的决策）

```
SimdImaging.slnx
Directory.Build.props          # TFM net10.0, IsAotCompatible, PublicAPI 分析器, 统一版本
Directory.Packages.props       # 中央包版本；Sdcb.SimdCodecs 锁 2.1.x
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
    Geometric,      // 需要源坐标逆映射（不进同尺寸计划）
    Global,         // 需要全图统计（直方图、量化、CLAHE）
    Composite,      // 双输入
}

// 公开的只读视图。ImageOp 实现它；数值来自内部的 OperatorContract。
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

`ImageOp` 是唯一算子定义：内部持有一个 `OperatorContract`（输入/输出端口、几何规则、InputFootprint、分区/状态/顺序/线程/确定性、Pass、Workspace、PixelSemantics）与 `KernelImplementations`（Plan / WholeImage / Into 三种执行体）。`PlanStep` 不再复制这些字段：它只持有定义引用与 kernel 闭包，`Name/HaloX/HaloY/Scratch/NeedsWholeSource` 全部转发到定义，`Kernel` 在构造时拷成字段以免每条 band 多一层解引用。

`MapSourceRect` 因此第一次是真的：footprint 有四种——identity（点算子）、halo（常数半径）、whole-source（整图统计与 Mirror/Wrap 边界）、block-aligned（块网格）。Pixelate 声明 `block-aligned + WholeImage 分区`，不再把块对齐伪装成常数 halo；通用卷积声明 whole-source，但仍带一个半径 halo——窗口里的邻居要读，边界语义又要整图。

`ExecuteOptions` 的当前形态见 §6.2（`MaxDegreeOfParallelism` / `MaxWorkingBytes` / `AllowFusion` / `ForceFusion` / `AllowUnestimatedScratch` / `Diagnostics` / `CancellationToken`）。

算子只有一套，就在 `ImagePipeline` 上——`Image` 上没有算子方法。区别只在结果去哪：

```csharp
// 结果是一张新图
using var result = image.Pipe()
    .Resize(1920, 1080, Resampler.Lanczos3)
    .Grayscale()
    .Contrast(1.1f)
    .ToImage();

// 结果写回这张图，省掉一次 Image 分配（同一条链、同一套算子）
image.Mutate(p => p.Resize(1920, 1080, Resampler.Lanczos3).Grayscale().Contrast(1.1f));
```

链的每一步是 `ProcessingPlan` 里的一个 `PlanStep`：算子自己带着「读多远」的支撑半径，`PlanOps` 的工厂从
算子参数推导它（高斯是 `ceil(3σ)`、盒式是半径、点算子是 0），所以调用方既不用写、也写不错。
`Add(ImageOp)` 是唯一的公共入口，`PlanSteps` 与字符串版 `Add` 都是 `internal`（后者只给测试与基准造临时步骤）。
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

### 6.2.1 什么时候融合

融合省的是**中间图**，花的是一遍里多出来的窗口和按带调度。它不是免费的，所以由计划自己判断，不是一律打开。
判定在 `ProcessingPlan.ShouldFuse`，`ExecuteOptions.ForceFusion` 可以覆盖（等价性测试用它把两条路径压在
同一批输入上）。

`fusion` 模式实测的「计划 ÷ 逐次」（< 1 表示计划更快），四档尺寸 640×360 / 1920×1080 / 3840×2160 / 7680×4320：

| 链 | 640×360 | 1920×1080 | 3840×2160 | 7680×4320 | 说明 |
|---|---|---|---|---|---|
| 3×3 ×2 | 0.42× | 0.41× | 0.38× | 0.34× | 小核：带内保留 halo 行，省掉两次整图往返 |
| 3×3 ×3 | 0.41× | 0.35× | 0.37× | 0.50× | 同上 |
| 模糊 σ=3 + 2 点 | 1.10× | 0.99× | 0.94× | 1.32× | 算术是主成本；8K 慢 32%，但省下 127 MiB |

规则：**没有 halo 的链直接融合**（没有窗口要重算，只省一张整图）；有 halo 的链只在「同一执行段内被融合省下的整图中转、
按源格式每像素字节计，超过 64 MiB」时融合。两个乘数都曾经写错：字节数固定按 4 算（`Rgba16` 少算一半、`Rgba32F` 少算
四分之三），中间图数按 `步数 − 1` 算（`Global` 步会把链切开、每段各自融合，只有段内步数才算数）。2026-11-25 起按
连续可带步骤段（与 `FusionPlanner` 同一规则，但不分配分段表）和 `source.Format.BytesPerPixel()` 计算。
不融合时按**核数**而非融合窗口切带（`StepwiseBandCount`），否则 360 行的图只切出 3 带、只用 3 个核。

峰值与总分配量实测（`peakmem` 模式，独立子进程，`ForceFusion=true` vs `AllowFusion=false`，三次取中位）。
**两个数列说的是两件事**：峰值是进程某一瞬间真正占着的内存（要多大机器），总分配量是这次调用累计向 GC
要过多少（GC 要做多少活，和耗时同向）。

| 链 | 1080p 峰值 | 1080p 总分配 | 4K 峰值 | 4K 总分配 | 8K 峰值 | 8K 总分配 |
|---|---|---|---|---|---|---|
| 点 ×5 | 22.2 / **18.3** | 21.2 / **16.1** | **56.2** / 65.6 | **56.1** / 64.1 | **157.1** / 255.4 | **157.1** / 254.1 |
| 3×3 ×3 | **37.2** / 44.1 | 43.9 / 44.5 | **101.1** / 131.0 | **136.0** / 153.9 | **370.4** / 439.6 | **413.4** / 447.8 |
| 模糊 σ=3 + 3 点 | **30.9** / 42.7 | **41.7** / 45.7 | **93.2** / 130.8 | **123.6** / 155.3 | **234.1** / 447.5 | **268.3** / 452.6 |

（MiB，融合 / 逐步；**粗体**是该列更小的一侧。）

**8K 下融合把三条链的峰值都压到逐步的 52–62%。** 逐步执行不会同时驻留 N 张——池子把上一张还回去再租下一张，
峰值是**两张**；融合省下的是其中一张的大部分，自己付「输出 + 每 worker 窗口 + 每步 scratch」。

- **1080p：分链而定。** 点链仍更费（0.82×），3×3 与模糊链已转为更省（1.18× / 1.38×）。
- **4K：三条都省**（1.17–1.40×）。
- **8K：省 19–91%**，两个指标同向。

`ShouldFuse` 的 64 MiB 支路仍然成立，但今天的实测把它变成了保守的一侧：真正花钱的是点链在 1080p 以下的融合，
而那条支路正是**不**融合它们的地方。内存优化之前，1080p 三条链全部更费；现在只剩点链。

### 6.2.2 带高按像素定，不按行

分带的窗口是给 cache 用的，所以它的尺寸该用**像素**表达。`DefaultBandRows = 128` 是 1080p 下的直觉：
1920 宽是 245 K 像素，7680 宽就是 983 K —— 可分离滤波每个 worker 要三块浮点平面，8K 下每块 **15.7 MiB**。

`DefaultBandPixels = 128 × 1920`，带高取**两个上界中更紧的那个**：

```csharp
byPixels  = DefaultBandPixels / width;      // 宽图：让一条带留在 cache 里
byThreads = height / RowParallel.Degree;    // 矮图：保证带够分给每个核
bandRows  = clamp(min(byPixels, byThreads), 8, 1024);
```

只有像素预算时，640×360 会算出 384 行——整张图一个带，融合退化成单线程。两个上界一起才同时满足
「8K 下 32 行」和「640 下 22 行」。调用方显式设 `BandRows` 时按调用方的值。

`chainpeak`（8K，16 worker，三条链，总分配量，确定性）：

| 链 | 改前 | 改后 |
|---|---|---|
| 3×3 ×3 | 736 MiB | **413 MiB（-44%）** |
| 点 ×5 | 223 MiB | **157 MiB（-30%）** |
| 模糊 σ=3 + 3 点 | 522 MiB | **268 MiB（-49%）** |

（一张 7680×4320 的 `Rgba8` 是 126.6 MiB。）

时间是**白拿的**：8K 下带高 128 / 64 / 33 分别是 836 / 794 / 833 ms，差别在噪声内。

**但这条规则只属于「带数就是并行度」的执行器。** 带泵与区域执行器是**顺序出带**、带内按行并行，带高必须大到
让带自己的行循环填满机器，否则每个算子都在调用线程上串行跑，而且不报错。实测 512 宽的 3 步链：64 行带
14.8–36.8 ms，192/256 行带 **3.0 ms**（同一批像素）。因此它们用另一条规则（`SequentialBands`）：

```csharp
byWorkers = degree × RowParallel.RowsPerWorker;          // 32 行/worker
byPixels  = ceil(MinWorkPixels / width);                 // 64 K 像素，否则 RowParallel.For 不进并行
byBudget  = 4 MiB / (width × bpp);                       // 一个窗口的上限
bandRows  = min(max(byWorkers, byPixels), byBudget, height);
```

1080p 下两者都算出 128–256 行，差别在噪声内；512 宽的区域从 3–5× 慢变成与整图同价（见 §6.6.5）。

配套的两步：

- `ImageOp.Into`：分带适配器原本让算子自己租一块窗口缓冲、算完再把属于自己那几行拷出来。接了这条路的算子
  直接写进执行器给的目标，每带每步省掉一次中等分配加一次整带拷贝。8K 单步实测（张 = 126.6 MiB）：

  | 算子 | 接之前 | 接之后 |
  |---|---|---|
  | Sobel | 2.07 张 | **1.06 张** |
  | Emboss | 2.07 张 | **1.06 张** |
  | Median3x3 | 2.07 张 | **1.00 张** |
  | Erode3x3 / Dilate3x3 | 2.07 张 | **1.08 张** |

- `Separable` 的行环：整条带（`bandRows + 2·radius` 行）压成 `2·radius + tileRows` 行的环形缓冲，tile 的竖排
  优化因此保留。8K 单步模糊 446 → **326 MiB（-27%）**，Sharpen 427 → **307 MiB（-28%）**。

对手对照（`peakmem`，8K，独立子进程，一张图 126.6 MiB）：

| 算子 | 我方 | ImageSharp | |
|---|---|---|---|
| invert | 127.8 | 130.8 | 1.0× |
| grayscale | 127.9 | 130.8 | 1.0× |
| **gaussian-blur** | **185.5** | 265.9 | **1.4×（我们更省）** |
| resize-bicubic | 46.9 | 39.8 | 0.8×（差 18%，速度快 2.2×） |

`Resize` 的 scratch 也从「每个 worker 一整条源带」改成 `taps + 2` 行的环：8K→960×540 的峰值从
94.3 MiB 降到 **46.8 MiB**，耗时 260 → 269 ms（噪声内）。之前那 94 MiB 是**带重叠重算**的代价——
带宽小就要在每带边界重做 `2·taps` 行，所以「内存小」和「时间快」被绑成了二选一；环让每个源行只被水平
缩放一次，两个指标同时改善。

  等价性由 `DestinationKernelTests` 守住：两跳链 vs 逐步调用逐字节比较，覆盖 bandRows 1/4/5 × degree 1/3/4。
  故意把目标行偏移写错一行会让其中 4 个测试变红——这条路确实被测到了。
- `Sharpen` 的合成写回模糊图本身（逐元素，读写同一行），不再多租一张整图：单步 569 → 427 MiB（4.50 →
  3.37 张，-25%），并把它原本丢掉的 `pool` 参数接上。

### 6.2.3 一次执行会走哪条路：全条件表

代码里有三条互相独立的开关——**要不要融合**、**要不要分带**、**要不要走算子的整图形式**——它们决定四种组合。
任何一次「为什么这条路慢」的问题，先在这张表里定位，不要拿两个不同路径的读数直接比。

| 条件 | 走向 | 并行度来自 | 代价 |
|---|---|---|---|
| 链为空 | 直接复制 | 拷贝本身 | 一次全图拷贝 |
| 单步 + 算子有整图形式（`ImageOp.Direct`） | 整图直调 | **算子自己**（`RowParallel.For`） | 无计划、无分带、无核派发 |
| 单步 + 无整图形式 | 计划，逐步 | 带数 | 每带一次核派发 |
| 多步 + `ForceFusion` | 融合 | 带数 | halo 每带重算 |
| 多步 + `AllowFusion=false` 或存在 observer | 逐步 | 带数 | 每步一张中间帧 |
| 多步 + 类别边界（Global/几何步骤） | 按边界切段，逐段判定 | 带数 | 段间落帧 |
| 多步 + `ShouldFuse`（见 6.2.1） | 融合或逐步 | 带数 | 见 6.2.1 |
| `MaxWorkingBytes > 0` 且步骤未声明 scratch | 抛异常 | — | 估计值只是下界，拒绝放行 |

**带数是两条路径共同的并行度来源，因为带内一定是串行的。** 每个计划步骤的适配器都包一层
`RowParallel.EnterNested()`，算子内部的 `RowParallel.For` 看到嵌套就把 `Degree` 当作 1，整段在带内顺序执行。
这是有意的：外层已经把机器占满了，内层再开线程只会超订。

于是**单带 = 全串行**，代价是 4 倍——实测同一链条 `Emboss → Sobel`：

| 尺寸 | 默认带高 | 16 带 | 单带 | `Pipe()` 直写 |
|---|---|---|---|---|
| 640×360 | 3.78 ms | 4.73 ms | **13.50 ms** | 5.32 ms |
| 1920×1080 | 39.2 ms | 35.2 ms | **130.7 ms** | 34.8 ms |

`StepwiseBandCount` 因此按 `min(Degree, height / 32)` 切带，而不是沿用融合窗口的带高——融合窗口是「一带装下所有
halo」用的，拿它切逐步路径会切出几带、只用几个核。

> **一个仍然存在的缺口**：单步计划（`new ProcessingPlan().Add(op).Execute(...)`）不走 `Direct`，它按带执行。
> 实测 1920×1080 的 Median3x3，同进程三条路径：算子直调 **0.75 ms**、`Pipe()` **0.95 ms**、单步计划 **1.06 ms**。
> 但计划路径**不该为此加一条 `Direct` 捷径**：点算子在计划里走 LUT 表核，比 `Direct` 的浮点路径快 24%
> （同尺寸 brightness：直调 1.69 ms、`Pipe()` 1.79、计划 1.29）。加捷径会让最常见的点算子退 30%。
> 数字与重跑命令见 `docs/measurements.md`。

### 6.3 分块

| 阶段 | 能力 |
|---|---|
| v1（M1–M3） | 整图执行。`MapSourceRect` 曾只在 `IImageOp` 上声明、全仓零实现；2026-11-26 起由 `ImageOp` 的 footprint 真正提供（identity / halo / whole-source / block-aligned），但执行器仍只读 `HaloX/HaloY`，footprint 的消费从 P1 编译器开始 |
| P1（M4） | 行带流式：只对 `Neighborhood` / `Separable` 打开，halo = 核半径；内存从 O(H) 降到 O(band + halo) |
| P2 · 已交付一半（M17） | **矩形执行**：任意源矩形 → 窗口 → `RunWindow` → 内圈，见 §6.6。源只有 TIFF 段级实现 |
| P2 · 未做 | `Geometric`（尺寸变化）与 `Global` 两遍（先统计后处理），仍受 `MaxWorkingBytes` 约束 |

分块的验收不是"能跑"，是**输出与整图执行逐字节相同**。这一条会让不少 op 在 P1 阶段被判定为"不能分块"，那是正确结果，不是失败。

### 6.3.1 编译产物（P1 骨架，尚未接生产入口）

`PipelineRecipe` 是不可变的步骤表：`OperatorStep` 与 `GeometryStep` 并列，几何步带两种执行形式（`Reshape` 与可选的 `Wrap`）。`PipelineCompiler.Compile(recipe, CompileOptions)` 只做判定、不执行，输出 `CompiledExecutionPlan`：

- **格式**：任一步的 `Formats` 不覆盖源格式 → `NotSupportedException`，消息带步骤名（`IImageOp.Formats` 第一次有消费者）。
- **分段**：算子连续段与几何步各自成 `CompiledSection`；算子段喂给现有 `ProcessingPlan`，几何段喂给现有几何调用。
- **融合**：与 `ProcessingPlan` 共用 `FusionDecision.ShouldFuse`（同一条规则、同一处代码）。
- **后端**：Tile 请求 → `TileRefused`（首个真实阻塞：无区域写 Sink / 块对齐 / Global / 无 Tile 内核）；Region 请求且源有 `RandomRegion`、无几何 → Region；全部算子段可流、几何步都有 `Wrap`、Sink 可写行 → Stream；否则 Frame。拒绝原因复用 `StreamRefusal` / `RegionRefusal`，不新增平行词汇。
- **解释**：每个决定一行 `Explanation`，测试直接断言这些行。

本提交只有测试消费者；`ImagePipeline` 与 `ImageTranscode` 的接入在后续两步完成。

### 6.4 超限行为

- `MaxWorkingBytes` 不够 → 先尝试降档（Rgba32F → Rgba16，若算子是精确整数语义）→ 仍不够则抛，**不静默改精度**。
- `MaxPixels` 在 Load 的头信息阶段就检查，避免先分配再失败。

### 6.5 文件级流式（M16，S1+S2 已落地）

现状：`Load` 把整个文件读成 `byte[]`（`ImageIo.cs:22`），`Save` 把整图编成 `byte[]` 再落盘（`ImageIo.cs:341`）。
执行器本身早已是「只持有一带 + halo」的窗口递归（§6.2）——**缺的从来不是算法，是输入从哪来、输出往哪去**。

`Sdcb.SimdCodecs` 2.1.0 正好提供了这道缝：`ImageCodecs.OpenRowReader(stream, format, options, leaveOpen, ct)`
返回 forward-only 的 `IImageRowReader`（`ReadRows(span, stride, maxRows, ct)`，行 i 写在 `i × stride`）；
`ImageCodecs.CreateEncoder(format, stream, options, leaveOpen)` 返回 `StreamEncoderSession`
（`AddFrame → StreamFrameEncoder.WriteRows → Commit`）。两者都有诚实的 `IsStreaming`：为 false 时编解码器
自己在内部缓冲整图（渐进式 JPEG、交错 PNG、带 `ScaleDenominator`/`Region` 的解码、WebP 无损编码、
非 seekable 流上的 TIFF 补丁字节），**字节仍然正确，只是不省内存**。

#### 6.5.1 三种模式由「源能力 × 汇能力」决定，不由 API 决定

| 模式 | 源 | 汇 | 何时 |
|---|---|---|---|
| Materialized（现状） | 整图 `PixelView` | 整图 buffer | 计划含 `Global` / `Geometric`、多段、Rgba32F 链 |
| ForwardRows（M16 新增） | `IRowSource`，只能向下 | `IRowSink`，只能追加 | 单段可带化计划（`Point` / `Neighborhood` / `Separable`） |
| RandomRegion（M17，§6.6） | `IRegionSource`，任意矩形 | 调用方给的 `PixelView` | 计划必须能带化；源目前只有 TIFF 段级实现 |

#### 6.5.2 契约放 Abstractions，适配器放门面

`IRowSource` / `IRowSink` 只谈行和 `WorkingFormat`，不 import codec（依赖方向：`Abstractions ← Processing ← SimdImaging`）。
`IImageRowReader` / `StreamFrameEncoder` 的适配器在 `SimdImaging`，因为那里才允许 `using Sdcb.SimdCodecs`。

```csharp
public interface IRowSource              // Abstractions
{
    int Width { get; }
    int Height { get; }                  // header 已知 → 边界钳制与整图路径同一套，不需要"未知高度"分支
    WorkingFormat Format { get; }
    int ReadRows(Span<byte> destination, int stride, int maxRows);   // 返回行数，0 = 结束
}

public interface IRowSink
{
    void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount);
}
```

对外是 `ImageTranscode` 的链（与 `Load` / `Save` 对称；`ProcessTo` 现在只是它的内部路由目标）：

```csharp
ProcessReport report = ImageTranscode.From(sourcePath).GaussianBlur(1f).Run(destinationPath);
// Stream 源/目标：ImageTranscode.From(requestBody).GaussianBlur(1f).Run(responseBody, ImageFormat.Png, ct)
```

`ProcessReport` 把两层分开报：`Mode`（带泵还是物化）与 `ReaderIsStreaming` / `EncoderIsStreaming`
（IO 层是不是真流式），`FullyStreamed` 是三者都成的派生结论。合成一个 bool 就是在撒谎——
「执行流式、解码器内部整图缓冲」是真实且常见的一档（渐进式 JPEG、交错 PNG、带缩放的解码）。

#### 6.5.3 带泵：复用 `RunBand`，不新写内核

把 `FusedExecutor.RunBand` 从「对整图 view 做 `Slice(winTop, rows)`」重构成「接收已填好的窗口 view」：
整图路径传 `source.Slice(...)`，流式路径传从行缓存拷进去的窗口。halo 递归、边界钳制、rect 计算
一行不改——这正是逐字节等价能成立的原因，也是验收要盯的唯一一件事。

每带的动作：
1. 需要 `[top−H, bottom+H)`（`H` = Σ `HaloY`，钳到图像边界）；
2. 与上一带重叠的 `2H` 行从行缓存滑过来，其余从 `IRowSource` 读；
3. 跑这一段；
4. 最后一步产出的 `bandRows` 行是终值，写 `IRowSink`。

内存 = 两份窗口 + 行缓存（`2H`）+ 每 worker scratch + 编码器约 64 KiB，与图像高度无关。

**并行度是这条路的暗礁。** 计划路径的并行度来自**带数**：每步适配器包一层 `RowParallel.EnterNested()`，
带内一律串行（§6.2.3），实测单带慢 4 倍。带泵若顺序出带又照搬 `EnterNested`，就会退化成单线程。因此：

- 第一版（S1，已实现）：带与带之间串行（解码本来就是顺序的），每带内部恢复行并行。注意 `EnterNested` 是烘进
  `PlanStep.Kernel` 的（`PlanSteps.cs:31/62/119/132/167`），直接调 `Kernel` 仍然是串行——带泵因此用
  `RowParallel.EnterTopLevel()`（ThreadStatic，scope 内 `EnterNested` 变成 no-op）。没有它第一版会静默退化成
  单线程，而且**不报错**。
- 第二版（S2）：**测量后取消**。有序带队列原本是为了恢复带级并行，但 S1 的带内行并行已经拿到了同一件事：
  实拍 JPEG → PNG 实测流式/整图 = **1.00 / 1.00 / 1.00×**（1280×720，point / blur / 3×3）、
  **1.08 / 1.09 / 1.06×**（3069×2653）。差距约等于解码没有与计算重叠的那一点；换它要 S 个窗口同时在飞
  （S = worker 数），用 ~9× 常驻内存买 ≤9% 不成立。留下的是把 `ExecuteOptions.MaxDegreeOfParallelism`
  接进带泵（`RowParallel.EnterTopLevel(degree)`，已完成）——这才是那个参数该有的行为。

#### 6.5.4 不能流式时，物化并说明原因

| 条件 | 处理 |
|---|---|
| 计划含 `Global`（直方图 / 量化 / CLAHE） | `ProcessTo` 物化（`StreamRefusal.GlobalStep`）；plan 级调用方拿到 `TryExecuteTo` 的 false 自行决定 |
| 计划含 `Composite`（两个输入） | 同上（`CompositeStep`）——行源只供一路 |
| 某步读整图（Mirror / Wrap 卷积） | `StepNeedsWholeSource`；给带泵一个带会让它在带边反射，接缝 |
| 计划含 `Geometric` | 不可能发生：`ProcessingPlan.Add` 对 `Geometric` 直接抛（尺寸会变） |
| 源无行读投影（float 样本、HDR 传输） | `ProcessTo` 抛 `NotSupportedException`；这里没有"物化"这条路，因为行源本身不存在 |
| `LoadOptions.PreferNativeScale` 且缩档 > 1 | `DecodeNeedsWholeInput`：输出尺寸要先从整个头部协商出来，行读器必须以确定的尺寸/格式打开 |
| `ReaderIsStreaming=false` / `EncoderIsStreaming=false` | 仍走带泵，报告如实记 IO 层非流式 |
| 目标流不可 seek（TIFF） | 编码器自己退化为整图编码，`EncoderIsStreaming=false` |

#### 6.5.5 验收（与 §6.3 同一把尺子）

1. **逐字节**：`StreamingTests` 的 `Streamed_MatchesWholeImage_ByteForByte`（5 条链 × 2 种深度）、
   `BandHeight_DoesNotChangeTheBytes`（带高 1/2/7/64）、`ProcessTo_Png_*`（8/16-bit，与
   `Load → Apply → Save` 比文件字节）。
2. **内存**：`RowsHeld_DoNotGrowWithTheImageHeight` —— 高度 64 vs 512，单次读取行数不变。
3. **回退**：`Global`（Equalize）、`StepNeedsWholeSource`（Mirror 卷积）、Rgba32F 各一例，断言原因。
4. **取消**：`Cancellation_StopsMidStream` 抛 `OperationCanceledException`（不是"截断的流"）。
5. **AOT**：`stream.plan` 已进 `AotSmoke` 的 `REQUIRED`——ILC 产物与 JIT 逐字节一致，并且条目内部就断言了
   「流式输出 == 整图路径输出」。

S2 已做：`MaxDegreeOfParallelism` 进带泵、AOT 条目、头部元数据随流写出（见下）。
仍未做：`Global` 的两遍执行（需要可重开的 path 源）。有序带队列按上面的测量取消。
**区域源已由 M17 交付，见 §6.6**——带泵与区域执行是同一段 `RunWindow` 的两种喂法。

**元数据（2026-11-25 修正）**：早先的实现用 `ImageDecoder.Open` 同时取深度与元数据，实测它会**遍历整个文件**
（202 KB 的 JPEG 读 206 KB、71.7 KB 的 TIFF 读 147 KB），整条 `ProcessTo` 的 I/O 因此是文件的 2.0–3.4×。
现在默认**只读头部**：TIFF 用 `TiffReader.ReadLayout`（几百字节），其它格式用 ≤4 KiB 前缀（不够再增长到 1 MiB）。
代价是元数据默认不随行——`ProcessOptions.PreserveMetadata = true` 才走整文件那一趟，`ProcessReport.MetadataCarried`
如实报告。实测：默认 JPEG 1.01× / PNG 1.07× / TIFF 1.41×（TIFF 行读器自身预读），保留元数据 2.01× / 2.14× / 3.40×。

不做：多帧（`Encode` 已按单帧拒绝，与 §6.5 一致）、任意角度旋转、`Global` 自动两遍、Float32 流式编码。
`Region` / `ScaleDenominator` 可直接透给 `OpenRowReader`——区域解码 + 流式是同一趟，不需要额外设计。

### 6.6 区域执行（M17）

**一句话**：把「窗口」当作一张独立的小图，喂给同一个 `FusedExecutor.RunWindow`；X 方向的 halo 靠窗口更宽表达，
Y 方向靠 `haloTotal`；只把窗口内圈（= 请求矩形 R）拷进目标。内核、halo 递归、矩形算术一行不改——这是逐字节
等价最有力的结构证据，与 §6.5 的带泵同一手法。

```
R ──expand(ΣHaloX, ΣHaloY)──► W ──clamp(图像)──► W'
        │                                          │
        │  每带：IRegionSource.ReadRegion(W')  ─────┤
        ▼                                          ▼
   RunWindow(winW, winH, srcWin, outWin, winTop:0, band:0, bandRows:winH, haloTotal:ΣHaloY)
        │  每步写满整个窗口
        ▼
   拷 outWin[(R.X−W'.X), (R.Y+bandTop−W'.Y)] ──► destination
```

#### 6.6.1 契约

`IRegionSource`（`SimdImaging.Abstractions`，不 import codec）：`Width/Height/Format` 描述**整图**，
`ReadRegion(PixelRect region, PixelView destination)` 写一个绝对坐标的矩形，调用方保证它已在图内。
`ImageTranscode.From(path|stream).Region(rect)` 是门面入口（`ProcessRegion` 是内部路由目标），`DescribeRegion` 只读头部报能力。
`ProcessingPlan.CanExecuteRegion` / `TryExecuteRegion` 是计划层入口，分类复用 `StreamingExecutor.Classify`
（唯一分类器），`RegionRefusal` 是它的 1:1 映射。

#### 6.6.2 等价性

记 `h_k`、`Hx=ΣHaloX`、`Hy=ΣHaloY`，窗口 `W = expand(R, Hx, Hy) ∩ image`。归纳：第 k 步写出的像素 q，若到窗口
每条**非图像边**的距离 ≥ `Σ_{j≤k} h_j`，则值与整图相同。k=0 时源是真像素；k→k+1 时 q 的支撑全落在 P(k) 成立区，
q 离视图边 ≥ halo 时算子内 clamp 不作用于 q；距离不足的那条边必是图像边（否则窗口会继续外扩），此时 clamp 与
整图路径一致。最后一步 `ρ=Hy/Hx`，R 内所有点满足。三条支撑事实：`RunWindow` 的 rect 跨满窗口宽；
`PlanSteps.Neighborhood` 只保留内圈行；声明的 halo 必须等于真实支撑（`AGENTS.md` 已写死）。

竖直方向同理，但注意 `RunWindow` 传的是 `band:0, bandRows:winH, winTop:0`，于是每一步都写满整个窗口
（`dstTop=max(0,−remaining)=0`、`dstBottom=min(winH, winH+remaining)=winH`）。代价是 steps≥2 时比带泵多算
`(步数−1)×窗口`；换来的是「不读池里未初始化的字节」和零改动。实测前先记账（见 `known-gaps.md`）。

#### 6.6.3 内存

```
windows      = 步数 > 1 ? 4 : 2                       // 源窗 + 输出窗（+ 两步 ping-pong ×2）
windowBytes  = min(W, R.Width+2Hx) · min(H, bandRows+2Hy) · bpp
estimate     = windows·windowBytes + R 的字节 + perWorkerScratch·workers
sourcePeak   ≈ 一个段缓冲（复用）
```
与图像总尺寸无关：`windowBytes` 只含 R 与 halo。`ExecuteOptions.MaxWorkingBytes` 事前拒绝，规则与带泵同文；
`RegionOptions.MaxSegmentBytes` 事前拒绝单段过大的页。实测见 §6.6.5。

#### 6.6.4 拒绝表

| 条件 | 结果 |
|---|---|
| 计划含 `Global` / `Composite` / 读整图步（Mirror·Wrap 卷积） | `RegionRefusal.{GlobalStep, CompositeStep, StepNeedsWholeSource}`，目标一个字节都不写 |
| 非 TIFF | `NotSupportedException` |
| TIFF 4/2 样本（`ExtraSamples` 不可见） | `NotSupportedException`，消息点名 `ExtraSamples` |
| Palette / CMYK / YCbCr / CIE Lab / 透明掩码 | `NotSupportedException`（`TiffColor` internal，颜色转换是 codec 的私有定义） |
| `Int` / `IeeeFloat` / 非 8·16 位 | `NotSupportedException` |
| 单段 > `MaxSegmentBytes` | `NotSupportedException`，消息教调用方改 `RowsPerStrip` / tile |
| 区域为空 / 越界 / 目标尺寸不符 | `ArgumentException`；区域像素 > `MaxPixels` → `InvalidOperationException`（与 `Load` 同口径） |
| 流不可 seek、TIFF 头不在 0 | `NotSupportedException`（TIFF 偏移是绝对的） |

#### 6.6.5 实测与验收

- 等价：`RegionExecutorTests` 25 例（4 链 × 2 深度 × 6 矩形、带高 1/7/64、degree 1/4、非对称 halo 5×3、
  1×1 区 + halo 18），全部与 `Execute(整图).Slice(R)` 逐字节相同；`RegionEndToEndTests` 4 例，
  `ProcessRegion == Load → Apply → Crop`（含 `BitDepth` / `Orientation` / `Format`）。
- 读量（16×16 区域，512 宽，RGB8，`RowsPerStrip=32`）：512 高读 **3.06 KB**（文件 43.8 KB），
  2048 高读 **3.44 KB**（文件 174.5 KB）——目录随段数增长，与高度无关。
- 内存：100 000² 的合成源读 64×64，`CountingPool.PeakBytes` 与 4096² 源同级（测试断言 ≤1.25×）。
- 速度（带高规则修正后，同像素量对比 `Execute(整图)`）：512² 区域 **9.7 ms vs 8.1 ms**（1.20×；修正前 36.8 ms，
  3.65×）；2048² 区域 **59.6 ms vs 73.1 ms**（0.81×，窗口没有整图中间缓冲）。带高见 §6.2.2。
- AOT：`region.plan` / `region.tiff` 进 `AotSmoke` 的 `REQUIRED`，条目内部断言「区域 == 整图裁剪」。

#### 6.6.6 本切片不做

`Image.Load(path, Region)` 的 TIFF 快路径（`Load` 没有「原生/物化」报告位，且它的元数据来自整文件解码）；
4 通道 / Palette / CMYK（等 `TiffPageLayout` 暴露 `ExtraSamples`）；`Geometric` 分块；`Global` 两遍；多输入；
把结果写回文件的某个矩形（没有区域 sink）。**区域结果不带 ICC/Exif**：codec 文档写明元数据需要读整个文件，
读它等于放弃这条路的全部意义；需要时用 `SaveOptions.Metadata` 附上。

### 6.7 几何进入执行模型（M18，第一切片已落地）

现状：`ProcessingPlan.Add` 对 `Geometric` 直接抛，几何算子唯一的通道是 `ImagePipeline.Reshape` 的整图执行；
区域执行只能配同尺寸计划。也就是说 §6.5 的三种源能力对几何算子全部无效。

要让它成立，缺的是三件事，缺一不可：

1. **尺寸变换**：`PlanStep` 要能声明「这一步把 w×h 变成 w'×h'」，`Execute` 的输出尺寸由链累积；带泵与区域的
   sink 必须**在解码前**知道输出尺寸（编码器 `AddFrame` 要 w/h）。这会改动 `ProcessingPlan` 的对外形状，
   是本里程碑最贵的一处。
2. **原点感知的重采样**：`Resample` 现在用传入 view 的 `Width/Height` 建权重、按 view 坐标 clamp（见 §6.7.1）。
   分块需要「全图尺寸 + 窗口原点」，而权重、边界钳制、累加序一个字都不能变——`known-gaps.md` 里那条
   FMA/1-ULP 记录就是这条路的雷。
3. **并行喂料**：行泵顺序读源。水平遍是 `O(srcH × destW × taps)`，4× 降采样时约为垂直遍的 4 倍，
   单线程实现会比 `Resize` 慢 5–6 倍。可行方案：每带把源行读进字节暂存（`span × srcRowBytes`），
   水平遍按行并行、垂直遍按行并行；32 MiB 暂存在 8K（30 KB/行）下约 1000 行。

#### 6.7.1 事实依据（2026-11-25 核查）

- `ProcessingPlan.Add` 第 191 行拒绝 `Geometric`；`PlanOps` 没有任何几何工厂。
- `Resize.cs:219` 用 `source.Width/Height` 建 `WeightTable`；`Resize.cs:402/412` 以 view 坐标索引与钳制；
  行号是绝对目标行（`:353`、`:362`），没有原点参数。
- `IImageOp.MapSourceRect` 在 2026-11-26 前全仓零实现；现在由 `ImageOp` 的 footprint 提供（见 §5.4），
  但带泵与区域执行器仍只用 `HaloX/HaloY`——footprint 的消费者是 P1 编译器。
- `Resize` 的并行度来自目标行带（`RowParallel.ForBands`），不是行内并行——这决定了「顺序喂料」不能直接复用它的并行结构。

#### 6.7.2 第一个切片（已落地）

`ImageTranscode.From(sourcePath).Resize(width, height, resampler)`：**只有一步 resize**，不碰 plan。

- 输出与 `Load → Resize` 逐字节相同（同一权重表、同一 clamp、同一累加序）。
- 内存：ring（`taps + chunk + 2` 行目标宽浮点）+ staging（`chunk` 行源字节）+ 输出批 + 一行源 + 每 worker 一行浮点，
  与源高度无关；预算是一个账本，超出时先缩 chunk 再拒绝（见 §6.7.3）。
- 判据：8K→1080p 峰值 ≤ 64 MiB，吞吐 ≥ `Resize` 的 0.8×；达不到就说明并行喂料没做对，先修它再谈 plan。
- 通过后再做尺寸变换与 plan 接线（M18 主体）。

#### 6.7.3 实测（2026-11-25，第一切片）

`ImageTranscode` 的缩放已落地：单步（或链上任意位置）缩放、行流式，与 `Load → Resize` 逐字节相同。

| 项 | 结果 |
|---|---|
| 逐字节 | 4 尺寸 × 2 深度 × 5 滤波器共 **40 组**，全部与 `Resample.Resize` 相同；8K→1080p 的 PNG 文件也逐字节相同 |
| 8K→1080p bicubic | 流式 **390 ms** vs 整图 **426 ms**（**0.92×**，门槛 1.25×） |
| 工作集 | ring `264×1920×16B ≈ 7.7 MiB` + 暂存 `256×30 KB ≈ 7.5 MiB` + 输出批 64 行 `1.9 MiB` + 16 个 worker 行 `1.9 MiB` ≈ **19 MiB**，与源高度无关；账本超限时先缩小 chunk |
| 拒绝 | 4000² → 4000×4 的极端降采样：`taps+2` 行目标宽的 ring 底数就超过剩余预算，直接抛并逐项说明（ring / staging / 输出批 / worker 行） |
| AOT | `resize.stream` 进 `REQUIRED`，三档 sha 相同，条目内部断言与整图一致 |

源是 `Stream` 时，`Run(path)` 与 `Run(stream, format)` 走同一条行链（2026-11-25 修）：此前只有路径源会进
`ProcessChain`，`Stream` 源的目标即使是路径也会被 `ToImageCore` 物化，而报告只能写 `Refusal=None`——字节相同，
所以只有报告能看出差别。现在两个入口都打开目标文件流后交给同一个 `ProcessChain`，`Mode` 如实报告 `Streamed`。

缩放按调用顺序出现在链里（`GaussianBlur(1f).Resize(...)`）：带泵产出的行直接交给 `ResizeRowSource`，链中间
不出现整图。实测 8K→1080p：纯缩放 **390 vs 426 ms（0.92×）**、`blur → 缩放 → sharpen` **835 vs 791 ms（1.06×）**，
逐字节相同。链上不流式的算子（`Global`）会物化执行，报告里 `Mode=Materialized`。

并行喂料：每带把源行按预算切块（8K 下 256 行）读进字节暂存，水平遍按行并行、垂直遍按 `ForBands` 并行；带之间顺序。
`ForBands` 而不是 `For`：4× 降采样时一次只完成十几行目的行，低于 `For` 的 64 K 像素门限，会被静默串行。

区域 + 几何：`ImageTranscode.From(path).Region(rect).Resize(w, h)` 只解码相交的 strip/tile，按带喂给
`ResizeRowSource`，与 `Load → Crop → Resize` 逐字节相同；区域本身不整块存在。带算子的形式把读入窗口按
`ΣHaloX/ΣHaloY` 扩大、跑完算子后用 `CropRowSource` 只把内圈交给缩放，等于 `Load → 处理 → Crop → Resize`
一趟完成（AOT 条目 `region.resizeplan` 内部就这样断言）。

**分层说明**：内存里的惯用法始终是 `image.Pipe()...ToImage()`，文件侧是 `ImageTranscode.From(...).Apply(...).Resize(...).Save(...)`
（`ProcessTo` / `ResizeTo` / `ProcessRegion` / `ResizeRegion` 自 2026-11-25 起为内部实现，公开面只有 builder 与 `DescribeRegion`；
计划是链的编译产物，文件侧不暴露 `Apply(plan)`，内存侧 `Image.Apply(plan)` 标为高级 API（IntelliSense 隐藏，
只给编译好的配方与 `PlanObserver` 用）；算子方法由 `ImageOpPipeline<TSelf>` 单一声明，
`ImagePipeline` 与 `ImageTranscode` 共用，几何/合成分支留在 `ImagePipeline`）
（算子调用在 `Resize` 前后分别落到下层的 `plan` / `after`）；`ProcessingPlan` 是它编译出的描述，`Apply(plan)` 是
低阶入口，`ProcessTo` 则因为文件没有 `Image` 可 pipe 才直接吃计划。两条路字节相同（`ApiLayerTests` 钉住）。

**计划本身可以以缩放结尾**：`ProcessingPlan.Resize(w, h, resampler)`，之前的步骤在同尺寸上跑、之后按新尺寸输出；
`Execute` 与 `TryExecuteTo` 都认它（流式仍是一趟，`PlanRowSource → ResizeRowSource`），区域执行拒绝
（`RegionRefusal.GeometricStep`，区域 + 缩放走 `ResizeRegion`）。`Add` 在 `Resize` 之后会抛——缩放只能是最后一级。

**缩放夹在两个计划之间**也已打通：`ResizeTo(..., plan: before, after: after)`。实现上把带泵改成了**拉式源**
（`PlanRowSource`：按需产出一带），于是整条链就是 `PlanRowSource → ResizeRowSource → PlanRowSource` 的串联，
`Pipeline.Copy` 只负责把最后一级写进 sink。每一级仍然只认识"上游给我行"。

区域 + 几何之外，文件侧还能流式**行局部几何**：`Crop`、`Pad`、`FlipHorizontal`（连同 `Resize`），按调用顺序
串成拉式链；输出尺寸由阶段折叠得到（`ProcessChain` 用 `SizeOf` 在编码器打开前算好）。`FlipVertical` /
`Rotate90` / `Transpose` / `Affine` / `Perspective` 需要整行重排或随机访问，前向流不可做。异步入口（`RunAsync` / `SaveAsync` / `ToImageAsync`）是把同步
管线放到线程池，不是重叠 IO。

仍未做：多级缩放（两次 resize 连用）、Pad/FlipHorizontal 的流式版、Affine/Perspective。

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

| **M16 文件级流式（完成；静态入口 2026-11-25 起内部化，公开面为 `ImageTranscode`）** | S1：`IRowSource` / `IRowSink`（Abstractions）+ `ProcessingPlan.CanStream` / `TryExecuteTo` + `Image.ProcessTo`（path / stream）+ `StreamRefusal` / `StreamResult` / `ProcessReport` + `FusedExecutor.RunWindow`（整图与流式共用同一段递归）+ `PlanStep.NeedsWholeSource`。S2：`RowParallel.EnterTopLevel(degree)` 把 `MaxDegreeOfParallelism` 接进带泵、`PreserveMetadata` 显式保留元数据（默认只读头部：I/O 从 2.0–3.4× 降到 1.01–1.41×）、`stream.plan` 进 AotSmoke `REQUIRED`；**有序带队列测量后取消**（见 §6.5.3） | ✅ 2026-11-25：`gates.sh --aot` observed `warnings=0 errors=0`、`failed=0 passed=1169`、快照只有新增、`stream.plan` 三档（jit / avx2 / base）sha 相同且条目内部断言「流式 == 整图」。`StreamingTests` 26 例：逐字节等价（5 链 × 2 深度、带高 1/2/7/64、PNG 8/16-bit、带 Exif 的 JPEG 走 `PreserveMetadata`）、默认丢元数据且如实报告、读量 ≤1.5×、行缓存不随高度增长、四种回退、串行度一致、取消。实拍 JPEG→PNG 实测流式/整图 **1.00–1.02×**（1280×720）、**1.06–1.09×**（3069×2653），输出逐字节相同（含 9.4 KB Exif）。**未做**：`Global` 两遍、TIFF 区域源 |
| **M17 区域执行（完成；静态入口 2026-11-25 起内部化）** | `IRegionSource`（Abstractions）+ `RegionExecutor` / `RegionRefusal` / `RegionResult` + `ProcessingPlan.CanExecuteRegion` / `TryExecuteRegion` + `Image.DescribeRegion` / `ProcessRegion` + `TiffRegionSource`（`TiffReader.ReadLayout` / `ReadSegment`，显式引用 `Sdcb.SimdCodecs.Tiff`）+ 运行期 TIFF 夹具（codec 写条带、手写 tile / MinIsWhite / float / 4 通道） | ✅ 2026-11-25：`gates.sh --aot` observed `warnings=0 errors=0`、`failed=0 passed=1211`（1 skip）、快照 128 行**只有新增、0 删除 0 改写**、AOT 三路 `region.plan` / `region.tiff` sha 相同且条目内部断言「区域 == 整图裁剪」。`RegionExecutorTests` 25 + `TiffRegionTests` 13 + `RegionEndToEndTests` 4；等价（4 链 × 2 深度 × 6 矩形、带高 1/7/64、非对称 5×3 halo、1×1 区 + halo 18）、读量（16² 区域：512 高 3.06 KB / 2048 高 3.44 KB）、内存（100 000² 源读 64² 与 4096² 同级）、拒绝表逐项、取消。**不做**：`Load(path, Region)` 快路径、4 通道 / Palette / CMYK、Geometric / Global 两遍、区域 sink |
| **M18 几何进入执行模型（完成）** | 拉式流水线：`PlanRowSource`（按需出一带）/ `ResizeRowSource`（按需出缩放行）/ `CropRowSource` / `Pipeline` 驱动；`Image.ResizeTo`（path / stream，`plan:` 在缩放前、`after:` 在缩放后）；`Image.ResizeRegion`（可带计划）；`Resample.TryNearestTap` 共享；AOT `resize.stream` / `resize.plan` / `resize.after` / `plan.resize` / `region.resize` / `region.resizeplan` / `file.builder` / `file.geometry`。未做：多级缩放、Affine/Perspective | ✅ 2026-11-25：40 组（4 尺寸 × 2 深度 × 5 滤波器）与 `Resample.Resize` 逐字节；8K→1080p 纯缩放 **390 vs 426 ms（0.92×）**、blur+contrast→缩放→sharpen **835 vs 791 ms（1.06×）**；区域链与 `Load → Apply/Crop → Resize` 逐字节；AOT 六条三档一致；`plan.resize` 与 `resize.plan` 的 sha 相同（两条路径、同一份字节） |

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
| AVX-512 降频 | 某些 SKU 反而更慢 | 档位可配置（`SIMDIMAGING_DISABLE_AVX512=1`，2026-11-16 实现，读一次进程级）；以实测决定默认 |
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
