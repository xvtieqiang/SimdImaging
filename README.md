# SimdImaging

托管 .NET 图片处理层。底层编解码全部由 [`Sdcb.SimdCodecs`](https://github.com/sdcb/SimdCodecs) 承担，本项目只做**解码之后、编码之前**的像素工作：几何、色彩、滤波、合成、绘制、量化、分析。

三层分工，接口都是交错像素 + stride，没有胶水层：

```
Sdcb.SimdCodecs       解码 / 编码 / 容器 / 元数据
      ↓  IDecodedImage / PixelConverter
SimdImaging          几何 / 色彩 / 滤波 / 合成 / 量化 / 分析
      ↓  BGR24 / RGB24 / BGRA32 / RGBA32
Sdcb.SimdPaddleOCR   文本检测 / 方向分类 / 文字识别
```

OCR 识别链路由 `Sdcb.SimdPaddleOCR` 负责。本项目不做文档垂直场景，只提供它需要的通用原语。

目标：功能对标 ImageSharp 4.1.x 并超越；NativeAOT 一等公民；四个 SIMD 档位输出逐字节一致。

## 状态

**M0 完成，M1 完成，M2 进行中。** 已可用：`Image.Load / Save / Encode`、三个工作格式、池化缓冲；几何
（Crop / Pad / Flip / Rotate90·180 / Rotate 任意角 / Affine / Perspective / Resize 五种采样器）；点算子（Brightness / Contrast / Saturation /
Grayscale / Invert / Gamma / Threshold / Posterize / Levels / ColorMatrix / HueRotate / Sepia / Normalize / Vibrance / Temperature / Tint / ColorBalance / ChannelSwap / BlackWhite / Curves / Opacity / Clamp）、直方图与均衡；滤波（GaussianBlur / BoxBlur / Median3x3 / Sobel / 形态学 3×3 / 双边 / 卷积 / 浮雕 / 锐化 / 运动模糊 / 散景 / 渐变 / 贴图 / 水印）；合成（Porter-Duff 12 式 + 18 种混合模式）、原地填充/清空、矩形描边、Bresenham 直线、椭圆与多边形填充、预乘/反预乘、调色板量化（中位切分 / Wu / WebSafe / 抖动）、直方图与均衡。

- `Release` 构建 0 警告，197 个测试全绿。
- Resize 走行带执行：4K→1080p 的分配从 70 MB 降到 **17 MB**（16 MB 预算），输出逐字节不变。
- 第一个向量内核已落地（Rgba8 预乘行读取 + 水平/垂直两遍向量化）：4K→1080p bicubic **200 → 97–111 ms（≈1.8–2.1×）**，
  Lanczos3 243 → 107–123 ms（≈2.0–2.3×）。其余算子仍是标量，含波动区间的明细见
  [`benchmarks/README.md`](benchmarks/README.md)。
- **NativeAOT 已验证**：两次发布（AVX2 基线 / 默认 SSE2 基线）与 JIT 逐字节一致，覆盖编解码**和**几何
  算子（crop / bicubic·Lanczos3 resize / 16 位 resize / rotate / flip / pad / composite）。

## 构建

```bash
dotnet build SimdImaging.slnx -c Release
dotnet test  SimdImaging.slnx -c Release
bash tests/SimdImaging.AotSmoke/run.sh
```

## 用法

```csharp
using SimdImaging;
using SimdImaging.Abstractions;
using Sdcb.SimdCodecs.Abstractions;

using var image = Image.Load("photo.jpg");                 // 位深跟随源：8 → Rgba8，16 → Rgba16
byte[] png = Image.Encode(image, ImageFormat.Png);
Image.Save(image, "out.png");

// 原生缩放解码（JPEG 可到 1/8），做不到就抛，不偷偷整幅解码
using var small = Image.Load(bytes, new LoadOptions { ScaleDenominator = 4 });
```

## 与 ImageSharp 4.1.x 的定位差异

| 维度 | ImageSharp 4.1.3 | SimdImaging |
|---|---|---|
| 许可 | Six Labors Split License 1.0（商用按条款购买） | MIT |
| 目标框架 | net8.0 | net10.0 |
| NativeAOT | 4.1 起为部分格式补 AOT 种子 | 全库构建门禁，零 trim/AOT 警告 |
| 位深 | 由像素类型决定 | 全链路 `Rgba16`，16 位不被 8 位中间层吞掉 |
| 确定性 | 无跨 SIMD 档位承诺 | 四档（AVX-512/AVX2/V128/标量）逐字节一致 |
| 内存 | 整图 `MemoryAllocator` | 池化 + ping-pong + 行带，稳态 0 分配 |
| 格式面 | 多 EXR / TGA / CgBI PNG / ANI | 多 HEIF / JPEG 2000 / JBIG2 / PNM 全族 / Ultra HDR |

格式面的差异由底层 `Sdcb.SimdCodecs` 决定，不是应用层的取舍。

> 命名与归属：`SimdImaging` 是配合 `Sdcb.SimdCodecs` 的处理层，**不是** SimdCodecs 项目的一部分，也不代表其作者。名字成对是为了表达分工：SimdCodecs 解码，SimdImaging 处理。

## 文档

| 文档 | 内容 |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | 分层、与 SimdCodecs 的边界、缓冲抽象、SIMD 策略、AOT、测试与基准方法、里程碑、风险 |
| [`docs/operators.md`](docs/operators.md) | 112 个算子的完整清单（语义 / 实现策略 / 确定性 / 阶段）+ ImageSharp 功能对标 |

## 一句话设计

- **三个工作格式**：`Rgba8` / `Rgba16` / `Rgba32F`。布局翻译只在边界发生一次，由 SimdCodecs 的 `PixelConverter` 完成。
- **两个缓冲，来回倒**：`PixelBuffer`（实现 `IMemoryOwner<byte>`，可零拷贝交给编码器）+ 池；稳态 0 分配。
- **计划先于执行**：点算子融合成一张 LUT、预乘只做一次、`Region`/`ScaleDenominator` 下推给解码器。
- **确定性**：整数路径跨平台逐字节；浮点路径固定运算序 + 单次舍入 FMA，跨档位逐字节。

## 目录

```
docs/                        设计与算子清单
src/
  SimdImaging.Abstractions/  工作格式 / 池 / PixelBuffer / PixelView / 算子契约 / 选项
  SimdImaging/               Image / ImageFrame / Load / Save / Encode
tests/
  SimdImaging.Tests/         18 个测试（PNG 8/16 位逐字节往返、JPEG、区域解码、池、释放）
  SimdImaging.AotSmoke/      NativeAOT vs JIT 逐字节比对；不在 slnx 里
SimdCodecs/                  依赖仓库的本地只读副本（参考用；产品依赖走 NuGet）
```

`SimdCodecs/` 是独立 git 仓库，构建本仓库时应忽略。
