# SimdImaging

托管 .NET 图片处理层。底层编解码全部由 [`Sdcb.SimdCodecs`](https://github.com/sdcb/SimdCodecs) 承担，本项目只做**解码之后、编码之前**的像素工作：几何、色彩、滤波、合成、绘制、量化、分析。

依赖：`Sdcb.SimdCodecs`（解码 / 编码 / 容器 / 元数据 / 像素布局转换）。

目标：功能对标 ImageSharp 4.1.x 并超越；NativeAOT 一等公民；跨 SIMD 档位输出逐字节一致（**三档已实现，AVX-512 档位预留、尚未实现**）。

## 状态

**M0–M4 的功能已全部落地；出口条件已部分核验（2026-11-11 补测，见 [`docs/known-gaps.md`](docs/known-gaps.md)），逐项状态见
[`docs/architecture.md` §11](docs/architecture.md)。** 全量测试通过、`Release` 构建 0 警告、public API 由快照锁定。

**已可用：**`Image.Load / Save / Encode`、三个工作格式、池化缓冲；几何
（Crop / Pad / Flip / Rotate90·180 / Rotate 任意角 / Affine / Perspective / Resize 五种采样器）；点算子（Brightness / Contrast / Saturation /
Grayscale / Invert / Gamma / Threshold / Posterize / Levels / ColorMatrix / HueRotate / Sepia / Normalize / Vibrance / Temperature / Tint / ColorBalance / ChannelSwap / BlackWhite / Curves / Opacity / Clamp）、直方图与均衡；滤波（GaussianBlur / BoxBlur / Median3x3 / Sobel / 形态学 3×3 / 双边 / 卷积 / 浮雕 / 锐化 / 运动模糊 / 散景 / 渐变 / 贴图 / 水印）；合成（Porter-Duff 12 式 + 18 种混合模式）、原地填充/清空、矩形描边、Bresenham 直线、椭圆与多边形填充、预乘/反预乘、调色板量化（中位切分 / Wu / WebSafe / 抖动）、直方图与均衡。

- `Release` 构建 0 警告，全量测试通过（此处不写死数量，以 CI 输出为准）。
- Resize 走行带执行：4K→1080p 的分配从 70 MB 降到 **17 MB**（16 MB 预算），输出逐字节不变。
- 第一个向量内核已落地（Rgba8 预乘行读取 + 水平/垂直两遍向量化）：4K→1080p bicubic **200 → 97–111 ms（≈1.8–2.1×）**，
  Lanczos3 243 → 107–123 ms（≈2.0–2.3×）。此后 `Median3x3` / `BokehBlur` / `Convolve2D` 等热点也已 SIMD 化、多个重算子改行带并行；
  **点算子走字节域 LUT 快路径，未做 SIMD 向量化**。明细见
  [`benchmarks/README.md`](benchmarks/README.md) 与 [`docs/known-gaps.md`](docs/known-gaps.md)。
- **NativeAOT 已验证**：两次发布（AVX2 基线 / 默认 SSE2 基线）与 JIT 逐字节一致，覆盖编解码**和**几何
  算子（crop / bicubic·Lanczos3 resize / 16 位 resize / rotate / flip / pad / composite）。

## 构建

```bash
dotnet build SimdImaging.slnx -c Release
dotnet test  SimdImaging.slnx -c Release
bash tests/SimdImaging.AotSmoke/run.sh
```

## 用法

三行就够：

```csharp
using SimdImaging;

using var photo = Image.Load("photo.jpg");       // 位深跟随源：8 → Rgba8，16 → Rgba16，float → Rgba32F
using var small = photo.Resize(800, 600);        // 默认 bicubic；返回新图，原图不动
Image.Save(small, "out.png");                    // 格式由扩展名决定，不需要枚举
```

`Resize` / `Crop` / `Rotate90` / `GaussianBlur` / `Saturation` … 都是 `Image` 上的实例方法，**返回新图**，
原图不动。一串编辑用 `Clone(x => ...)`：操作先记录、最后一次性执行，中间缓冲读完立刻还池，只有结果要 `using`。

```csharp
using var edited = photo.Clone(x => x.Crop(rect).Resize(800, 600).Saturation(1.2f).GaussianBlur(1.5f));
```

1080p 实测（三个点算子）：链式 **8.8 ms / 14 KiB**，逐步写法 9.4 ms / 8.2 MB —— 链式不仅不用逐个 `using`，
分配还少两个数量级。

需要控制解码/编码时，才用两个选项类型（`SimdImaging.Abstractions`）：

| 我要 | 写 |
|---|---|
| 1/4 缩放解码（精确尺寸） | `Load(p, new LoadOptions { ScaleDenominator = 4 })` |
| 只要快，尺寸可以大一点 | `Load(p, new LoadOptions { ScaleDenominator = 16, PreferNativeScale = true })` |
| 按 EXIF 转正 | `Load(p, new LoadOptions { ApplyOrientation = true })` |
| 只解一块（地图/大图切片） | `Load(p, new LoadOptions { Region = new PixelRect(x, y, w, h) })` |
| 限内存（防解压炸弹） | `Load(p, new LoadOptions { MaxPixels = 40_000_000 })` |
| 从流读（HTTP 响应体） | `await Image.LoadAsync(stream)` |
| JPEG 质量 | `Save(img, p, new SaveOptions { JpegQuality = 85 })` |
| WebP 无损 | `Save(img, p, new SaveOptions { WebpLossless = true })` |
| 要字节不要文件 | `byte[] png = Image.Encode(img, ImageFormat.Png)`（这个枚举来自 `Sdcb.SimdCodecs.Abstractions`） |

失败分两种，别混：输入不是图片 → `InvalidDataException`（或 `TryLoad` 返回 false）；调用方参数错了 →
`ArgumentException` / `NotSupportedException`。没有静默降级。

## 与 ImageSharp 4.1.x 的定位差异

| 维度 | ImageSharp 4.1.3 | SimdImaging |
|---|---|---|
| 许可 | Six Labors Split License 1.0（商用按条款购买） | MIT |
| 目标框架 | net8.0 | net10.0 |
| NativeAOT | 4.1 起为部分格式补 AOT 种子 | 全库构建门禁，零 trim/AOT 警告 |
| 位深 | 由像素类型决定 | 全链路 `Rgba16`，16 位不被 8 位中间层吞掉 |
| 确定性 | 无跨 SIMD 档位承诺 | **三档**（AVX2 / V128 / 标量）逐字节一致；AVX-512 档位预留，尚未实现 |
| 内存 | 整图 `MemoryAllocator` | 池化 + ping-pong + 行带；实测 7–39 KiB/op，`0 B/op` 目标未达成 |
| 格式面 | 多 EXR / TGA / CgBI PNG / ANI | 多 JPEG 2000 / JBIG2 / PNM 全族 / Ultra HDR；HEIF/HEIC 需自行引用 `Sdcb.SimdCodecs.Heif` 并注册 |

格式面的差异由底层 `Sdcb.SimdCodecs` 决定，不是应用层的取舍。

> 命名与归属：`SimdImaging` 是配合 `Sdcb.SimdCodecs` 的处理层，**不是** SimdCodecs 项目的一部分，也不代表其作者。名字成对是为了表达分工：SimdCodecs 解码，SimdImaging 处理。

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | 分层、与 SimdCodecs 的边界、缓冲抽象、SIMD 策略、AOT、测试与基准方法、里程碑、风险 |
| [`docs/operators.md`](docs/operators.md) | 112 个算子的完整清单（语义 / 实现策略 / 确定性 / 阶段）+ ImageSharp 功能对标 |

## 一句话设计

- **三个工作格式**：`Rgba8` / `Rgba16` / `Rgba32F`。布局翻译只在边界发生一次，由 SimdCodecs 的 `PixelConverter` 完成。
- **两个缓冲，来回倒**：`WorkingBuffer`（实现 `IMemoryOwner<byte>`，可零拷贝交给编码器）+ 池；稳态分配实测 7–39 KiB/op（§1.3 的 `0 B/op` 目标未达成）。
- **计划先于执行**（设计目标）：点算子融合成一张 LUT、预乘只做一次、`Region`/`ScaleDenominator` 下推给解码器。**跨步骤融合目前未实现**——`Execute` 仍按步骤整图循环、每步一张整图 ping-pong（见 `architecture.md` §5.4 与 `known-gaps.md`）。
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

编解码层是 **NuGet 依赖**：`Sdcb.SimdCodecs` **2.0.0**，版本锁在 `Directory.Packages.props`（与 `Sdcb.SimdCodecs.Abstractions` 同版本、同步升级）。仓库不包含、也不引用任何外部源码检出。
