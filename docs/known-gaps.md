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
| 处理算子 | 已实现 119 个（全量）；实现边界见下方「实现边界」 |
| SIMD 内核 | Rgba8 行读写、resize/blur 两遍卷积已向量化；点算子走「字节 LUT + 值主序矩阵记录 + 寄存器内钳制打包」，与标量孪生逐位一致（有端到端对比测试）。全部逐位一致，有测试兜底 |
| `ProcessingPlan` | 已实现：同尺寸步骤按行带执行，`PlanSteps` 把现有算子适配成步骤；`Mutate` 未提供（算子返回新图，原地绘制归 DrawOps） |
| `SimdImaging.Drawing` 包 | 未创建，P1 起 |
| `NativePixelPool` / `NullPixelPool` | 只有 `ArrayPixelPool` |
| 公开 API 门禁 | 不接 RS0016 分析器，改用 `docs/public-api.txt` 快照：`PublicApiTests` 反射出全部公开签名（含可空标注）逐行比对，改动必须显式重新生成并 review diff |
| 分块 / 行带执行 | 行带执行已实现（`ProcessingPlan`）；二维 tile 未做，几何与全局算子仍整图执行 |
| 公开参考向量包 | 已实现（M6，2026-11-14）：`tests/Fixtures/vectors/` 固定输入 / 期望 / SHA-256 + 独立高精度生成脚本；初始 3 条 `warp-identity-*`，`VectorTests` 独立重算哈希。新增向量允许，为过测试改已有 `expected` 禁止 |
| 像素差异工具 | 已实现（M6）：`src/Shared/PixelDiff.cs` 单一源码共享，测试项目与 Comparison 工程链接复用，不造第二把尺子；补首个不匹配位置、每通道误差、Top N、SHA-256、P6 差异热图，分级沿用 `Metrics.Grade` |
| SIMD 档位自动化 | 已实现（M6）：`benchmarks/simd-tiers.sh` 三档 JIT 全量测试 + 算子输出 body SHA-256 逐档比对。修正了 §7.5 设计稿的 `--filter "FullyQualifiedName~Kernel"` —— 它实际只匹配名字里恰好带 Kernel 的用例，不匹配 scalar-twin 测试，现改为跑全量 |
| 性能回退门禁 | **M15 起真的会判，判的是机器校正后的读数**：29 个场景里 **24 个设闸**（阈值 15–48%，由三次运行归一化后的偏差 `max(15, ceil(2.5×S))` 推出），`value` 取三次 `best-of-3` 均值。只比对 `machine` + `binarySha256` + `protocol` 全同的运行，否则打印 "not comparable" 并跳过。校正是每跑自带两个探针（`degradation = max(mem, alu)` 相对基线）；负载验证：4 核抢占下探针报 x1.59、校正后最差 −1.2% 全过，而未校正的原始读数均值 +40%。`--self-test` 把阈值压到 0 并**要求失败**（29 条纳入、exit=1）。仍留 `null` 的是 crop / fill / clip / ICC 两条——归一化后三次偏差 >20%，这台机器的分辨率不够 |
| CC0 语料 | 已实现（M6）：`tests/Fixtures/corpus.tsv` 2 条 Wikimedia Commons `Category:CC-Zero` 真实照片，URL + 实测 SHA-256（2026-11-14 下载校验）；`.cache/` git-ignored 不入库；离线由 `SyntheticCorpus.cs` 的 9 类确定性合成图兜底。实验台 3 张 Unsplash 演示图仍非测试夹具 |
| AOT smoke 覆盖 | M6 补 `warp` / `samplepixel` 两条 hash 与 `REQUIRED` 必需项检查，防止「绿的 gate 什么都没验」；全量 31+ 条目核对留 M15 |

## M5 验收（完成，2026-11-15）

出口条件是「§1.3 七条全部出数，输的条目写清原因」。下表即那七条：**每条都有数字，或有明确的「测不了」理由**。
判定口径不修饰：达标就是达标，没达标就写没达标，测不了的写明为什么测不了。

| # | 维度 | 目标 | 实测（本机，2026-11-15） | 判定 |
|---|---|---|---|---|
| 1 | 端到端吞吐 | 19 个固定场景 ≥ 4.1.3，其中 resize/模糊/合成 ≥ 1.5× | 1080p **19/19 领先、几何均值 2.1632×**（cold 1.7513×）；resize 2.09–2.77×、gaussian-blur 1.75×、equalize 7.20×。**640×360 的历史读数是 12/17**（11-07，最小缩放 0.22×）；**「合成」没有单列对拍场景**，只有本库自己的 composite 计时 | ✅ 达标（按 1080p 记账）；两处口径缺口已写明：小尺寸不达标、合成无对拍 |
| 2 | 稳态分配 | 池化 + 计划复用后 **0 B/op** | 融合路径最强链 `AddPointLut`×3 稳态 **≈4.3 KiB/op**（每线程尺）/ **≈6.0 KiB/op**（进程级）；`degree=1` 仍 2144 B/op。残余是 per-call 计划重建，不是像素缓冲 | ❌ **未达标**（已判定，三次独立运行可复现） |
| 3 | 峰值内存 | 8K ≤ 2.5× 单帧（316.4 MiB） | 4 场景 **0.74 / 2.68 / 1.01 / 1.01×**；`gaussian-blur` 339.0 MiB **超 7.1%**，对手同项 265.9 MiB | ❌ **3/4 达标**，`gaussian-blur` 未达标 |
| 4 | 位深保真 | 10/16 bit 全链路不塌到 8 bit | 16-bit PNG 往返**逐字节精确**（`Png_Rgba16_RoundTripsByteExact`）；10-bit HEIF 端点**测不了**：HEIF 是独立包且需手动注册，本库不接入，也没有 10-bit 语料 | ⚠ 部分：16-bit 端点达标，HEIF 端点按「架构下不可测」记账 |
| 5 | 确定性 | 四档 SIMD 逐字节一致 | **三档**（AVX2 / V128 / 标量）今日实测：各 **963 例全绿**，算子输出 body SHA 同一 `9bd6c8df…`；第四档 `src/` 内 0 处 `Vector512`，未实现 | ⚠ 部分：三档达标，第四档是预留 |
| 6 | 色彩 | ICC→ICC 与 4.1.3 对拍，ΔE2000 ≤ 1（矩阵/TRC）、≤ 2（CLUT） | 尺子已落地：ΔE2000 对 Sharma 34 组全部 ≤1e-4；ICC 1080p 实测 40.0 ms（grid=33）/ 34.8 ms（matrix-TRC）。**与 4.1.3 的同图对拍未做**（对拍场景集里没有 ICC 项） | ⚠ 部分：尺子达标，对拍缺一项 |
| 7 | AOT | 全库 NativeAOT + 零 trim 警告作为构建门禁 | `IsAotCompatible` 全库开启，构建 0 警告；AOT 双档（AVX2 / SSE2）与 JIT **逐字节一致**，55 条 hash / 23 条设闸 | ✅ 达标 |

**总账：2 条达标、3 条部分、2 条未达标。** 两条未达标（0 B/op、8K 峰值）不是「没测」，是「测了、输了」；
三条部分各有明确的缺口：HEIF 端点不可测、AVX-512 未实现、ICC 缺对手对拍。
M5 的出口条件要求的是「全部出数 + 输的写清原因」，据此关闭；**但这不等于七条都达标**——那是产品目标，不是本里程碑的门槛。

## M8 采样三链统一（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| 唯一权重生成器 | 已落地：`Geometry/TapPlan.cs` 的 `TapPlanBuilder.Parameters` / `FillAt` / `ForPoint` 逐字复刻 `WeightTable.BuildCore:54-105` 数学；`Resize.cs BuildCore` 已改为消费它，实测三档既有算子输出字节零变化 |
| 分层冲突 | 已解：`TransferCurve` 在 `SimdImaging.Processing`，`SampleOptions` 在 Abstractions，不能反向引用；新增 Abstractions 镜像 `SampleTransferCurve`（`Srgb=0`），Processing 层 `SampleTransfer` 映射数学。`SimdImaging.Processing.TransferCurve` 六行快照全不动 |
| `Curve` 字段 | 已落地：`SampleOptions.Curve`（默认 sRGB）；`SampleTransfer.ToLinear/FromLinear` 是 per-sample 数学，**不钳 [0,1]**，负值 / NaN / Inf 原样保留；`TransferOps.Apply/BuildTable/Lookup` 整图 LUT 管线未复用（`Lookup:97` 钳位） |
| 统一重建核心 | 已落地（新路径）：`SamplingCore.SampleWithOptions`（含 tap-plan 注入核心重载）走 `TapPlan` 的 5 核 + 域变换 + 预乘 + 冻结累加序 + `OutOfGamut.Clamp/Throw`；`Sampling.WarpWithOptions` / `Image.WarpWithOptions` 与第二波 `Mapping.TransformWithOptions` / `PerspectiveWithOptions` / `Resample.ResizeWithOptions` / `Image.{Resize,Rotate,Affine,Perspective}WithOptions` 六组新重载。旧 `Sampling.Sample` / `SampleRaw` / `Warp` / `Mapping.Transform` / `Mapping.Perspective` / `Resample.Resize` 路径**一行未改**（`ImageGeometryOps.Rotate` 的画布计算提取为 `RotationCanvas` 纯代码移动） |
| **Mapping 接入 TapPlan：分支 B** | 等价扫描（`MappingTapPlanEquivalenceTests`，10 矩阵 × 3 格式 × 全像素）结论：Rgba8/Rgba16 与 legacy **逐字节一致**；Rgba32F 在 33 例中 2 例差 1 ULP（shear / strong-far-band）。根因已定位（`Diagnose_DumpsBothWeightPathsForTheShearCase` 可复现）：权重位模式、乘积、读点与累加顺序全部一致，但 JIT 把 legacy 一次性表达式 `Σw·c` 融合成 FMA 链（乘积无限精度）而循环累加 `r += tr*w` 每项先舍入——实现细节级不对称，跨 JIT 版本不可依赖。**legacy `Mapping` 逐字冻结；仅 `…WithOptions` 走 `MappingTapSampler`**。扫描中发现并修复了符号零分歧（首 tap 直接播种累加器，`(-0)·1` 与 legacy 同号） |
| **零值张力** | `SampleOptions` 是 record struct：全零 == `default` == 该链历史契约（Mapping/Resize 历史 = 预乘）。因此「全零字段的字面统一契约（Straight/Clamp/Encoded/Preserve）不可表达」——需要统一语义时至少设一个非零字段（如 `Edge=Background`），`Alpha` 在统一路径内按字面解释（0=Straight）。显式 `Alpha=Premultiplied`（非零）进入统一路径的 per-tap 预乘管线，与历史 read-premultiplied 数值可能差 1 ULP（量化格式吸收） |
| `ResizeWithOptions` | 非 default 走逐点统一路径：scale-aware `TapPlanBuilder.Parameters`（下采样展宽与 `WeightTable` 同约定）+ `SamplingCore` tap-plan 注入核心 → 5 核 / LinearLight / Premultiplied / `Edge=Background`（越界 tap 换背景、**权重不重归一化**）/ OutOfGamut 全部支持。逐点路径无行带 scratch 与向量层（语义优先，吞吐留 M15）；`default` 转发冻结的 `Resample.Resize` |
| 零既有字节变化证据 | 剥离 AotSmoke 新增 5 行（`warp.linear` / `warp.premul` / `resize.options` / `rotate.options` / `perspective.options`）后，既有算子 body SHA-256 仍为 `44390203…6763`（实测）；三档新 body SHA `7a7e74fe…` 一致；668 tests green（M8 第二波后）；AOT 三路 `diff` 无差 |
| 快照 | 两次显式重生成 + 逐行 review：M8 合计新增 `SampleOptions.Curve`、`SampleTransferCurve`、`SampleOptions` 类型行、`Sampling.WarpWithOptions`、`Sampling.SampleRaw`、`Image.WarpWithOptions`，第二波 `Image.{Resize,Rotate,Affine,Perspective}WithOptions` + `Mapping.{Transform,Perspective}WithOptions` + `Resample.ResizeWithOptions`；**0 删除 0 改写** |
| 参考向量 | `sample-options-encoded-midpoint`（encoded 中点 = 128）、`warp.tap.bilinear-phase`（0.37 相位 TapPlan 权重 → 94，独立 f32 推导）、`resize.background-edge`（1×1→2×2 背景场 → 143×4，独立推导）；三条均有执行断言（`GeometryOptionsContractTests.Vectors_ExecuteByteExactly` / `SampleOptionsContractTests`）；M6 三条 `warp-identity-*` 未改动 |
| **仍未做** | `ColorConversionOps.Sample:191-196` 第 4 插值器明确排除；HDR 背景语义（`RgbaColor` 为 8-bit）仍为已知边界，留 M13；WithOptions 的向量/吞吐优化留 M15 |

## M9 EWA / 自适应抗锯齿（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| 入口形态 | Wall 拍板「仅解析变换」：`SampleAaMode{None=0, Elliptical=1}` 只在 Rotate/Affine/Perspective 的 WithOptions 入口生效（affine Jacobian = 逆矩阵 2×2 常量；perspective = 逐像素解析 `∂sx/∂px=(ia·w−N·ig)/w²`，double 序冻结）；`WarpWithOptions` 传非 None **显式抛**（map 是用户 lambda，无解析导数；AGENTS.md:95 禁静默回退）；`Nearest+Elliptical` 显式抛（点采样无核可适配） |
| 足迹数学 | `Q=JJᵀ`、`R=Q⁻¹`、`w=exp(-0.5·dᵀRd)`、2σ 包围盒（半径 `2·sqrt(λmax(Q))`）按 tap 预算截断（`dᵀRd≤4`）、double 归一化后由累加器一次 f32 截断（TapPlanBuilder 纪律）；权重缓冲按 `(maxTaps+1)²` 租（半整数相位会多出一行一列，2026-11-14 修）。**2026-11-14 勘误**：此前的 `Q=MᵀM` + `k²` 把缩小变成了锐化（2× 缩小 σ≈0.35 px），已按 Heckbert 足迹定义改正；legacy 路径不受影响（剥离 6 条 M8/M9 smoke 行后 body SHA 仍为 `44390203…6763`） |
| 退化显式处理 | `\|det J\|<1e-6`、`detQ` 非有限或非正、或轴比>64（初始值，待测 `ewa.aa.*`）→ `EwaStatus.DegenerateIsotropic` 返回给调用方 + 回退 `TapPlanBuilder.ForPoint` 各向同性（`DegenerateJacobian_…` 测试断言回退输出逐字节等于各向同性路径）；绝不静默换语义。NaN/Inf 在 `Build` 入口用 `!(detQ > 0)` / `!(Math.Abs(det) >= ε)` 直接拒绝（比较式取反，NaN 也落入退化分支） |
| SIMD 决策 | **M9 不引入 EWA 的 SIMD 层**（偏离原计划的「SIMD 权重累加 + scalar twin」）：M8 等价扫描证明「legacy 表达式 FMA 融合 vs 循环累加不融合」的形状不对称会造成 1 ULP 分歧；EWA 是新路径无 legacy 对照，纯标量循环形状（Resize 标量尾三档一致的事实）跨档确定。SIMD twin 顺延 M15，known-gaps M7 的「M9 引入 EWA 权重 SIMD 时补 twin」承诺**不触发**（M9 未引入 SIMD） |
| 质量判据 | `EwaPrecisionTests`：16×16 里一条单像素白线 0.25× 缩小，EWA 四行都必须保留线的能量（每行 1–120、列能量 20–140），legacy 双线性在同相位丢失该线（≤1）——证明「缩小真的在模糊」而不是点采样；`(maxTaps+1)²` 缓冲边界单独一条；`ewa.identity`=96 / `ewa.minify`=128 两条独立推导向量逐字节执行一致；2D 权重走 `DoubleScratch` per-call Rent/Return（零稳态分配纪律） |
| **待测登记** | `ewa.aa.tier.1..3` 分档边界、`ewa.aa.tap.budget` 上界、高斯 k 曲线——全部待摩尔纹语料（SyntheticCorpus high-frequency / thin-diagonal-lines + cc0-architecture）扫描回填（探针 P4）；回填前分档值是初始值不是调优结论 |

## M10 执行架构：真正融合（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| 入口形态 | `ProcessingPlan.ExecuteWithOptions(source, options[, out report], pool, observe)` + `Image.ApplyWithOptions(plan, options, pool, observe)`；旧 `Execute` / `Apply` 保持原签名，等价于默认 `ExecuteOptions`（融合开、无诊断、无上限、不取消）。用 `…WithOptions` 而不是给旧方法加重载：`Execute(view, null)` 在 `IPixelPool?` 与 `ExecuteOptions?` 之间会变成二义调用 |
| 融合语义 | 连续 `Point`/`Neighborhood`/`Separable` 步骤为一段；`Global`/`Composite` 是整图段边界。段内按 band 窗口递归：`R_k = Σ_{j>k} halo_j`，第 k 步写 `band ± R_k`、读上一步写过的同一矩形；所有视图（含输出）都 Slice 到同一 `winTop` 原点，rect 是「全局行 − winTop」。每 worker 两张窗口 ping-pong，`count == 1` 不融合。输出与逐步骤路径**逐字节相同**，由 `FusedExecutorEquivalenceTests` 的 18 例覆盖（含 bandRows 1/2/7/64、degree 1/2/4、Rgba16/Rgba32F） |
| `Composite` 改判 | 旧 `Execute` 只把 `Global` 当整图，`Composite` 走 band 循环——而 `PlanSteps.Global` 适配的合成 kernel 会写整幅目标，多 worker 并发写同一目标属竞态。`Composite` 现在与 `Global` 一样整图执行；全仓此前没有任何 `Composite` 步骤，字节与行为不变 |
| 回退与报告 | `PlanObserver` 存在 → 强制逐步骤（`ObserverPresent`）；`AllowFusion = false` → `AllowFusionDisabled`；链中有整图步骤 → `CategoryBoundary`；空链 → `EmptyPlan`。`PlanReport` 只在 `Diagnostics = true` 时构造（段/回退原因/worker 数/band 数/池租借/ticks），诊断关闭时不建对象、不读 `Stopwatch`。`Segments[i].FirstStep` 始终是**原始**步骤下标（折叠后的执行步会映射回去） |
| LUT 折叠 | `ProcessingPlan.AddPointLut(name, 1024 字节表)`：连续表步骤按 `T_AB[c][v] = T_B[c][T_A[c][v]]` 折叠成一张、单遍执行。构造性恒等（逐步执行本来就在字节域量化），`PointLutCompositionTests` 用真实点算子取表验证。**Rgba8 only**：其它格式在 kernel 入口抛 `NotSupportedException` |
| 内存模型 | 融合段 = 1 张整图输出 + 每 worker 2 张 band 窗口；逐步骤 = 每步 1 张整图（活动集 2 张）。`MaxWorkingBytes` 用前者估算并在执行前抛 `InvalidOperationException`（消息含预算与估算值），估算不是测量。窗口按 worker 租、按「全局最大 windowRows」租（band 0 更短），kernel 在 worker 抛时输出与窗口都在 `finally` 释放 |
| 仍未做 | 批处理并发（`MaxConcurrentImages` / `ImageBatchRunner` 没有调用方，未引入）；几何算子进 Plan（`Add` 仍拒绝）；Rgba16/Rgba32F 的 LUT 折叠；融合段的 SIMD；`IImageOp` 仍零实现（`PlanStep` 不引用它）。**halo 必须等于算子真实支撑**：给小时融合与逐步骤本来就会给出不同字节，这不是等价性测试的覆盖范围 |
| ✅ 已判定（未达标） | 「点算子融合后分配 = 0」（M2 出口条件）：2026-11-15 在融合路径上重测，稳态仍 ≈4.3 KiB/op（`AddPointLut` LUT 折叠、§1.3 字面尺）/ ≈6.0 KiB/op（进程级），**未达标但已出数**。见 §「M2『点算子融合后分配』重测与判定」 |

## M11 高级照片算法 I（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| 入口形态 | `NonLocalMeansOps.NonLocalMeans` + `Image.NonLocalMeans`；`LocalLaplacianOps.LocalLaplacian` + `Image.LocalLaplacian`。NLM 是 `OpCategory.Neighborhood`（halo = `searchRadius + patchRadius`），可切带、可融合；LL 是 `Global`（整幅金字塔），必为融合段边界，`PlanReport.FallbackReason = CategoryBoundary` 有断言 |
| NLM 契约 | 精确、无剪枝：候选 dy→dx 升序，double 累加，`(float)(acc/sumW)` 一次截断；`w = exp(−max(d²_norm − 2σ², 0)/h²)`，`d²_norm` 已按 code range 平方归一（否则同一 `h` 在不同位深表示不同强度）；straight alpha，alpha 参与距离与平均（与 `Bilateral` 同一条取舍）。Rgba8 用 int32 整数距离（可结合 ⇒ AVX2 与标量同值，有孪生测试）；Rgba16 用 long（int32 在 65535² 就溢出）；Rgba32F 用 double 冻结序 |
| NLM 成本 | 1080p Rgba8 实测：默认 **S=3/P=1 5.8 s**，质量档 **S=5/P=2 16.1 s**（本机 best-of-1 一次预热）。默认值按实测从计划的 S=5/P=2 下调——计划里「SIMD 后 0.7–1.2 s」的估计低了 13×。SIMD 化 SSD 后 **exp 是地板**（每候选一次 `Math.Exp`，~2.5e8 次/1080p），整数 d² 的 memo 对噪声图命中率低，未做；剪枝不做（会改变结果） |
| NLM 的 halo 前提 | 可切带依赖四条同时成立：整幅宽视图、对**自己收到的视图** clamp、无整图统计、行环按 `start−(S+P)` 播种。第四条照 `Bilateral` 的 `computed = start - 1` 写必错，`3×3 图 + S=5` 的用例专门守它 |
| LL 契约 | Paris remap（`α=β=1` 为恒等）；**按层选样**（第 l 层用输入 G 金字塔第 l 层的强度挑最近样本，用全分辨率选样会让粗层选错、出阶梯）；**基带用原始 G_{L-1}**，不是 remap 后的残差——否则常数图会被最近的采样强度拖走；8/16-bit 用按 code value 索引的每样本表（与直算逐字节一致，有 `ForceDirectRemap` 对拍），Rgba32F 逐像素 `Math.Pow`；层原生存储 |
| LL 增益与量测 | 细节增益由「采样间距 / σr」决定：默认 32 样本、σr=0.15、α=0.3 实测 **1.5×** 纹波放大（`BoostsLocalContrastWithoutMovingTheMean` 的阈值按此定 1.3×）。**未做样本间线性插值**——它能把量化台阶抹平，但不会显著提高斜率；提高增益的现实手段是加 `samples` 或降 `σr`，两者都写在 xmldoc |
| LL 内存与耗时 | 层原生（4/3 张图）而不是每层整幅。1080p 实测 **1.81 s / 分配 139.7 MB**（`SimdImaging.Benchmarks`，默认 L=5/S=32）；4K 未实测，按面积外推约 560 MB。每层整幅会到 2.9 GB 且结果一样。算子自带 `MaxScratchBytes = 2 GiB` 硬上限，超了抛 |
| ⚠ `MaxWorkingBytes` 盲区 | `ProcessingPlan.EstimateWorkingBytes` 只算 plan 自己的缓冲，**看不到算子内部 scratch**（`Glow` 早已如此）。`ExecuteOptions.MaxWorkingBytes` 对 NLM/LL 不构成真实约束，只有算子内部的 2 GiB 上限生效。**M14 已修机制**：`Add(..., haloX, haloY, scratch)` 加了一个 6 参重载（旧的 5 参签名原样保留，快照 0 改写），`MemoryEstimator` 把它算进每 worker 项。剩下的欠账是**给 `Glow/GuidedFilter/NLM/LL` 各补一个 `ScratchBytes` 声明**——机制在、接线未全 |
| 跨 ISA | `Math.Exp`（NLM 权重）与 `Math.Pow`（LL remap）沿用 EWA 口径：x64 实测位模式稳定，ARM 未在硬件执行——不宣称跨架构已验 |

## M12 反卷积与内容感知缩放（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| 入口形态 | `RichardsonLucyOps.RichardsonLucy` / `.RichardsonLucyMotion` + `Image.` 同名两个方法；`SeamCarving.RemoveSeams` / `.InsertSeams` / `.CarveTo` + `Image.RemoveSeams` / `.InsertSeams` / `.SeamCarve` |
| RL 契约 | `e₀=I`；每迭代 `c=e⊛K`、`r=(I+ε)/(c+ε)`、`e←e·(r⊛K)`，`ε=1e-6`。**固定迭代次数**而非「相对变化 < 阈值」：后者是整图归约，band 并行下迭代次数会随 worker 切分变化，等于换了一个答案。文档给的是实测 PSNR 峰值（高斯 10.55→13.58 dB，运动 7.23→11.52 dB），不是猜的默认值 |
| RL 的处理域 | **straight RGB，alpha 原样拷贝**。RL 是乘性比值更新而非线性组合：对预乘 RGB 反卷积等于在反卷积覆盖率，半透明边缘会在 alpha 上振铃。这与本库「线性滤波一律预乘」的纪律**故意不同**，理由写在 xmldoc 与 `operators.md` |
| RL 的 PSF | 高斯：`radius=ceil(3σ)`，double 归一后一次截断到 f32。运动：**取 `MotionBlur` 的同一套整数偏移**（`MathF.Round(t·cosθ)` + 重复计数归一），所以 `RichardsonLucyMotion(n,θ)` 是 `MotionBlur(n,θ)` 的真逆对；稀疏 tap 表长度≈`length`（15 对稠密 225） |
| RL 的确定性 | `PlanarConvolve` 的向量轴是**像素**、每 lane 按 tap 升序累加，与标量孪生逐字同序 ⇒ D1 由构造保证。档位矩阵实测三档一致；另有 `ForceScalar` 孪生测试守着（`MathF.Cos/Sin` 沿用 EWA 口径：x64 实测稳定，ARM 未在硬件执行） |
| RL 的成本 | 1080p Rgba8 实测：σ=1.5 十次迭代 **254 ms / 分配 95 MiB**；运动 len=15 十次 **347 ms**。四组平面（输入/估计/预测/修正）+ 每 worker 行带 scratch；1080p 约 38 B/px。超 2 GiB 直接抛，不静默 OOM |
| Seam 契约 | 能量 `E=|L(x+1)−L(x−1)|+|L(x,y+1)−L(x,y−1)|`，边界 clamp。8/16-bit 用整数 luma `(54R+183G+19B)>>8`（权重和 256）⇒ 能量与 DP 全是精确整数，**D0**；Rgba32F 用 float luma + double DP（D1）。像素只被**搬运**不被重采样，保留下来的像素字节不变 |
| Seam 的平局规则 | DP 比较 up→left→right 且只在**严格更小**时替换；起点取末行最左最小 ⇒ 平坦图恒删第 0 列。这条规则可测（`Ties` 用常量 RGB + alpha 阶梯构造零能量场，断言删掉的是第 0 列），不是「差不多」 |
| Seam 的方向与插入 | 水平缝 = 转置 → 删竖缝 → 转置：转置是纯置换，且 `|Δx|+|Δy|` 在转置下对称，数学上等价于另写一份横向 DP。插入时把已插入路径在掩码里标脏（`E=+∞`），k 次插入得到 k 条不同路径，而不是同一条复制 k 次 |
| Seam 的成本 | 每条缝一次全图能量 + DP：1080p 删 50 列 **502 ms（≈10 ms/缝）/ 分配 4 MiB**。4K 未实测 |
| **增量能量更新：实测否决** | 移除一条缝会把它右侧所有像素左移一位，受影响列集合的宽度由该缝的 x 跨度决定、**无上界**；DP 的影响锥每行扩散一列，整高缝到底行就覆盖全宽。所谓「增量」仍要碰约半个图，省不下一个数量级，却多一条可能与全量结果不一致的代码路径。文档在 `RemoveSeams` 的 xmldoc 里写明理由 |
| ⚠ RL 的 halo 随迭代增长 | RL 作为计划步时支撑是 `iterations × PSF 半径`，不是 PSF 半径：第 2 次迭代的估计依赖第 1 次在 ±r 内的结果，逐次外扩。声明成一个半径时，融合/逐步两条路径都对（它们用同样的窗口），但 band 边界会比整图调用差 1 LSB——`RunningAsAPlanStep_MatchesTheWholeImageCall` 是唯一能抓到的测试，`RequiredHalo` / `RequiredHaloMotion` 是给调用方的正确值 |
| Seam **不是** plan step | 它改变尺寸，与 `Crop`/`Resize` 同类，不进 `ProcessingPlan`（plan 按 `width×height` 租缓冲）。`operators.md` 有 GEO-4 与 GEO-21 的逐条对照，防再次混淆 |
| FFT 反卷积 | **评估后不落地**，见 [`fft-evaluation.md`](fft-evaluation.md)：1080p 补零 2048² 每次迭代 4 次变换 ≈1.84 GFLOP，是直接卷积（9.1e7）的 20×；交叉点 σ≈18 px；且蝶形顺序随档位变，D1 只能靠冻结标量 FFT，正好抵消优势 |
| ⚠ `MaxWorkingBytes` 盲区 | 与 M11 同一条：`EstimateWorkingBytes` 只算 plan 自己的缓冲，看不到 RL 的 95 MiB 平面。**M14 起可用**：`RichardsonLucyOps.ScratchBytes` 是声明函数，`plan.Add("rl", …, scratch: RichardsonLucyOps.ScratchBytes)` 之后 `MemoryEstimator` 会把它算进每 worker 项（1080p 单步计划 = 48 B/px × workers）；不声明则仍然报 `HasEstimatedTerms = false`，不假装知道 |

## M13 HDR GainMap 与 ICC LUT（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| ICC 入口 | `IccTransformOps.Transform` / `Image.IccTransform` 签名不变，行为扩展：现在的解析器按方向分别选 LUT 或 matrix/TRC，LUT 优先（CMM 语义） |
| ICC 的数值契约 | 1D 表与 CLUT 全整数：u16 网格、Q15 权重（`0..32768`，允许整 1）、int64 累加、最后一次 `(acc + 16384) >> 15` 舍入。**CLUT 段因此是 D0**；整条 `Transform` 因 PCS 缝合与目标端 matrix/TRC 的 float 路径登记 **D1**，不宣称整链 D0 |
| ⚠ PCS 编码有三套，按 tag 类型分 | `mAB`/`mBA`：`L*=v/65535·100`、`a*=v/257−128`、XYZ 为 u1Fixed15（`0x8000 = 1.0`）；`mft2`：遗留编码 `L*=v/65280·100`、`a*=v/256−128`；`mft1` 经 ×257 展宽后 `L*` 与 `mft2` 同、`a*/b*` 与通用同。把三套混成一套会让 L* 差 0.04、a*/b* 差半个单位，且**没有任何字节级像素测试能抓到**——只有 `IccProfile_Lut16MidGreyIsHalfOfFullScale` 这类直接对码值的常量测试能 |
| ICC 的加速：行带并行，**不做 per-pixel SIMD**（实测否决） | 实测 1080p：`grid=33`（215 KB 的 CLUT）**40.0 ms**、`grid=2`（24 个样本，全在 L1）**36.5 ms**、同图的 matrix/TRC **34.8 ms**。也就是说整个 CLUT 的数据量与取址只值约 3.5 ms（≈9%），剩下的是格式转换与共享的 sRGB/PCS 数学。再叠一层：3D CLUT 的累加需要 34 位，**AVX2 没有 64 位整数乘（`vpmullq` 是 AVX-512DQ）**，int64 的向量乘会被 JIT 展开成标量序列；8 角点又是 gather。两头都不利，所以第二阶段的结论是**只保留标量核 + 行带并行**，向量化留到 M15 的 AVX-512 评估（那里 `vpmullq` 才存在） |
| ICC 的语料缺口 | 测试用的 LUT profile 是 `SyntheticIccProfiles` 手写字节构造的（`mft2` 恒等/常量、`mAB` 全阶段、`mft1`+Lab、`mBA`、以及各拒绝面），期望值手算或由 `ColorMath` 独立算出。**没有可分发的真实扫描仪/打印机 profile**，因此"已对拍真实 profile"这句话不成立；`DeltaE2000` 对独立双精度参考的均值/最大值也未登记成阈值（构造向量逐字节一致已覆盖） |
| ΔE2000 | `ColorDifference.DeltaE2000` 两种形态（单色对、整图 Mean/Max）。正确性判据是 Sharma et al. (2005) 的 34 组参考对，全部误差 ≤1e-4；这 34 组里包含 4 组正好落在 180° 色相不连续上的用例 |
| GainMap 承载 | 新成对类型 `GainMappedImage`（`Base` + `GainMap` + `GainMapParameters`，拥有两张图）。`GainMapParameters` 放在 Abstractions，`SimdImaging` 在文件边界与 codec 的 `GainMapMetadata` 互转 —— `Processing` 永远看不到 codec 类型 |
| ⚠ 几何联动的派生规则 | 本仓 `AffineMatrix` 与 homography 都是 **source→destination、列向量、平移在第 3 列**，所以 `M_gain = T⁻¹·M_base·S`，闭式（非两次矩阵乘，否则对角项 `A·sx/sx ≠ A` 会破坏逐字节）。Crop 的增益矩形用整数 floor/ceil（round 会丢边列）；`Rotate` 的画布从**base 画布**派生，不对 gain 尺寸重跑 `RotationCanvas`（那里的 `Ceiling(-1e-4f)` 会让两张图各走各的）。`Affine_ConjugationMeansTheSameMappingInBothPixelGrids` 是唯一能抓到方向写反的测试 |
| 未配对的操作 | `Trim` / `EntropyCrop` / `SeamCarve` 由内容决定，两张图会裁出不同区域；`Pad` / `Tile` / `ResizeCanvas` / 锚定式 `Resize` / `ResizeToWidth·Height` 用的是每图自己的画布规则；`RoundCorners` 是 alpha 掩膜合成。这些**故意不提供**，`ThePairDoesNotOfferOperationsWhoseAnswerTwoRenditionsWouldDisagreeOn` 用反射守着，防止以后被"顺手"加上 |
| 像素空间 `Warp` 被拒 | `PixelWarp` 是 destination→source 的**像素**坐标委托，没有解析逆，两张图各按自己的像素数解释会得到两个映射。配对类型只给 `NormalizedWarp`（归一化坐标，同一个函数对象交给两张图） |
| Ultra HDR 的读写 | 读：`JpegGainMap.TryRead`，Base/GainMap 各走一次 `Image.FromDecodedImage`；写：base 与 gain 各编一张 JPEG 再 `JpegGainMap.Assemble`；建：`JpegGainMap.Compute`。**不需要 `ImageCodecs.Register`** —— gain-map 解码器已在内置注册表里（codec README 的 facade 表 + `Sdcb.SimdCodecs.xml` 的 facade 摘要），而 `Register` 只用于包未引用的 codec（今天只有 HEIF），且注册项排在所有内置之后，注册它反而会被普通 JPEG 抢先 |
| `Image.Load` 对 Ultra HDR 的既有行为 | 已实测并写成测试：facade 的 `TryDecode` 返回 `GainMapDecodedImage`（`SampleFormat = Float32`），所以 `Image.Load` **本来就给出 Rgba32F 的 HDR 重建值**，`LoadOptions.Format = Rgba8` 才落到 SDR 底图。M13 没有改这条路径，只是把它变成断言 |
| `Image.Encode` 仍存不了 Rgba32F | `WorkingFormat.ToPixelFormat` 对 float 抛异常。配对类型存盘走 Ultra HDR（base 是 8/16 位），要存重建出的 HDR 得先 `ToneMap` + `Delinearize` 到 8/16 位，或用 TIFF/PNG 的浮点路径（未接）。`GainMappedImage.ToHdr` 的 xmldoc 写明 |
| 旧 `HdrOps.ApplyGainMap` 冻结 | 4 参简化模型（固定 sRGB 解码、单一 headroom、无 offset/gamma/capacity）**一个字节都没改**，与 hdrgm 的 `ReconstructGainMap` 并存；`ApplyGainMap_KeepsItsPublishedFormula` 守着它不被顺手"统一"掉 |

## M14 内存预算与预估 API（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| 公开面 | `MemoryBudget.Snapshot()` / `Trim()` / `SetLimits(...)` + 纯函数 `MemoryEstimator.Estimate(...)`。快照里 `Scratch` 是**调用线程**的数（存储是 `[ThreadStatic]`，这是无锁与逐字节确定性的前提）；`WeightCache` 是进程级共享 |
| FloatScratch 预算 | 每线程 **64 MB** 保留、**32 MB** 单块上限，默认值；超单块上限的缓冲**仍然分配并正常使用**，只是用完不留在缓存里 —— 这条是刻意的，否则 8K resize 每次都要重分配。淘汰按"最久未用"（空闲表只存归还顺序，表头即最旧） |
| 权重表缓存 | 从「24 条 + 溢出时 `Cache.Clear()`」改为**字节预算 8 MB + 64 条上限 + LRU**。旧策略在超过 24 种尺寸时会把整张缓存清空，于是每次 Resize 都重建全部表；现在只淘汰最久未用的那一条。命中/未命中/淘汰计数可从快照读 |
| 预估模型 | `InputBytes`（调用方的源）/ `OutputBytes`（执行器的目标）/ `PlanScratchBytes`（融合窗口 = `租借峰值 − 一张目标图`）/ `PerWorkerBytes`（各步 `Scratch` 声明里**最大**的那个，不是求和：一个 worker 一次只跑一步）/ `PeakBytes = 源 + 租借 + 每worker × workers`。workers 与 `RowParallel` 同规则（每 band 一个，受 degree 与 band 数双重限制） |
| 精确 vs 估算 | 前四项里前三项是执行器自己的算术（`checked`，溢出抛异常不绕回）；只有 `PerWorkerBytes` 依赖算子声明，`HasEstimatedTerms` 明确标出来。没有声明时它报 0 而不是猜一个数 |
| 验收口径 | 用 `CountingPool.PeakBytes`（执行器实际租借的字节峰值）对拍：`PeakBytes ≥ 实测 + 源`，三个尺寸（64×48 / 1920×1080 / 200×1080）全过。另有一条把融合算术**锁成常量**的测试（两个 point 步 + 64 行 band + degree 4 → `2×4×491520`），因为「融合总是更省内存」是错的：每 worker 两个窗口在 point 步上比一张全图中间结果更贵 |
| 一条不能碰的边界 | `ProcessingPlan.Add` 的 5 参签名**原样保留**，新增的是 6 参重载。把 `scratch` 直接加到旧签名上会改写公开快照里的一行——那违反"快照只允许新增"的纪律，即使源码层面兼容 |
| 与预算无关的输出 | `ABudgetCannotChangeAnOutputByte`：把两个预算都压到 0（每次重分配、每次重算权重）后，resize 输出与基线逐字节相同；恢复默认再跑一次仍然相同 |
| 实测（2026-11-14，`SimdImaging.Benchmarks` 的 `memory:` 行） | 1080p 连续 40 个各不相同的尺寸 → 权重缓存**顶到 64 条 / 1.98 MB**（字节预算生效，24 次淘汰），命中率 8.3%；随后 12 个尺寸重复 4 轮 → 累计命中率 **41.7%**。scratch 侧：保留 **14.65 MB / 5 块**、命中率 **98.8%**；`Trim()` 之后两项都归 0 |
| 仍缺 | `Glow` / `GuidedFilter` / `NLM` / `LL` 还没有 `ScratchBytes` 声明；直接调用算子（不进 plan）时没有预估入口，只有 `RichardsonLucyOps.ScratchBytes` 这类公开函数可自己算 |

## M15 性能回退门禁（判定口径于 2026-11-15 重做）

| 项 | 现状 |
|---|---|
| 基线文件 | `benchmarks/baselines/windows-10.0.19045-x64-16-m15.json`：29 个场景，**24 个设闸**（15–48%），`value` 取同一 binary **三次** `best-of-3` 的均值，并带一个 `calibration` 块。2026-11-15 重录，指纹 `6042a84c…` : `93cabe0a…` |
| 为什么不再判绝对毫秒 | 2026-11-15 实测：同一份源码、同一台机器，04:00 与 07:00 两跑，**黑盒参照 ImageSharp 4.1.3 的 resize-bicubic 从 12.5 → 19.3 ms（1.54×）**，我方同项 4.97 → 7.91 ms（1.59×），29 场景整表慢 1.2–1.8×。参照不受本库代码影响，故变的是机器。绝对毫秒在这种漂移下只能二选一：误报，或把真回退埋在膨胀的基线里 |
| 归一化 | 每跑在首尾各测一次两个**不调用本库**的探针（256 MB 并行流式求和 / 32M 次 xorshift），各取 best-of-7、再取两端更快的读数。判定用 `got_raw / degradation` 对 `baseline.value`，`degradation = max(run.mem/base.mem, run.alu/base.alu)`——哪种资源被抢就按哪种校正；探针不动时 `degradation = 1`，等价于按原始读数判。失败行同时打印 raw 与 normalised，读的人能分清「机器漂」与「代码回退」 |
| 阈值来源 | 三次运行各自先按自己的 calibration 归一化，取三次的最大相对偏差 `S`；`threshold = max(15, ceil(2.5 × S))`，即门槛至少是实测噪声的 2.5 倍；`S > 20%` 留 `null`（crop / fill / clip / icc grid=33 / icc matrix-trc 五个）。11-14 那版「一对记录 ≤10% 才设 15%」已废弃——它用**一对**记录把当天恰好安静的 15 个场景当成稳定场景；同一份代码在 11-15 的三次运行里，bicubic 的归一化偏差就到 15% |
| 自检 | `--self-test` 把阈值压到 0 并要求**非零退出**：实测 `comparable-baselines=1 enforced=29`、exit=1、6 条报超（最差 +34.4%）。正常模式 exit=0、`enforced=24`、最差 +4.1% |
| 负载验证（整套设计成立与否的判据） | 人为起 4 个忙循环抢占 4/16 核：`alu` 探针报 **x1.59**，门禁按 x1.59 校正，**24 条设闸场景最差 −1.2%、全部通过**；同一负载下未校正的原始读数均值 **+40%** |
| 可比性 | `machine`（`OS|arch|核数`）+ `binarySha256`（Processing 与 Abstractions 两个 dll）+ `protocol`（含 `slow=` 与 `norm=max(mem,alu)`）全同才算可比；`repeats` 只在读取基线时决定本次跑几次，**不参与比较**（脚本头注释旧称「含 repeat count」，以实现为准）。任一不同就跳过并打印原因——跨机器的秒表读数不是门禁 |
| 已知局限 | 校正只消掉「跟探针同向退化」的机器状态。若退化只影响某类访存而两个探针都不动，仍会误报——失败行里 raw 与 normalised 的差值就是识别它的线索。探针不调用本库，所以「代码慢到把探针一起拖慢」不会发生 |
| 已知近似 | `machine` 不含 CPU 型号，两台同 OS/同核数的机器会撞 id；`binarySha256` 因此成为第二道闸，但换编译器版本后旧基线同样会"not comparable"而静默跳过——**基线需要随 binary 一起更新**，这是设计而非疏漏 |
| 第四档（AVX-512） | **已出评估，未出数**：`docs/simd-tiers-evaluation.md`。关键结论是今天的代码在有 AVX-512 的机器上跑的仍是同一套 `Vector256` 内核（`Vector512.IsHardwareAccelerated` 未用），所以"四档一致"不构成任何 512 位验证。值得写 512 位的只有 `Median3x3` 与 `Convolve2D`（NLM 次之，ICC CLUT 明确不做），且必须运行时分派，`IlcInstructionSet=avx2` 不能动 |
| AotSmoke 全量核对 | **已核对**：53 条 hash，`REQUIRED` 23 条，**无悬空项**（`REQUIRED ⊆ hashed`，0 条 REQUIRED 没有对应 hash）。M15 点名的六类都在闸内：`warp` / `ewa.rotate` / `nlm` / `gainmap.hdr` / `icc.lut` / `plan.fused`；`dt` 与 `guided.cross` 于 2026-11-14 补入。其余 30 条只 hash 不设闸，属信息性覆盖 |
| 契约表 | **已完成**：`docs/contracts.md` 30 条，每条锚定一个测试类；`ContractsTests` 反射校验类存在、且该类至少有一个 `[Fact]/[Theory]`，同时校验"恰好 30 条、编号 1..30 连续"。它不解析 markdown——解析只能证明文件与数组一致，数组才是"必须存在"的那份清单 |

## Distance Transform（完成，2026-11-14）

| 项 | 现状 |
|---|---|
| 已落地 | `DistanceTransformOps.SquaredEuclidean(PixelView, threshold, pool)`：Felzenszwalb 抛物线包络，两遍可分扫描 O(N)；alpha 为掩膜通道（≥0.5 为前景）；输出 Rgba32F，R/G/B 为**平方**距离、A=1。门面 `Image.DistanceTransform(...)` 经 `ReplaceBuffer`，格式取自返回缓冲（Rgba32F）而不是输入 —— 与 `ColorSpaceConvert` 一样属于拓宽算子，格式重贴标测在 `DistanceTransformTests` 里 |
| 为什么输出平方值 | 平方距离在 2²⁴ 以内是精确整数 ⇒ 整个算子 **D0**，测试可与手算/暴力扫描逐位对拍，不靠容差；需要距离本身时调用方 `MathF.Sqrt` |
| 测试 | `DistanceTransformTests` 8 例：单行 `[4,1,0,1,4]`、3×3 角上 2、全前景 0、空蒙版返回极大值、阈值越界拒绝、9×7 图案与 O(n²) 定义逐像素对拍、3×40 高窄图（越界回归）、门面返回 Rgba32F 且字段同上 |
| 门禁 | AotSmoke 的 `dt` hash + `REQUIRED`：三路（JIT / AOT-avx2 / AOT-SSE2）同为 `6584AC8BE598B04F`，**列遍改写成 tile 之后仍然不变**（逐字节前提）；`docs/contracts.md` 第 30 条锚定 `DistanceTransformTests`；`gates.sh` 全绿（**899 例 / 0 警告**，快照只增门面一行） |
| 实测（1080p，8px 棋盘蒙版，分配 45 KiB） | 分块前 **43.1 ms**（43.6 / 42.6），分块后 **40.6 ms**（39.5 / 40.7 / 40.8 / 41.8 / 40.1 五跑）—— **−5.8%**。仍留 `null`：更早那对 **41.0 / 49.9**（+21.7%）就是「门槛会被自己的实测打穿」的反例 |
| 分块转置（已做，收益有限） | 列遍改为 64 列、列主序 tile：源行一次读 256 B 而不是每元素一条缓存行，tile 的 64 条目标行跨行驻留 L1，扫描在 L2 里跑（1080p 276 KB）。**只值 5.8%** ⇒ 瓶颈不在访存，而在两遍 1-D 扫描本身：每样本一次除法（`Intersection`）加数据相关的回溯 / 前进分支，串行依赖，SIMD 无从下手。此前「列方向跨整行访问是主要成本」是推断，已被这次 A/B 推翻 |
| 高窄图越界（已修，P0） | `height > width` 时列遍把 `height` 个样本写进按 `width` 租的 scratch，直接 `IndexOutOfRangeException`；此前所有用例都是宽 ≥ 高（5×1、9×7、64×48……），从未覆盖。修复 = scratch 取 `max(width, height)` + 列遍不再借用行缓冲；`ATallImageIsTransformedColumnByColumn`（3×40）修复前确实抛，现钉住 |
| 同批（2026-11-14） | ①**cross guided filter 已补**：`GuidedFilterOps.GuidedFilter(source, guide, ...)` 与门面 `Image.GuidedFilter(guide, ...)`，`a = cov(I,G)/(var(G)+eps)`、`b = mean(I) − a·mean(G)`；guide 与 source 是同一块内存时回落自导向老路径（逐字节相同，`GuidedFilterTests` 5 例钉住，含 window 参考对拍）。4K r=4 实测 **774 ms / 分配 17 MB**（自导向同次 582 ms / 10.7 MB）。②**domain transform 已评估、不落地**：`docs/domain-transform-evaluation.md`（54 个平面遍 + 每像素 18 次 exp，卖点已被箱均值 O(1)/px 覆盖） |
| 口径 | `operators.md` 记 `ANL-9`、总数 119；`docs/contracts.md` 30 条；M15 基线已重录：`guided.cross` 设 15% 门槛（两跑 2.4%），`dt` 在文件里但留 `null` |

## P0 正确性与采样精度（M7，2026-11-14）

| 项 | 现状 |
|---|---|
| `ProcessingPlan.Execute` 缓冲所有权 | 已修：Rent 与 `owned` 移交之间的无主窗口由显式 `pending` 消除；kernel / band worker / observer 任一抛异常时，`pending`（当前帧）与 `owned`（上一帧）各释放一次、互不重叠。`ProcessingPlanLeakTests` 用计数 `IPixelPool` 验证 `Rent == Dispose`、重复释放 0（5 例） |
| `Transforms.Copy` | 已修：Rent 后用 `try/catch` 包裹，复制期间异常释放目标帧 |
| `Sampling.Warp` 精度 | 已修：不再经过 `RgbaColor`(4×byte)。`SamplingCore` 按工作格式原生读写；Rgba16 保留低 8 位、Rgba32F 不钳位（HDR / 负值 / 超范围原样写回）。`WarpPrecisionTests` 12 例 + `tests/Fixtures/vectors/warp-identity-*` 三条独立推导的参考向量 |
| 公开 API 快照 | M7 显式重生成并 review diff：**只有新增**（`SampleOptions`、4 个 enum、`SampleRaw`、类型清单行），无现有签名删除或改写；旧 8-bit 便利重载完整保留 |
| Alpha 语义 | M7 仍是 **straight alpha**，与既有 Rgba8 输出字节一致 —— 三档 body SHA-256 与 M6 完全相同（`44390203…6763`）。预乘域统一与色域外策略属 M8 |
| 三链统一 | **未做**：`Sampling.cs` / `Mapping.cs` / `Resize.cs` 仍各自采样，相位、边界、Alpha 约定不同；统一 Resampler、`LinearLight` / `Premultiplied` 属 M8 |
| `SampleOptions` 未实现组合 | 显式 `NotSupportedException`，无静默回退 |
| Warp 的 SIMD twin | `SamplingCore` 是纯标量参考实现，无向量内核可对拍；跨档位一致性由 `benchmarks/simd-tiers.sh` 覆盖，M9 引入 EWA 权重 SIMD 时补 twin |

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
| FLT-21 | GuidedFilter | 自引导与 cross guide 两条内核，逐通道；箱均值的窗口求和按 8 像素向量化（float 前缀和），行带执行，工作集 = 5 × (band + 4r) 行（4K / band 128 约 11 MB）；cross 为 8 平面 / 约 17 MB，guide 与源同内存时回落自导向 |
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
| COL-30 IccTransform | RGB 的 **matrix/TRC 与 LUT 型都支持**：`mft1` / `mft2` / `mAB` / `mBA` 的 `A2B0` / `B2A0`，PCS 为 XYZ 或 Lab，1D 预整形 + 3D CLUT 四面体 + 输出 1D。仍拒绝：非 RGB 设备空间（CMYK / 灰度 / Lab）、`mft1` + PCSXYZ（规范本身未定义 8 位 XYZ）、只有 Intent 1/2 的 profile、`mft` 的非恒等矩阵。边界与数值契约见 [`icc-boundary-decision.md`](icc-boundary-decision.md) |
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
| 重采样器 | ~~只实现 5 种~~ **2026-11-15 补齐**：16 个成员 / 15 个核，覆盖 4.1.3 全部 15 个 `KnownResamplers` 名称（实测反射是 15 个，此前写 16 是错的）。数值各自实现、不逐位对齐，属 B2 口径 |
| Brightness 参数语义 | 本库**自有设计**是加性偏移（0.2 = +0.2×满量程）；参照实现同名 API 是另一套语义，同参数对拍 PSNR 仅 **4.23 dB**。这是功能对比时发现的「名称相同、语义不同」事实，**不是缺陷**：本项目按自己的语义文档使用，**不为兼容其他项目改 API、不提供兼容模式/兼容参数**。对标表必须写出口径，不得据同名宣称覆盖 |
| 4.1.3 同进程 A/B | 已解决：`docs/sixlabors.lic` 在位，对比工程通过 `SixLaborsLicenseFile` 按路径引用，Release 构建与 4.1.3 黑盒 A/B 均已跑通（见 `docs/compare-4.1.3-2026-11-06.md` 与 `docs/optimization-4.1.3-2026-11-06.md`） |

## 审计确认的缺口（2026-11-06，`audit-2026-11-06.md` 核对结果）

| 项 | 事实 | 状态 |
|---|---|---|
| AVX-512 档 | `src/` 全库 **0 处 `Vector512`**，机器 `avx512=False`，第四档从未被执行 | **未修**。对外不得再宣称"四档 SIMD 逐字节一致"，只能写"三档实现 + 一档预留" |
| `.git` 不存在 | `docs/sixlabors.lic` 没有任何忽略规则保护，密钥泄漏防护为零 | **未修**，需先建仓 |
| `FloatScratch` 无大小上限 | `[ThreadStatic]` + `MaxRetained=8`，每 worker 可驻留 8 块任意大小缓冲且永不收缩 | **M14 已修**：每线程保留预算 64 MB + 单块保留上限 32 MB，超限淘汰（但**照常分配**，只是不留），`MemoryBudget.Trim()` 立即释放。上限只约束"保留"，所以 8K 的 64 MB scratch 不会因此变慢 |
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
| 已实现（2026-11-14，M10） | **跨步骤融合**：连续可切带步骤按段走 `FusedExecutor` 的窗口递归（halo 累积、同原点视图、每 worker 两张窗口 ping-pong），`Global`/`Composite` 为整图段边界；`AddPointLut` 把连续 4×256 字节表折叠成单遍（Rgba8）。逐步骤路径保留：`ExecuteOptions.AllowFusion = false` 或存在 `PlanObserver`。`PlanSteps` 仍是每 band 租缓冲再拷行（适配器本身未改） |
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

## 8K 冷启动峰值差额：一次性池预热，不随调用增长（2026-11-12 实测）

`peakmem 7680x4320` 连跑两轮（11-11 与 `2026-11-12.rerun.peakmem.md`）逐位复现：我方 / 对手峰值
`resize-bicubic 94.2 / 39.8`、`gaussian-blur 339.3 / 266.2`、`grayscale 128.0 / 130.9`、
`invert 127.6 / 131.0` MiB。点算子两边都正好一张 8K 帧（126.56 MiB），差额只出现在有行带 scratch 的两项。

为判定这 54 / 73 MiB 是**一次性**还是**随调用累积**，新增 `memsteady` 模式（`Comparison/MemSteady.cs`）：
每格一支全新冷子进程，夹具与 `peakmem` 一致，只改变同一算子连续调用 1 / 3 / 10 次，每次结果立刻
`Dispose()`，读峰值增量与强制 GC 后的托管存活量。原始数字：`results/2026-11-12.mem.memsteady.md`。

| 场景 | 我方 live @1 / @3 / @10 MiB | 收敛 Δ(3→10) | 判定 |
|---|---|---|---|
| resize-bicubic | 107.0 / 107.0 / 112.0 | +5.0（≤8 MiB 噪声阈值） | 收敛 |
| gaussian-blur | 342.2 / 342.2 / 342.2 | +0.0 | 收敛 |
| grayscale | 127.0 / 127.0 / 127.0 | +0.0 | 收敛 |
| invert | 127.0 / 127.0 / 127.0 | +0.0 | 收敛 |

**结论：四项都在 3 次调用内饱和，1→3 的预热增量为 0，之后不再增长。** 差额是首次调用一次性提交的
池缓冲——`FloatScratch` 的 `[ThreadStatic]` 每 worker 一套，加上 `ArrayPixelPool` 保留的输出图——
不是泄漏，也不随调用次数累积。稳态每次调用的分配在 `perf` 表里是 0–8 KiB/op（对手 8–32 KiB/op）。
**判为已知代价，不改产品代码**：每 worker 独享 scratch 是 `ForBands`「输出字节与 worker 数无关」的
前提，换来的是 `2026-11-12.rerun.sizes.md` 的 4K 19/19 领先（1.6907×）与 1080p 1.7060×。

⚠ **口径边界**：`live` 是 `GC.GetTotalMemory` 的**托管堆**口径，对手实现的大像素缓冲由其自身分配器
管理，读数恒为 0，**不能读作「对手零占用」**；两侧内存高低只能看 `peakmem` 的进程级 WorkingSet。
`peakmem` 本身是「冷进程 + 单次调用」，不是工作负载，不能拿它当「谁更省内存」的结论，更不能拿它
当优化目标去改稳态热路径（§「测量方法论」铁律 4）。

**两处作废的推算**：此前「~11 套 scratch、每套 17.9 MiB、乘起来正好对上 212.7 MiB」是用
`Cores` 的核数估算凑出来的，不是实测。`memsteady` 实测 `RowParallel.LastWorkerCount = 16`，且这是
`ForBands` / `For` 的**计划** worker 数而非实际调度线程数，单套大小不能用峰值 ÷ workers 得出。
该推算作废，只保留上表的可复现数字。

**三个被否的「降内存」方向**（均不实施）：全局预算 ÷ Degree —— σ3 的 halo 是 18 行，摊薄后每 worker
有效行约 12，重算量涨到 1.5 倍，`Resize.cs` 的 16/8/4/2/1 MiB sweep 也早已给出 4 MiB 最优；
限制 8K worker 数 —— 11→4 按 Amdahl 粗估耗时翻倍，19/19 全胜当场消失；
共享 `FloatScratch` —— 破坏 `[ThreadStatic]` 无锁前提与逐字节确定性。三条都是拿冷启动峰值这把
错尺子去改产品热路径，按 `known-gaps.md` 铁律 4「不要自己造尺子」否决。

## nearest 内层位拷贝优化（2026-11-15，对标超越）

**动机**：跨尺寸 A/B 矩阵（`sizes`）里 `resize-nearest` 是唯一「小/中图稳定落后、4K 才领先」的算子——
640×360 **0.82×** / 720p **0.81×** / 1080p **0.84×** / 1440p **0.91×** / 4K **1.21×**。profile 实测「只租还缓冲
0.0001 ms」，瓶颈不在池而在内核。读 `Resize.cs` 的 Rgba8 nearest 快路径：每个目标像素做 **4 次单字节 load/store**。

**先量后改（`measure before optimizing`）**：新增 `SimdImaging.Comparison -- nearest-ablate`（复刻内层循环、用项目自己的
`Timing.Time` 交错采样，同 `Profile.MatrixSchemeBenchmark` 范式），三种搬运形态 1080p→960×540、逐字节比对：

| 形态 | 第 1 次 | 第 2 次 | 字节一致 |
|---|---|---|---|
| `byte`×4（原实现） | 2.061 ms | 1.909 ms | 基准 |
| `uint` 一次 32 位位拷贝 | 1.082 ms（**1.90×**） | 0.959 ms（**1.99×**） | ✅ |
| `Slice(4).CopyTo` | 1.310 ms（1.57×） | 1.202 ms（1.59×） | ✅ |

选 `uint`：把 4 个 Rgba8 字节当作一个未对齐 32 位整数读回再写出，是**纯位搬运**、不做整数重解释，故字节序列在
大端/小端、AVX2/V128/标量三档、JIT/AOT 下都恒等。`System.Runtime.CompilerServices.Unsafe` 已在 `Resize.cs` 引用。

**端到端（同机器、同协议、`perf`/`sizes` 前后各一轮）**：我方 nearest 稳态绝对时间在中等尺寸下降——
720p **0.147→0.094 ms**、1080p **0.307→0.183 ms**、1440p **0.537→0.387 ms**；跨尺寸名次由「4/5 落后、翻面」变为
**5/5 稳定领先**；`perf` 1080p 超越判定 **2.0151×（17/19）→ 2.2755×（19/19）**。4K 我方 1.273→1.346 ms 无改善
（纯字节搬运已到内存带宽墙，属预期）。
> ⚠ 两轮里**对手侧读数大幅抖动**（4K 1.545→2.268 ms），所以加速比（如 0.89×→2.21×）**不能**当作优化功劳；
> 可信的因果证据是同进程交错消融的 1.90–1.99× 与我方绝对时间的下降，端到端 A/B 只作方向佐证。按 §1.3「单个 cell
> 数字两次差 2× 不算数」的纪律，不引用单格比值下结论。

**字节与门禁**：910 单测通过、公开 API 快照无漂移；三档 SIMD（`simd-tiers.sh`）与 AOT 三路逐字节一致；
AotSmoke 补 `resize.nearest` 直接输出 hash（`05CAB372…`，JIT/AVX2/SSE2 三路全等）并加入 `REQUIRED`（AGENTS.md：
改实现的算子必须进 hash list，否则门禁绿了却什么都没验）。改动落在 `SimdImaging.Processing.dll`，`binarySha256`
随之变化 → `check-baseline.sh` 按设计判「binary differs」并跳过（comparable=0），**没有**改基线值/指纹强行对齐：
一次试图对齐的尝试拿满载读数比空闲基线，15 场景 +30~98% 假 FAIL，把 nearest 还原成旧写法同时段重测同样 +10~70%，
证明是机器负载而非回归。细节与「故意跳过」纪律见 `benchmarks/baselines/README.md`。

**剩余优化空间（2026-11-15 实测证伪，关闭）**：上轮登记「矩阵类点算子小尺寸 0.85–0.96× 是 `ApplyMatrixRgba8`
每次调用重建 `3×256×4` float records 表的固定开销，可缓存」这一假设。本轮用 `SimdImaging.Comparison -- records-ablate`
（复刻内核、同进程交错、`Timing.Time` 中位、三尺寸、逐字节校验）量化「缓存 records 表」的收益上限：

| 尺寸 | 第 1 次缓存收益 | 第 2 次缓存收益 | 判定 |
|---|---|---|---|
| 640×360 | 5.3%（1.06×） | 6.8%（1.07×） | 仅最小图、贴近噪声 |
| 1920×1080 | 0.1%（1.00×） | 3.8%（1.04×） | **两次差 3.7pt，在噪声内** |
| 3840×2160 | −2.7%（0.97×） | 4.7%（1.05×） | **符号翻转，纯噪声** |

同一尺寸两次读数差 4~7 个百分点、4K 甚至符号翻转 → 按「单个 cell 数字两次差 2× 不算数」，缓存收益**不成立**。
3072 次浮点乘相对 2M 像素内核微不足道，且缓存 `[ThreadStatic]` 表要按 12 float 矩阵键判定（sepia amount / 灰度 /
任意 ColorMatrix 各异），有串味破坏逐字节一致的风险。故**不改产品代码**。点算子整体已接近单核内存带宽墙
（16.6 MB ÷ 1.7 ms ≈ 9.8 GB/s），SIMD 内核（值主序记录 + 三级 Narrow 打包）已高度优化，不宜凭猜再动。

**位拷贝边界证明（2026-11-15）**：`NearestBitCopyTests` 9 例把 uint 位拷贝的字节恒等钉成机械断言，覆盖既有
`NearestResizeTests` 未测的非紧排（padded-stride）源：① 每个目标像素按独立权重表从**原始 flat 数组按 padded stride**
取 4 字节做参照（与内核 `Row(y)` 不同寻址路径）；② tight 与 padded 两种布局的同像素必须 resize 成逐字节一致输出
（越界读 padding 会让两者分叉，无需易误报的 sentinel 扫描）；③ 覆盖 2:1 下采样（快路径）、非整数比、放大、恒等、
极小图。5 尺寸逐像素参考 + 4 尺寸布局对照全过。

**机器状态与基线（2026-11-15 复查）**：连跑两次 benchmark，`|run1−run2|` 中位 7%、最大 52%（短场景 fill/crop/
lanczos 抖动），长场景（nlm/ll/guided/composite，≥150 ms）相对 M15 基线回到 ±11% 内 → **优化无回归再次佐证**。
但短场景仍 ±20~50%，机器未完全空闲，按 `benchmarks/baselines/README.md` 纪律**不在负载里重录基线**：改了产品 dll
的 `check-baseline` 继续按设计「故意跳过」（comparable=0），待机器空闲、同 binary 两次 best-of-3 稳定后重录才让门禁
重新有牙。这是刻意的收口条件，不是遗漏。

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

## 基准扩容与三项性能修复（2026-11-12）

口径：`benchmarks/SimdImaging.Comparison -- perf`，同进程 A/B、7 轮交错取稳态。**双方都按各自默认并行度跑**——`RowParallel.Degree` 默认 `ProcessorCount`，我方从未强制单线程（`Infrastructure.cs` 第 50 行原话：Our side is not single-threaded either）；对手用 ImageSharp 默认。场景从 17 项扩到 **19 项**。

### 当前总账

| 分辨率 | 超越判定（steady） | 领先项 |
|---|---|---|
| 640×480 | **1.981×** | 16 / 19 |
| 1920×1080 | **2.248×** | 19 / 19 |
| 3840×2160 | **1.881×** | 19 / 19 |

同日三轮优化的起点是 640 / 1080p / 4K = 1.371× / 1.595× / 1.746×（13 / 16 / 19 项领先）。

### 补进表里的两个场景

`saturation` 与 `pixelate` 从来不在表里，所以「比对手慢 3.7 倍」活了很久没被任何门禁发现——是实验台第一次全量扫描暴露的。

| 项 | 问题 | 修法 |
|---|---|---|
| `Saturation` | 逐像素 float 循环（同类 `Grayscale`/`Sepia` 早就在矩阵路径上） | 展开成 3×3 矩阵走 `ColorMatrixCore`；`amount=0` 时矩阵与 Grayscale 逐项相同 |
| `Pixelate` | 每块行两次整行 float 转换（字节→解预乘→回字节）+ 每像素一次除法 | Rgba8 走全整数：块内累加 `c×a`（`long`），每块两次整数除法，整块 32 位写入铺开 |

`Saturation` 0.67 → 0.26 ms（640×480，0.27× → 0.75×），1080p 2.098 ms（0.96×）。`Pixelate` 三档 0.37 / 0.80 / 0.86 → **0.85 / 1.60 / 2.04**。

> 顺带修了 `Pixelate` 一个真 bug：旧实现 alpha 除的是**块高**而不是块面积，任何半透明图（alpha=128）像素化后都饱和成 255。现在按面积平均，`Pixelate_KeepsATranslucentBlockTranslucent` 锁住。
>
> **字节变化范围（实测，512×512 随机字节，block=12）**：alpha 通道 100% 改变（这正是修复本身）；**不透明图的 RGB 也有约 0.93%（2448/262144）像素差 1 LSB**——旧实现走 float 累加 + `round-half-up`，新实现是整数 `(sum + area/2)/area`，当块均值恰好落在 x.5 上时两者取整方向不同。整数路径无法复现 float 的表示误差，这 1 LSB 是算法换轨的固有代价，不是 bug。

### 并行审计：谁还没并行

脚本扫全库，命中 **56 个**「方法里有 `for (var y = 0; y < …)` 但方法体内没有 `RowParallel`」的方法。只动了其中语义上逐行独立、且被基准覆盖的五个：

| 场景 | 改前 640 / 1080p | 改后 640 / 1080p | 改法 |
|---|---|---|---|
| crop | 0.93 / 1.57 | **1.92 / 6.37** | `RowParallel.For` 行带 |
| flip-horizontal | 0.95 / 1.01 | **3.14 / 2.83** | 单趟反向拷贝 + `Avx2.PermuteVar8x32` |
| flip-vertical | 1.75 / 1.63 | **5.29 / 3.67** | `RowParallel.For` 行带 |
| rotate180 | 1.57 / **0.88** | **7.34 / 2.60** | 同上；旧版是「整行 CopyTo 再 Reverse」两遍内存 |
| box-blur | 0.77 / 1.20 | **1.33 / 1.42** | 带高从钉死 128 行改为 `clamp(height/(Degree×2), 32, 128)` |
| gaussian-blur | 1.04 / 1.61 | **1.51 / 1.78** | 同上 |

模糊那两项的根因不是代码写法，是**分不到核**：480 行 ÷ 128 行只有 4 个带，`cores 640x480` 实测我方 **3.77 核**、对手 **8.95 核**。带大小随高度自适应后同样 480 行切成 15 带。

**跨行依赖的算子一律不切带**（`Dither` / `FloodFill` / `AdaptiveThreshold` / `GuidedFilter` / 扫描线 `Rasterize`）——切了会改字节。规则已写进 `AGENTS.md`。

### 还没动的并候选

`AlphaOps.Premultiply` / `Unpremultiply`、`ColorConversionOps.Convert`、`PointOps.Normalize`（第二遍可并行）、`TransferOps.Apply`、`DitherOps.Ordered`、`HdrOps.ToneMap`、`PathOps.Rasterize`、`PaletteQuantizer.Build/Dither`。每个都要单独确认「切带不改变字节」。

### 读数纪律

- **单次数字不能当结论。** 同一二进制、同一尺寸连跑三次，`resize-bicubic` 在 640×480 上是 4.86× 中位、波动 3.68；`rotate180` 曾一次 1.50、一次 0.88。
- **两套协议结论可能相反。** 640×480 的 `grayscale`：`perf` 0.91×，`cores`（子进程隔离、21 轮取最小）0.24×。引用必须说明协议。
- 现在 640×480 上低于 1.0 的只剩 `saturation` 0.87 与 `pixelate` 0.92，绝对时间都是 0.3 ms 量级，属平手区间。

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
| **`.git` 已建立（2026-11-12 修正）** | 此前两轮审计写「仓库内没有 `.git`」的依据是当时的工作区快照；实际仓库已有提交历史，`docs/sixlabors.lic` 被 `.gitignore:18` 忽略且从未入库（`git check-ignore -v` 可复核） | 该结论已过时，不要再按「不可回滚 / 密钥裸露」判断。**仍未提交**的是 M6–M10 的工作区改动（由维护者用 VS 提交） |
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
| M2 点算子融合后分配 = 0 | 11-11 `perfpipe` 三条链 **7 / 39 / 8 KiB/op**（融合前读数）；`perf16` 单算子 0–15 KiB/op。**2026-11-15 融合后重测**：`AddPointLut` LUT 折叠稳态 ≈4.3 KiB/op（§1.3 字面尺）/ ≈6.0 KiB/op（进程级），见 §「M2『点算子融合后分配』重测与判定」 | ❌ **未达标（已判定）**：0 B/op 未达成，但已在 M10 融合路径上出数、两尺子/两次运行/两尺寸可复现；残余为 per-call 计划重建，非像素缓冲 |
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
  2026-11-15 在 M10 融合路径上重测仍 ≈4.3–6.0 KiB/op，见 §「M2『点算子融合后分配』重测与判定」。
- 顺带出的数（不属 §11 出口条件，仅作证据）：`Rgba16` 端到端 **7 / 7 领先、几何平均 2.1003×**；
  流水线 **1.1074×（2/3）**，`point-5` 0.61× 落后；`parity` B1 17 场景中 5 项逐字节一致、6 项 ≤1 LSB、
  3 项视觉等价、3 项算法/语义不同；B2 对手对 `GaussianBlur(NaN)` 静默接受。

⚠ **三项仍未关闭**：8K `gaussian-blur` 超阈值、抗锯齿覆盖率判据缺失、HEIF 端点无语料。
这三项是验收欠账，不是功能缺失；但按 §11 口径，M2–M4 都还不能称完成。
（M2 的「融合后分配 = 0」原是第 4 个「未判定」，2026-11-15 已判定为**未达标**并出数，见下节；
它是「判过了、输了」，不再属于「没测」，但同样不构成达标。）

## M2「点算子融合后分配」重测与判定（2026-11-15，M5 出数）

§11 M2 行与本文档 M10 节的「⚠ 未判定：分配曲线未重测」自此**关闭为「已判定：未达标」**。11-11 的
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
| ~~`Mutate` 无实现~~ **已补（2026-11-15）** | `Image.Mutate(Action<ImageChain>)` 真正原地生效（返回 `this`，旧缓冲回池），因为移植过来的调用点写 `image.Mutate(x => x.Resize(...))` 之后继续用 `image`；返回新图会让它编译通过却什么都不改。**仍是源码差异**：lambda 参数类型是 `ImageChain` 而不是 ImageSharp 的 `IImageProcessingContext`，链上没有的操作要改 | 调用点改写成 `image.Mutate(x => x.Resize(w, h))`；`Resize`/`Crop`/`Rotate90`/`Flip` 等同名方法直接可用。7 例 `ImageMutateTests` 钉住原地语义、异常不改图、缓冲回收 |
| `Image` 无泛型 `Image<TPixel>`，只有三个 `WorkingFormat` | `architecture.md:230`、`docs/public-api.txt` | 依赖像素类型的泛型代码要重写 | **不追**（§5.1 是核心设计），列为迁移成本 |
| `Save/Encode` 是静态方法且参数顺序不同（`Image.Save(image, path)` vs `image.Save(path)`） | `docs/public-api.txt:157`、`:226` | 每个保存点改 | 加实例方法薄封装，只加不改 |
| ~~`Resampler` 只有 5 种~~ **已补（2026-11-15）** | 现为 **16 个成员 / 15 个核**，覆盖 ImageSharp 4.1.3 全部 15 个 `KnownResamplers` 名称（实测反射该包是 15 个，不是 16）。`ResamplerCatalogTests` 37 例 | 用了 Mitchell / Lanczos5 / Lanczos8 / Spline / Welch / Robidoux 的代码现在编得过；**数值不与对手逐位对齐**（各自实现，见 B2 口径） |

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
