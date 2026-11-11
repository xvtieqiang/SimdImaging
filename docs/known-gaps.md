# Known gaps

> **文档口径（2026-11-07 清理）**：本目录只保留四份——`architecture.md`（设计）、`operators.md`（算子表）、`known-gaps.md`（本账本）、`compare-4.1.3-2026-11-07.md`（对外结果）；`audit-2026-11-07.md` 是最近一轮独立复审。11-07 之前的全部过程文档（11 份 + 2 份一次性调研）已打包进 `_archive-before-2026-11-07.tar.gz`，**正文里指向它们的链接现在会失效**，需要时从压缩包取。

有意不做、做到什么程度、以及对标里"少"的部分。仿 SimdCodecs 的同名文档：这里只写事实与原因，不写"后续支持"。

## 有意不做

| 项 | 原因 |
|---|---|
| 任何格式的编解码 | 归 `Sdcb.SimdCodecs`；重复实现只会引入两套 bug |
| 文字渲染与排版 | 需要完整的 TrueType/CFF 解析器 + shaper，是独立产品 |
| GPU 后端 | 与 NativeAOT + 纯托管 + 跨档位确定性三条约束冲突 |
| 图层 / 撤销栈 | 应用层要的是流水线，不是画布 |
| IPTC 语义编辑 | 只做元数据透传与少量字段；解析归 `Sdcb.SimdCodecs.Exif` |

## 逐项现状（M0 起，已做 / 未做混排）

| 项 | 现状 |
|---|---|
| 多帧 / 动画 | 已实现：`Load` 见 `FrameCount > 1` 走会话逐帧解码（`LoadFrames`），保留每帧 `Duration`；保存仍只写单帧，对多帧抛 `NotSupportedException`（见「实现边界」） |
| 处理算子 | 已实现 112 个（全量）；实现边界见下方「实现边界」 |
| SIMD 内核 | Rgba8 行读写、resize/blur 两遍卷积已向量化；点算子走「字节 LUT + 值主序矩阵记录 + 寄存器内钳制打包」，与标量孪生逐位一致（有端到端对比测试）。全部逐位一致，有测试兜底 |
| `ProcessingPlan` | 已实现：同尺寸步骤按行带执行，`PlanSteps` 把现有算子适配成步骤；`Mutate` 未提供（算子返回新图，原地绘制归 DrawOps） |
| `SimdImaging.Drawing` 包 | 未创建，P1 起 |
| `NativePixelPool` / `NullPixelPool` | 只有 `ArrayPixelPool` |
| 公开 API 门禁 | 不接 RS0016 分析器，改用 `docs/public-api.txt` 快照：`PublicApiTests` 反射出全部公开签名（含可空标注）逐行比对，改动必须显式重新生成并 review diff |
| 分块 / 行带执行 | 行带执行已实现（`ProcessingPlan`）；二维 tile 未做，几何与全局算子仍整图执行 |

## 已实现算子（M1，标量）

| 编号 | 算子 | 备注 |
|---|---|---|
| GEO-1 | Crop | 行拷贝，无损 |
| GEO-2 | Pad | 四边距 + 背景色 |
| GEO-19 | RoundCorners | 圆角矩形路径 → 覆盖率 → 乘 alpha；半径夹到短边一半（20×10 得到体育场形） |
| GEO-5 | Resize | Nearest / Box / Bilinear / Bicubic(CatmullRom) / Lanczos3；可分离两遍，滤前预乘 |
| GEO-9 | Rotate 90 / 270 / 180 | 无重采样 |
| GEO-13 | Rotate | 任意角，画布按旋转后包围盒扩张 |
| GEO-14 | Affine | 6 参数矩阵，双线性逆映射 |
| GEO-15 | Perspective | 8 参数单应，伴随矩阵求逆 |
| GEO-4 | EntropyCrop | 行/列 luma 熵阈值裁剪；两遍扫描，列直方图 W×256（4K 约 3.9 MB），不建整图亮度平面 |
| GEO-11 | Transpose | 转置 / 副转置，单遍 |
| GEO-12 | RotateFlip | 四分之一圈旋转 + 水平镜像合成一次遍历，负数与超 4 归一化 |
| GEO-18 | Warp | 委托映射 + 共享采样器；越界填背景色。接口式 ISampler 未提供（委托已是 AOT 安全形式） |
| GEO-20 | Sample | 单点与批量，仅 Nearest / Bilinear，其余抛 NotSupportedException |
| IO-5 | Frames | 多帧 Load（会话解码）+ `Frames` / `KeepFrames`，保留 Duration |
| IO-6 | AnimationCompose | `ComposeAnimation`：逐帧 SourceOver 叠加到运行画布，每帧为累计画面 |
| IO-8 | Thumbnail | 优先解码器内置预览（`TryLoadEmbeddedThumbnail`），否则缩放 + 轻锐化 |
| IO-3 | Transcode | 解码 + 重新编码，可在途中替换元数据块；不做压缩流直通 |
| IO-4 | AutoOrient | EXIF 1–8 单遍应用，输出把标签复位为 Normal |
| IO-7 | MetadataTransfer | `WithMetadata` / `CopyMetadataFrom`：复制像素、替换 Exif / XMP / ICC 块 |
| GEO-7 | ResizeToWidth / Height | 按一边缩放，另一边四舍五入且至少 1 px |
| COL-5 | Lightness | 乘性增益，1 为恒等 |
| GEO-3 | Trim | 与左上角像素比较，容差按归一化通道差 |
| GEO-6 | ResizeMode | Stretch / BoxPad / CropPad / Max / Min，9 个锚点；Max/Min 输出缩放后尺寸 |
| GEO-8 | ResizeCanvas | 不缩放，变大补背景、变小按锚点裁剪 |
| GEO-10 | Flip H / V | 无重采样 |
| GEO-17 | Tile | 周期重复，可镜像交替 |
| COL-1 | Brightness | 加性，采样单位 |
| COL-2 | Contrast | 围绕中灰缩放 |
| COL-3 | Saturation | 围绕 luma 缩放，BT.601/709 |
| COL-7 | Invert | 只反色，不动 alpha |
| COL-8 | Gamma | `v^(1/gamma)` |
| COL-4 | HueRotate | RGB 轴旋转矩阵 |
| COL-6 | Opacity | 只乘 alpha |
| COL-9 | Levels | 1024 项 LUT |
| COL-10 | Curves | 单调三次（Fritsch–Carlson），1024 项 LUT |
| COL-11 | ColorMatrix | 3×4 矩阵，仅颜色 |
| COL-12 | ChannelSwap | 通道重排，alpha 不动 |
| COL-13 | Grayscale | BT.601/709 |
| COL-14 | BlackWhite | luma 硬阈值 |
| COL-15 | Threshold | 亮度二值化，保留 alpha |
| COL-16 | Posterize | 每通道 n 级量化 |
| COL-17 | Sepia | 固定矩阵，按 amount 插值 |
| COL-18 | Tint | 向目标色 lerp |
| COL-19 | ColorBalance | 阴影/中间调/高光三段权重 |
| COL-20 | Temperature | R/B 增益，±30% |
| COL-21 | Vibrance | 按 (1-sat) 加权的饱和度增益 |
| COL-24 | AlphaThreshold | alpha 二值化，颜色不变 |
| COL-25 | Normalize | 每通道 min–max 拉伸 |
| COL-26 | Clamp | 颜色钳制到区间 |
| COL-27 | Linearize / Delinearize | 4096 项 LUT + 线性插值；sRGB / γ2.2 / Rec.709 / PQ / HLG |
| COL-28 | ToneMap | 逐通道曲线；BT.2390 走 PQ 域的 Hermite EETF（显示峰值在 PQ 天花板时退化为钳制）；可选按对数平均亮度自动曝光 |
| COL-29 | GainMap | 基础图 sRGB→线性 × 2^(gain·log2(headroom)·weight)，输出 Rgba32F |
| COL-30 | IccTransform | matrix/TRC 型 ICC：rXYZ/gXYZ/bXYZ + curv/para，D50 PCS，Bradford 适配；LUT 型拒绝 |
| COL-31 | ColorSpaceConvert | 11 个色彩空间；XYZ 派生空间先做 D65→目标白点的 Bradford / Von Kries 适应；输出固定为 Rgba32F（Lab 的 L 是 0..100） |
| COL-32 | ChromaResample | 4:2:2 / 4:2:0：色度盒均值下采样 + 双线性上采样，亮度不动；按行带只算当前带需要的色度行，4K 临时 1.1 MB（原 57 MB） |
| COL-33 | AdaptiveThreshold | 滑动窗口局部均值（Bradley），O(1)/px，环形 luma 行缓存 |
| COL-34 | ColorBlindness | LMS 3×3；severity 在单位阵与缺陷阵之间插值；校正走 Daltonize 而非求逆（满强度矩阵奇异） |
| COL-35 | BackgroundColor | RGB 均方根色距 + 容差 + 羽化带 |
| COL-22 | Premultiply | 浮点中间量 + 舍入 |
| COL-23 | Unpremultiply | a≤0 → 颜色 0 |
| FLT-1 | Convolve | 仅 3×3，边界复制 |
| FLT-2 | GaussianBlur | 真高斯，±3σ；可分离两遍按行带执行，4K 临时 8.9 MB（原 162 MB） |
| FLT-3 | BoxBlur | 半径 r，归一化；与高斯共用行带两遍实现 |
| FLT-4 | Median3x3 | 逐通道排序取中值 |
| FLT-6 | BokehBlur | 圆盘核，最近邻取样 |
| FLT-7 | MotionBlur | 线段核，最近邻取样 |
| FLT-8 | Sharpen | unsharp mask，alpha 取源 |
| FLT-9 | GaussianSharpen | 负高斯壳 + 中心冲激合成单核，一次 2D 卷积；alpha 取中心像素 |
| FLT-11 | Emboss | 零和核 + 0.5 偏移 |
| FLT-5 | Bilateral | 空间核 × 值域核，值域核 512 项 LUT；标量 O(r²) |
| FLT-10 | Sobel | luma 梯度幅值，写入 RGB |
| FLT-12 | OilPaint | 邻域直方图取众数强度，输出该 bin 的平均色；全整数计数，与遍历序无关 |
| FLT-13 | Pixelate | 块内预乘平均后整块写平 |
| FLT-14 | Vignette | 半对角线归一化的 smoothstep 径向压暗 |
| FLT-15 | Glow | 亮度阈值取高光 → 高斯模糊 → 加法回叠 |
| FLT-16 | Dither | 每通道位深抖动；5 种误差扩散核 + Bayer 2·4·8 + 直接取整 |
| FLT-17 | Morphology | 3×3 腐蚀/膨胀/开/闭；滚动三行，内存 O(行) |
| FLT-18 | HistogramEqualization | 逐通道 CDF；单占位图不拉伸 |
| FLT-19 | Clahe | 分块直方图 + clip limit 均匀回灌 + 双线性插值 LUT；只有单一灰度的 tile 保持恒等 |
| FLT-20 | Noise | xorshift128+（splitmix64 播种），12 次均匀和近似高斯；无 libm，跨平台一致 |
| FLT-21 | GuidedFilter | 自引导，逐通道；箱均值的窗口求和按 8 像素向量化（float 前缀和），行带执行，工作集 = 5 × (band + 4r) 行（4K / band 128 约 11 MB） |
| ANL-1 | Histogram | 每通道 256 桶 |
| ANL-2 | Statistics | 四通道 min/max/mean/stddev/entropy，一趟；归一化到 0..1，熵按 256 级 |
| ANL-6 | Ssim | 11×11 高斯（σ1.5）窗，逐行环形缓冲，4K 只要不到 1 MB 临时内存 |
| ANL-3 | DominantColors | 中位切分建表 → 按覆盖像素数排序 → 用被分配像素的均值回代 |
| ANL-4 | AlphaBounds | alpha 阈值包围盒，全透明返回零尺寸 |
| ANL-5 | Psnr | RGB 上的 PSNR，峰值固定 1；逐位相同返回 +∞ |
| ANL-7 | Diff | 每像素最差通道差的最大值/均值/超阈值计数 |
| ANL-2 | MinMax | 每通道 min/max |
| CMP-1 | DrawImage | 双线性缩放 + 合成 |
| CMP-10 | Gradient | 线性（任意角）/ 径向 / 扫描 |
| CMP-13 | Watermark | 九宫格位置 + 边距 + 不透明度 |
| CMP-5 | FloodFill | 扫描线 + visited 掩码 + 颜色容差 |
| CMP-4 | Fill / FillRect | 原地写入，按格式广播 |
| CMP-6 | DrawLine | Bresenham，先做 Liang-Barsky 裁剪 |
| CMP-7 | DrawRect / FillEllipse / FillPolygon | 多边形用 even-odd 扫描线 |
| CMP-8 | FillPath | 贝塞尔自适应细分（0.25 px），even-odd / non-zero 扫描线；16 条子扫描线 × 水平精确覆盖得到 alpha，内部整像素替换、边缘 source-over |
| CMP-9 | StrokePath | 线段矩形 + 连接/端点拼合后按 non-zero 求并；miter / round / bevel、butt / round / square、虚线；与填充共用覆盖率光栅器 |
| CMP-11 | Clip | 路径覆盖率乘 alpha；外部图像 mask 来源未做 |
| CMP-12 | Clear | 整幅清零（含行尾 padding） |
| CMP-2 | BlendModes | 18 种，W3C 公式；Hue/Sat/Color/Lum 走 HSL |
| CMP-3 | PorterDuff | 12 式全，预乘空间计算 |
| QNT-1 | OctreeQuantizer | 8 层八叉树，插入期按叶预算剪枝，最深最稀节点先合并 |
| QNT-2 | WuQuantizer | 5 bit 三维矩 + 方差最小切分；矩存真实 0..255 值 |
| QNT-3 | PaletteQuantizer | 5 bit 直方图 + 中位切分；未做 k-means 精修 |
| QNT-5 | BuildPalette | 从图建调色板 |
| QNT-6 | MapToPalette | 32³ 最近色查找表 |
| QNT-4 | WebSafe | 216 色固定表 |
| QNT-7 | ReduceColors | 建调色板 + 映射，默认 Floyd–Steinberg；核可换 |
| QNT-8 | DitherToPalette | Floyd–Steinberg，误差限幅 |

## 实现边界（已实现，但有明确上限）

| 项 | 边界 |
|---|---|
| COL-30 IccTransform | 只支持 **matrix/TRC** 型 RGB 配置文件（sRGB、Display P3、Adobe RGB）。LUT 型（mAB/mBA、CMYK、扫描仪 profile）抛 `NotSupportedException`——CLUT 求值需要解析器交出网格，`Sdcb.SimdCodecs.Color.Icc` 未暴露，解析器被重复实现时会漂移 |
| 多帧保存 | `Save` / `Encode` 对 `FrameCount > 1` 抛 `NotSupportedException`，而不是只写第 0 帧。要导出动画得先 `KeepFrames` 选一帧 |
| 多帧解码路径 | 静图仍走零拷贝 span 路径；多帧需要会话持有数据，会先 `ToArray()` 复制一次 |
| IO-8 Thumbnail | 没有 DCT 域下推（JPEG 1/8 缩放），缩略图来自解码器内置预览或整图缩放 |
| IO-1 `PreferNativeScale` | 输出**可能大于**请求的 `⌈w/d⌉`：它取 codec 原生档，跳过 `BoxDownscaler` 那趟整图过滤。512² JPEG 要 1/16（32×32）会拿到 1/8（64×64）。这是选项的定义不是缺陷——要精确尺寸就别开；调 `.Width` 拿实际结果。与 `Region` 互斥（同开抛 `ArgumentException`），因为两者对"档位"的假设冲突 |
| HEIF / HEIC | **未接入**：`Sdcb.SimdCodecs.Heif` 是独立包，且要求启动时 `ImageCodecs.Register(HeifImageDecoder.Registration)`。本库既不加包也不注册，`Image.Load` 拿到 HEIC 会抛 `InvalidDataException("not a recognised image format")`。要用就自己引用那个包并注册——同时 HEVC 专利义务落在调用方 |

## 对标基线里"少"的部分

| 项 | 说明 |
|---|---|
| EXR / TGA / CgBI PNG / ANI | ImageSharp 4.x 有，`Sdcb.SimdCodecs` 侧没有；属 codec 层缺口，不在本项目范围 |
| 文字 | ImageSharp 靠 `SixLabors.Fonts`；我们不做 |
| 重采样器 | 设计 16 种，`Resampler` 枚举**只实现 5 种**（Nearest / Box / Bilinear / Bicubic / Lanczos3）；4.1.3 公开 16 个 `KnownResamplers` 名称。属功能缺口，不假装覆盖 |
| Brightness 参数语义 | 本库**自有设计**是加性偏移（0.2 = +0.2×满量程）；参照实现同名 API 是另一套语义，同参数对拍 PSNR 仅 **4.23 dB**。这是功能对比时发现的「名称相同、语义不同」事实，**不是缺陷**：本项目按自己的语义文档使用，**不为兼容其他项目改 API、不提供兼容模式/兼容参数**。对标表必须写出口径，不得据同名宣称覆盖 |
| 4.1.3 同进程 A/B | 已解决：`docs/sixlabors.lic` 在位，对比工程通过 `SixLaborsLicenseFile` 按路径引用，Release 构建与 4.1.3 黑盒 A/B 均已跑通（见 `docs/compare-4.1.3-2026-11-06.md` 与 `docs/optimization-4.1.3-2026-11-06.md`） |

## 审计确认的缺口（2026-11-06，`audit-2026-11-06.md` 核对结果）

| 项 | 事实 | 状态 |
|---|---|---|
| AVX-512 档 | `src/` 全库 **0 处 `Vector512`**，机器 `avx512=False`，第四档从未被执行 | **未修**。对外不得再宣称"四档 SIMD 逐字节一致"，只能写"三档实现 + 一档预留" |
| `.git` 不存在 | `docs/sixlabors.lic` 没有任何忽略规则保护，密钥泄漏防护为零 | **未修**，需先建仓 |
| `FloatScratch` 无大小上限 | `[ThreadStatic]` + `MaxRetained=8`，每 worker 可驻留 8 块任意大小缓冲且永不收缩 | **未修**，64 核机器上可观 |
| `Rgba32F` 的 rotate90 | 泛化只覆盖 4 与 8 字节像素，16 字节仍走 `Slice+CopyTo` 慢路径 | **未修** |
| 偶发测试失败 | **已定位并修复**。`GaussianBlur_KeepsAConstantImageConstant(sigma: 5)`，60 次里 4 次失败。根因：平面化横向的前缀循环写成 `for (; x < radius; x++)`，**只以 radius 为界没以 width 为界**；11 像素宽的图配 sigma 5（radius 15）时 `planes[to + x]` 越过 `8*width`。`FloatScratch` 是 best-fit、可能返回更大的缓冲，于是**缓冲够大时静默写脏相邻平面、刚好等大时抛 `IndexOutOfRangeException`** —— 异常那次反而是运气好。已修（`Math.Min(radius, width)`），修复后 60/60 全绿 |
| 同类写法已全库排查 | grep `< radius` / `<= radius` 后逐处核对：`GuidedFilterOps` 第 150 行有 `interior = width > radius * 2 + 1` 守住了横向边界（误报）；`ThresholdOps` 是核内 tap 循环。**只发现这一处，没有第二例** |
| 测试覆盖从"单尺寸"改为"尺寸 × sigma 扫描" | 新增 `Gaussian_SizeSweep_MatchesNaiveReference` 与 `BoxBlur_SizeSweep_MatchesNaiveReference`：宽度 3/5/8/11/16/40 × 高度 3/7/9/27 × σ 0.8/3/5，共 144 个用例，全部对朴素参照逐字节比对。测试总数 415 → **559**，559 × 30 轮压测 0 失败 |
| 同类静默污染没有测试能抓 | 那个失败用例是**纯色图**，写进去也是同一个值，所以断言从未报错；goldens 也都是宽图。已补 `Gaussian_NarrowImageWiderKernel_MatchesNaiveReference`（11×9、非纯色、对朴素参照逐字节比对），并**验证过回退修复它会变红** |

## `ProcessingPlan` 的实际形态与宣称不符（2026-11-06 实测）

| 项 | 事实 |
|---|---|
| 宣称 | 同尺寸步骤按行带一次执行，步骤之间不产生中间图 |
| 实际 | `PlanSteps.Point/Neighborhood/Global` 都是「切片 → 调完整算子 → 拷行」的**适配器**，每个 band 每个步骤会租一整带 `WorkingBuffer`、在 band worker 内部**再开一次嵌套并行**、再拷一遍 |
| 代价 | 3 个点算子单独跑 5.1 ms，走 plan 变 14.5 ms（**+2.8×**）；gaussian 17.1 ms → 67.3 ms（**+3.9×**） |
| 对标的后果 | 流水线 A/B 是 **0 / 3**（唯一那个"赢"是参照实现首个用例的预热假象），与 12 个单算子的 12/12 完全相反 |
| 已修 | `Execute` 的 band 循环由串行 `for` 改为 `RowParallel.ForBands`，并在 `PlanSteps` 内抑制嵌套并行让算子内联。流水线 A/B 从 **0/3 翻到 3/3**（几何平均 0.3793 → 1.9954×） |
| 契约变更 | band 并行后**内核会被并发调用**，用户 kernel 必须线程安全。已写入 `Execute` 文档注释；`Plan_ReportsTheSourceRectWithHaloClampedToTheImage` 因裸 `List.Add` 在并行下损坏而改成 `ConcurrentBag` + 排序断言 |
| 仍未做 | **跨步骤融合**。`Execute` 仍按步骤整图循环、每步租一张整图 ping-pong；`PlanSteps` 仍每 band 租缓冲再拷行。注意：`ProcessingPlan` 的类注释**本来就写准了**（"每步的目标缓冲是整图大小，这部分无法避免"），此前转述为"零中间图"属转述错误，不是代码问题 |
| 已提供（2026-11-07） | `PlanObserver`：`Execute`/`Apply` 可在每步结束、缓冲仍存活时回调，实验台的场景链视图就建在它上面。它**不改变内存模型**（仍每步一张整图），反而依赖这个模型——融合之后就没有中间结果可看。两者是两条路径，不是一件事 |
| 场景链的 ImageSharp 对照 | 每条配方至少一步参照实现没有对等实现：复古胶片的 `grain`（参照实现无噪声生成器）、风光增强的 `CLAHE`（参照实现无此算子）、线稿化的 `Sobel`（语义不同）、两条风格链的 `Vignette`（参照实现要求颜色参数，语义不同）。做"部分链"对照测出来的既不是本库链也不是参照实现链，数字没有意义 | **评估后不做**（2026-11-07）：遵守"没有公平对等就不设倍数"的既有原则；等某个链的每一步都有对等实现时再补 |
| 按 band 切会错的算子 | `Vignette` 从**传入视图的中心**算衰减，切 band 后每带各自算中心，输出变成"每带一个椭圆"。它必须走 `Global`。这是实测发现的，不是推断：第一版链把它标成 `Neighborhood` + halo 0，出图后暗角是每带一个 |

## 并发与批量（2026-11-07 补测）

在此之前**没有任何测试从多线程调用过算子或 plan**：589 个测试全部是单线程。批量处理（一个 plan、多张图、
若干线程）是这个库最真实的使用方式，所以补了 `ConcurrencyTests`：

| 用例 | 结果 |
|---|---|
| 24 个线程并发执行同一个 4 步 plan（Point + Neighborhood + Global + Point） | 24 个 SHA-256 全部相同，且等于串行值 |
| 16 张不同图并行批处理 | 每张都与串行结果逐字节一致 |
| 种子噪声并发执行 12 次 | 12 个哈希相同——RNG 状态没有跨调用共享 |

结论：`ArrayPixelPool`（并发字典 + 并发包）、`FloatScratch`（`[ThreadStatic]`）、`PlanSteps` 闭包里的
LUT 都是安全的。**没有发现缺陷**，但在此之前这一点只是"应该对"，不是"测过"。

⚠ **吞吐已补测（2026-11-07）**。1080p `detail` × 8 张、`gaussian` σ3，并发度 1/4/8：

| 并发 | 只跑算子 | 含编码出图 |
|---|---|---|
| 1 | 37.62 张/秒 | 4.56 张/秒 |
| 4 | 60.58 张/秒 | 13.11 张/秒 |
| 8 | 62.82 张/秒 | 16.71 张/秒 |

两条结论：**编码是瓶颈不是算子**（加上出图吞吐掉 8 倍）；**并发 4 已基本饱和**（对 8 并发只差 3.6%），
因为每个 `Execute` 内部本来就有行带并行。所以批量接口把 `concurrency` 暴露出来而不是写死，实验台取 4 并
默认 `encode:false`。

## 内存：池的 2 的幂放大（2026-11-07 实测并修复）

一次 4K 六步链把实验台进程推到 **426–876 MiB**，而链路本身只需要两张 33 MiB 的缓冲。逐层拆开：

| 层 | 实测 | 原因 | 状态 |
|---|---|---|---|
| `ArrayPool<byte>.Shared` 的桶 | 托管堆 **462 MiB** | 每帧 4K Rgba8 = 33.18 MiB，被向上取整到 **64 MiB** 桶且不主动归还。2 倍放大的正好是占内存大头的那一次分配 | **已修**：`ArrayPixelPool` 对 >4 MiB 的请求改走 1 MiB 粒度的自有池，每尺寸最多留 2 块，并加 `Trim()`。托管堆降到 **217 MiB** |
| 实验台的 `outputs` / `images` 字典 | 7 张 4K PNG = **74.6 MiB**，且无上限 | 每次运行都往字典里塞结果，永不删除。按"张数"封顶是错的：48 张 4K PNG 是半个 GB | **已修**：按**字节**封顶（outputs 96 MiB / images 64 MiB），FIFO 淘汰 |
| 中间图按全尺寸编码 | 7 张 4K PNG / 轮 | 中间步骤只用于"看哪一步坏了"，不需要 4K | **已修**：snapshot 宽上限 1600（PNG 而非 JPEG——链要能处理带 alpha 的图）。降到 39 MiB |
| LOH 已回收但不归还 | `live` 217 MiB vs `ws` 876 MiB | 大对象堆不压缩，死对象的页留在 committed 里 | **已缓解**：空闲 4 秒后自动 `Trim()` + `CompactOnce` 强制压缩；有请求进来就放弃。实测 **ws 712 → 211 MiB** |

**结论：不是泄漏。** 强制 GC 后 `live` 稳定在 89–217 MiB 且不随运行次数增长，涨的全是可回收的 committed 页。
诊断入口：`GET /api/memory`（`?compact=true` 手动压缩）。

## 实验台暴露的两个真短板（2026-11-06）

| 项 | 事实 | 状态 |
|---|---|---|
| `EqualizeHistogram` 慢 5× | 真实 1080p 照片上 **36–45 ms**，ImageSharp **8–9 ms**（0.21×）。根因：`Histogram()` 被调 3 次、每次把整行转 float 再转回 int，而且整条链路单线程 | **已修**：一趟三通道整数直方图（per-worker 局部计数、整数合并，字节不变）+ 行带并行应用。**1.60–1.90 ms，4.65–5.54×**，约 20 倍提速。559 测试通过、15 轮压测 0 失败 |
| `Median3x3` 慢（已两轮修完） | 起底：1080p 彩条 130 ms、真实照片 575 ms（逐像素插入排序 + 单线程）。第一轮改成选择网络 + 行带并行 + 无分支交换 → 38–50 ms。第二轮改**字节域 SIMD**：`Vector128<byte>` 上同一条 19 次比较交换网络，一次处理 4 像素/16 个样本 | **已完成，真实照片 575 → 0.8–1.2 ms（约 500 倍）**，且与内容无关。字节域的合法性靠 `ByteToFloatRoundTrip_IsIdentity_ForEveryByte`（256 个字节穷举证明）撑住，输出靠 4 种宽度的全排序参照逐字节比对。565 测试通过、8 轮压测 0 失败 |
| 三个重算子全串行 | 真实 1080p：`oilpaint` **414 ms**、`bilateral` **412 ms**、`sharpen` **282 ms**。三者都是「跨行滑动环 + 单线程」，环让它们无法直接并行 | **已修**：环只是行缓存，不是跨行状态，所以把环、计数器和输出行都搬进每个 worker，再按行带切分。`oilpaint 414→79`、`bilateral 412→97`、`Convolve2D（sharpen/emboss 共用）282→65`，各约 4–5 倍。565 测试通过、6 轮压测 0 失败 |
| `OilPaint` 算法层减工作 | 并行后仍有 79 ms：每像素把 4 个 levels 大小的数组清零（levels=20 → 80 次 int 写）再重数 (2r+1)² 个样本 | **已修**：改成每行播种一次、随后**滑动窗口直方图**（离开列减、进入列加，`clamp(x-1-radius)` 与 `clamp(x+radius)` 精确对应钳制语义）。计数是整数，减是加的精确逆运算，所以与逐像素重算**逐字节一致**——`OilPaintOpsTests` 用旧算法做参照，3 个尺寸/半径组合全过，含"窗口比行还宽"的边界。**79 → 39–45 ms**，从起底 414 ms 累计约 10 倍 |
| `Sobel` 三处非算法浪费 | 1080p **51 ms** 对一个 3×3 / 8 tap 的算子不合理。三处：每行 luma 被重算 3 遍、`outputRow` 每行 new 一次（每调用约 8 MB 垃圾）、三个 luma Span 在 x 循环内反复构造 | **已修**：三行 luma 环缓存（值由同一表达式、同一输入算出，故是同一个 float）+ 提升 Span 与输出行 + 行带并行。**51 → 7.2–7.9 ms（6.6×）**。`SobelOpsTests` 用旧算法逐像素比对，3 个尺寸含 4 行极矮图，568 测试通过、5 轮压测 0 失败 |
| `Bilateral` 的剪枝走不通 | 曾考虑"按 sigmaRange 剪掉值域外的邻域"，**核对后放弃**：`exp(-d²/2σ²)` 对有限 d² 永远非严格 0，跳过会改变求和，不是逐字节一致。目前 73–86 ms 基本是固有成本（49 次距离 + 2 次查表/像素） | 该方向不成立。要再快只能改近似算法，那就不是"锁字节"了 |
| 第二批重算子排序 | 把 13 个没量过的算子接进实验台后重排：`bokeh` **533** / `guided` 128 / `noise` 92 / `glow` 82 / `erode` 46 / `dilate` 44 / `adaptive` 43 / `emboss` 38 / `tonemap` 30 / `quantize-wu` 28 / `delinearize` 25 / `linearize` 24 / `quantize-median` 18 ms | `bokeh` 走的是 `AverageOffsets`（滑动环 + 串行），同样把它并行化：**533 → 105–132 ms（约 4–5×）**。代价是 `offsets` 是 `ReadOnlySpan` 不能被 lambda 捕获，需先 `ToArray()`（几百个元组，对 2M 像素可忽略）。571 测试通过、4 轮压测 0 失败 |
| 剩余最慢 | 1080p 真实照片：`guided` 134 / `bokeh` 105–132 / `noise` 100 / `bilateral` 73–86 / `dither` 79 / `motion` 75 / `sharpen` 65 / `sobel` 51 ms | 都还在标量路径。`bilateral` 有 (2r+1)² 次 exp 查表，`oilpaint` 有 levels² 次直方图清零，都是「算法层面可减工作量」而不是单纯缺 SIMD |
| `Clahe` 慢且全串行 | 真实 1080p **80 ms**，两个阶段（每 tile 直方图、四邻域双线性混合）都在单线程上跑 | **已修**：直方图阶段按 tile 行切（每 tile 行独占一片 `histograms`，无需锁、计数逐位不变），应用阶段按行带并行。**80 → 21–27 ms（约 3.2×）**，561 测试通过、8 轮压测 0 失败。仍有空间：两阶段都还在做 `ReadRowRaw` 的 float 往返，且应用阶段每像素 12 次 LUT 取样，要再快需改字节域（会动到逐位一致性，需单独验证） |
| `Curves` 慢 10 倍 | 实验台补入口后暴露：800×600 **25.8 ms**，同类点算子 `Levels` 只要 0.34 ms。根因：`Curves` 走 `Apply`（每像素 8 次 float↔byte 往返），而其它通道无关点算子都走 `ApplyLut` 字节域快路径——1024 项 float 表早就建好了，却没享受它 | **已修**：改走 `ApplyLut`。字节域表用**同一条 row transform** 按 256 个字节值各跑一次构建（`PointLut` 的 by-construction 设计，不是第二份实现），`PointLutTests` 新增 Curves 不对称 S 曲线对拍锁字节。**25.8 → 0.34–0.41 ms（800×600）**，1080p 1.2–1.5 ms，与 `Levels` 持平。589 测试通过 |
| grayscale 0.74× 是噪声 | 连跑 5 次是 1.38 / 1.46 / 1.75 / 1.41 / 1.38×，中位 1.41× | 以前一版报告里把它当结论是**错的**，已纠正 |

## 正式基准（2026-11-07，13 场景，1080p Rgba8）

`benchmarks/SimdImaging.Comparison` 的 A/B 表加入了 `equalize`（唯一一个"今天修好、且 ImageSharp 4.1.3 有对等实现"的算子），
单算子场景由 12 增到 13：

- **超越判定 1.8224×，11 / 13 项领先。**
- `equalize` **7.54×**（本库 1.595 ms / 参照实现 12.0 ms）——与实验台的 6.58× 同一量级，现在有权威口径背书。
- 两项落后：`grayscale` 0.95×、`sepia` 0.95×，差值都是 **0.08 ms**（1.690 vs 1.605 / 1.689 vs 1.609）。这两项已到 1.6 ms 的地板，**实质是平手，不该当"落后项"读**——上一轮把 box 0.99× 当结论就是犯过这个错。
- 未进表的算子（`median3x3` 0.8–1.2 ms、`sobel` 7.2 ms、`oilpaint` 38–44 ms、`bilateral` 73–86 ms、`sharpen` 65 ms、`clahe` 17–21 ms）在 ImageSharp 4.1.3 里**没有对等实现**，给不出有意义的倍数，只能留在实验台做单侧展示。

## SIMD 路线：先立参照，再动内核（2026-11-07）

结论：SIMD 值得上，但**只对"移位行累加"这一类**——`out[x] = Σ_k w_k · row_k[clamp(x+dx_k)]`。
`median` 的 40 倍（字节域 16 车道）不能外推到这里，float 8 车道按 **4–8 倍**估算。

| 该上 SIMD | 当前 1080p | taps | | 不该上 | 原因 |
|---|---|---|---|---|---|
| bokeh | 105–132 ms | 169 | | dither 79 | 误差扩散沿扫描线串行 |
| motion | 75 ms | 依长度 | | noise 100 | RNG 序列固定 |
| sharpen (`Convolve2D`) | 65 ms | 81 | | bilateral 73–86 | LUT 索引依赖像素值，是 gather（实测 gather 1.10×） |
| emboss | 38 ms | 9 | | guided 134 | 已分块向量化 |
| oilpaint `Accumulate` | 38–44 ms | 10 | | median 0.8 | 已到头 |

**纪律**：每步同进程 A/B，数字不动就回滚；**前置条件是先补参照测试**——移位累加要重排 clamp 与循环顺序，
是最容易静默算错的一类改动（今天 sobel 的环、oilpaint 的滑动钳制都栽过）。

**已完成第一步**：`BokehBlurTests` 用当前逐像素算法做参照，4 个尺寸含"圆盘比图还宽"的全钳制情形，6/6 通过。
**试点已完成（bokeh）**：内核改成"按 tap 平移整行累加"——每个 tap 只做一次 `Math.Clamp(y+dy)`，沿 x 用
`Vector256` 加 8 个 float，边缘 `maxDx` 个像素走逐像素钳制路径覆盖结果。**105–132 → 27–40 ms（约 3.5×）**，
相对起点 533 ms 累计 **约 20 倍**。575 测试通过（含 `BokehBlurTests` 的 4 个尺寸逐字节比对）、4 轮压测 0 失败。
**门槛通过（≥3×）**，按计划继续推 motion → Convolve2D。

**Convolve2D 也已改（sharpen / emboss 共用）**：同一套移位累加，每 tap 一次 `acc += src * w`（`Vector256`）。
`sharpen` 1080p **65 → 30–32 ms（2.1×）**，从起底 282 ms 累计 **约 9 倍**。`Convolve2DTests` 用旧逐像素算法
做参照，3 个尺寸含"核比图还宽"的全钳制情形，逐字节一致。

⚠ **emboss 没有变化（38 → 37.5–38.5 ms），原因是技术本身有门槛**：移位累加每行要多做三次整行 pass
（清累加器、拷贝到输出、单独写 alpha），约 3×stride 次浮点操作；3×3/5×5 只有 9–25 个 tap，
这部分开销把收益吃平了。**只有 tap 数够多（≳25）时这套才有赚**。所以它没有回退，但也不该指望小核受益。

**原计划（保留）**：——每 tap 一次 `Math.Clamp` 提到行偏移上，
沿 x 用 `Vector256` 加 8 个 float；边缘 `maxDx` 个像素仍走逐像素钳制路径并覆盖结果。试点若拿不到 3 倍就收手，
不扩散到 motion / Convolve2D。

## 测量方法论（2026-11-07 定稿）

**三个工具，三种口径，不可混用。**

| 工具 | 做法 | 回答什么 |
|---|---|---|
| `-- perf <size>` | 每侧连续跑 30 ms 取中位 | **吞吐** |
| `-- cores <size>` | 一支子进程内两侧交错 21 轮取**最小**；核数另用单侧子进程 | **单次延迟** + 真实核数 |
| `-- sizes` | 同一张表跑 5 个分辨率并标出翻面项 | 尺寸敏感性 |

**四条铁律（每条都对应一次实际失败）**

1. **单格数字两次运行差 ≥2× 不得写入结论。** 引区间或几何平均。反例：`resize-nearest` 在 640×360 的参照实现时间，两次 `perf` 分别是 0.0310 与 0.1542 ms（5 倍），此前"输 4.5 倍"的说法因此作废。
2. **单分辨率不算定论。** 17 项里 **7 项在 640×360 与 4K 之间翻面**。
3. **进程级 `TotalProcessorTime` 不能测单侧核数。** 本库并行留下线程池线程会干扰参照实现的测量：同一算子两次读到 1.22 与 7.86 核，还出现过 120×。
4. **不要自己造尺子。** 为对比"优化前后"而复刻旧实现做同进程 A/B 是错的——它衡量的是复刻的忠实度，不是产品。实测证据：给旧侧补一个环缓存（纯属让复刻更忠实），bokeh 的"倍数"就从 18.82× 跳到 28.42×。该工具已删除。"优化有没有用"只由两件事回答：**参照测试保证字节不变**，**ImageSharp A/B 回答是否领先**。

**跨尺寸实测（17 场景，Rgba8）**

| 尺寸 | 超越判定 | 领先项 |
|---|---|---|
| 640×360 | 1.3139× | 12 / 17 |
| 1280×720 | 1.5085× | 13 / 17 |
| 1920×1080 | 1.8068× | 15 / 17 |
| 2560×1440 | 1.8318× | 17 / 17 |
| 3840×2160 | 1.8535× | 17 / 17 |

稳定领先（不随尺寸翻面）：`equalize` 5.5–7.7×、`rotate90` 2.1–6.4×、`resize-bicubic` 2.2–4.4×、`resize-bilinear` 2.1–2.8×、`resize-lanczos3` 1.6–2.3×、`flip-vertical` 1.6–1.9×、`crop` 1.2–2.0×。翻面的 7 项见上表口径说明。

**核数：两边相当，不是"靠核多赢"**

本库默认平均 8–11 核、参照实现 6–10 核（ImageSharp 4.1.3 也并行）。**同核数（本库强制单线程）汇总慢 1.1–1.3×**，分项波动 ±40%，只有汇总值可引用。早前"每核落后 4 倍"的结论是同进程测量污染的结果，已作废。

**`resize-nearest` 小图静默单线程（已修）**

根因不是切带逻辑，而是 **`RowParallel.For` 的 64 K 像素门槛**：Nearest 快路径用它，而 640×360 → 320×180 的输出只有 57,600 像素，于是退到单线程（`LastWorkerCount = 1`）。改 `ForBands` 显式切带后 worker 升到 5.5，本库绝对耗时 **0.1419 → 0.1229 ms（−13%）**，输出逐字节不变。

全库 13 个 `RowParallel.For` 调用点已静态复查：传参只有 `width,height`（源图，7 处）、`source.Width/Height`（5 处）、`destination.Width/Height`（`Transpose`，1 处，面积相同）。**没有第二处传缩小尺寸**。反方向的问题是 `ForBands` 无像素下限，`Clahe` 在极小图上仍按 `tilesY` 派 8 个 worker——微秒量级，未处理。

## 审计二轮（2026-11-07）提出、已修的三项

| 项 | 问题 | 处理 |
|---|---|---|
| `new float[]` worker 缓冲 | `Convolve2D` / `AverageOffsets` 每 worker 每次调用分配 side×stride floats（81 taps ≈ 311 KB、bokeh r6 ≈ 449 KB，×16 worker ≈ 5–7 MB 垃圾），与「稳态 0 分配」纪律矛盾 | 改 `FloatScratch.Rent/Return`（线程本地复用），并用 try/finally 保证异常路径也归还 |
| `Global` 步骤误抑制内部并行 | `PlanSteps.Global` 也调了 `EnterNested`，导致 plan 内 Equalize/CLAHE 的应用阶段读到 nesting=1 被压成单线程 | 去掉该包裹：Global 在调用线程上执行，外面没有 band 循环可嵌套 |
| 三档矩阵缺证据 | 新内核（`Vector128<byte>` 比较交换、`Vector256<float>` 累加）只有默认档的测试记录 | **已补跑**：`DOTNET_EnableAVX512F=0` / `EnableAVX2=0` / `EnableHWIntrinsic=0` 三档各 **589/589 通过** |

**审计认为但实际不成立的**：§2.4 说 pipeline 的 `point-3` 5.22× 是参照实现被干扰。这一格本就是被判定为预热假象并排除的格子，
报告里写的是"3/3 翻正、幅度 1.2–1.3×（point-3 存疑）"，从未引用 1.9954×。但提醒有效：该数字仍留在原始文件里，
翻到就会误引，应加作废标记。

## 未决事项（审计二轮遗留，等待决策，不是技术问题）

| 项 | 现状 | 影响 |
|---|---|---|
| **没有 `.git`** | 连续两轮审计标记；本人也确认"合规红线悬空"。当前所有改动不可回滚、不可追溯 | 仓库公开前的每一天都是密钥泄漏敞口 |
| **golden v2 / 定点化未立项** | 720p 上 `grayscale` / `sepia` 仍 0.83–0.84×；浮点重排的空间在收敛，但迟早要面对"锁字节 vs 定点化"的选择 | 决定这两个算子还能不能继续提速 |

## 测量口径（实践中遇到的问题）

| 问题 | 事实 |
|---|---|
| 逐算子串行扫描当基线 | `Profile` 第 1 节 `DegreeScan` 是「一个算子连测 5 个并行度、再测下一个」，各算子所处的池/GC 状态不同。同一段代码的 sepia 在这张表里是 13.8 ms、灰度 6.2 ms，而交错复测两者相等（6.68～8.76 ms）。**判方案好坏只能用同一循环里交替采样的比值**，见 `docs/scheme-2026-11-06.md` |
| 分块测量当 A/B | 先测旧实现一整块、再测新实现一整块，会得到「端到端没动（2.848 → 2.982）」的错误结论；把开关翻进 runner、同一轮轮转后是 1.5～2× 的真实差距。机器漂移足以吃掉 100% 的改动 |
| 单轮绝对值跨轮比较 | 同一台机器同一天，参照实现 resize-bicubic 在两次运行里是 11.6 ms 与 27.0 ms。跨轮只能比同进程比值，绝对值只作方向性参考 |
| 提前宣布「瓶颈是 X」 | sepia 先后被判为 `ToByte`（矢量化后收益 28%）、gather（实测 1.00×）、表布局重排（无变化）、band 高度、worker 粒度。真正的 2× 来自**换表排布**，而它是靠「9 个乘积为什么是 9 次读」这个问句找到的，不是靠任何一次微调 |
| 对照实现写成稻草人 | 纵向 pass 的 A/B 第一版把「旧」写成纯标量循环（原实现是 `Vector256` 累加器 + clamp 内层），差距被放大到 0.44×；逐字还原后是 0.95×。**对照写错，结论会差 2 倍**，A/B 的旧实现必须逐字复制 |
| 共享缓冲当只读 | 平面化 bench 的 `plane` 被多个 band worker 同时写，99.6% 样本不一致，还一度被误判成「FMA 舍入差异」。多 worker 并行下，任何被写的临时缓冲都必须每 worker 一份 |
| 单 pass 收益当整算子收益 | 纵向列块单 pass 1.57×，整算子只有 5～7%（横向 pass 与行写入不在这条链上）。报告里必须两个数字都写，否则会高估 |
| 向量组内的偏移窗口忘了边界 | 平面化横向用 `plane[x+k .. x+k+7]` 一次加载 8 个像素，等价于假设组内 8 个 lane 偏移相同；只要组内有像素要 `clamp` 就读错样本。首尾各 `radius` 像素必须走标量孪生。漏掉它是 1.1% 样本不一致的根因，而当时还被误判成「FMA 收缩差异」 |
| 只在一种尺寸/格式上验收 | 滤波改造全部只在 1080p `Rgba8` 上验证过；换到 `Rgba16` 复测才暴露 `Rotate90` 的通用分支没有并行、每像素一次 `Slice(8B).CopyTo`，比参照实现慢 **4×**（Rgba8 下却是快 3.9×）。这个缺陷与当轮改动无关，是之前就存在的。**结论必须跨尺寸与跨像素格式各跑一遍** |
| 顺序扫描的排序当参数选择依据 | 二维 tile 的 12 组参数是顺序测的，漂移没被分摊；三个决赛配置再用同一循环轮转判一次才敢改库（`1×64` 1.00× → `4×128` 0.85×） |

## M2–M4 出口条件核验（2026-11-11）

`architecture.md` §11 里标「未验」的行，本轮把能跑的都跑了（M2 模糊沿用 11-07 已达标数，未重跑）。
机器与 11-07 报告相同（`Windows 10.0.19045 | X64 | .NET 10.0.14 | avx512=False avx2=True v128=True`），
对手 `SixLabors.ImageSharp` **4.1.3**，密钥只按路径引用。原始结果在
`benchmarks/SimdImaging.Comparison/results/`（`.gitignore:26` 忽略，**仅本机可见**）；
**下面表格里的数字已写入本文件，随仓库走**：

| 模式 | 本地原始结果 |
|---|---|
| `peakmem 7680x4320` | `benchmarks/SimdImaging.Comparison/results/2026-11-11.peakmem.md` |
| `parity`（含新增 B4 量化对拍） | `.../2026-11-11.parity.md` |
| `perfpipe 1920x1080` | `.../2026-11-11.perfpipe.md` |
| `perf16 1920x1080` | `.../2026-11-11.perf16.md` |

### 逐条结论

| 出口条件 | 实测 | 判定 |
|---|---|---|
| M2 模糊 ≥ 1.5× | 1080p **1.61–1.86×**（11-07） | ✅ 达标 |
| M2 点算子融合后分配 = 0 | `perfpipe` 三条链 **7 / 39 / 8 KiB/op**；`perf16` 单算子 0–15 KiB/op | ❌ **前提不成立**：§5.4 明写跨步骤融合未实现。这不是「没测」，是功能不存在，不能用测数补救 |
| M3 量化 PSNR 对同参数 | **16.61 dB**，49152/49152 像素不同，平均差 31.32，最大通道差 115（Wu / 64 色 / 无抖动，256×192） | ⚠ 已出数，判定「算法/语义不同」。§11 未给阈值，**不能据此判达标或落后** |
| M3 抗锯齿覆盖率 | 无数值判据；`PathOpsTests` 只有 `partial.ShouldBeGreaterThan(0)` 这类存在性断言 | ❌ 判据未定义（无测法、无阈值），不可判定；M5 需先写死测法 |
| M4 10-bit HEIF → 16-bit PNG 端点精确 | **这条测不了**：HEIF 解码器是独立包 `Sdcb.SimdCodecs.Heif`，要启动时 `ImageCodecs.Register` 注册，本库不接入（见「实现边界」）；仓库也没有 10-bit HEIF 语料。现有 `Png_Rgba16_RoundTripsByteExact` 只证 16-bit PNG 往返字节精确 | ❌ 未出数，且**当前架构下无法出数**。不拿 PNG 往返冒充 HEIF 端点 |
| M4 8K 峰值内存 ≤ 2.5× 单帧 | 单帧 7680×4320×4 B = **126.56 MiB**，2.5× = **316.4 MiB**。实测 `resize-bicubic` 94.2（0.74×）、`gaussian-blur` **339.0（2.68×）**、`grayscale` 127.9（1.01×）、`invert` 127.6（1.01×）MiB | ⚠ **3 / 4 达标**，`gaussian-blur` 超 22.6 MiB（7.1%）；对手同项 265.9 MiB |

### 口径与限制

- `peakmem` 是**独立子进程冷启动 + 算子执行期 `WorkingSet64` 峰值增量 + 3 次中位数**，只覆盖 4 个单算子，
  **不是完整链路**；§1.3 第 3 条原测法写的是 `GC.GetTotalMemory` + WorkingSet，这里只用了后者。
  所以上表不能宣称完全兑现 §1.3 第 3 条，只能说「这 4 个场景的数」。
- 量化「同参数」只对齐**量化器族（Wu）+ 色数（64）+ 无抖动**；两边实现不同（我方 5-bit 三维矩 + 方差切分），
  16.61 dB 反映的是算法差异，不是质量胜负。对拍入口建在 `Parity.cs` 的 B4，刻意不进 `Cases.Build`，
  避免改变既有 13 场景吞吐表与 1.8224× 判定。
- §1.3 第 2 条「稳态分配 0 B/op」本轮同样**未达成**：流水线 7–39 KiB/op、Rgba16 单算子 0–15 KiB/op。
- 顺带出的数（不属 §11 出口条件，仅作证据）：`Rgba16` 端到端 **7 / 7 领先、几何平均 2.1003×**；
  流水线 **1.1074×（2/3）**，`point-5` 0.61× 落后；`parity` B1 17 场景中 5 项逐字节一致、6 项 ≤1 LSB、
  3 项视觉等价、3 项算法/语义不同；B2 对手对 `GaussianBlur(NaN)` 静默接受。

⚠ **三项仍未关闭**：8K `gaussian-blur` 超阈值、抗锯齿覆盖率判据缺失、HEIF 端点无语料。
这三项是验收欠账，不是功能缺失；但按 §11 口径，M2–M4 都还不能称完成。

## ImageSharp 替换：迁移阻塞清单（2026-11-11）

**用途口径**：本项目的目标是替换 ImageSharp，动机是其许可限制仍然存在（Split License 商用门槛 /
4.x 构建期校验许可密钥，见 `architecture.md:50`、`:696`）。即「做出来是干什么的」=
**给被这些限制卡住的 .NET 项目一个能换上去的处理层**。下面按迁移阻塞程度排序，只列事实与出处。

### 前置决策：两条现有立场与 drop-in 直接冲突

| 现有立场 | 出处 | 冲突点 |
|---|---|---|
| 「不为兼容其他项目改 API、不提供兼容模式/兼容参数」 | `known-gaps.md` 对标基线里「少」的部分 | 兼容外观、参数换算都属被明确排除的「兼容模式」 |
| 「命名、参数、默认值按我们自己的设计」 | `operators.md:196` | 同名 API 语义对齐被排除 |

**不先定这一条，后面 B1/B2 无从谈起**：要么推翻立场做 drop-in，要么接受「自主设计的替代品 +
迁移差异表」，明确告诉用户要改代码。两者不能混着说。

### B0 引用不了（环境前置）

| 事实 | 出处 | 处置 |
|---|---|---|
| 只 `net10.0`；ImageSharp 4.x 是 `net8.0` | `Directory.Build.props:4`、`architecture.md:735` | 建议**不追**多目标（会牺牲 `Vector512` 与 AOT 约束），写成迁移准入门槛 |

### B1 编译不过（API 形状）

| 事实 | 出处 | 迁移影响 | 处置 |
|---|---|---|---|
| `Mutate` 无实现；设计稿写了 `image.Mutate(x => ...)`，公开 API 里没有 | `architecture.md:366`、`known-gaps.md:25`、`docs/public-api.txt` | ImageSharp 绝大多数惯用调用点要重写 | 兼容外观，内部映射到现有算子 + `ProcessingPlan.Apply`；验收用 `AotOracle/Program.cs:11` 那行对手代码能编译并产出可对拍结果 |
| `Image` 无泛型 `Image<TPixel>`，只有三个 `WorkingFormat` | `architecture.md:230`、`docs/public-api.txt` | 依赖像素类型的泛型代码要重写 | **不追**（§5.1 是核心设计），列为迁移成本 |
| `Save/Encode` 是静态方法且参数顺序不同（`Image.Save(image, path)` vs `image.Save(path)`） | `docs/public-api.txt:157`、`:226` | 每个保存点改 | 加实例方法薄封装，只加不改 |
| `Resampler` 只有 5 种，4.1.3 公开 16 个 `KnownResamplers` 名称 | `src/SimdImaging.Processing/Geometry/Resampler.cs:5`、`known-gaps.md:161` | 用了 Mitchell / Lanczos5 / Spline36 等的代码编译不过 | 按 `operators.md:66` 的 16 种表逐项实现或**显式抛** `NotSupportedException`（不得静默回落） |

### B2 能编译但结果不同 —— 最危险，会静默出偏差

| 同名 API | 实测 | 出处 |
|---|---|---|
| `Brightness` | 加性 vs 乘性，PSNR **4.23 dB** | `known-gaps.md:162`、`results/2026-11-11.parity.md` |
| `Resize` nearest | PSNR **12.34 dB** | 同上 parity B1 |
| `HistogramEqualization` | PSNR **10.29 dB** | 同上 parity B1 |
| `Sobel` / `Vignette` / `BokehBlur` / `AdaptiveThreshold` | 语义或参数不同，无对等倍数 | `known-gaps.md:190` |

处置：把 `parity` 从 17 场景扩到**覆盖全部同名 API**，每项给 PSNR 与判定，全部列进迁移差异表；
**不调黄金、不改产品语义**（AGENTS.md 输出契约）。验收 = 差异表里没有一项是「未测」。

### B3 功能缺口（业务要改或另找方案）

| 事实 | 出处 | 性质 |
|---|---|---|
| 文字渲染 / 排版不做（ImageSharp 靠 `SixLabors.Fonts`） | `architecture.md:731`、`known-gaps.md:160` | **有意不做，决策级**：水印文字、验证码、图上标注全缺；卫星包规模「比整个 Processing 还大」 |
| `Save/Encode` 对 `FrameCount > 1` 抛 `NotSupportedException` | `known-gaps.md:151` | 未做：GIF/WebP 动图导出断 |
| EXR / TGA / CgBI PNG / ANI 缺 | `known-gaps.md:159` | **不归本库**，属 SimdCodecs；迁移前核对输入格式清单，不承诺补 |
| IPTC 语义编辑、图层/撤销栈、GPU | `known-gaps.md:13-16` | 有意不做 |

### B4 对外口径（不属代码，但会误导迁移方）

- ~~`README.md` 的分层图与额外关系说明~~：**已删（2026-11-11）**，开头只保留 `Sdcb.SimdCodecs` 依赖说明。
- ~~`README.md:67` 宣称「四档 SIMD 逐字节一致」~~：**已修**，改为「三档实现 + 一档预留」。
- ~~`README.md:68`、`:85` 写「稳态 0 分配」~~：**已修**，改为实测 7–39 KiB/op 并注明 `0 B/op` 目标未达成；README 不再写死测试数，避免与账本 589 漂移。

### 替换后拿到什么（收益侧，也是「为什么值得迁」）

MIT 免密钥、全库 NativeAOT 零 P/Invoke、16-bit 全链路不塌精度、跨 SIMD 档位逐字节一致、
13 场景 11/13 领先（1.8224×）、Rgba16 7/7 领先（2.1003×）、格式面多 JPEG2000 / JBIG2 / PNM 全族。
以及最根本的一条：**不需要那个许可**。

⚠ 清单里 B2 是迁移事故的高发区——编译能过、图看着差不多、但数值不同。它比 B1 的编译错误贵得多。
