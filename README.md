# SimdImaging

托管 .NET 图片处理层。底层编解码全部由 [`Sdcb.SimdCodecs`](https://github.com/sdcb/SimdCodecs) 承担，本项目只做**解码之后、编码之前**的像素工作：几何、色彩、滤波、合成、绘制、量化、分析。

依赖：`Sdcb.SimdCodecs`（解码 / 编码 / 容器 / 元数据 / 像素布局转换）。

目标：功能对标 ImageSharp 4.1.x 并超越；NativeAOT 一等公民；跨 SIMD 档位输出逐字节一致（**AVX2 / Vector128 / 标量三档全库覆盖，AVX-512 已在卷积内核落地**——正确性经软件模拟逐字节验证，性能本机无硬件可测）。

## 定位

**高性能 .NET 图像处理与计算摄影引擎。** 四个方向建立竞争力：

| 方向 | 目标 | 现状 |
|---|---|---|
| **1. 基础图像处理** | 性能、精度、完整性超过普通 .NET 图片库 | 算子清单见 `docs/operators.md`、三工作格式、**16 个重采样器**（15 个核，`Triangle`≡`Bilinear`、`CatmullRom`≡`Bicubic`；见 `docs/operators.md`）；Resize 4K→1080p 比 ImageSharp 快 1.8–2.3×（同参数对拍） |
| **2. 高级计算摄影** | 去模糊、去噪、去雾、HDR、多帧融合、频域处理 | 去模糊（Richardson-Lucy 高斯/运动 PSF）、去噪（NLM / 局部拉普拉斯 / 双边 / 导向）、HDR（5 种色调映射 + 增益图 + Ultra HDR 读写）、去雾（暗通道先验，1080p 187 ms；FLT-24）、多帧曝光融合（Laplacian 金字塔混合，1080p×3 593 ms；FLT-25）、频域处理**评估后不落地**（评估记录见 `docs/known-gaps.md`） |
| **3. 计算架构** | 纯托管 SIMD、融合执行、内存预算、NativeAOT | 纯托管零原生依赖；三档 SIMD 逐字节一致，AVX-512 已进卷积内核；`ProcessingPlan` 分段融合；`MemoryBudget` + `MemoryEstimator`；NativeAOT 作为构建门禁 |
| **4. 图像数据保真** | 16-bit、32-bit float、HDR、Alpha、色彩管理 | 全链路 Rgba16 不塌 8 位；Rgba32F 的 HDR>1 不钳位；直通 alpha 与预乘语义分离；ICC matrix/TRC **与 RGB LUT** + CIEDE2000 |

## 状态

**M0、M1、M5、M6–M14 已完成并验收；M15（SIMD 收口 / 门禁 / 契约）进行中；M16（文件级流式）与 M17（区域执行）已完成；
M2–M4 功能已落地、出口条件部分核验**——M2 的「点算子融合后分配 = 0」已判定**未达标**（≈4.3–6.0 KiB/op，2026-11-15），
M4 的 8K `gaussian-blur` 峰值超 7.1%，M3 抗锯齿覆盖率与 M4 的 HEIF 端点仍无判据可测。
逐项状态见 [`docs/architecture.md` §11](docs/architecture.md) 与 [`docs/known-gaps.md`](docs/known-gaps.md)。
全量测试通过、`Release` 构建 0 警告、public API 由快照锁定。

**已可用：**`Image.Load / Save / Encode`、`Image.Mutate`（原地链式）、`Image.Pipe`（返回新图的链）、三个工作格式、池化缓冲；几何
（Crop / Pad / Flip / Rotate90·180 / Rotate 任意角 / Affine / Perspective / Resize 十六种采样器）；点算子（Brightness / Contrast / Saturation /
Grayscale / Invert / Gamma / Threshold / Posterize / Levels / ColorMatrix / HueRotate / Sepia / Normalize / Vibrance / Temperature / Tint / ColorBalance / ChannelSwap / BlackWhite / Curves / Opacity / Clamp）、直方图与均衡；滤波（GaussianBlur / BoxBlur / Median3x3 / Sobel / 形态学 3×3 / 双边 / 卷积 / 浮雕 / 锐化 / 运动模糊 / 散景 / 渐变 / 贴图 / 水印）；合成（Porter-Duff 12 式 + 18 种混合模式）、原地填充/清空、矩形描边、Bresenham 直线、椭圆与多边形填充、预乘/反预乘、调色板量化（中位切分 / Wu / WebSafe / 抖动）、直方图与均衡；高级摄影（非局部均值 / 局部拉普拉斯 / Richardson-Lucy 反卷积 / 接缝裁剪）；色彩管理（ICC 的 matrix/TRC **与 RGB LUT 型 profile**、CIEDE2000）；HDR（五种色调映射、增益图成对类型 `GainMappedImage` + Ultra HDR 读写）；内存治理（每线程 scratch 预算 64 MB、权重表缓存 8 MB + LRU、`MemoryBudget` 统计与 Trim、`MemoryEstimator.Estimate` 预估峰值且区分精确项与算子声明项）；文件级流水线 `ImageTranscode`（`From → Apply/Resize → Save/Run/ToImage`：解码、计划、缩放、编码一条流水线，常驻内存不随高度增长；TIFF 可只处理一个矩形）；区域能力查询 `Image.DescribeRegion`。

- `Release` 构建 0 警告，全量测试通过（此处不写死数量，以 CI 输出为准）。
- Resize 走行带执行：4K→1080p 的分配从 70 MB 降到 **17 MB**（16 MB 预算），输出逐字节不变。
- 第一个向量内核已落地（Rgba8 预乘行读取 + 水平/垂直两遍向量化）：4K→1080p bicubic **200 → 97–111 ms（≈1.8–2.1×）**，
  Lanczos3 243 → 107–123 ms（≈2.0–2.3×）。此后 `Median3x3` / `BokehBlur` / `Convolve2D` 等热点也已 SIMD 化、多个重算子改行带并行；
  **点算子走字节域 LUT 快路径，未做 SIMD 向量化**。明细见
  [`docs/measurements.md`](docs/measurements.md) 与 [`docs/known-gaps.md`](docs/known-gaps.md)。
- **NativeAOT 已验证**：两次发布（AVX2 基线 / 默认 SSE2 基线）与 JIT 逐字节一致，覆盖编解码**和**几何
  算子（crop / bicubic·Lanczos3 resize / 16 位 resize / rotate / flip / pad / composite）。

## 构建

```bash
dotnet build SimdImaging.slnx -c Release
dotnet test  SimdImaging.slnx -c Release
bash tests/SimdImaging.AotSmoke/run.sh
```

## 用法

### 单个算子

```csharp
using SimdImaging;

using var photo = Image.Load("photo.jpg");   // 位深跟随源：8 → Rgba8，16 → Rgba16，float → Rgba32F
using var small = photo.Pipe().Resize(800, 600).ToImage();   // 默认 bicubic；返回新图，原图不动
Image.Save(small, "out.png");                // 格式由扩展名决定
```

算子只有一个入口：`image.Pipe()`。单步也要 `Pipe().ToImage()`——**只有一条路，没有第二条更慢的**。

### 一串算子

```csharp
using var edited = photo.Pipe()
    .Crop(rect)
    .Resize(800, 600)
    .Saturation(1.2f)
    .GaussianBlur(1.5f)
    .ToImage();
```

链的价值在**内存**，不在少打几个字。逐步调用每一步都要一张整图（8K 下 132 MiB 一张，五步就是五张）；
链把同尺寸的连续步骤压成**一遍行带**，中间图不存在。实测 8K 五步链：**逐次 381 MiB → 链 72 KiB**。

三点须知：

| 事情 | 说明 |
|---|---|
| **必须 `ToImage()`** | 链是惰性的，加到第几步由你决定，只有终结调用会执行 |
| **必须 `using`** | 结果图持有一块池化内存；不还回去，池下次只能重新分配（实测 4K 每轮 2.4 → 29.4 MB） |
| **尺寸变化不用管** | `Resize` / `Crop` / `Rotate90` 会自动切开，链照常写 |

个别算子需要整图（`Vignette`、`CLAHE`、`LocalLaplacian`、`Normalize`、`AdaptiveThreshold`、`Pixelate`），
链会把它们当独立一段整体执行——**写法不变**，代价是该步多一张中间图（和直接调用它一样）。
逐条判定与原因见 [`docs/operators.md`](docs/operators.md)。

### 需要控制解码 / 编码时

用两个选项类型（`SimdImaging.Abstractions`）：

**解码**

```csharp
using var quarter = Image.Load(path, new LoadOptions { ScaleDenominator = 4 });   // 精确 1/4 尺寸
using var fast    = Image.Load(path, new LoadOptions { ScaleDenominator = 16, PreferNativeScale = true });
using var upright = Image.Load(path, new LoadOptions { ApplyOrientation = true });   // 按 EXIF 转正
using var tile    = Image.Load(path, new LoadOptions { Region = new PixelRect(x, y, w, h) });
using var capped  = Image.Load(path, new LoadOptions { MaxPixels = 40_000_000 });     // 防解压炸弹
using var remote  = await Image.LoadAsync(stream);                                    // HTTP 响应体
```

**编码**

```csharp
Image.Save(img, path, new SaveOptions { JpegQuality = 85 });
Image.Save(img, path, new SaveOptions { WebpLossless = true });
byte[] png = Image.Encode(img, ImageFormat.Png);   // ImageFormat 来自 Sdcb.SimdCodecs.Abstractions
```

失败分两种，别混：输入不是图片 → `InvalidDataException`（或 `TryLoad` 返回 false）；调用方参数错了 →
`ArgumentException` / `NotSupportedException`。没有静默降级。

### 文件级流水线（不整图载入）

文件进、文件出没有 `Image` 可以 pipe，用 `ImageTranscode`——动词与 `Pipe()` 对齐，内部路由到行带流水线：

```csharp
// 一步到位：读 → 模糊 → 缩放 → 写
ImageTranscode.From("in.jpg").GaussianBlur(1f).Resize(1600, 900).Save("out.png");

// 要报告（流式与否、读了多少行、元数据带没带）
ProcessReport report = ImageTranscode.From("in.jpg").GaussianBlur(1f).Resize(1600, 900).Run("out.png");

// 不可 seek 的源（HTTP 请求体）也行，容器由目标格式给出
ProcessReport report = ImageTranscode.From(requestBody).GaussianBlur(1f).Run(responseBody, ImageFormat.Png);

// 只处理一块再缩小（crop 是最先的 stage，解码不多读）
ImageTranscode.From("huge.jpg").Crop(rect).GaussianBlur(1f).Resize(1600, 900).Save("out.png");

// 行局部几何可以自由组合：裁剪 → 加边 → 水平翻转 → 滤波 → 缩放
ImageTranscode.From("in.png").Crop(rect).Pad(8, RgbaColor.White).FlipHorizontal().Resize(800, 600).Save("out.png");

// 也要整图在手里
using var image2 = ImageTranscode.From("in.jpg").GaussianBlur(1f).Resize(1600, 900).ToImage();

// 异步：把同步管线放到线程池（codec 的行 IO 是同步的），可取消在带之间生效
await ImageTranscode.From("in.jpg").GaussianBlur(1f).Resize(1600, 900).SaveAsync("out.png");
```

- 算子面与 `Pipe()` 是**同一份声明**（`ImageOpPipeline<TSelf>`，40 个计划算子）；几何与合成算子只在 `Pipe()`，因为它们不是同尺寸步骤。**文件侧不出现 plan**——所有算子都是链上的一级，计划只存在于实现里。
- 不带 `Region` 时全程流式：读行 → 算子 → 缩放 → 写行，常驻内存是窗口 + 环形缓冲，**不随图片高度增长**。
- 结果与 `Load → Pipe()...ToImage() → Save` **逐字节相同**（源文件不带元数据时）。元数据默认**不**随行：读它要遍历整个文件；要就开 `TranscodeOptions.PreserveMetadata`（多读一趟），或直接用 `SaveOptions.Metadata` 附上，报告的 `MetadataCarried` 如实说明。
- 默认只扫一遍源：JPEG **1.01×**、PNG **1.07×**、TIFF **1.41×** 的文件字节（TIFF 行读器自身预读）。
- 8K→1080p：纯缩放 **0.92×**、`blur → 缩放 → sharpen` **1.06×**，工作集约 15 MiB。
- 链里有 `Global` 算子（直方图 / 量化 / CLAHE）或读整图的步骤（Mirror / Wrap 卷积）时不**做近似**：物化执行，`report.Refusal` 说明原因；float 样本没有行读投影，直接抛。
- 解码器 / 编码器是否真流式分开报（`ReaderIsStreaming` / `EncoderIsStreaming`），`FullyStreamed` 是三者都成的结论。
- 文件侧能流式的几何是**行局部**的那几个：`Crop`、`Pad`、`FlipHorizontal`、`Resize`，按调用顺序执行。`FlipVertical` / `Rotate90` / `Transpose` / `Affine` / `Perspective` / 接缝要么需要整行重排、要么需要随机访问，`Pipe()` 整图路径是唯一正确通道。`Region` 模式下这些几何阶段一律拒绝——矩形已经由 `Region` 选定。

#### 只处理一个矩形（超大 TIFF）

```csharp
// 只解相交的 strip/tile，再处理、再缩放
using var thumb = ImageTranscode.From("huge.tif").Region(rect).GaussianBlur(1f).Resize(480, 320).ToImage();

// 不读像素，先问能不能读、最大段多大
RegionCapabilities caps = Image.DescribeRegion("huge.tif");
```

- 输出与 `Load(整图) → 处理 → Crop(rect)`（带缩放时再加 `Resize`）**逐字节相同**，halo 边界也一样。
- 只读与矩形相交的 strip/tile：16×16 区域在 512² 上读 **3.1 KB**（文件 43.8 KB），图像高 4 倍后仍 **3.4 KB**。
- 不带缩放时那块按带读、按带处理，区域本身不整块存在；带缩放时按 Σhalo 扩大读入、只把内圈交给缩放，一趟完成。
- 读不了就抛，不物化：非 TIFF、4 通道（`ExtraSamples` 不可见）、Palette / CMYK / YCbCr、单段超预算、含 `Global` 的计划，各有明确消息。
- 结果不带 ICC/Exif（codec 读元数据要整个文件）；需要时用 `SaveOptions.Metadata` 附上。
- 本库 `Save` 出的 TIFF 是 4 通道单条带，会被区域读拒绝——这个入口是给「别处写的大图」用的。

> 内存里不需要 builder：`image.Pipe().GaussianBlur(1f).Resize(1600, 900).ToImage()` 就是惯用法。
> 计划是低阶：`Image.Apply(plan)` / `ApplyWithOptions` 标为 `EditorBrowsable(Advanced)`，只给「编译好的配方」与逐步
> 计时用；用户层只有 `Pipe()` / `Mutate()` / `ImageTranscode`。
> 旧的静态入口（`ProcessTo` / `ResizeTo` / `ProcessRegion` / `ResizeRegion`）已改为内部实现，公开面只留 builder 与 `DescribeRegion`。

### 原地修改

不想换变量时用 `Mutate`，链一样，结果写回这张图：

```csharp
photo.Mutate(p => p.Resize(800, 600).Saturation(1.2f));
```

## 与 ImageSharp 4.1.x 的定位差异

| 维度 | ImageSharp 4.1.3 | SimdImaging |
|---|---|---|
| 许可 | Six Labors Split License 1.0（商用按条款购买） | MIT |
| 目标框架 | net8.0 | net10.0 |
| NativeAOT | 4.1 起为部分格式补 AOT 种子 | 全库构建门禁，零 trim/AOT 警告 |
| 位深 | 由像素类型决定 | 全链路 `Rgba16`，16 位不被 8 位中间层吞掉 |
| 确定性 | 无跨 SIMD 档位承诺 | **三档**（AVX2 / V128 / 标量）全库逐字节一致；**AVX-512** 在卷积内核有实现，`Vector512<T>` 软件模拟下与另三档逐字节相同，硬件上未跑过 |
| 内存 | 整图 `MemoryAllocator` | 池化 + ping-pong + 行带；实测 7–39 KiB/op，`0 B/op` 目标未达成 |
| 格式面 | 多 EXR / TGA / CgBI PNG / ANI | 多 JPEG 2000 / JBIG2 / PNM 全族 / Ultra HDR（读 + 写，含增益图与底图的同步几何）；HEIF/HEIC 需自行引用 `Sdcb.SimdCodecs.Heif` 并注册 |

格式面的差异由底层 `Sdcb.SimdCodecs` 决定，不是应用层的取舍。

> 命名与归属：`SimdImaging` 是配合 `Sdcb.SimdCodecs` 的处理层，**不是** SimdCodecs 项目的一部分，也不代表其作者。名字成对是为了表达分工：SimdCodecs 解码，SimdImaging 处理。

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | 分层、边界、SIMD 策略、AOT、里程碑 |
| [`docs/operators.md`](docs/operators.md) | 算子清单 + 进不进流水线的判据 |
| [`docs/known-gaps.md`](docs/known-gaps.md) | 未达标项、已否决方向、评估记录、方法论 |
| [`docs/measurements.md`](docs/measurements.md) | 各基准模式的结论与数字 |
| [`docs/contracts.md`](docs/contracts.md) | 对外契约 |
| [`benchmarks/README.md`](benchmarks/README.md) | 基准怎么跑 |

> 2026-11-16 合并：原 `pipeline-coverage` 并入 `operators`，日期类报告（`report-*`、`roadmap-*`）与六份评估文档
> 并入 `known-gaps` 的「评估记录」。**文档只保留现状，过程进提交历史**——两份东西写同一件事，早晚对不齐。

## 一句话设计

- **三个工作格式**：`Rgba8` / `Rgba16` / `Rgba32F`。布局翻译只在边界发生一次，由 SimdCodecs 的 `PixelConverter` 完成。
- **两个缓冲，来回倒**：`WorkingBuffer`（实现 `IMemoryOwner<byte>`，可零拷贝交给编码器）+ 池；稳态分配实测 7–39 KiB/op（§1.3 的 `0 B/op` 目标**未达成**）。2026-11-15 在融合路径上把执行形状缓存后降到 **2493 B/op**（单线程 256 B/op），仍未到 0，残余是缓冲包装与并行调度（见 `known-gaps.md`）。
- **计划先于执行**：点算子链可用 `AddPointLut` 折叠成一张 4×256 表单遍跑；连续可切带步骤由 M10 的窗口递归融合执行（halo 沿链累积，输出与逐步骤逐字节相同）；`Region`/`ScaleDenominator` 下推给解码器。`PlanObserver` 存在时回退逐步骤（见 `architecture.md` §5.5 与 `known-gaps.md`）。
- **确定性**：整数路径跨平台逐字节；浮点路径固定运算序 + 单次舍入 FMA，跨档位逐字节。

## 目录

```
docs/                        设计与算子清单
src/
  SimdImaging.Abstractions/  工作格式 / 池 / WorkingBuffer / PixelView / 算子契约 / 选项
  SimdImaging/               Image / ImageFrame / Load / Save / Encode
tests/
  SimdImaging.Tests/         全量单元测试（PNG 8/16 位逐字节往返、JPEG、区域解码、池、释放、档位一致性等）
  SimdImaging.AotSmoke/      NativeAOT vs JIT 逐字节比对；不在 slnx 里
```

编解码层是 **NuGet 依赖**：`Sdcb.SimdCodecs` **2.1.0**，版本锁在 `Directory.Packages.props`（与 `Sdcb.SimdCodecs.Abstractions` 同版本、同步升级）。仓库不包含、也不引用任何外部源码检出。
