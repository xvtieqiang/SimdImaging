# Benchmarks

A stopwatch harness, not BenchmarkDotNet: it answers one question — **did the end-to-end operation move?**
Kernel-in-isolation numbers are not recorded here on purpose (a faster kernel once made the real pipeline
42% slower; see SimdCodecs' benchmark lessons).

```bash
dotnet run -c Release --project benchmarks/SimdImaging.Benchmarks [iterations]
```



## 内存占用（`mem` 模式，2026-11-15）

`dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- mem 3840x2160`：**先预热一次**，再连续跑 5 次，
报「峰值增量 / 调用后常驻 / 每次分配」。

| 算子（4K） | 每次分配 | 调用后常驻 |
|---|---|---|
| resize-bicubic / gaussian-blur | 0 | 0 |
| **seam-50** | **39.7 MiB**（原 47.6，`_blocked` 改惰性） | 0 |
| guided-filter / guided.cross | 10.6 MiB | 0 |
| quantize-64 | 1.8 MiB | 0 |
| oilpaint | 1.5 MiB | 0 |

**结论一：不涨。** 除第一支之外，重复调用后 `GC.GetTotalMemory(forceFullCollection)` 相对基线为 0，
说明 `FloatScratch` 与 `ArrayPixelPool` 的保留是有限的、不是泄漏。

**结论二：seam 的 39.7 MiB 里约 33 MiB 是 luma 平面**——那是当天为 −17% 加的（每条缝只算一次 luma，
而不是 50 次）。换来的时间是真的，代价是这个平面本身。**下一个该改的正是它**：8/16 位下 luma 恒为 0..255 的整数，
用 `byte[]` 存放即 4 倍缩减到 8.3 MiB，DP 读入时再展开成 float，字节不变。

**插曲（值得记）**：把插入路径专用的 `_blocked` 掩码改成惰性分配后，**峰值 RSS 一点没动**（8K 仍 416 MiB），
因为 33 MiB 是 LOH 大对象、释放后页仍被进程留着；只有分配口径（这张表）能看到它从 47.6 降到 39.7 MiB。
**两个口径回答的是两个问题**，不能互相替代。

**这个模式测不了峰值**：冷进程试过，七项全读 0 MiB——夹具已经把堆提交好了，17 MiB 的平面落在已驻留页里，
工作集不再增长。绝对峰值仍以 `peakmem`（真子进程）为准。


## 内存占用（`mem` 模式，2026-11-15）

`dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- mem 3840x2160`：**先预热一次**，再连续跑 5 次，
报「峰值增量 / 调用后常驻 / 每次分配」。

| 算子（4K） | 每次分配 | 调用后常驻 |
|---|---|---|
| resize-bicubic / gaussian-blur | 0 | 0 |
| **seam-50** | **39.7 MiB**（原 47.6，`_blocked` 改惰性） | 0 |
| guided-filter / guided.cross | 10.6 MiB | 0 |
| quantize-64 | 1.8 MiB | 0 |
| oilpaint | 1.5 MiB | 0 |

**结论一：不涨。** 除第一支之外，重复调用后 `GC.GetTotalMemory(forceFullCollection)` 相对基线为 0，
说明 `FloatScratch` 与 `ArrayPixelPool` 的保留是有限的、不是泄漏。

**结论二：seam 的 39.7 MiB 里约 33 MiB 是 luma 平面**——那是当天为 −17% 加的（每条缝只算一次 luma，
而不是 50 次）。换来的时间是真的，代价是这个平面本身。**下一个该改的正是它**：8/16 位下 luma 恒为 0..255 的整数，
用 `byte[]` 存放即 4 倍缩减到 8.3 MiB，DP 读入时再展开成 float，字节不变。

**插曲（值得记）**：把插入路径专用的 `_blocked` 掩码改成惰性分配后，**峰值 RSS 一点没动**（8K 仍 416 MiB），
因为 33 MiB 是 LOH 大对象、释放后页仍被进程留着；只有分配口径（这张表）能看到它从 47.6 降到 39.7 MiB。
**两个口径回答的是两个问题**，不能互相替代。

**这个模式测不了峰值**：冷进程试过，七项全读 0 MiB——夹具已经把堆提交好了，17 MiB 的平面落在已驻留页里，
工作集不再增长。绝对峰值仍以 `peakmem`（真子进程）为准。


## Seam Carving 分段剖析（`seamprofile`，2026-11-15）

```
dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- seamprofile
```

相位读数来自**产品算子自己**的 `SeamCarving.ProfilePhases` 计数器（每阶段一次、每缝四次，默认关、
关闭时只剩一个不跳转的分支），不是复刻件的计时——铁律 4 在这里同样适用。每格是**全新子进程**：
三遍 GC 取基线、1 ms 采样 `WorkingSet64`，冷格只调一次、热格 3 次中位。

| 尺寸 | 冷启动总 ms | FindSeam | CutSeam | ShiftLuma | Copy+Prime+Crop | ms/缝 | 峰值 RSS | 调用后 live |
|---|---|---|---|---|---|---|---|---|
| 1920×1080，50 缝 | 456.7 | **374.2（82%）** | 26.6（5.8%） | 24.0（5.3%） | 28.9（6.3%） | 9.1 | 28.4 MiB | 16.0 MiB |
| 3840×2160，50 缝 | 1722.9 | **1455.4（84%）** | 101.6（5.9%） | 102.9（6.0%） | 60.3（3.5%） | 34.5 | 107.5 MiB | 64.0 MiB |
| 7680×4320，10 缝 | 1526.9 | **1196.0（78%）** | 73.3（4.8%） | 73.8（4.8%） | 180.9（11.8%） | 152.7 | **416.3 MiB** | 254.0 MiB |

两条自检列是这张表存在的条件：**Σ相位/总 = 99.7–99.9%**（漏了括号点就会掉下来），
**同尺寸冷热 SHA 相同**（不同尺寸的 SHA 本来就不同，拿它们互比是错的——第一版就是这么错的）。

**2026-11-16 更新**：整数 DP（8 lane）后 FindSeam 占比从 82% 降到 72%，总时长 1080p 457 → 277 ms；
luma 改 `ushort` 后 8K 峰值 RSS 从 415 降到 **352 MiB**（正好是 luma 减半），`ShiftLuma` 从 25.4 降到 15.6 ms。

结论：**85% 的时间在 `FindSeam`**，Cut/Shift 各 5%。所以「减少数据搬移」（Cut/Shift）天花板是 10%，
真正的空间在能量与 DP 内部。内存侧 8K 峰值 416 MiB 里，`Workspace` 推算约 199 MiB（其中 `_blocked`
33 MiB 在删除路径上从不被读）。


### 能量在 `FindSeam` 里只占 17%（消融，2026-11-16）

`FindSeam` 占整次切割的 80–85%，所以「增量能量更新」看着像大机会。**消融说不是**：把 `Energy()`
在标量路径与 `WriteRow4` 里同时短路（只测时间，字节必然变，用完即回滚），1080p 删 50 列：

| | FindSeam | 总时长 |
|---|---|---|
| 原样 | 400.5 ms | 487.2 ms |
| 能量短路 | **331.4 ms** | 447.6 ms |

**能量 = 69 ms = `FindSeam` 的 17%、总时长的 14%。** 即使缓存命中率 100% 也只值这么多，而增量实现
还要为「逻辑列 → 原始列」付 gather 的代价。结论：瓶颈是 **DP 递推本身**，那条 O(W·H) 且已向量化的链。
这条实验同时说明 `known-gaps.md` 里原来的否决理由（「受影响列无上界」）**不成立**——按原始坐标存时每行只
变 1–2 格；结论仍然是不做，但依据换成了这个数字。


### seam 的 8K 内存账（2026-11-16）

峰值 **352 MiB** 的构成，以及每一块能不能动：

| 块 | 8K 字节 | 能不能省 |
|---|---|---|
| 源图副本（`Transforms.Copy` 进工作缓冲） | 133 MiB | **不能**：carve 就地改像素，不能改调用者的图 |
| `Crop` 的结果缓冲 | 132 MiB | **不能**（见下） |
| Workspace：luma `ushort` | 66 MiB | 已减半（原 float 132） |
| Workspace：`_back` | 33 MiB | 能，但见下 |
| Workspace：行缓冲 / `_seam` | <1 MiB | — |

**为什么 `Crop` 不能省**：`PixelView.Span` 返回的是 `Stride × Height` 字节。如果让结果直接复用工作缓冲、
只把 `Width` 改小，那么每个用 `WritablePixels` / `ToArray()` 的调用者都会读到**被删掉的那些列**（陈旧字节）。
Crop 存在的理由就是这个契约；去掉它等于允许「非紧凑结果」，那是公共 API 的设计变更，不是局部优化。

**为什么 2-bit parent 现在不做**：`_back` 33 MiB → 8 MiB 是真的，但写入点在 DP 内层（占 72% 的时间），
而按 4 格打包会引入跨字节的读改写（`row + x` 从 x=1 起步，不保证 4 对齐）。**用最热的那条循环换 25 MiB**，
在现在的性能曲线上不划算。等有独立需求（比如 16K）再评估。



## 第三种工作格式：Rgba32F（`perf32`，2026-11-16）

```
dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- perf32 1920x1080
```

Rgba32F 之前**完全没有 A/B 对照**——Rgba8 那张表回答「形状快不快」，Rgba16 那张回答「16 位还快不快」，
浮点这一格是空白。对手侧用 ImageSharp 的 `RgbaVector`（每通道 float），两边每像素都是 16 字节。

**不与对手逐字节比较**：两条独立的浮点管线舍入不同，逐字节对齐既做不到也无意义。这张表只回答吞吐。

| 尺寸 | 判定 | 落后项 |
|---|---|---|
| 640×360 | 1.6734×（8/8） | 无 |
| 1920×1080 | 1.70–1.93×（8/8，三轮；`gaussian-blur` 区间 1.10–2.79 属噪声） | 无 |
| 3840×2160 | 1.7255×（8/8） | 无 |
| 7680×4320 | 1.8286×（8/8） | 无 |

> 修复前的 1080p 是 **1.44×（6/8）**，`rotate90` 0.39×——那是这张表建立当天就抓到的东西。

第一次运行就抓到 `rotate90` 在浮点上只有 **0.39×**：`Rotate90` 只为 4 字节和 8 字节像素准备了向量化转置，
16 字节掉进了逐像素 `Slice().CopyTo()` 的串行循环。补上 `Transpose<Vector128<byte>>` 后
**23.0 → 4.9 ms（1.8×）**，8K 上 476 → 155 ms，判定从 7/8 变 8/8。

> 这条也说明「空白」比「慢」更值得查：它不是某处代码写得不好，而是一条从来没有被建起来的测量。

## 多轮次：`repeat.sh`（2026-11-16）

```
bash benchmarks/repeat.sh perf 640x360 6
bash benchmarks/repeat.sh perf16 7680x4320 4
```

单轮读数分不清 5% 的回归和机器漂移——同一场景在不同轮次里能从 0.93 跳到 2.74。这个脚本跑 N 轮，
按场景给**中位 / 最小 / 最大 / 落后次数**，并单独列出两类结论：

- **每一轮都 < 0.95**：真实落后，可以写进文档；
- **min 与 max 相差 > 1.5 倍**：采样噪声主导，单次读数不可引用。

第一次用它就抓到两处：`pixelate` 在 640×360 上 6/6 落后（真实），`resize-nearest` 在同尺寸上 0.93–2.74
（噪声）；以及 Rgba16 的 `invert` 6/6 落后 0.19×——那是我自己把 16 位表门槛设低了，修完是 3.48×。

**唯一的残留**：`pixelate` 640×360 每像素 3.10 周期，对手 2.36。差的 0.74 周期要靠字节域 SIMD 求和
（`vpmaddubsw` 一类）才能补，而它只值 0.05 ms——**判定不做**，理由记在这里而不是留成一个待办。

## 尺寸 × 工作格式矩阵（`matrix` 模式，2026-11-15）

`dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- matrix` 跑三个尺寸 × 两种工作格式，
每格都是一次完整的同进程 A/B（cold + steady 7 轮交错、进程级分配、峰值 WS），尺子与 `perf` / `perf16` 完全相同，
**没有新增度量**。

| 尺寸 | Rgba8（19 项） | Rgba16（7 项） |
|---|---|---|
| 640×360（0.23 MPix） | 1.5050×（领先 13 / 持平 2 / 落后 4，**含一格离群**） | **2.8424×（7/7）** |
| 1920×1080（2.07 MPix） | **2.3403×（19/19）** | **2.7361×（7/7）** |
| 3840×2160（8.29 MPix） | **2.0257×（19/19）** | **2.7433×（7/7）** |
| 7680×4320（33.2 MPix） | **2.0038×（19/19）** | **2.5553×（7/7）** |

> 640×360 那一格的「落后 4」里有一格是**离群值**：该轮 `resize-bicubic` 读到 0.38×（我方 5.32 ms），
> 而 `repeat.sh perf 640x360 3` 复核为 **4.22×（3.98–5.37）**，六轮中位 4.06×。剔除后是**领先 14 / 持平 2 / 落后 1**
> （唯一的真落后是 `pixelate`）。**单次 matrix 运行可能被一个受干扰的单元格带偏整行结论**——这正是 `repeat.sh` 存在的理由。
>
> 第一版矩阵只到 4K，是漏项——8K 那一格才暴露出下一节说的问题。参数档位现在也在每个尺寸上跑
> （质量与尺寸会相互作用：σ=1 的模糊在 1080p 便宜，8K 不便宜）。

**小尺寸不是弱点，是测量地板**：640×360 上 19 项原本报「15/19 领先」，但把逐项拉出来看，落后的几项是
0.95–0.99× 或 0.06–0.21 ms 的**亚毫秒**场景。`resize-nearest`（0.09 vs 0.058 ms）在两次运行之间从 1.46× 摆到
0.64× —— 这个量级的抖动来自测量本身，不是算子。因此判定列改成三档（领先 >1.05× / 持平 / 落后 <0.95×），
报告写成「领先 14 / 持平 3 / 落后 2」而不是「15/19」。**亚毫秒场景不引用为胜负**，要么加大轮次，要么不报。

试过并回滚的：把 `RowParallel` 的固定 64K 像素门槛改成「按工作量给 worker 数」（32K 像素/worker）。
点算子确实变快（brightness 0.90× → 1.15×），但同一个改动把 640×360 的 gaussian-blur 从 1.64× 压到 1.39× ——
一个按像素数的启发式分不开「便宜的点算子」和「昂贵的模糊」，净效果为零，已回滚。
另试过把 Rgba8 点算子表改成 32 位打包读写（一次 load/store 替代四次）：**无收益**（1.15× → 1.14×），同样回滚。
**Rgba16 的短板很集中**：三个点算子（grayscale / sepia / invert）在三种尺寸上都是 0.63–0.67×，
因为 `PointLut` 只对 `Rgba8` 生效，16 位走的是 float 行路径。这是矩阵第一次运行就暴露出来的真问题。

> **2026-11-15 修复**：新增 16 位点算子表（4 × 65536 个 `ushort`，512 KiB），用与 Rgba8 相同的手法构造——
> 拿算子自己的行变换跑每个值、记下结果，因此不存在第二份算术。低于 `Lut16PixelFloor`（16384 像素）不建表。
> 逐通道算子（invert / brightness / gamma / contrast）走这张表；矩阵类算子（sepia / grayscale）不走，
> 三条独立测量确认它们字节未变。Rgba16 整体：**1.53–1.72×（4/7）→ 2.01–2.29×（7/7）**。

> **2026-11-15 修复**：新增 16 位点算子表（4 × 65536 个 `ushort`，512 KiB），用与 Rgba8 相同的手法构造——
> 拿算子自己的行变换跑每个值、记下结果，因此不存在第二份算术。低于 `Lut16PixelFloor`（16384 像素）不建表。
> 逐通道算子（invert / brightness / gamma / contrast）走这张表；矩阵类算子（sepia / grayscale）不走，
> 三条独立测量确认它们字节未变。Rgba16 整体：**1.53–1.72×（4/7）→ 2.01–2.29×（7/7）**。

**8K 暴露的短板（已修，见下）**：Rgba16 在 8K 只有 5/7，落后的两项是 **grayscale 0.67× / sepia 0.68×**——它们是**矩阵类**
算子，不吃逐通道表，走 `Pixels.ReadRowRaw` / `WriteRowRaw` 的 float 行路径，而 **Rgba16 的这两条读写路径是
逐像素标量**（每像素 4 次 `ushort × (1/65535)`、回来再 4 次 `ToUInt16`）。1080p 上看不出来（那两项还是 1.3×），
8K 上 33 MPix 把它放大成 148 ms vs 对手 99 ms。这是**所有 16 位算子的共同瓶颈**，不只是这两个。

> **已修（2026-11-16）**：`ReadRowRaw` / `WriteRowRaw` 的 Rgba16 分支改成八路向量：读 `ushort → widen → int32
> → float → ×1/65535`，写 `×65535 + 0.5 → clamp → trunc`。写入那条必须逐位复现标量的顺序——`Vector256.Max(v, 0)`
> 顺便把 NaN 变成 0，与 x64 上标量 `(ushort)NaN` 的行为一致。**8K Rgba16：1.77×（5/7）→ 2.67×（7/7）**，
> grayscale 148 → **52 ms**、sepia 146 → **52 ms**；两次独立运行 2.6754× / 2.6635×。

参数档位（每尺寸都跑，这里列 1080p 与 8K）：

| 档位 | 我方 ms | 对手 ms | 比值 |
|---|---|---|---|
| blur sigma=1 / 3 / 8 | 8.3 / 17.8 / 54.8 | 11.4 / 27.3 / 66.4 | 1.37× / 1.53× / 1.21× |
| quantize 16 / 64 / 256（两侧均 Wu） | 20.6 / 33.4 / 170.2 | 332 / 359 / 449 | **16.1× / 10.7× / 2.64×** |


## 尺寸 × 工作格式矩阵（`matrix` 模式，2026-11-15）

`dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- matrix` 跑三个尺寸 × 两种工作格式，
每格都是一次完整的同进程 A/B（cold + steady 7 轮交错、进程级分配、峰值 WS），尺子与 `perf` / `perf16` 完全相同，
**没有新增度量**。

| 尺寸 | Rgba8（19 项） | Rgba16（7 项） |
|---|---|---|
| 640×360（0.23 MPix） | 1.7234×（领先 14 / 持平 2 / 落后 3） | 1.3021×（4/7） |
| 1920×1080（2.07 MPix） | **2.3033×**（19/19） | 1.8433×（5/7） |
| 3840×2160（8.29 MPix） | 1.9898×（19/19） | 1.8270×（5/7） |
| **7680×4320（33.2 MPix）** | **1.9898×（19/19）** | **1.7692×（5/7）** |

> 第一版矩阵只到 4K，是漏项——8K 那一格才暴露出下一节说的问题。参数档位现在也在每个尺寸上跑
> （质量与尺寸会相互作用：σ=1 的模糊在 1080p 便宜，8K 不便宜）。

**小尺寸不是弱点，是测量地板**：640×360 上 19 项原本报「15/19 领先」，但把逐项拉出来看，落后的几项是
0.95–0.99× 或 0.06–0.21 ms 的**亚毫秒**场景。`resize-nearest`（0.09 vs 0.058 ms）在两次运行之间从 1.46× 摆到
0.64× —— 这个量级的抖动来自测量本身，不是算子。因此判定列改成三档（领先 >1.05× / 持平 / 落后 <0.95×），
报告写成「领先 14 / 持平 3 / 落后 2」而不是「15/19」。**亚毫秒场景不引用为胜负**，要么加大轮次，要么不报。

试过并回滚的：把 `RowParallel` 的固定 64K 像素门槛改成「按工作量给 worker 数」（32K 像素/worker）。
点算子确实变快（brightness 0.90× → 1.15×），但同一个改动把 640×360 的 gaussian-blur 从 1.64× 压到 1.39× ——
一个按像素数的启发式分不开「便宜的点算子」和「昂贵的模糊」，净效果为零，已回滚。
另试过把 Rgba8 点算子表改成 32 位打包读写（一次 load/store 替代四次）：**无收益**（1.15× → 1.14×），同样回滚。
**Rgba16 的短板很集中**：三个点算子（grayscale / sepia / invert）在三种尺寸上都是 0.63–0.67×，
因为 `PointLut` 只对 `Rgba8` 生效，16 位走的是 float 行路径。这是矩阵第一次运行就暴露出来的真问题。

> **2026-11-15 修复**：新增 16 位点算子表（4 × 65536 个 `ushort`，512 KiB），用与 Rgba8 相同的手法构造——
> 拿算子自己的行变换跑每个值、记下结果，因此不存在第二份算术。低于 `Lut16PixelFloor`（16384 像素）不建表。
> 逐通道算子（invert / brightness / gamma / contrast）走这张表；矩阵类算子（sepia / grayscale）不走，
> 三条独立测量确认它们字节未变。Rgba16 整体：**1.53–1.72×（4/7）→ 2.01–2.29×（7/7）**。

> **2026-11-15 修复**：新增 16 位点算子表（4 × 65536 个 `ushort`，512 KiB），用与 Rgba8 相同的手法构造——
> 拿算子自己的行变换跑每个值、记下结果，因此不存在第二份算术。低于 `Lut16PixelFloor`（16384 像素）不建表。
> 逐通道算子（invert / brightness / gamma / contrast）走这张表；矩阵类算子（sepia / grayscale）不走，
> 三条独立测量确认它们字节未变。Rgba16 整体：**1.53–1.72×（4/7）→ 2.01–2.29×（7/7）**。

**8K 暴露的短板（已修，见下）**：Rgba16 在 8K 只有 5/7，落后的两项是 **grayscale 0.67× / sepia 0.68×**——它们是**矩阵类**
算子，不吃逐通道表，走 `Pixels.ReadRowRaw` / `WriteRowRaw` 的 float 行路径，而 **Rgba16 的这两条读写路径是
逐像素标量**（每像素 4 次 `ushort × (1/65535)`、回来再 4 次 `ToUInt16`）。1080p 上看不出来（那两项还是 1.3×），
8K 上 33 MPix 把它放大成 148 ms vs 对手 99 ms。这是**所有 16 位算子的共同瓶颈**，不只是这两个。

> **已修（2026-11-16）**：`ReadRowRaw` / `WriteRowRaw` 的 Rgba16 分支改成八路向量：读 `ushort → widen → int32
> → float → ×1/65535`，写 `×65535 + 0.5 → clamp → trunc`。写入那条必须逐位复现标量的顺序——`Vector256.Max(v, 0)`
> 顺便把 NaN 变成 0，与 x64 上标量 `(ushort)NaN` 的行为一致。**8K Rgba16：1.77×（5/7）→ 2.67×（7/7）**，
> grayscale 148 → **52 ms**、sepia 146 → **52 ms**；两次独立运行 2.6754× / 2.6635×。

参数档位（每尺寸都跑，这里列 1080p 与 8K）：

| 档位 | 我方 ms | 对手 ms | 比值 |
|---|---|---|---|
| blur sigma=1 / 3 / 8 | 8.3 / 17.8 / 54.8 | 11.4 / 27.3 / 66.4 | 1.37× / 1.53× / 1.21× |
| quantize 16 / 64 / 256（两侧均 Wu） | 20.6 / 33.4 / 170.2 | 332 / 359 / 449 | **16.1× / 10.7× / 2.64×** |

## Recorded results

Machine: Windows 10.0.19045, X64, .NET 10.0.14, `avx512=False avx2=True v128=True`.
Source: 3840×2160 Rgba8 (8.3 MPix). Best of 3 after one warm-up run, **two sessions**.

| Operation | Baseline (scalar) | Current | Change |
|---|---:|---:|---:|
| Resize 4K→1080p, Bicubic | 200.1 ms / 10.4 MPix/s | **97–111 ms / 18.6–21.4 MPix/s** | **≈1.8–2.1×** |
| Resize 4K→1080p, Lanczos3 | 243.0 ms / 8.5 MPix/s | **107–123 ms / 16.9–19.4 MPix/s** | **≈2.0–2.3×** |
| Crop 1000×1000 | 0.4 ms | 0.5 ms | noise (memcpy-bound) |
| Quantize 4K→64 colours (median cut) | 213 ms / 38.9 MPix/s, 1 069 KiB | **59–69 ms / 120–140 MPix/s, 949 KiB** | **3.6×** |
| Quantize 4K→64 colours (Wu) | 132 ms / 62.9 MPix/s | **110 ms / 75.4 MPix/s, 1 440 KiB** | ≈1.5× slower than median cut **in the same run** |
| Quantize 4K→64 colours (octree) | — | 227 ms / 36.6 MPix/s, 85 KiB | new; per-pixel tree pruned to a 4 096-leaf budget |
| Reduce colors 4K→64, dithered | — | 488 ms / 17.0 MPix/s, 265 KiB | new; octree + Floyd–Steinberg |
| Fill 4K ellipse path (AA) | — | 53 ms / 156 MPix/s, 47 KiB | new; 16 sub-scanlines, interior runs replace |
| Stroke 4K ellipse path (AA, w=6) | — | 19 ms / 443 MPix/s, 231 KiB | new |
| Clip 4K ellipse path (AA) | — | 79 ms / 105 MPix/s, 47 KiB | new; every row is visited, alpha scaled per pixel |
| Chroma 4:2:0 4K | 216 ms / 38.5 MPix/s, 56 760 KiB | **213 ms / 38.9 MPix/s, 1 088 KiB** | row-banded: chroma rows for the band plus its neighbours, down 52× |
| Gaussian blur 4K, σ=3 | 549 ms / 15.1 MPix/s, 162 489 KiB | **473 ms / 17.5 MPix/s, 8 880 KiB** | row-banded scratch: 18× less memory and 14% faster (the plane no longer blows cache) |
| Oil paint 4K, r=2, 20 levels | — | 2 015 ms / 4.1 MPix/s, 361 KiB | new; 25-tap window + 20-bin scan per pixel, scalar |
| Guided filter 4K, r=4 | 893–990 ms / 8.4–9.3 MPix/s, 162 121 KiB | **523–540 ms / 15.4–15.8 MPix/s, 10 891 KiB** | row-banded (162 MB → 11 MB) and the window sum vectorised over a float prefix; matches a naive 2-D reference within 1 LSB |
| Composite 1080p overlay | 35.7 ms | 28.8–46.7 ms | noise (unchanged code) |
| Bilateral 4K, r=2 | — | 1 160 ms / 7.1 MPix/s, 363 KiB | new; 25 taps/px, scalar + 512-entry range LUT |
| Resize allocation | 70 450 KiB | **17 020 KiB** | **4.1× lower**: row bands bound the scratch to 16 MB |

**Read the ranges, not the best case.** Run-to-run spread on this machine reached 40% during this session (other load), so absolute numbers move between sessions; comparisons are only trustworthy when both sides are measured in the same run. And the baseline was
recorded *before* the change in an earlier session — by this repository's own rule that is not a valid
baseline. Treat this as a directional result; a same-process A/B (`baseline <git-ref>`) is part of M5.

### What produced the change

1. **Clamp-free interior in both passes.** The old inner loop called `Math.Clamp` per tap per pixel and
   computed `taps > sourceWidth` edge cases through a wrap-around `uint` cast — the wrapped case read past
   the row. The check is now `start >= 0 && start + taps <= sourceWidth`, with a separate clamped path for
   the first and last few pixels.
2. **Horizontal pass: one `Vector128<float>` per destination pixel** (the four channels of a pixel in one
   vector), taps accumulated in the same order as the scalar twin.
3. **Vertical pass: `Vector256<float>` across the row** (eight floats per iteration).
4. **Premultiplied row read vectorized for Rgba8** (four pixels per iteration: 16 bytes → ushort → uint →
   float, alpha broadcast over RGB, alpha lane stored unmultiplied).
5. **Quantizer byte path.** Build and apply were converting every row to floats only to rebuild a 5-bit bin
   from 8-bit values; the Rgba8 path now indexes the bytes directly (213 → 59 ms).
6. **Row-band resizing.** The horizontal pass now runs per band of destination rows, sized so its float
   scratch stays within a 16 MB budget, instead of materialising `dstW × srcH` floats (4K: 66 MB).
7. **Premultiplied row write vectorized for Rgba8** (reciprocal, clamp, truncate, pack 4×4 bytes). No
   measurable end-to-end change on this machine — kept because it is proven byte-identical and the reference
   is needed on other hardware.

The write kernel found a real bug on the way in: the scalar twin guards `alpha > 0`, the vector version
guarded only `alpha == 0`. A Lanczos overshoot makes filtered alpha **negative**, and dividing a negative
colour by a negative alpha flips it into a bright pixel. The smoke-pattern test caught it (`scalar=0
vector=245` at one channel); the fix is `LessThanOrEqual(alpha, 0)` and the AOT hash returned to its previous
value.

Output is unchanged: every AOT smoke hash (crop / bicubic / Lanczos3 / 16-bit resize / rotate / flip / pad /
composite) is byte-identical before and after the vector work, and `RowMathTests` compares the vector kernel
against its scalar twin for widths 1..65 exactly.

## Gates, tiers and baselines (M6, 2026-11-14)

Four scripts now share one ruler:

- `bash benchmarks/gates.sh` — build (0 warnings), full tests, public-API snapshot.
- `bash benchmarks/gates.sh --aot --perf` — adds the NativeAOT smoke and the versioned baseline check.
- `bash benchmarks/simd-tiers.sh [--aot]` — runs the **full** suite under `DOTNET_EnableAVX512F=0`,
  `AVX2=0` and `HWIntrinsic=0`, then diffs the AOT smoke program's operator-output hash bodies.
  It deliberately does not use `--filter "FullyQualifiedName~Kernel"`: that filter only matched tests
  whose name happened to contain "Kernel" and never covered the scalar-twin tests.
- `bash tests/Fixtures/download-corpus.sh --verify` — verifies the CC0 corpus by SHA-256.

Observed 2026-11-14 on the machine line above: `gates.sh` reported `warnings=0 errors=0`,
`failed=0`, passed above the 500 floor, snapshot unchanged. All three JIT tiers passed the whole
suite and produced the same operator-body SHA-256
`4439020325bc2f27a251abd9106e0b07cb181676dc6441da03330428e9067663`, unchanged after the M7 warp
precision rewrite (the Rgba8 output bytes did not move). The exact pass count is deliberately not
pinned here — it grows with every milestone; read it from the gate's `observed:` line instead.
`tests/SimdImaging.AotSmoke/run.sh` additionally diffed JIT / avx2 / SSE2 bodies with no difference
and now requires `warp` and `samplepixel` hashes. An AOT publish cannot run xUnit, so the smoke's
byte-for-byte diff stands in for re-running the suite under AOT — a different axis (build product),
not a fourth SIMD tier, and not a claim that four full suites were executed.

M8 update (2026-11-14): `AotSmoke/Program.cs` gained two explicit-contract operators
(`warp.linear`, `warp.premul`), so the operator-body SHA moved to
`7a7e74fe860e76ebe8c2895dfe80f5d2e730a8778c8cfd140931a26538b10e1f` and all three JIT tiers still
agree. **Stripping those two new lines reproduces the M7 body SHA
`4439020325bc2f27a251abd9106e0b07cb181676dc6441da03330428e9067663` exactly** — that is the check
that no EXISTING operator output byte changed when `WeightTable.BuildCore` moved to the shared
`TapPlanBuilder`. A body hash moving because new operators were added is not a byte regression;
do not confuse the two.

M8 second wave (2026-11-14): three more explicit-contract operators joined the smoke
(`resize.options`, `rotate.options`, `perspective.options`); the body SHA is unchanged at
`7a7e74fe…` because the new Console lines are part of the same hash they define — **stripping all
five new lines (`warp.linear`, `warp.premul`, `resize.options`, `rotate.options`,
`perspective.options`) still reproduces `44390203…6763` exactly**, which is the check that the
Mapping/Resize WithOptions additions, the `RotationCanvas` extraction and the
`SamplingCore` plan-injecting refactor changed no existing operator byte.

M9 update (2026-11-14): the `ewa.rotate` operator joined the smoke, so the body SHA moved to
`2a66d31a2eec905b32bf7b74086456f48413f9c303684654fe88e9d6585da902` across all three JIT tiers.
**Stripping all six M8+M9 lines (`warp.linear`, `warp.premul`, `resize.options`, `rotate.options`,
`perspective.options`, `ewa.rotate`) still reproduces `44390203…6763` exactly** — the EWA additions
(`SampleOptions.AaMode`, `EwaKernel`, the `SamplingCore` Jacobian overload, the analytic Jacobians in
`Mapping`) changed no existing operator byte.

M9 erratum (2026-11-14): the EWA footprint formula was inverted (`Q = MᵀM` with an extra `k²` made
minification sharpen instead of blur). After the fix the body SHA is
`42c595fe324c0af52ec126f3987ce790e612db495de2e2c7a8384852d5594992`; `ewa.rotate` changed because the
footprint changed, and stripping the same six lines still returns `44390203…6763`. The two `ewa.*`
reference vectors are unchanged (both are symmetric footprints, where equal weights make the formula
direction irrelevant).

M10 update (2026-11-14): the `plan.fused` chain (point → 3×3 blur → point through `ProcessingPlan`)
joined the smoke, so the body SHA is now
`d6781e6e4b946a69b0b83763d36f2e6b9f30a85c901eda7f1d98d1bee641370b` across all three JIT tiers.
**Stripping all seven new lines (the six above plus `plan.fused`) still reproduces `44390203…6763`
exactly** — the fused executor changed no operator byte. `run.sh`'s `REQUIRED` list grew with
`plan.fused` in the same change (AGENTS.md: every operator whose implementation changes must be in it).

M11 update (2026-11-14): `nlm` (non-local means) and `ll` (local Laplacian) joined the smoke, so the body
SHA is `3d51c3a6bfd5a9385483b9873b2908e1aff2035a759dc6783b4aee038a54f1c6` across all three JIT tiers.
**Stripping all nine lines (the seven above plus `nlm` and `ll`) reproduces `44390203…6763` exactly** —
both new operators sit outside every existing path. `bilateral` had been hashed but missing from
`REQUIRED`; it is listed there now, which is the first time that hash has actually gated anything.

M12 update (2026-11-14): `rl`, `rl.motion` and `seam` joined the smoke, so the body SHA is
`cf7dc7f8d060e3d5dd3a039e437367879245e87cbc2db1d6460309f594389838` across all three JIT tiers.
**Stripping all thirteen lines (the ten above plus `rl`, `rl.motion` and `seam`) reproduces
`44390203…6763` exactly** — deconvolution and seam carving touch no existing operator.

M12 measured on this machine at 1080p Rgba8: `RichardsonLucy(1.5, 10)` **254 ms**, alloc 95 MiB;
`RichardsonLucyMotion(15, 25°, 10)` **347 ms**; `RemoveSeams(Vertical, 50)` **502 ms** (≈10 ms per seam),
alloc 4 MiB. Deconvolution cost is `2 · taps · iterations` per pixel; FFT would need σ ≳ 18 px to win and
would break cross-tier byte equality — see `docs/fft-evaluation.md`.

M15 (2026-11-14): `gates.sh --perf` stopped being a placeholder. `benchmarks/baselines/windows-10.0.19045-x64-16-m16.json`
records 29 scenarios from two `best-of-3` runs of the same binary, **15 of them with `thresholdPercent: 15`**,
and `check-baseline.sh` compares a fresh run against them: `PASS` at worst **+1.9%** and **+3.5%** on two
validation runs (`enforced=15`), and
`--self-test` (thresholds forced to 0) exits **1** with all 29 over — a comparison that only ever passes is
not a gate. A scenario is gated only if **every** recorded pair agreed within 10%, so the 15% threshold keeps
at least five points of margin: that keeps stroke (23% between runs), gaussian (15.6%), bilateral (21.8%),
ICC (19.6%), crop (20.5%), `dt` (21.7% in one pair) and resize-Lanczos (25.9%) unenforced rather than turning
the gate into a coin flip, and it is why RL and seam stay out despite a quiet pair today (29% and 25% earlier).
Comparability needs the same machine id, binary hash, protocol and repeat count; anything else prints
"not comparable" and is skipped, because a cross-machine stopwatch difference is noise.

M15 re-record (2026-11-14, same machine and protocol): the file was rewritten twice — first after
`DistanceTransformOps` and the cross-guided filter landed, then again after the distance transform's column
pass moved to 64-column tiles — so `binarySha256` matches the current Processing/Abstractions pair and
`--perf` reports `enforced=15` instead of `comparable-baselines=0`. Membership moved with the rule above
against the original 14: **+** `resize 4K->1080p bicubic`, `composite 1080p overlay`, `guided.cross 4K r=4`;
**−** `stroke 4K ellipse`, `gaussian blur 4K` (`clip` stayed in, 4.9% on the defining pair).

M15 gate rework (2026-11-15): absolute milliseconds are no longer what gets judged. The same source measured
1.2–1.8× apart four hours apart, and the black-box ImageSharp reference drifted by the same factor
(resize-bicubic 12.5 → 19.3 ms at 1920×1080), so that drift belongs to the machine — and an absolute baseline
recorded in one state either false-alarms or hides a real regression in another.
`SimdImaging.Benchmarks` now carries two probes that never call this library (a 256 MB parallel streaming sum
and 32M xorshift rounds, both best-of-7, measured at the start and end of the run), and `check-baseline.sh`
divides each scenario by `max(run/baseline)` of the two — whichever resource was contended, the correction
covers it. Thresholds are derived from **three** runs rather than one pair: each run is normalised by its own
calibration, the largest relative deviation `S` across the three sets `threshold = max(15, ceil(2.5·S))`, and
`S > 20%` stays `null`. Result on the recording run: **16 of 29 gated, at 20–59%**. The count tracks how quiet the machine
was; the rule does not - it is the same rule either way, and a noisier hour simply means fewer
scenarios can be judged. Proven under deliberate
load: four busy cores drove the `alu` probe to ×1.59, the gate corrected by ×1.59, and every gated scenario
passed with a worst delta of **−1.2%** while the raw readings averaged **+40%**. `--self-test` still exits 1.

M15 smoke update (2026-11-14): `dt` (the exact squared EDT through the facade) and `guided.cross` (the
cross-guided filter) joined the smoke, so the body SHA is
`ff856b8bf81f45fbf910a4ea638b7a56b5685dffbbbf33275e516d6c5bfabd4e` across all three JIT tiers.
**Stripping those two lines reproduces the M13 body SHA `846c1c62…20c6` exactly** — both are new operator
paths and changed no existing operator byte. `REQUIRED` grew to 49 hashes / 22 gated in the same change.

M13 update (2026-11-14): `icc.lut` (a hand-built lut16 profile through `Image.IccTransform`) and
`gainmap.hdr` (hdrgm reconstruction of a quarter-size map) joined the smoke, so the body SHA is
`846c1c62280b8cb6b61fd486356b3f373bb077ab28459c7737355b98038620c6` across all three JIT tiers.
**Stripping those two lines reproduces the M12 body SHA
`cf7dc7f8d060e3d5dd3a039e437367879245e87cbc2db1d6460309f594389838` exactly**, and stripping the M12
list as well reproduces `44390203…6763` — the LUT parser, the CLUT and the gain-map reconstruction
changed no existing operator byte.

M13 measured on this machine at 1080p Rgba8 (`SimdImaging.Benchmarks`, best of five warm runs):
`icc 1080p lut16 grid=33` **40.0 ms** / 51.8 MPix/s, `icc 1080p lut16 grid=2` **36.5 ms**,
`icc 1080p matrix/trc` **34.8 ms** — so the whole 215 KB CLUT (data plus address arithmetic) is worth
about 3.5 ms, 9% of the call; the rest is format conversion and the sRGB/PCS maths both paths share.
That is why the LUT path ships scalar with row-band parallelism instead of a vector body: AVX2 has no
64-bit integer multiply (`vpmullq` is AVX-512DQ) and the accumulator needs 34 bits, so a `Vector256<long>`
product would be expanded to scalar anyway, on top of the eight-corner gather. `gainmap.reconstruct 1080p`
**28.4 ms** — 138.6 ms before the row-band split, i.e. 4.9× from parallelism; `gainmap resize 1080p→720p`
**5.2 ms**.

NLM measured on this machine at 1080p Rgba8 (best of one warm run, `SimdImaging.Benchmarks`):
`S=3 P=1` (the default) 5.8 s, `S=5 P=2` (quality) 16.1 s. The operator's cost is
`(2S+1)²·(2P+1)²` distance terms per pixel; after the AVX2 distance the exponential dominates, so lowering
`h`/radii is the only lever that does not change the result.

`benchmarks/baselines/` is versioned (unlike the git-ignored `results/`). `thresholdPercent` is set for the
Scenarios whose normalised spread over five runs stayed under 20% are gated at `max(20, 3×spread)` — always
above the measured noise, never a round number chosen to make today green. `check-baseline.sh` prints
the enforced count plus the machine correction it applied. The five that stay `null` (Lanczos3, crop, both path fills, one ICC grid) either exceeded 20% spread
across the three runs or jumped to +77% in a verification run: Lanczos3 went 30.8 → 54.1 ms, and an
earlier pair read 89.7 / 53.9 ms for the same scenario, so it is bimodal rather than merely noisy. Default `gates.sh`
stays at ~1 min; `--aot`, `--perf` and `simd-tiers.sh` are opt-in.

## What is still slow

97 ms for 4K→1080p is still behind ImageSharp's published class of result. The remaining costs, in order:

| Cost | Why it is still there |
|---|---|
| `WriteRowUnpremultiplied` (scalar, per-pixel division) | Not vectorized yet; float→byte packing must match the scalar rounding exactly |
| Horizontal tap loop | Per-pixel `Vector128.Create(weight)` and a zero-weight branch per tap |
| No same-run comparison | An ImageSharp number is only valid measured in the same process (M5, needs the licence present at build time) |

Next measurement target: after vectorizing the write path, re-run this harness — if the end-to-end number
does not move, the change does not land.
