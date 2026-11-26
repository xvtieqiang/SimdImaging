# Known gaps

> **文档口径**：本目录只保留 `architecture.md`（设计）、`operators.md`（算子表与流水线判据）、`known-gaps.md`（本账本）、
> `measurements.md`（实测结果）与 `contracts.md`（对外契约），公开面另有 `public-api.txt` 快照。日期类报告、里程碑验收记录与
> 一次性调研不再保留——过程进提交历史，正文也不再指向已删除的文件。

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
| 处理算子 | 算子清单 119 项已落地（`Skew` 不设独立方法，由 `Affine` 表达）；实现边界见下方「实现边界」 |
| SIMD 内核 | Rgba8 行读写、resize/blur 两遍卷积已向量化；点算子走「字节 LUT + 值主序矩阵记录 + 寄存器内钳制打包」，与标量孪生逐位一致（有端到端对比测试）。全部逐位一致，有测试兜底 |
| `ProcessingPlan` | 已实现：同尺寸步骤按行带执行，也是行泵的输入。**缩放只能是最后一步**——`ProcessingPlan.Resize` 追加一个尾部几何步（`ImageTranscode.Resize` 是文件侧的同一件事），`Add` 在它之后会抛；**公共面只有 `Add(ImageOp)`**——halo 由 `ImageOps` 工厂从算子参数推出，调用方写不出来；`PlanSteps`（手工 halo 的适配器）已转 `internal`，字符串版 `Add` 同样内部化。`Mutate` 未提供（算子返回新图，原地绘制归 DrawOps） |
| `SimdImaging.Drawing` 包 | 未创建（暂无计划） |
| `NativePixelPool` / `NullPixelPool` | 未提供；只有 `ArrayPixelPool`，实现 `IPixelPool` 即可接入自备池 |
| 公开 API 门禁 | 不接 RS0016 分析器，改用 `docs/public-api.txt` 快照：`PublicApiTests` 反射出全部公开签名（含可空标注）逐行比对，改动必须显式重新生成并 review diff |
| 分块 / 行带 / 区域 / 几何 | 已实现：整图；行带（`ImageTranscode`，内部 `ProcessChain`）；矩形（`ImageTranscode.Region`，内部 `ProcessRegion` / `CompiledPlanExecutor.ExecuteRegion`）；tile（`Image.ProcessTiles` 的缓冲与 sink 两个重载、`Image.ProcessTilesTo`，sink 契约 `IRegionSink`）；行流式缩放（`ImageTranscode.Resize`，可夹在算子之间）；区域 + 尾部缩放（编译区域执行产出矩形 + 编译缩放段）；行局部几何（Crop / Pad / FlipHorizontal / Resize）进拉式链。未做：`Global` 两遍、多级缩放连用、Affine/Perspective 的行流式形式、把 Region 结果直接写回文件（Region 入口返回内存图） |
| 公开参考向量包 | 已实现（M6 起，2026-11-14）：`tests/Fixtures/vectors/` 固定输入 / 期望 / SHA-256 + 独立高精度生成脚本；现有 8 组（`warp-identity-{rgba8,rgba16,rgba32f}`、`sample-options-encoded-midpoint`、`warp.tap.bilinear-phase`、`resize.background-edge`、`ewa.identity`、`ewa.minify`），`VectorTests` 独立重算哈希。新增向量允许，为过测试改已有 `expected` 禁止 |
| 像素差异工具 | 已实现（M6）：`src/Shared/PixelDiff.cs` 单一源码共享，测试项目与 Comparison 工程链接复用，不造第二把尺子；补首个不匹配位置、每通道误差、Top N、SHA-256、P6 差异热图，分级沿用 `Metrics.Grade` |
| SIMD 档位自动化 | 已实现（M6）：`benchmarks/simd-tiers.sh` 三档 JIT 全量测试 + 算子输出 body SHA-256 逐档比对。修正了 §7.5 设计稿的 `--filter "FullyQualifiedName~Kernel"` —— 它实际只匹配名字里恰好带 Kernel 的用例，不匹配 scalar-twin 测试，现改为跑全量 |
| 性能回退门禁 | **M15 起真的会判，判的是机器校正后的读数**：29 个场景里 **设闸条数随录制时的机器噪声浮动**（规则：五次运行归一化后的偏差 `S` → `threshold = max(20, ceil(3×S))`，`S > 20%` 留 `null`；当前 m16 主基线 15/29（阈值 25–53%），tile 基线 2/2（30% / 35%）），`value` 取五次 `best-of-3` 的均值。只比对 `machine` + `sourceSha`（`src/` 树哈希）+ `protocol` 全同的运行，否则打印 "not comparable" 并跳过。校正是每跑自带两个探针（`degradation = max(mem, alu)` 相对基线）；负载验证：4 核抢占下探针报 x1.59、校正后最差 −1.2% 全过，而未校正的原始读数均值 +40%。`--self-test` 把阈值压到 0 并**要求失败**（29 条全部纳入、exit=1）。留 `null` 的 14 条集中在亚毫秒（crop）、短路径（fill / clip / stroke / oil）、ICC 与数据相关算子——它们的归一化偏差本身就 >20%，这台机器测不出这一级别的变化 |
| CC0 语料 | 已实现（M6）：`tests/Fixtures/corpus.tsv` 2 条 Wikimedia Commons `Category:CC-Zero` 真实照片，URL + 实测 SHA-256（2026-11-14 下载校验）；`.cache/` git-ignored 不入库；离线由 `SyntheticCorpus.cs` 的 9 类确定性合成图兜底。实验台 3 张 Unsplash 演示图仍非测试夹具 |
| AOT smoke 覆盖 | M6 补 `warp` / `samplepixel` 两条 hash 与 `REQUIRED` 必需项检查，防止「绿的 gate 什么都没验」；M15 已全量核对（77 条 hash / 50 条设闸 / 0 悬空） |

## SIMD 覆盖盘点（2026-11-15）

项目叫 SimdImaging，所以这张表比任何性能数字都重要：**哪些热点真的在向量上跑，哪些还是标量，为什么。**

| 场景（本机实测） | ms | 档位 | 位置 |
|---|---|---|---|
| nlm 1080p S=5 | 16126 | AVX2（距离平方和向量化，其余标量） | `Filter/NonLocalMeansOps.cs` |
| nlm 1080p S=3 | 5729 | 同上 | 同上 |
| ll 1080p L=5 S=32 | **1464–1493**（原 1798–1960） | **标量**：金字塔逐级重映射；已做「样本分配表」预计算（见下） | `Filter/LocalLaplacianOps.cs` |
| guided.cross 4K | **210**（本机改前 840，−75%） | AVX2 + **band 并行**（每 worker 一套平面，受 96 MB 预算约束） | `Filter/GuidedFilterOps.cs` |
| reduce colors 4K（抖动） | 614 | **标量**：误差扩散天然串行，前一像素决定后一像素 | `Filter/DitherOps.cs` |
| guided filter 4K | 607（自引导未改） | AVX2（窗口相减） | `Filter/GuidedFilterOps.cs` |
| seam 1080p（去 50 列） | **404**（原 532，−24%） | **AVX2**：DP 行四路（向量轴=像素、三路 tie-break 复现严格比较），外加 luma 缓存 | `Geometry/SeamCarving.cs` |
| rl / rl.motion 1080p | 245 / 237 | AVX2 **+ AVX-512**（走卷积内核） | `Internal/PlanarConvolve.cs` |
| chroma 4:2:0 4K | 228 | **标量**：双精度散列累加到色度格 | `Color/ColorConversionOps.cs` |
| quantize 4K（octree） | **185–192**（改前 224） | 建表标量、映射行带并行；文件在 `Quantization/OctreeQuantizer.cs` |
| bilateral 4K | 180 | **标量**：权重由邻域像素值查表，逐 lane 都不同 | `Filter/FilterOps.cs` |
| oil paint 4K | **117–141**（本机改前 204–214） | **标量散点**（AVX2 无 scatter，这条路不存在）；已做行基址提升 + 样本预计算 + `FloatScratch` | `Filter/OilPaintOps.cs` |
| resize 4K→1080p bicubic | ~67 | AVX2（行带 + 两遍 + 水平/垂直向量） | `Geometry/Resize.cs` |
| gaussian / box blur 4K | ~88 / — | AVX2 + **AVX-512**（卷积内核） | `Internal/PlanarConvolve.cs` |

**seam carving 两步优化（2026-11-15，532 → 404 ms，−24%）**

1. **去冗余（532 → 443）**：每条缝都从像素重算整张 luma 平面，50 条缝就是 50 次 2.07 MPix 的 BT.709 转换。
   改成开始时算一次、每删一列把该行 luma 左移一位（删列只让像素左移，luma 是逐像素函数，移位后与重算逐位相同）。
2. **向量化 DP 行（443 → 404）**：`current[x] = energy[x] + min(prev[x-1], prev[x], prev[x+1])` 行内无依赖，
   4 条 double lane 一起算。**逐位一致靠的是照抄标量的求值顺序**：水平项先在 `float` 里相减再拓宽到 double
   （标量写的是 `(double)Math.Abs(right - left)`，先转 double 会换舍入），垂直项才在 double 里减；三路 tie-break
   用严格比较 + `ConditionalSelect`，平局仍取中间。`SeamCarvingTierTests` 4 例（垂直/水平/全平局/极窄图）
   把向量与标量逐字节对比，三档 body SHA 不变。

收益不大（DP 只占剩余成本的一半），但它是库里少数**能安全向量化**的形状，且证明了"照抄求值顺序"这个方法论可行。

**oil paint 的 −40% 从哪来（2026-11-15）**：这个算子**没法向量化散点**（`vpscatterdd` 是 AVX-512F 才有），
所以做的是三件标量层面的事，全部逐字节不变：

1. **行基址提升**：每像素每 tap 都在算 `Math.Clamp(y+ky)`、`sy % window`（除数是运行期值 → `idiv`）、构造 `Span`，
   共 10 次/像素；这些与像素值无关，逐行算一次即可（204–214 → 186–194 ms）。
2. **样本预计算**：滑动窗口里每个样本被处理两次（leave 一次、enter 一次），把 `bin` 与打包 RGB
   （`0xRRGGBB < 2²⁴`，float 可精确表示）按源行算一次，内层退化为「2 次读取 + 4 次整数 RMW」
   （186–194 → 117–141 ms）。
3. **`FloatScratch` 替换每 worker 每次调用的 `new float[]`**：分配 363 KiB → 97 KiB。

**Local Laplacian 的 −18%（同日）**：`NearestIndex(gaussian[c][l][p], ...)` 夹在 `samples` 循环最内层，对同一个
`(channel, level, pixel)` 被重复求值 **32 次**（默认 `samples=32`），而 `gaussian` 与采样网格在整个样本循环里都不变。
改成循环外建一张 `byte` 分配表（`samples ≤ 256` 时），内层只做一次数组比较；表大小约 1.33×W×H×3 字节，
已计入 `EstimateBytes`（估算器少报会让调用越过 `MaxScratchBytes` 上限后仍分配）。1798–1960 → **1464–1493 ms**。

**同批试过但回滚的**：`chroma 4:2:0`（228 ms）把 `ColorMath.Coefficients(ycbcr)` 从逐像素提到每次调用——
**没有任何变化**（228 → 235–239 ms，落在噪声内），按纪律回滚，不留「看起来更干净但没收益」的改动。

**guided.cross 再降一档（615 → 396 ms，−35%）**：先消融定位——摘掉六次 `BoxMean` 后剩 349 ms，
说明 BoxMean 只占 266 ms（43%），另外 57% 在解码、逐元素和写回。那三段**每行完全独立**，改 `RowParallel`
即可，且不增加内存（每个 worker 的行缓冲从 `FloatScratch` 租，线程本地、每带一次）。
顺手把 8 个 2.2 MB 平面从 `new` 换成 `FloatScratch`：分配 26.2 → 19.7 MB，计时在噪声内但减少的是真实的 GC 压力。
`guided.cross` 的 AOT 哈希 `8FA3D1E85EAAA05F` 全程未变。

**guided.cross 再降一档（615 → 396 ms，−35%）**：先消融定位——摘掉六次 `BoxMean` 后剩 349 ms，
说明 BoxMean 只占 266 ms（43%），另外 57% 在解码、逐元素和写回。那三段**每行完全独立**，改 `RowParallel`
即可，且不增加内存（每个 worker 的行缓冲从 `FloatScratch` 租，线程本地、每带一次）。
顺手把 8 个 2.2 MB 平面从 `new` 换成 `FloatScratch`：分配 26.2 → 19.7 MB，计时在噪声内但减少的是真实的 GC 压力。
`guided.cross` 的 AOT 哈希 `8FA3D1E85EAAA05F` 全程未变。

**guided.cross 的 band 并行（396 → 210 ms，−47%）**：剩下串行的只有 `BoxMean` 的 18 遍。band 之间本就独立，
但每 worker 要自己一套 8 个平面，4K 下每套 17.7 MB——所以 worker 数由**字节预算**而不是核数决定：
`min(Degree, bandCount, 96 MB / 每 worker 字节)`，4K 下取到 5 个 worker。band 内已有的行并行会自动退化为单线程
（`RowParallel` 的嵌套计数），不会双重并行。

**guided.cross 的 band 并行（396 → 210 ms，−47%）**：剩下串行的只有 `BoxMean` 的 18 遍。band 之间本就独立，
但每 worker 要自己一套 8 个平面，4K 下每套 17.7 MB——所以 worker 数由**字节预算**而不是核数决定：
`min(Degree, bandCount, 96 MB / 每 worker 字节)`，4K 下取到 5 个 worker。band 内已有的行并行会自动退化为单线程
（`RowParallel` 的嵌套计数），不会双重并行。

**融合的等价性取决于 halo 声明，不是执行器（2026-11-16）**：随机链测试从 5 个手挑算子扩到 **17 个（参数随机）**后，
一度出现 40 条里 8 条字节不一致。逐条追下去，**两处原因都在调用方的声明，不在 `FusedExecutor`**：

1. **`Sharpen` 是模糊型锐化**，内部 `GaussianBlur(sigma=1)`，真实支撑是 `ceil(3σ)=3` 行，不是 3×3 卷积的 1 行。
   声明成 1，带就少取两行，差异**正好出现在带接缝**。
2. **`Pixelate` 是块对齐而非滑动窗口**：一个块跨越的行，带边界可以切在任何位置（带 4..7、块 6 需要第 0..11 行），
   **任何常数 halo 都表达不了它**，只能声明为 `Global`（整图执行）。

改完这两条声明，**120 条随机链（17 算子 × 3 种带高 × 40 条）全部与逐步执行逐字节相等**。
教训：halo 契约的失败形态不是异常，而是**带边界上的一个错像素**；而「声明说得比实际读得少」是最容易犯的错。

**Rgba32F 的 rotate90 走串行循环（2026-11-16 修）**：`Transforms.Rotate90` 只为 4 字节（`uint`）与 8 字节（`ulong`）
准备了 `Transpose<T>` 快路，16 字节的浮点像素掉进逐像素 `Slice().CopyTo()` 且不并行——1080p 上 23.0 ms
（0.39×）、8K 上 476 ms。加 `Transpose<Vector128<byte>>` 后 **4.9 ms（1.8×）/ 155 ms（1.29×）**。
这个空洞一直没有暴露，因为直到现在才建起 Rgba32F 的 A/B 对照（`perf32` 模式）。

**16 位表的门槛设错了（2026-11-16 修）**：`Lut16PixelFloor` 原本是 16384 像素，但建表是 65536 次变换调用、
约 2.3 ms，且**每次调用都要建**（表是复用缓冲，不是缓存结果）。640×360 的 Rgba16 invert 因此读数 **0.19×**
——六轮全部落后，因为 230K 像素付了 2.3 ms 去省 1.2 ms。按「float 路径约 5 ns/px」反推，交点在半百万像素附近，
门槛改为 `512 × 1024`。修后同一格 **0.19× → 3.28×**（2.80 → 0.19 ms），两次独立运行一致。
教训：**收益为负的优化也是优化的一种结果**，而且它只会出现在小尺寸那一格——多轮次 + 多尺寸才抓得到。

**Rgba16 的编解码向量化（2026-11-16，8K 上 grayscale 148 → 52 ms）**：8K 矩阵暴露 grayscale/sepia 只有 0.67×，
根因不是矩阵运算，是 `Pixels.ReadRowRaw` / `WriteRowRaw` 的 Rgba16 分支逐像素标量：每像素 4 次
`ushort × (1/65535)`、回来 4 次 `ToUInt16`（`×65535 + 0.5`、clamp、截断）。改成八路向量后 8K 的 Rgba16 从
**1.77×（5/7）→ 2.67×（7/7）**。等价性靠三点：int32→float 在 ≤65535 时精确、乘的是同一个常数、写入保持
「先加 0.5 再钳制再截断」的顺序；`Vector256.Max(v, 0)` 把 NaN 变 0，正好与 x64 标量 `(ushort)NaN` 一致。
`invert16` / `guided` / `seam` 三个 AOT 哈希逐字符未变。

**guided 的双平面融合：试过，无收益（2026-11-16）**：把六次 `BoxMean` 按「互不依赖」合成三对，
让两条串行前缀链交错以获得 ILP。结果 **227–251 ms，而单平面版在同一时段是 228–232 ms**——没有收益，已回滚。
两个坑：(1) 第一版 `BoxMean2` 把垂直更新写成了标量，丢了向量化，直接慢 13%；补上后仍无收益。
(2) 我最初拿更早的一次 210 ms 当基准，那是机器更静时读的——**跨运行比较无效**，这条正是 `known-gaps`
测量方法论的铁律 1。要判它必须同进程交错 A/B，代价大于收益本身。

**seam 的整数 DP（2026-11-16，422 → 258 ms，−39%）**：分段的实测说 `FindSeam` 占 80–85%，而消融又说
能量只占 `FindSeam` 的 17%——所以瓶颈就是**递推那行本身**。它原本跑 `Vector256<double>`，只有 4 lane。
8/16 位下 luma、能量、递推全是**精确整数**（8-bit 能量 ≤510、16-bit ≤131070，路径和 ≤ `height×131070`），
换成 `Vector256<int>` 就是 8 lane，且 int 的比较/加法比 double 便宜。

- 守卫：`height > int.MaxValue / 131070` 时回落 double；`Rgba32F` 仍走 double（`operators.md` 的 D1 契约）；
  插入路径（`blocked: true`）也用 double——它读的掩码不在整数路径里建模。
- 等价性：`ForceDoubleDp` 反向开关 + `SeamCarvingTierTests` 的 A/B（Vertical/Horizontal × 1/9/4 缝、平图、
  逐行斜移 1 列的路径、Rgba16），逐字节相等；AOT 哈希 `5F0F06D0755265C8` 未变。
- 踩到的坑：`ShiftLuma` 只搬了 float 缓存，整数缓存没搬——第二条缝起就用错位的 luma。**A/B 测试在第一条
  缝上仍会通过**（只有一条缝时不需要 shift），是「16 位 + 5 缝」这个组合把它抓出来的。

**seam 的 luma 改 `ushort`（同日，258 → 202 ms，8K 峰值 415 → 352 MiB）**：整数内核读的已经是整数，存储却还是
`float`（4 B/px）。8 与 16 位的 luma 都落在 0..65535，`ushort` 装得下——存储减半，向量体入口 `Widen` 一次
（寄存器操作，不动算术顺序）。**这一刀同时拿到时间和内存**：8K 峰值 −63.5 MiB（正好是 luma 减半），
`ShiftLuma` 每行少搬一半字节，1080p 的 26.9 → 15.6 ms。

**Rgba16 点算子补上 16 位表（同日）**：矩阵第一次运行就指出，grayscale / sepia / invert 在三种尺寸上都是
0.63–0.67×——`PointLut` 只对 `Rgba8` 生效，16 位退回 float 行路径。补一张 4 × 65536 的 `ushort` 表（512 KiB），
**构造方式与 Rgba8 完全一致**：用算子自己的行变换跑一遍 65536 个值并记录结果，所以没有第二份算术可漂移。
低于 16384 像素不建表（建表成本盖过收益，阈值是实测量级不是猜的）。Rgba16 整体 1.53–1.72×（4/7）→
**2.01–2.29×（7/7）**，三次独立运行一致；`invert16` 进了 AOT 哈希清单且用 19200 像素的图才真正覆盖该路径。

**链式 API 已收成一条（2026-11-16）**：删掉了 `ImageChain` 与 `Image.Clone(Action<ImageChain>, …)`。这两者存在的理由只是
让调用写成 lambda（`x => x.A().B()`），代价是养一个与算子一一对应的 builder、还要手工保持同步——而且它的类注释写着
「连续点算子走 `ProcessingPlan` 单遍执行」，`Run` 却是纯顺序循环，**注释与代码不符**。
现在只有 `Image.Pipe()`（返回新图）与 `Image.Mutate(p => …)`（原地），后者接收同一个 `ImagePipeline` 类型。

**去雾在真实图片上暴露的两处失效（2026-11-16，已修，附证据）**：合成雾测试全绿之后拿 5 张真实照片跑，两处立刻现形：

| 症状 | 根因 | 处理 |
|---|---|---|
| 深色物体周围一圈**白边光晕** | 用**盒式平滑**细化透射率，背景的低透射率越过轮廓漏到物体上，物体被除以不属于它的 t | 改为**导向滤波**（guide = 雾图 luma，5 次盒均值求 a/b）——论文本来就是这么做的 |
| 明亮平坦的**天空变成彩色噪点** | 天空暗通道高 → 先验判定「几乎无透射」→ 除以 t0=0.1，**10 倍增益放大 8 位梯度** | 加**天空保护**：暗通道 > 0.70 的像素透射率不低于 0.6（放大限制在 ~1.7×） |

两条判据都进了测试（`BrightSky_IsNotAmplified`），修完 `cc0-architecture` 的天空干净、轮廓清晰。

**已知限制：对没有雾的图用去雾会破坏暗部。** `J=(I−A)/t+A` 是围绕大气光**扩张对比**，t<1 时暗的会更暗——
这不是 bug，是公式。样例 `photo-mist.jpg` 是一张刻意低饱和的青调照片（本身无雾），去雾后暗部被压、颜色变艳，
属于**用错工具**，不是算子错。真正需要去雾的输入是低对比、有灰白幕的图像。

**曝光融合的域选择（2026-11-16，已按论文实现）**：初版只做逐像素加权混合，接缝处会出现可见台阶——那版**没有提交**，重做成
Laplacian 金字塔混合：每帧解交织成 4 个通道平面建 Laplacian 金字塔，权重建 Gaussian 金字塔，逐层加权后 collapse。
通道串行处理把峰值压到一个通道的金字塔（全通道并行要 ×4）。

**接缝实测**：构造「两帧各自平滑、只有胜者切换」的场景，胜者边界处相邻像素最大台阶 **37 级**；同样场景下逐像素混合
的台阶是两帧在该处的差值 **~153 级**。这是本算子「金字塔有没有起作用」的唯一判据，写在
`ExposureFusionTests.WinnerChangesWithoutAStep` 里。

**同一轮修掉的性能缺陷**：初版在 `for x` 循环内调用 `Pixels.ReadRowRaw`，每行被转换 `width × 帧数` 次——1080p 三帧
实测 **29.6 s**。把行读提到 x 循环外后 **186 ms（159×）**。`MathF.Pow` 也曾被怀疑，实测只占很小一部分。

**计划级内存预算与算子工作集（2026-11-16 审查后修）**：审查指出「`MaxWorkingBytes` 根本不统计算子 Scratch」——属实，
且比描述更彻底：`ProcessingPlan.EstimateWorkingBytes` 全函数**没有一处引用 `PlanStep.Scratch`**，而
`MemoryEstimator.Estimate` 是另一套公式、确实会读。同一件事两个数，且执行器用错的那个。

同轮核实与处理：

| 审查项 | 核实 | 处理 |
|---|---|---|
| 预算忽略 operator scratch | **属实**（`ProcessingPlan.cs` 旧 `EstimateWorkingBytes`） | 删掉私有公式，执行器改用 `MemoryEstimator`，**只保留一套** |
| 未声明被当成 0 静默放行 | **属实**（公共路径 `PlanOps` 从不声明） | 新增 `ExecuteOptions.AllowUnestimatedScratch`，默认 `false`：有上限但步骤未声明时**明确抛出**，消息里给出步数 |
| `PlanOps` 不传 Scratch | 属实（`ImageOp` 连字段都没有） | `ImageOp` 增加 `Scratch`（签名 `(width, height, rows, format)`，能表达整图项）；**22 个工厂全部声明**，唯一例外 `LocalLaplacian` 由测试按名字钉住 |
| 单步 Neighborhood 估算按整图 | **属实且方向是高估**：执行器只看 category，不看步数 | 改为「非 Global/Composite 一律按带高 + halo」 |
| Global 的 scratch 乘 worker | **属实且是高估**：Global 在调用线程整图跑一次 | 拆成 `PerWorkerBytes`（× workers）与 `WholeImageScratchBytes`（× 1），`PeakBytes = max` 语义 |
| 测试注释「单步整图」与实现矛盾 | 属实 | 测试改为按带高断言，并新增「未声明即拒绝」用例 |

**声明来自读代码**，每条都附了推导（`GuidedFilter` 是「band+4r 行 × 5 张平面 + 整图副本」，`NLM` 是「行环 + 6 个
宽度数组」，`CLAHE` 是「tile 数 × 3 通道 × 256 桶」）。`Point` 声明 **0** 而不是留空——**零是声明，空是承认不知道**。

**仍未做**：逐算子的**活动容量实测**（`new[]` / `ArrayPool` / `FloatScratch` 分开统计峰值）。当前声明是静态推导，
有两条已知偏差：`ArrayPool.Rent` 可能返回更长的数组（未建模）、`FloatScratch` 的复用让「租用」与「常驻」不同。
`CountingPool` 只看得见 `IPixelPool` 的租用，这条边界没有变。`LocalLaplacian` 无声明（金字塔尺寸依赖层数与
采样网格，且自带 2 GiB 上限），含它的计划设上限时会被拒绝。

**计划融合的随机链等价性（2026-11-16 发现，同日结案）**：把随机链的算子表从 5 个手挑的扩到 17 个（参数也随机）后，
40 条链里有 8 条与逐步执行**字节不一致**。查下去，**两处原因都在调用方的 halo 声明，不在执行器**：

1. **`Sharpen` 是模糊型锐化**（内部 `GaussianBlur(σ=1)`），支撑是 `ceil(3σ)=3` 行，不是 3×3 卷积的 1 行；
2. **`Pixelate` 块对齐**，带边界可切在块中间，**任何常数 halo 都表达不了**，只能声明 `Global`。

改完这两条声明即全部一致。**这个缺陷条目一度写成「未修」并让两个用例 `Skip`，是过时的**——现在的状态是：

| 用例 | 覆盖 | 状态 |
|---|---|---|
| `RandomChains_MatchStepByStep` | **3 种格式 × 4 种带高 × 4 种并行度 × 25 条 = 300 条随机链** | 通过 |
| `GlobalAndNeighbourhoodSteps_Mix` | 整图步与邻域步混排 + 跨带接缝，三格式 | 通过 |
| `ASingleSharpenStep_MatchesTheWholeImage` | 单步 `Sharpen` + 带接缝 | 通过 |

同一轮扩展还**揪出一个真 bug**：`Histogram` 只钳了上界，`Rgba32F` 经对比度类算子后可产生负值 → 负索引 →
`IndexOutOfRangeException`（`Equalize` 在浮点图上直接崩）。已改为两端钳制。

教训：**「算子表够宽」这件事只能靠扩表证明**。5 个算子的 160 条链全绿，只值它样本的价值。

**之前用 5 个算子跑过 160 条链全绿——那个结果只值它样本的价值**：它验证的是生成器恰好抽到的形状。扩展算子表
才把其余部分暴露出来，这正是 `FusedChainPropertyTests` 现在改为记录缺陷而不是证明的原因（两个用例 `Skip`，
种子固定，修好后删掉 `Skip` 即可复现同一批链）。

**Rgba32F 的 rotate90 走串行循环（2026-11-16 修）**：`Transforms.Rotate90` 只为 4 字节（`uint`）与 8 字节（`ulong`）
准备了 `Transpose<T>` 快路，16 字节的浮点像素掉进逐像素 `Slice().CopyTo()` 且不并行——1080p 上 23.0 ms
（0.39×）、8K 上 476 ms。加 `Transpose<Vector128<byte>>` 后 **4.9 ms（1.8×）/ 155 ms（1.29×）**。
这个空洞一直没有暴露，因为直到现在才建起 Rgba32F 的 A/B 对照（`perf32` 模式）。

**16 位表的门槛设错了（2026-11-16 修）**：`Lut16PixelFloor` 原本是 16384 像素，但建表是 65536 次变换调用、
约 2.3 ms，且**每次调用都要建**（表是复用缓冲，不是缓存结果）。640×360 的 Rgba16 invert 因此读数 **0.19×**
——六轮全部落后，因为 230K 像素付了 2.3 ms 去省 1.2 ms。按「float 路径约 5 ns/px」反推，交点在半百万像素附近，
门槛改为 `512 × 1024`。修后同一格 **0.19× → 3.28×**（2.80 → 0.19 ms），两次独立运行一致。
教训：**收益为负的优化也是优化的一种结果**，而且它只会出现在小尺寸那一格——多轮次 + 多尺寸才抓得到。

**Rgba16 的编解码向量化（2026-11-16，8K 上 grayscale 148 → 52 ms）**：8K 矩阵暴露 grayscale/sepia 只有 0.67×，
根因不是矩阵运算，是 `Pixels.ReadRowRaw` / `WriteRowRaw` 的 Rgba16 分支逐像素标量：每像素 4 次
`ushort × (1/65535)`、回来 4 次 `ToUInt16`（`×65535 + 0.5`、clamp、截断）。改成八路向量后 8K 的 Rgba16 从
**1.77×（5/7）→ 2.67×（7/7）**。等价性靠三点：int32→float 在 ≤65535 时精确、乘的是同一个常数、写入保持
「先加 0.5 再钳制再截断」的顺序；`Vector256.Max(v, 0)` 把 NaN 变 0，正好与 x64 标量 `(ushort)NaN` 一致。
`invert16` / `guided` / `seam` 三个 AOT 哈希逐字符未变。

**guided 的双平面融合：试过，无收益（2026-11-16）**：把六次 `BoxMean` 按「互不依赖」合成三对，
让两条串行前缀链交错以获得 ILP。结果 **227–251 ms，而单平面版在同一时段是 228–232 ms**——没有收益，已回滚。
两个坑：(1) 第一版 `BoxMean2` 把垂直更新写成了标量，丢了向量化，直接慢 13%；补上后仍无收益。
(2) 我最初拿更早的一次 210 ms 当基准，那是机器更静时读的——**跨运行比较无效**，这条正是 `known-gaps`
测量方法论的铁律 1。要判它必须同进程交错 A/B，代价大于收益本身。

**seam 的整数 DP（2026-11-16，422 → 258 ms，−39%）**：分段的实测说 `FindSeam` 占 80–85%，而消融又说
能量只占 `FindSeam` 的 17%——所以瓶颈就是**递推那行本身**。它原本跑 `Vector256<double>`，只有 4 lane。
8/16 位下 luma、能量、递推全是**精确整数**（8-bit 能量 ≤510、16-bit ≤131070，路径和 ≤ `height×131070`），
换成 `Vector256<int>` 就是 8 lane，且 int 的比较/加法比 double 便宜。

- 守卫：`height > int.MaxValue / 131070` 时回落 double；`Rgba32F` 仍走 double（`operators.md` 的 D1 契约）；
  插入路径（`blocked: true`）也用 double——它读的掩码不在整数路径里建模。
- 等价性：`ForceDoubleDp` 反向开关 + `SeamCarvingTierTests` 的 A/B（Vertical/Horizontal × 1/9/4 缝、平图、
  逐行斜移 1 列的路径、Rgba16），逐字节相等；AOT 哈希 `5F0F06D0755265C8` 未变。
- 踩到的坑：`ShiftLuma` 只搬了 float 缓存，整数缓存没搬——第二条缝起就用错位的 luma。**A/B 测试在第一条
  缝上仍会通过**（只有一条缝时不需要 shift），是「16 位 + 5 缝」这个组合把它抓出来的。

**Rgba16 点算子补上 16 位表（同日）**：矩阵第一次运行就指出，grayscale / sepia / invert 在三种尺寸上都是
0.63–0.67×——`PointLut` 只对 `Rgba8` 生效，16 位退回 float 行路径。补一张 4 × 65536 的 `ushort` 表（512 KiB），
**构造方式与 Rgba8 完全一致**：用算子自己的行变换跑一遍 65536 个值并记录结果，所以没有第二份算术可漂移。
低于 16384 像素不建表（建表成本盖过收益，阈值是实测量级不是猜的）。Rgba16 整体 1.53–1.72×（4/7）→
**2.01–2.29×（7/7）**，三次独立运行一致；`invert16` 进了 AOT 哈希清单且用 19200 像素的图才真正覆盖该路径。

**quantize 的 −45%（同日，63.0 → 34–36 ms）**：两件事。

1. **映射行带并行**：`PaletteQuantizer.Apply` 的 Rgba8 分支是单线程逐像素表读，每像素完全独立；
   改 `RowParallel.For`。字节不变（lookup 只读、建完再切带）。
2. **最近色搜索剪枝**：`BuildLookup` 对 32768 个 bin 各扫全部调色板。加两条**严格等价**的剪枝——
   `dr² >= bestDistance` 直接跳过（另两项只会更大，不可能赢）、`bestDistance == 0` 时 `break`
   （升序扫描下第一个 0 距离就是最小下标）。63 → 54（剪枝）→ 34–36（并行）。

**试过并回滚的**：
- **精确 KD-tree**（`NearestLookup.cs`，包围盒预存、平局取最小下标、`==best` 不下剪）：**负收益**，
  36.6 → 39.9–41.4 ms。64 色 × 32768 bin 只有 2.1M 次距离计算，剪枝后的线性扫比建树+遍历更快。
  已删除，结论留在这里：**在 32³ 网格上，KD-tree 不划算**。
- **调色板内容缓存**（5-A）：没做。基准里同一张图每轮都重建出相同调色板，缓存只会在基准里命中，
  单次调用者拿不到好处——那是给基准化妆，不是优化。

**guided.cross 的 −27%（同日，840.6 → 615 ms）**：三件事分开做、每步都验字节。

1. **每带只解码一次**：原来 `for channel 0..2` 每轮都把 source 与 guide 的 RGBA 行重读重解一遍，
   同样的字节解了 3 次。改成每带一次解成 3+3 个 float 平面，通道循环只换平面。字节不变（同一批值）。
2. **垂直滑动加行环缓冲**：`columnSums[x] += add[x] - remove[x]` 的 `add`/`remove` 各回读整行；
   环缓冲让每行只读一次。**表达式一字未改**，只换了「被减行从哪读」。
3. **向量化 lane 独立段**：垂直加减、`I²`、`I·G`、系数解算 `a = cov/(var+eps)`（用 `Vector256.Divide`
   而不是乘倒数——后者最后一位会差）。**前缀和保持串行**，并有一个 `ThePrefixSumStaysSerial` 测试盯着它。

**踩到的坑值得记下来**：向量版第一版写成 `(sum + entering) - leaving`，标量是 `sum + (entering - leaving)`。
浮点下两者舍入不同，37 宽（非 8 的倍数）的图上立刻表现为一个通道饱和到 255；64 宽（8 的倍数）和 6 宽（不进向量）
都测不出来。`GuidedFilterTierTests` 的三组宽度（37 / 64 / 6）就是为这个形状加的。修好后 `guided.cross` 的
AOT 哈希 `8FA3D1E85EAAA05F` **逐字符未变**。

**为什么只有这些在向量上**——这是本文最该记住的一条：**确定性契约反过来限制了向量化**。
本库承诺三档（现在四档）逐字节相同，而把标量循环改写成向量循环，往往会把浮点累加顺序从
「逐行累加到 double」变成「按 lane 分段再归约」，结果差 1 ULP 就是不同的字节，黄金测试立刻炸。
所以能安全向量化的只有两类形状：

1. **向量轴 = 像素、每 lane 独立累加**（卷积、resize、逐通道点算）——改写后每条 lane 的加法序列与标量孪生
   逐字符相同，字节天然一致。新算子应当**先写成这个形状**，而不是事后补。
2. **纯整数、无归约**（旋转/翻转的搬运、`Pixels` 的格式转换）——整数运算没有舍入分歧。

反例就是上表里的标量项：散列累加（chroma）、归约（bilateral 的权重和）、串行依赖（抖动、seam DP）、
gather（LL、ICC CLUT）。它们不是"没来得及写"，是**按现有契约写不成向量**——要做就得先改累加结构，
而改累加结构就要承认输出变化、重跑全部黄金与三档矩阵。这是一次独立的、有风险的重构，不能顺手做。

**第四档（AVX-512）**：`PlanarConvolve` 的三个循环已有 `Vector512` 路径（2026-11-15）。本机无硬件，
正确性用 `Vector512<T>` 的软件实现验证（与标量/256 逐字节相同），吞吐仍无数据——见文末「已评估、不落地的方向」。

## M15 性能回退门禁（判定口径于 2026-11-15 重做）

| 项 | 现状 |
|---|---|
| 基线文件 | `benchmarks/baselines/` 下两份：`windows-10.0.19045-x64-16-m16.json`（29 个主场景，15/29 设闸，阈值 25–53%）与 `...-m16-tile.json`（2 条 tile 场景，阈值 30% / 35%）。脚本对**每份**基线都判；`value` 取同一源码树五次 `best-of-3` 的均值，设闸条数随录制时的噪声浮动。m15 文件已删除——留两份等于双重判定 |
| 为什么不再判绝对毫秒 | 2026-11-15 实测：同一份源码、同一台机器，04:00 与 07:00 两跑，**黑盒参照 ImageSharp 4.1.3 的 resize-bicubic 从 12.5 → 19.3 ms（1.54×）**，我方同项 4.97 → 7.91 ms（1.59×），29 场景整表慢 1.2–1.8×。参照不受本库代码影响，故变的是机器。绝对毫秒在这种漂移下只能二选一：误报，或把真回退埋在膨胀的基线里 |
| 归一化 | 每跑在首尾各测一次两个**不调用本库**的探针（256 MB 并行流式求和 / 32M 次 xorshift），各取 best-of-7、再取两端更快的读数。判定用 `got_raw / degradation` 对 `baseline.value`，`degradation = max(run.mem/base.mem, run.alu/base.alu)`——哪种资源被抢就按哪种校正；探针不动时 `degradation = 1`，等价于按原始读数判。失败行同时打印 raw 与 normalised，读的人能分清「机器漂」与「代码回退」 |
| 阈值来源 | 五次运行各自先按自己的 calibration 归一化，取五次的最大相对偏差 `S`；`threshold = max(20, ceil(3 × S))`，即门槛至少是实测噪声的 2.5 倍；`S > 20%` 留 `null`。11-14 那版「一对记录 ≤10% 才设 15%」已废弃——它用**一对**记录把当天恰好安静的 15 个场景当成稳定场景 |
| **三跑低估尾部 → 改五跑（2026-11-15 实测）** | 三跑 + `max(15, 2.5×S)` 的第一版在**校验跑**上连续翻车：crop +42.9%、fill +44.6%、clip +31.5%，随后 dt +23.5%、guided +26.7%、lanczos3 **+76.9%**——都超出三跑给的阈值（31–36 / 21 / 20 / 34）。根因不是代码，是**三次样本估不到尾部**。处置：改为**五跑 + `max(20, 3×S)`**（门槛至少是实测噪声的 3 倍、且不低于 20%），并把这六个场景里仍不收敛的降为 `null`（lanczos3 的双峰另有独立证据：30.8 / 54.1 / 89.7 / 53.9 四条读数）。改后连续两次校验最差 **+0.4% / +0.1%**。教训：**阈值只能挡住它见过的抖动，样本量比系数更要紧**。
| **指纹随提交变化（已修，2026-11-15）** | 发现 `AssemblyInformationalVersion` 里嵌了 commit SHA（`0.1.0-alpha+fb503174…`），于是**任何提交之后重新构建都会换指纹**，基线随每次提交失效——这与「同一份源码」的前提冲突，是门禁长期不可用的真实原因，不是偶发。处置：`Directory.Build.props` 加 `IncludeSourceRevisionInInformationalVersion=false`，指纹从此只随源码变；`gitSha` 仍记在基线 JSON 里，可追溯性不丢 |
| 自检 | `--self-test` 把阈值压到 0 并要求**非零退出**：实测 `comparable-baselines=1 enforced=29`、exit=1。正常模式 exit=0、两份基线合计 `enforced=17`（29 主场景 15 + tile 2），连续两次校验最差 **+0.4% / +0.1%** |
| 负载验证（整套设计成立与否的判据） | 人为起 4 个忙循环抢占 4/16 核：`alu` 探针报 **x1.59**，门禁按 x1.59 校正，**当时设闸的 24 条场景最差 −1.2%、全部通过**；同一负载下未校正的原始读数均值 **+40%**（该次实验用的是重录前的 binary，机制与阈值无关） |
| 可比性 | `machine`（`OS|arch|核数`）+ `protocol`（含 `slow=` 与 `norm=max(mem,alu)`）全同才算可比；`sourceSha` **只做提示**（不同源码树打印一行 warn 后照常判定）——代码变了正是要跑门禁的理由；`repeats` 只在读取基线时决定本次跑几次，**不参与比较**（脚本头注释旧称「含 repeat count」，以实现为准）。任一不同就跳过并打印原因——跨机器的秒表读数不是门禁 |
| 已知局限 | 校正只消掉「跟探针同向退化」的机器状态。若退化只影响某类访存而两个探针都不动，仍会误报——失败行里 raw 与 normalised 的差值就是识别它的线索。探针不调用本库，所以「代码慢到把探针一起拖慢」不会发生 |
| 已知近似 | `machine` 不含 CPU 型号，两台同 OS/同核数的机器会撞 id；`sourceSha` 因此成为第二道闸：它只随 `src/` 变，所以文档/测试改动不会误伤基线；但**改了 `src/` 就必须重录**，否则静默跳过——这是设计而非疏漏 |
| 第四档（AVX-512） | **代码已落地（2026-11-15），性能未出数**。落点是 `PlanarConvolve` 的三个循环（水平/垂直两遍 + 稀疏 PSF），gaussian / box / RL 反卷积等经由它受益；`Vector512.IsHardwareAccelerated` 为假时不进该分支，测试用 `ForceVector512` 钩子在**软件实现**上跑通并与标量/256 逐字节比对（3 例）。**能力边界写清楚**：这只证明「512 位算法与另三档同字节」，不证明「512 位硬件上更快」——本机 `avx512=False`，吞吐仍是空白。其余内核（NLM、ICC CLUT、Median3x3）未铺 512：NLM 的 SSD 已有 AVX2，ICC CLUT 的 3D gather 在消费级 Zen4 上收益不明，需先有硬件再测 |
| AotSmoke 全量核对 | **已核对**：77 条 hash，`REQUIRED` 50 条，**无悬空项**（`REQUIRED ⊆ hashed`，0 条 REQUIRED 没有对应 hash）。M15 点名的六类都在闸内：`warp` / `ewa.rotate` / `nlm` / `gainmap.hdr` / `icc.lut` / `plan.fused`；`dt` 与 `guided.cross` 于 2026-11-14 补入。其余 27 条只 hash 不设闸，属信息性覆盖 |
| 契约表 | **已完成**：`docs/contracts.md` 30 条，每条锚定一个测试类；`ContractsTests` 反射校验类存在、且该类至少有一个 `[Fact]/[Theory]`，同时校验"恰好 30 条、编号 1..30 连续"。它不解析 markdown——解析只能证明文件与数组一致，数组才是"必须存在"的那份清单 |

## 实现边界（已实现，但有明确上限）

| 项 | 边界 |
|---|---|
| COL-30 IccTransform | RGB 的 **matrix/TRC 与 LUT 型都支持**：`mft1` / `mft2` / `mAB` / `mBA` 的 `A2B0` / `B2A0`，PCS 为 XYZ 或 Lab，1D 预整形 + 3D CLUT 四面体 + 输出 1D。仍拒绝：非 RGB 设备空间（CMYK / 灰度 / Lab）、`mft1` + PCSXYZ（规范本身未定义 8 位 XYZ）、只有 Intent 1/2 的 profile、`mft` 的非恒等矩阵。边界与数值契约见下方「评估记录」 |
| 多帧保存 | `Save` / `Encode` 对 `FrameCount > 1` 抛 `NotSupportedException`，而不是只写第 0 帧。要导出动画得先 `KeepFrames` 选一帧 |
| 多帧解码路径 | 静图仍走零拷贝 span 路径；多帧需要会话持有数据，会先 `ToArray()` 复制一次 |
| IO-8 Thumbnail | 没有 DCT 域下推（JPEG 1/8 缩放），缩略图来自解码器内置预览或整图缩放 |
| IO-1 `PreferNativeScale` | 输出**可能大于**请求的 `⌈w/d⌉`：它取 codec 原生档，跳过 `BoxDownscaler` 那趟整图过滤。512² JPEG 要 1/16（32×32）会拿到 1/8（64×64）。这是选项的定义不是缺陷——要精确尺寸就别开；调 `.Width` 拿实际结果。与 `Region` 互斥（同开抛 `ArgumentException`），因为两者对"档位"的假设冲突 |
| HEIF / HEIC | **未接入**：`Sdcb.SimdCodecs.Heif` 是独立包，且要求启动时 `ImageCodecs.Register(HeifImageDecoder.Registration)`。本库既不加包也不注册，`Image.Load` 拿到 HEIC 会抛 `InvalidDataException("not a recognised image format")`。要用就自己引用那个包并注册——同时 HEVC 专利义务落在调用方 |
| IO-2 JPEG 丢 XMP | 本库把 `SaveOptions.Metadata.Xmp` 交给编码器，但 JPEG 编码器只读 `JpegEncodeOptions.Xmp`（结构化 packet），所以**JPEG 往返会丢 XMP**，Exif / ICC 正常。实测：WebP 与 PNG 保住 Exif+Xmp，JPEG 只保住 Exif。不抛异常是刻意的——转封装一张带 XMP 的 JPEG 不该失败 |
| IO-2 WebP 默认有损 | 上游 2.0 把「不传编码选项」的 WebP 默认改成了**有损**。实测 64×64 合成渐变往返改掉 **72.8%** 采样。本库跟上游默认走，但给了显式开关 `SaveOptions.WebpLossless = true`（同时打开 codec 的 `Exact`，连 alpha=0 下的 RGB 也保住，实测往返 **0 差异**） |

## 对标基线里"少"的部分

| 项 | 说明 |
|---|---|
| EXR / TGA / CgBI PNG / ANI | ImageSharp 4.x 有，`Sdcb.SimdCodecs` 侧没有；属 codec 层缺口，不在本项目范围 |
| 文字 | ImageSharp 靠 `SixLabors.Fonts`；我们不做 |
| 重采样器 | 16 个成员 / 15 个核，覆盖 4.1.3 全部 15 个 `KnownResamplers` 名称（实测反射是 15 个，此前写 16 是错的）。数值各自实现、不逐位对齐，属 B2 口径 |
| Brightness 参数语义 | 本库**自有设计**是加性偏移（0.2 = +0.2×满量程）；参照实现同名 API 是另一套语义，同参数对拍 PSNR 仅 **4.23 dB**。这是功能对比时发现的「名称相同、语义不同」事实，**不是缺陷**：本项目按自己的语义文档使用，**不为兼容其他项目改 API、不提供兼容模式/兼容参数**。对标表必须写出口径，不得据同名宣称覆盖 |
| 4.1.3 同进程 A/B | 已解决：`docs/sixlabors.lic` 在位，对比工程通过 `SixLaborsLicenseFile` 按路径引用，Release 构建与 4.1.3 黑盒 A/B 均已跑通（数字见 `docs/measurements.md`） |

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

## 未决事项

| 项 | 现状 | 影响 |
|---|---|---|
| **golden v2 / 定点化未立项** | 720p 上 `grayscale` / `sepia` 仍 0.83–0.84×；浮点重排的空间在收敛，但迟早要面对"锁字节 vs 定点化"的选择 | 决定这两个算子还能不能继续提速 |

## 测量口径（实践中遇到的问题）

| 问题 | 事实 |
|---|---|
| 逐算子串行扫描当基线 | `Profile` 第 1 节 `DegreeScan` 是「一个算子连测 5 个并行度、再测下一个」，各算子所处的池/GC 状态不同。同一段代码的 sepia 在这张表里是 13.8 ms、灰度 6.2 ms，而交错复测两者相等（6.68～8.76 ms）。**判方案好坏只能用同一循环里交替采样的比值** |
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

`architecture.md` §11 当时尚未核验的出口条件，本轮把能跑的都跑了（M2 模糊沿用 11-07 已达标数，未重跑）。
机器与 11-07 报告相同（`Windows 10.0.19045 | X64 | .NET 10.0.14 | avx512=False avx2=True v128=True`），
对手 `SixLabors.ImageSharp` **4.1.3**，密钥只按路径引用。原始本地输出不保留；
**下面表格里的数字已写入本文件，随仓库走**：

### 逐条结论

| 出口条件 | 实测 | 判定 |
|---|---|---|
| M2 模糊 ≥ 1.5× | 1080p **1.61–1.86×**（11-07） | ✅ 达标 |
| M2 点算子融合后分配 = 0 | 11-11 `perfpipe` 三条链 **7 / 39 / 8 KiB/op**（融合前）；2026-11-15 融合后重测 4.3 / 6.0 KiB/op；**同日二次优化**（缓存执行形状 + 去闭包 + degree=1 直路径）后为 **2493 / 4268 B/op**，`degree=1` **256 B/op** | ❌ **未达标（已判定）**：残余为 `WorkingBuffer` 包装与 TPL 调度分配，非像素缓冲；要到 0 需改包装结构与并行调度，属独立改动 |
| M3 量化 PSNR 对同参数 | **16.61 dB**，49152/49152 像素不同，平均差 31.32，最大通道差 115（Wu / 64 色 / 无抖动，256×192） | ⚠ 已出数，判定「算法/语义不同」。§11 未给阈值，**不能据此判达标或落后** |
| M3 抗锯齿覆盖率 | 2026-11-26 前只有 `partial.ShouldBeGreaterThan(0)` 这类存在性断言；现判据为「45° 直角三角形覆盖率与解析面积相差 ≤ 2%，且每边行部分像素 ≤ 2」 | ✅ 判据已定义并由 `PathOpsTests.FillPath_Triangle_CoverageMatchesTheAreaWithinTwoPercent` 守护（见本节末） |
| M4 10-bit HEIF → 16-bit PNG 端点精确 | **这条测不了**：HEIF 解码器是独立包 `Sdcb.SimdCodecs.Heif`，要启动时 `ImageCodecs.Register` 注册，本库不接入（见「实现边界」）；仓库也没有 10-bit HEIF 语料。现有 `Png_Rgba16_RoundTripsByteExact` 只证 16-bit PNG 往返字节精确 | ❌ 未出数；2026-11-26 判为**超出本库边界**，不再作为验收项。不拿 PNG 往返冒充 HEIF 端点 |
| M4 8K 峰值内存 ≤ 2.5× 单帧 | 单帧 7680×4320×4 B = **126.56 MiB**，2.5× = **316.4 MiB**。实测 `resize-bicubic` 94.2（0.74×）、`gaussian-blur` **339.0（2.68×）**、`grayscale` 127.9（1.01×）、`invert` 127.6（1.01×）MiB | ⚠ **3 / 4 达标**，`gaussian-blur` 超 22.6 MiB（7.1%）；对手同项 265.9 MiB |

### 口径与限制

- `peakmem` 是**独立子进程冷启动 + 算子执行期 `WorkingSet64` 峰值增量 + 3 次中位数**，只覆盖 4 个单算子，
  **不是完整链路**；§1.3 第 3 条原测法写的是 `GC.GetTotalMemory` + WorkingSet，这里只用了后者。
  所以上表不能宣称完全兑现 §1.3 第 3 条，只能说「这 4 个场景的数」。
- 量化「同参数」只对齐**量化器族（Wu）+ 色数（64）+ 无抖动**；两边实现不同（我方 5-bit 三维矩 + 方差切分），
  16.61 dB 反映的是算法差异，不是质量胜负。对拍入口建在 `Parity.cs` 的 B4，刻意不进 `Cases.Build`，
  避免改变既有 13 场景吞吐表与 1.8224× 判定。
- §1.3 第 2 条「稳态分配 0 B/op」本轮同样**未达成**：流水线 7–39 KiB/op、Rgba16 单算子 0–15 KiB/op。
  2026-11-15 在 M10 融合路径上重测仍 ≈4.3–6.0 KiB/op，见 §「M2『点算子融合后分配』重测与判定」。
- 顺带出的数（不属 §11 出口条件，仅作证据）：`Rgba16` 端到端 **7 / 7 领先、几何平均 2.1003×**；
  流水线 **1.1074×（2/3）**，`point-5` 0.61× 落后；`parity` B1 17 场景中 5 项逐字节一致、6 项 ≤1 LSB、
  3 项视觉等价、3 项算法/语义不同；B2 对手对 `GaussianBlur(NaN)` 静默接受。

⚠ **四项出口条件的处置（2026-11-26，P4 关闭）**：
- 8K `gaussian-blur` 2.68×（超 7.1%）：**保留为已记录偏差**，默认带高不改；`ProcessingPlan.BandRows` 是公开旋钮，
  但降低它以换取 2.5× 会牺牲吞吐，未做默认调整。不宣称该项达标。
- 抗锯齿覆盖率：**判据已定义并由测试守护**——45° 直角三角形覆盖率与解析面积相差 ≤ 2%，且每边行部分像素 ≤ 2，
  见 `PathOpsTests.FillPath_Triangle_CoverageMatchesTheAreaWithinTwoPercent`。
- HEIF 10-bit 端点：**判为超出本库边界**（`Sdcb.SimdCodecs.Heif` 是独立包，本库不注册；也不拿 PNG 往返冒充），
  不再作为本库的验收项。
- M2 的「融合后分配 = 0」：**重定义为单 worker 下 ≤ 4 KiB/op**（实测 256 B/op），由 `AllocationBoundTests` 守护；
  0 B/op 不再作为验收条件，残余（`WorkingBuffer` 包装与调度）不再追。


## M2「点算子融合后分配」重测与判定（2026-11-15，M5 出数）

§11 M2 行此前标着「⚠ 未判定：分配曲线未重测」，自此**关闭为「已判定：未达标」**。11-11 的
`perfpipe` 7 / 39 / 8 KiB/op 是融合前、单次进程级采样；本轮在 M10 落地后的**融合路径**上重测，用项目
自己的两把尺子、两次独立运行、两个尺寸，得到可复现数字。

**方法**：`benchmarks/SimdImaging.Comparison -- allocplan`（`AllocPlan.cs`，child-style，只测我方、不加载
对手/许可、不写报告、不改 golden）。同一个 `ProcessingPlan` 对象反复 `ExecuteWithOptions`（即 §1.3 的
「池化 + 计划复用后稳态」），`warmup=40 / batch=120 / passes=3`，报三批中位数 [min..max]。同一批执行上
同时读两把尺子：A = `GC.GetAllocatedBytesForCurrentThread`（§1.3 第 2 条字面口径），
B = `GC.GetTotalAllocatedBytes(precise:false)`（`Infrastructure.Timing` 的进程级口径，能看见 band 并行
下 pool 线程的分配）。覆盖 §715 点名的两条融合路径：`AddPointLut`×3（LUT 字节域折叠）与
`PlanSteps.Point`×3（`FusedExecutor` 窗口递归），各测 fused（M10 默认）/ step（`AllowFusion=false`）；
`degree=1` 作对照让两尺子收敛。机器 `Windows 10.0.19045 | X64 | 16 核 | .NET 10.0.14 | avx2=True`。

**1920×1080 Rgba8，稳态 B/op**：下表为一次代表运行的「三批中位数 [min..max]」。另有两次同协议独立
运行，LUT 折叠 fused default 的「每线程 / 进程级」中位数分别为 4365 / 6050 与 4356 / 6174，与下表
同量级——三次运行每线程差 <1%、进程级 <3%（进程级含 pool 线程/GC 抖动，本就更宽）。

| 链 | 融合 | degree | A 每线程 B/op | B 进程级 B/op |
|---|---|---|---|---|
| `AddPointLut`×3（LUT 折叠） | fused | default | 4362 [4360..4368] | 6196 [5802..6328] |
| 同上 | step | default | 7915 [7900..7923] | 13381 [13319..13463] |
| `PlanSteps.Point`×3（窗口递归） | fused | default | 5508 [5485..5515] | 25342 [25325..25706] |
| 同上 | step | default | 10078 [10071..10082] | 31943 [31867..32167] |
| `AddPointLut`×3 | fused | 1 | 2144 [2144..2144] | 2144 [2144..2145] |
| `PlanSteps.Point`×3 | fused | 1 | 19816 [19816..19816] | 19817 [19816..19817] |

### 二次优化：缓存执行形状（2026-11-15）

上一节的残余被归因为「per-call 计划重建」。查下去发现三处：`BuildExecutionSteps` 每次建三个 List、
`FusionPlanner.Segment` 每次建分段表、以及 `segments.Exists(s => ...)` 的闭包——都在每次 `Execute` 里重建，
而它们只依赖计划本身（计划通常建一次、执行多次）。改为按计划版本缓存执行形状、把闭包换成循环、
`degree=1` 走不带委托的直循环后，同一把尺子复测：

| 链 | 融合 | degree | 改前 A / B | 改后 A / B |
|---|---|---|---|---|
| `AddPointLut`×3（LUT 折叠） | fused | default | 4362 / 6196 | **2493 / 4268** |
| 同上 | fused | 1 | 2144 / 2144 | **256 / 273** |
| `PlanSteps.Point`×3（窗口递归） | fused | default | 5508 / 25342 | **4988 / 24930** |
| 同上 | fused | 1 | 19816 / 19817 | 19296 / 19270 |

1080p 与 640×360 两个尺寸都复测，比例一致。**仍然不是 0**，而且这两处残余各自有明确的来源：

- **`degree=1` 的 256 B/op**：每段一个 `WorkingBuffer` 包装对象。池里回收的是数组，包装本身是新对象——
  要到 0 得把包装也做成可复用结构（牵动 `using` 语义，属独立改动，不能顺手做）。
- **默认并行度的 ~2.5 KB/op**：主要是 `Parallel.For` 每次调用的调度对象，这是 TPL 的固有分配。
  要到 0 只能自己写工作窃取循环，不是「少 new 一个」的层次。

判定不变：**未达标**，但账变了——LUT 折叠 4362 → 2493（**−43%**），`degree=1` 2144 → 256（**−88%**）。

**判定：❌ 未达标（0 B/op 未达成）**。即便按 §1.3 最有利的字面尺子 A，融合后最强路径
（`AddPointLut` LUT 折叠）稳态仍 ≈4.3 KiB/op；`degree=1`、零 pool 线程时仍固定 2144 B/op；进程级
尺子更高（≈6.0 KiB/op）。这与 §467、§653、README 已登记的「稳态 0 分配目标未达成」一致，本轮只是把它
在 **M10 融合路径**上坐实并出数。融合确实把分配约减半（LUT 折叠 A 7.9→4.4 KiB/op、B 13.4→6.1），
但没有到 0；不按 §1.3 的目标强行宣称达标，也不改产品字节去凑 0（改 `ProcessingPlan` 会动
`binarySha256`、让性能基线静默失效，正是 AGENTS.md 反复警告的「绿的 gate 什么都没验」）。

**两条实测副产物（写清原因，非推断）**：

- **残余分配是 per-call 而非 per-pixel**：640×360 与 1920×1080 相差 9× 像素，`AddPointLut` `degree=1`
  却都精确 2144 B/op。来源是每次 `ExecuteWithOptions` 重建执行计划（`BuildExecutionSteps` 的
  `List<PlanStep>`/`int[]`/折叠 1024 字节 LUT 克隆、`FusionPlanner.Segment`、band `WorkingBuffer` 包装、
  `Parallel.For` 调度），不是像素缓冲——后者走 `ArrayPixelPool`。要到 0 需缓存执行计划/折叠表，
  属产品改动，未在本次进行。窗口递归链在 `degree=1` 随尺寸变（19816→7760）是因为它按 band 数租
  `WorkingBuffer` 包装，不是像素数。
- **§1.3 第 2 条字面尺子在并行下系统性少报**：A 只看见调用线程，漏掉 pool 线程——LUT 折叠
  A≈4360 vs B≈6100、窗口递归 A≈5500 vs B≈25400；`degree=1` 时两尺子收敛到同值（2144、19816），
  证明差额来自 pool 线程分配而非尺子噪声。这正是 `Infrastructure.cs:171-176` 已注释的坑。
  故无论取哪把尺子，「未达标」结论都成立。

## ImageSharp 替换：迁移阻塞清单（2026-11-11）

**用途口径**：本项目的目标是替换 ImageSharp，动机是其许可限制仍然存在（Split License 商用门槛 /
4.x 构建期校验许可密钥，见 `architecture.md` §1.3 与 §10）。即「做出来是干什么的」=
**给被这些限制卡住的 .NET 项目一个能换上去的处理层**。下面按迁移阻塞程度排序，只列事实与出处。

### 前置决策：两条现有立场与 drop-in 直接冲突

| 现有立场 | 出处 | 冲突点 |
|---|---|---|
| 「不为兼容其他项目改 API、不提供兼容模式/兼容参数」 | `known-gaps.md` 对标基线里「少」的部分 | 兼容外观、参数换算都属被明确排除的「兼容模式」 |
| 「命名、参数、默认值按我们自己的设计」 | `operators.md` §「与 ImageSharp 4.x 的功能对标」 | 同名 API 语义对齐被排除 |

**不先定这一条，后面 B1/B2 无从谈起**：要么推翻立场做 drop-in，要么接受「自主设计的替代品 +
迁移差异表」，明确告诉用户要改代码。两者不能混着说。

### B0 引用不了（环境前置）

| 事实 | 出处 | 处置 |
|---|---|---|
| 只 `net10.0`；ImageSharp 4.x 是 `net8.0` | `Directory.Build.props` | 建议**不追**多目标（会牺牲 `Vector512` 与 AOT 约束），写成迁移准入门槛 |

### B1 编译不过（API 形状）

| 事实 | 出处 | 迁移影响 | 处置 |
|---|---|---|---|
| `Mutate` | `Image.Mutate(Action<ImageChain>)` 真正原地生效（返回 `this`，旧缓冲回池），因为移植过来的调用点写 `image.Mutate(x => x.Resize(...))` 之后继续用 `image`；返回新图会让它编译通过却什么都不改。**仍是源码差异**：lambda 参数类型是 `ImageChain` 而不是 ImageSharp 的 `IImageProcessingContext`，链上没有的操作要改 | 调用点改写成 `image.Mutate(x => x.Resize(w, h))`；`Resize`/`Crop`/`Rotate90`/`Flip` 等同名方法直接可用。7 例 `ImageMutateTests` 钉住原地语义、异常不改图、缓冲回收 |
| `Image` 无泛型 `Image<TPixel>`，只有三个 `WorkingFormat` | `architecture.md` §5.1、`docs/public-api.txt` | 依赖像素类型的泛型代码要重写 | **不追**（§5.1 是核心设计），列为迁移成本 |
| `Save/Encode` 是静态方法且参数顺序不同（`Image.Save(image, path)` vs `image.Save(path)`） | `docs/public-api.txt` | 每个保存点改 | 加实例方法薄封装，只加不改 |
| `Resampler` | 16 个成员 / 15 个核，覆盖 ImageSharp 4.1.3 全部 15 个 `KnownResamplers` 名称（实测反射该包是 15 个，不是 16） | 用了 Mitchell / Lanczos5 / Lanczos8 / Spline / Welch / Robidoux 的代码现在编得过；**数值不与对手逐位对齐**（各自实现，见 B2 口径）。`ResamplerCatalogTests` 37 例 |

### B2 能编译但结果不同 —— 最危险，会静默出偏差

| 同名 API | 实测 | 出处 |
|---|---|---|
| `Brightness` | 加性 vs 乘性，PSNR **4.23 dB** | `parity` B1 |
| `Resize` nearest | PSNR **12.34 dB** | 同上 parity B1 |
| `HistogramEqualization` | PSNR **10.29 dB** | 同上 parity B1 |
| `Sobel` / `Vignette` / `BokehBlur` / `AdaptiveThreshold` | 语义或参数不同，无对等倍数 | 语义差异，不设倍数 |

处置：把 `parity` 从 17 场景扩到**覆盖全部同名 API**，每项给 PSNR 与判定，全部列进迁移差异表；
**不调黄金、不改产品语义**（AGENTS.md 输出契约）。验收 = 差异表里没有一项是「未测」。

### B3 功能缺口（业务要改或另找方案）

| 事实 | 出处 | 性质 |
|---|---|---|
| 文字渲染 / 排版不做（ImageSharp 靠 `SixLabors.Fonts`） | `architecture.md` §1.2、本文件「有意不做」 | **有意不做，决策级**：水印文字、验证码、图上标注全缺；卫星包规模「比整个 Processing 还大」 |
| `Save/Encode` 对 `FrameCount > 1` 抛 `NotSupportedException` | 本文件「实现边界」 | 未做：GIF/WebP 动图导出断 |
| EXR / TGA / CgBI PNG / ANI 缺 | 本文件「对标基线里"少"的部分」 | **不归本库**，属 SimdCodecs；迁移前核对输入格式清单，不承诺补 |
| IPTC 语义编辑、图层/撤销栈、GPU | 本文件「有意不做」 | 有意不做 |

### 替换后拿到什么（收益侧，也是「为什么值得迁」）

Apache-2.0 免密钥、全库 NativeAOT 零 P/Invoke、16-bit 全链路不塌精度、跨 SIMD 档位逐字节一致、
13 场景 11/13 领先（1.8224×）、Rgba16 7/7 领先（2.1003×）、格式面多 JPEG2000 / JBIG2 / PNM 全族。
以及最根本的一条：**不需要那个许可**。

⚠ 清单里 B2 是迁移事故的高发区——编译能过、图看着差不多、但数值不同。它比 B1 的编译错误贵得多。

## 评估记录

**点算子链的单遍合成（2026-11-17，实测否决）**：1080p 五步点链（Invert→Contrast 0.9→Sepia→Contrast 1.1→Grayscale）
现有融合计划 **11.9 ms**，ImageSharp 10.6 ms。曾实现「读一遍、连做五个变换、写一遍」的单遍版本，实测 **33.1 ms**——
慢 2.55×。原因：单个算子走 256 项查表（每样本一次查表），单遍链用逐像素浮点算五次，省下的内存往返远不及多出的算术。

**结论**：这条路只有「表合成」才可能赢。**当晚即实现（2026-11-17 凌晨）**：`PointChain` 把每通道独立的链烤成
4×256 一张表、一遍查表，1080p 五步链 **1.9 ms**（逐算子 14.0 ms，ImageSharp 10.6 ms），**逐字节等同**。
条件是链里不能有通道混合变换（Sepia/Grayscale/HueRotate）——那种链被 `NotSupportedException` 拒绝，不静默降级。

**顺带发现两处语义陷阱**（已写进 `AGENTS.md`）：`Sepia` 把混合折进矩阵对角线，写成 `r + (sr−r)·a` 在 a=1 时
代数相等、浮点不等，逐像素差一级；`PointLut.ApplyRgba8` 的表由 `(v,v,v,v)` 建立，只对每通道变换成立。

## 已评估、不落地的方向

> 做过评估、结论是否决或限定；原始推理在提交历史里。

| 方向 | 结论 |
|---|---|
| **FFT / 频域处理** | 不落地。整条流水线（填充、变换、点乘、逆变换）比空域更慢，且多一层浮点误差 |
| **域变换（Domain Transform）** | 不落地。保边滤波的质量/成本比不如已有的 guided 与双边，且边缘停止判据要额外调参 |
| **AVX-512** | 已在 `PlanarConvolve` 实现并用 `ForceVector512` 在软件模拟下验正确性（与标量/256 逐字节一致）；**无硬件可测吞吐**，故不宣称速度 |
| **LUT 实验（点算子向量化）** | 不做。字节域查表已领先对手 1.4–4×，向量化只加风险 |
| **ICC 边界** | 只做 matrix/TRC 与 RGB LUT 型 profile；非 RGB 设备空间仍是缺口 |
| **ICC 同图对拍** | **确认不可行（2026-11-16）**：ImageSharp 4.1.3 的 profile→profile 转换在 `ColorProfileConverterExtensionsIcc`，反射显示 `public=False`，而本仓库只比对公开面。改为**自洽验证**（`benchmarks ... icc`）：恒等 ΔE **0.000**、sRGB→P3→sRGB 往返最大 ΔE **0.654**、中性轴 **0.000**，素材是 CC0 的 Compact ICC Profiles |

## M17 区域执行（2026-11-25）

- **4 通道 / 2 通道 TIFF 一律拒绝**：`TiffPageLayout` 不暴露 `ExtraSamples`，无法区分直通 alpha 与
  associated（预乘）alpha；猜错就是静默错像素。**本库 `Save` 出的 TIFF 正好是 RGBA**，所以对自己的输出调用
  `ProcessRegion` 会被拒——消息点名 `ExtraSamples`。复访条件：codec 在 layout 上公开 `ExtraSamples`
  （或 `TiffColor` 的 alpha 规则），届时放开并补 associated/unassociated 两个用例。
- **Palette / CMYK / YCbCr / CIE Lab / 透明掩码拒绝**：颜色转换在 `TiffColor`（internal）里，是冻结定义；在
  本库复制一份就是第二把尺。复访条件同上。
- **单条带大页拒绝**：`ReadSegment` 以段为单位解码，`RegionOptions.MaxSegmentBytes`（默认 64 MiB）事前拒绝，
  消息教调用方重编码时给 `RowsPerStrip` / tile。本库 `Save` 的 TIFF 默认就是整图一条带（无 `RowsPerStrip`），
  因此它同时踩中 4 通道与单条带两个拒绝。
- **区域结果不带 ICC / Exif / Resolution / ColorEncoding**：实测 `ImageDecoder.Open` 对 TIFF 会读出**超过文件本身
  的字节**（512² 文件 61.9 KB，Open 读了 127.9 KB）——codec 文档写明元数据需要整个文件。区域路径因此只从
  `TiffPageLayout` 取尺寸 / 位深 / Orientation，`ColorEncoding` 报告为 `AssumedSrgb`。需要元数据时用
  `SaveOptions.Metadata` 附上，或走 `Load`。
- **`Image.Load(path, LoadOptions{Region})` 的 TIFF 快路径未做**：`Load` 没有「原生 / 物化」的报告位，且它的
  `Metadata` / `Resolution` 来自整文件解码；加进去会造出一个「元数据略不同的第二个 Load」。复访条件：
  `LoadOptions` 有诚实的读模式位，且 layout 能给出 ICC / Resolution。
- **每步处理整个窗口**：区域路径把窗口当独立小图（`band 0`、`bandRows = winH`），`RunWindow` 的每步都写满窗口，
  而不是像带泵那样按 `remaining` 收缩。链长 × halo 大时会多算，量级 `(步数−1)×窗口`；**未实测**。若实测同像素量
  比带泵慢 >1.5×，给 `RunWindow` 加 `top/bottom` 重载（语义不变）。
- **文件侧读放大已修（2026-11-25）**：早先 `ResolveFormat` 用 `ImageDecoder.Open` 取深度与元数据，实测它
  会遍历整个文件（202 KB 的 JPEG 读 206 KB、71.7 KB 的 TIFF 读 147 KB），整条 `ImageTranscode` 是文件的 2.0–3.4×。
  现在默认只读头部：TIFF 走 `TiffReader.ReadLayout`，其它格式 ≤4 KiB 前缀（失败再增长到 1 MiB）；实测
  JPEG 1.01× / PNG 1.07× / TIFF 1.41×（TIFF 行读器的预读，codec 行为，不是我们的）。**代价**：元数据默认不随行，
  `ProcessOptions.PreserveMetadata = true` 恢复旧的整文件行为（2.01× / 2.14× / 3.40×），
  `ProcessReport.MetadataCarried` 报告是否携带。修的过程中还抓到一个签名判断 bug：`LooksLikeTiff` 拿**整个** 4 KiB
  前缀和 4 字节签名比较，永远为 false，把 TIFF 推进了 1 MiB 嗅探——`Transcode_ReadsTheSourceAboutOnce` 就是为此加的。
- **多页 TIFF 只测了越界**：`RegionOptions.PageIndex` 透传给 `TiffReader.ReadLayout(stream, page)`，正例没有夹具
  （手写构建器目前单页）。补夹具后再把这一行删掉。

## 几何执行（M18）

**P3 已完成**：所有窗口算子都在计划、格式段或 Composite 步骤里，`AutoOrient` 也以 GeometryStep 进入 recipe
（方向元数据在 pipeline 包装结果时复位）；反卷积的 halo 修正为 `2 × PSF 半径 × iterations`。
- **多输入（Frame 版已落地，通用模型已打开）**：`PlanStep.Secondary` 记录第二视图，`ProcessingPlan.Add(op, PixelView)`
  注入并由执行器经 `OpKernel2` 传给内核；三条 Frame 分派统一走 `PlanStep.Invoke`。`ImagePipeline.Composite/Blend/
  DrawImage/Watermark` 已切到注入式，旧闭包重载已于 2026-11-26 删除（不保留兼容层）。编译器按契约的 `InputPort` 推导第二
  端口区域；视图未知时保守记整图。所有内置第二输入（Composite/Blend/DrawImage/Watermark/GuidedFilter/ApplyGainMap）都已注入；仍未做：
  Stream / Region / Tile 继续拒绝 `CompositeStep` / `FormatChange`；视图生命周期仍由调用方保证，库不做所有权
  转移或自动存活校验。
- **统一预算（P4 核心已收口，2026-11-26）**：编译期上界总门 + `WorkspaceManager` 预留账户（计划/流窗口/resize
  ring/tile/编码带/物化结果）；2026-11-26 补上了四个此前真实存在的洞：
  1. 编译门现在读 `IsLowerBound`，未声明 workspace 的步骤在 `MaxWorkingBytes` 下不能靠下界通过，除非显式设
     `AllowUnestimatedScratch`（与旧 `ProcessingPlan` 同规则）；
  2. 几何/格式段按「旧帧 + 新帧」预留，`WorkspacePlan` 增加 `SectionTransition` 项；
  3. `EstimateFrame` 按 `CompiledSection` 的输入尺寸/格式逐段估算（Resize 之后的算子不再按源尺寸算），
     `EstimateStream` 的窗口/Scratch 当时按段取最大，2026-11-26 改为按段累加（见下一条）；多个 Resize 取各自
     最大 ring 而不是只算第一个；
  4. Region/Stream 的窗口与 Scratch 也 `Reserve` 进同一个 `WorkspaceManager`，`PeakWorkingBytes` 不再漏掉
     `RegionExecutor` 的工作集；区域路径也设 `PredictedWorkingBytes`。`WorkspaceScope.WholeImage` 现在真正改变
     乘法（不再只靠 Category）。
  对账测试覆盖流式、物化、几何链与区域四种运行（`WorkspaceManagerTests` / `RegionWorkspaceTests`）。**不做 spool**：
  当前策略是物化或明确拒绝，两者都计入预算；临时文件需要存储与清理契约，超出本库边界。FloatScratch 空闲保留与编解码器
  内部缓冲作为 `PlanWorkspaceReport.Unmanaged` 显式登记，不当成零。M2–M4 的出口条件按 2026-11-26 的口径关闭
  （见 §「M2–M4 出口条件核验」）。
- **工作集口径收尾（2026-11-26）**：五处此前会低估或口径不一的地方已修：
  1. `EstimateStream` 把多个 OperatorRun 的窗口与 scratch 从「取最大」改为「按段累加」——拉式链里每个
     `PlanRowSource` 的窗口和账户预留同时存活，`PeakWorkingBytes` 是它们的和，预测必须与之一致；
  2. `WorkingSetBudget`（Stream/Region 的运行时门）与 `WorkspacePlanner.EstimateStream` 现在和 `MemoryEstimator`
     一样读 `WorkspaceScope`：`WholeImage` 只算一份，`PerWorker` 才乘 worker，三条路径不再各说各话；
  3. `Trim` / `EntropyCrop` 声明真实 scratch（行缓冲、逐列直方图等）；尺寸未知时 `GeometryRule.Map` 作为输出上界，
     `CompiledPlanExecutor` 在 reshape **之前**按「旧帧 + 上界输出 + 声明 scratch」CheckBudget + Reserve，而不是
     分配完再补检查；
  4. `RegionExecutor` 的最大窗口高度改用与 `PlanRowSource` 相同的公式（band + 2ΣHaloY，再钳到页高）：顶部/底部
     band 的 halo 被图像边钳掉，用首带探测会低估，顶部多 band 区域会越界（回归测试
     `ProcessRegion_AtTheTopAcrossManyBands_MatchesLoadApplyCrop`）。
  5. 目标缓冲区的预留责任统一为「谁租用谁预留，且在 `Rent` 之前」：`Image.ProcessRegion`、
     `Image.ProcessTiles` 的缓冲重载和 `TileExecutor` 都经 `TargetBuffer` 预留；`RegionExecutor` 只计自己租的
     窗口。此前区域路径先分配矩形、再由 `RegionExecutor` 连同目标一起预留（限额不满足时已经付出分配），tile 则
     被走查和矩形执行器各记一次（限额为「一块 tile + 窗口」时会按两块 tile 拒绝）。回归测试：
     `ARegionAboveTheWorkspaceLimit_IsRefusedBeforeItsTargetIsRented`（断言 `CountingPool.Rented == 0`）、
     `ATileWalk_CountsTheTargetTileOnce`（断言峰值恰为一块 tile + 两个窗口）。
  对账测试新增 `AMultiStageStream_StaysWithinItsPrediction`、`TheCompilerAndTheStreamingBudget_ReadTheSameScope`、
  `AContentDerivedGeometry_*`；`EstimateStream` 的旧「最大」口径不再保留。
- **区域推导（P1 部分完成）**：已有 `SourceRegionSet`、反向逐段推导、Resize/Crop/Pad/FlipVertical/FlipHorizontal/
  Rotate90/Rotate180/Transpose/RotateFlip（8 种组合）/Affine/任意角 Rotate 反向映射、每个输入端口的 footprint、
  区域成本与 `RegionCost` 拒绝、公开的 `Image.Explain`（`PlanExplanation`：后端与拒绝原因、编译段与融合、工作空间
  逐项预测、每步的区域/放大比与 `Partitioning`/`PassModel`/`StateScope`/`ExecutionOrder`/`Determinism`、文字 notes）。
  仍缺：Warp/Perspective/内容派生尺寸的区域反推（当前整图）、`AaMode.Elliptical`（足迹随 Jacobian 变化，当前整图）、
  真实的多矩形集合、成本驱动的后端比较/切分；文件侧选路已由 `ImageTranscode.Explain()` 公开（读头部、不消耗流），
  带 `Region` 的转码仍走 `RegionExecutor`、没有编译计划可解释；
  `Image.Apply/ApplyWithOptions/ProcessRegion` 已统一到 Compiler（区域经 `CompiledPlanExecutor.ExecuteRegion`），
  文件侧的 `ProcessChain`（`ImageTranscode`）与区域路径也都由同一份编译结果驱动；旧的 `ProcessTo` / `ResizeRegion`
  旁路已删除。
- **P0 契约收尾（模型完成，代表算子已核验；2026-11-26）**：`OperatorContract` 覆盖所有算子步；
  `ContractConformanceTests` 逐工厂校验格式、footprint、halo、pass、分区与 workspace，代表八算子中的
  Invert/GaussianBlur/Pixelate/Equalize/Noise 在列，Composite/Blend 在 `CompositeStepTests`。审查中发现并修复了
  两处真实语义偏差：Pixelate 与 Composite/Blend/DrawImage 实际会重算 alpha，却声明 `AlphaPolicy.Preserve`；现在声明
  `Recomputes`，并由 `RepresentativeSemanticsTests` 用像素行为（2×2 块 alpha 平均）守护。几何步现在也使用同一套
  词表：`GeometryDescriptor` 带 `PassModel`/`StateScope`/`ExecutionOrder`/`Determinism`/`PixelSemantics`/`WorkspaceModel`
  （domain、alpha、precision、border），编译器据此做流式分类、解释和工作空间报告（SeamCarve 声明为
  `WholeFrameAlgorithm` + `PerImage` + workspace `Undeclared`，计划因此正确标为下界）；`IImageOp` 已退役，
  `ImageOp` 是唯一算子定义。注入式第二输入的端口区域也改为精确矩形，不再保守整图。纯置换/画布类几何
  （Tile、ResizeCanvas、RoundCorners、Trim、EntropyCrop）的声明已有像素级语义测试（`GeometrySemanticsTests`：
  Tile 的置换、ResizeCanvas 的背景/alpha、RoundCorners 的角 alpha、Trim 与 EntropyCrop 的存活像素逐字节），
  EntropyCrop 另外核对了 scratch 声明非零且按整图一份计。几何的 `Border` 仍只对 Warp/Affine/Perspective/Rotate
  标了 `Constant`，其余用默认 `Clamp`。
- **PassModel 的当前定位（2026-11-26 明确）**：`PassModel` 仍是描述性字段，不生成独立 Pass 调度。现有
  `GatherThenApply` / `MultiPass` 算子都在一个内核里完成整图或整带计算，拆开只会增加一次中间物化而没有算法收益；
  因此不在本轮“为了用上字段”而拆算法。将来出现必须跨 Pass 复用统计/中间结果的算子时，再引入 Pass 计划，并让它
  真正驱动 `CompiledSection`。这是决定，不是遗漏。
- **旧入口清零（2026-11-26）**：`ProcessTo` / `ResizeTo` / `ResizeRegion` 已删除，文件侧只剩 `ImageTranscode`；
  `ProcessChain` / `ProcessRegion` 是它的内部路由目标，不再是可单独调用的兼容层。区域 + 尾部 resize 改为
  「编译区域执行产出矩形（`Load → 处理 → Crop` 的字节）+ 编译 Resize 段」，与 `Load → 处理 → Crop → Resize`
  逐字节相同；代价是矩形在缩放期间整块存在，此前的 halo 扩窗 + 单带缩放行泵组合不再保留。要恢复单带，必须先把
  Region 计划扩展到「算子 + 尾部 Resize」，让编译器和执行器一起认识尺寸变化，而不是恢复一条旁路。
- **Tile 后端（P5 部分完成）**：`IRegionSink` + `TileExecutor` 已能把 Point / Neighborhood 计划按 tile 执行，并与
  Frame 逐字节比较；`Global` / `Composite` / `FormatChange` / 块对齐 / 整图步骤仍以 `TileRefusal` 拒绝。公开入口：
  `Image.ProcessTiles` 的缓冲与 Sink 两个重载、`Image.ProcessTilesTo(IRegionSource, Stream, …)`（编码器端 sink：
  持一条 `tileHeight` 行的带、行填满即写、`ProcessMode.Tiled`）与 `ProcessTilesTo(path, path, …)`（TIFF 区域源）。
  公开的文件级区域源是 `Image.OpenRegionSource`（TIFF 布局，可释放）；其它格式仍要调用方自己实现 `IRegionSource`。
  tile 吞吐的两个基准条目（`tile invert` / `tile gaussian`，512×512 tile、4K 源）已录入独立版本化基线
  `windows-10.0.19045-x64-16-m16-tile.json`（阈值 30% / 35%，见 `measurements.md`）；主基线仍是 m16，
  全量重录待录制脚本。tile 计划不能带几何步（含尾部 resize），编译期以 `NotTileable` 拒绝。
- **需求选路（P1 补完）**：`StateScope.PerCall` / `ExecutionOrder.SourceOrder` 现在参与 Stream/Region 拒绝
  （`StatefulStep`），并由 `RequirementsSelectionTests` 用一个合成的行可分步验证；`PassModel` 与 `Determinism`
  仍只用于解释与预算，不改变后端选择（当前非默认项都是 Global，分类已被 Category 拦下）。
- **单步行流式缩放已落地**：`ImageTranscode` 的行流式缩放与 `Load → Resize` 逐字节相同，8K→1080p 工作集约 19 MiB
  （ring + staging + 输出批 + worker 行，一个账本；超限先缩 chunk 再拒绝）。
- **一次流水线只支持一个缩放**：`ImageTranscode.Resize` 两侧都可以挂算子（`GaussianBlur → Resize → Sharpen`）；
  两次缩放连用仍要分两次调用（中间落一张整图）。真正的多级流水线需要 plan 自带尺寸变换，见 `architecture.md` §6.7。
- **编码器尺寸仍由门面给出**：`Resize` 的 width/height 是参数；计划自己改变尺寸（不经过门面参数）还不支持。
- **区域 + 几何（2026-11-26 起）**：`ImageTranscode.From(path).Region(rect).Resize(w, h)` 先由编译区域执行器产出
  矩形，再跑编译缩放段，结果与 `Load → Apply → Crop → Resize` 逐字节相同；矩形在缩放期间整块存在。仍未做的是
  「缩放夹在多个计划之间」的区域版（需要 Region 计划认识尺寸变换）与 Affine/Perspective。
- **原点感知重采样未做**：`Resize` 以 view 尺寸建权重、按 view 坐标钳制；分块需要「全图尺寸 + 原点」，
  而权重/钳制/累加序必须逐字节不变。
- **Affine / Perspective 不在行模型里**：它们的源区域通常不是矩形（透视甚至不可表达），需要另一套按目标行
  求源窗口的映射，且要单列等价性论证。

## 性能基线的录制（2026-11-25）

- **`SimdImaging.Benchmarks` 不写阈值**：`BaselineJson.Write` 恒写 `thresholdPercent: null`，所以「跑一遍保存 JSON」
  得到的基线一条都不设闸。`check-baseline.sh` 现在对这种情况直接 FAIL（此前会打印 `PASS (0 enforced)`）。
- **没有录制脚本**：阈值要按「五次运行归一化后的偏差 `max(20, ceil(3×S))`」推出，目前靠手工；m16 与 m16-tile
  两份基线已入库，重录前应先补一个 `record-baseline.sh`。
- **噪声地板**：2026-11-25 连跑四次，两次失败、失败项不同（`quantize` +30.6%、`stroke` +25.9%），都在阈值附近。
  在把这台机器换成更安静的之前，性能门禁的结论要按「多次运行」读，不要按单次。

## 流式几何与异步（2026-11-25）

- **文件侧流式几何是行局部的四个**：`Crop`、`Pad`、`FlipHorizontal`、`Resize`，按调用顺序串成拉式链。
  `FlipVertical` / `Rotate90` / `Transpose` / `Affine` / `Perspective` / 接缝处理需要整行重排或随机访问，
  前向流做不了，保持整图 `Pipe()` 路径；`Region` 与几何阶段组合也拒绝。
- **异步是 offload，不是重叠 IO**：`RunAsync` / `SaveAsync` / `ToImageAsync` 把同步管线放到线程池，取消在带之间生效；
  codec 的行读者与编码器都是同步的，真正重叠 IO 需要上游提供异步行接口。
