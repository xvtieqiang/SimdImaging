# 引擎定位六项：源码与实测核对

核对日期 2026-11-28，基于 `f65eb4f`。每条给出源码位置与实测数据，不引用架构描述。

## 结论速览

| 定位 | 状态 | 关键证据 |
|---|---|---|
| 默认轻量 | **成立** | 内存路径不进 `ExecutionPlanner`；规划耗时 16 µs（512²）至 22 µs（4K） |
| 约束驱动 | **成立** | 候选按能力/预算过滤，`Evaluate` 逐候选真编译 |
| 收益驱动 | **部分成立** | 融合阈值有实测依据；但 `FuseIntermediateBytes = 64 MiB` 是单一常量，对 1080p 以下几乎永不触发 |
| 按需升级 | **成立** | `ExecutionPlanner` 仅 5 个调用点，全部是 Tile/Stream 显式入口 |
| 可收缩 | **成立** | 回退候选也跑完整 `Evaluate`，不是裸降级 |
| 可验证 | **基本成立** | 报告/执行一致性有 3 个钉住用例；但「预测 vs 实测」无闭环 |

## 1. 默认轻量：成立

**源码事实**：`ExecutionPlanner.Plan` 全仓只有 5 个调用点（`ImageTiles.cs:41/89/235`、`ImageTranscode.cs:459`、`ProcessingPlan.cs:344`）。内存路径（`Image.Apply` → `ImagePlanOps.cs:129`）**完全不经过规划器**，直接 `PipelineCompiler.Compile` + `CompiledPlanExecutor.ExecuteFrame`。所以普通图片连候选枚举都不会发生。

**实测**（同进程，200 次取中位，`Pipe().Invert().ToImage()` vs 裸算子）：

| 尺寸 | Pipe 总计 | 裸算子 | 其中编译 | 编译占比 |
|---|---|---|---|---|
| 512×512 | 0.287 ms | 0.277 ms | **0.016 ms** | **3.5%** |
| 1920×1080 | 1.276 ms | 1.281 ms | **0.0075 ms** | ~0%（低于噪声） |
| 3840×2160 | 5.227 ms | 6.169 ms | **0.022 ms** | ~0% |

最坏情况（最便宜算子 × 最小图）规划占 3.5%；图像越大占比越低，因为执行是 O(像素) 而编译是 O(步数)。

**一处值得记录的历史**：`ImagePipeline.Flush`（`ImagePipeline.cs:526-535`）对单算子有一条直调捷径，绕过编译。实测它没有带来增益——512² 上 `Pipe()` 与单步计划差异在噪声内（0.87×~1.31× 两次运行方向相反）。所以这条捷径**不是**「默认轻量」的必要条件，只是历史产物。

## 2. 约束驱动：成立

`ExecutionPlanner.Evaluate`（`ExecutionPlanner.cs:131-160`）对每个候选调用真实的 `PipelineCompiler.Compile`，把 `NotSupportedException` / `InvalidOperationException` 转成 `InvalidPlan` 候选。也就是说**合法性是真编译验证的，不是估算出来的**——这符合基线 §2.3「所有资源预算、算子能力必须真实验证」。

预算不是评分扣分而是硬拒绝：`BudgetEnforcementTests` 有 10 个用例覆盖 Frame/Region/Tile/几何/格式五条路径。

## 3. 收益驱动：部分成立

**成立的部分**：`FusionDecision.ShouldFuse` 的每条规则都标注了实测数字（`FusionDecision.cs:26-55`），包括「20 步链 Σhalo=52，融合 252 ms vs 逐步 157 ms」这种反例。`PointBandBytes = 192 KiB` 也有实测来源。

**不成立的部分**：`FuseIntermediateBytes = 64 MiB`（`FusionDecision.cs:74`）作为「省下的整帧是否值得融合」的唯一阈值，是一条**与图像尺寸无关的常量**。640×360 的一张 Rgba8 帧只有 900 KiB，需要 71 张才够 64 MiB——所以小图上这条规则几乎永不触发，融合与否完全由「有无 halo」决定。

这不是 bug（零 halo 链走 `maxHaloY == 0 → true` 分支，仍然融合），但它意味着**这条阈值对中小图没有区分能力**。真正的收益门槛在中小图上是缺失的。

## 4. 按需升级：成立

`ExecutionPolicy` 只有 `Auto / RequireTiled / RequireStreaming` 三个值，Tile 需要调用方走 `Image.ProcessTiles*` 并显式要求，Region 需要显式矩形。内存 `Apply` 的 `Sink = SinkCapabilities.None`（`ImagePlanOps.cs:97/129`），会被 `TileRefusal.NoRegionSink` 直接拒掉（`PipelineCompiler.cs:413`）。

即：**普通内存图片不可能走 Tile 或 Region**，符合基线 §9「不为普通内存图像强行流式化 / 不默认 Tile」。

## 5. 可收缩：成立（且比基线要求的更严）

基线 §2.5 要求「回退只能回到执行前已验证合法的计划」。当前实现满足：`ExecutionPlanner.Plan` 对**每个候选**都跑完整 `Evaluate`（含预算校验），选中即返回；全部失败才抛异常。所以回退到的 Frame 是**通过同一套校验**的，不是裸降级。

`ImageTiles.RequireDecision`（`ImageTiles.cs:169-188`）在无合法候选时抛异常并带上主候选与终候选两条原因，符合「拒绝原因可审计」。

## 6. 可验证：基本成立

**成立**：报告路径 == 执行路径有三条钉住用例——`StreamingTests.ReportedShape_IsTheShapeThatRuns_OnATallImage`（把预测值当 `WorkspaceManager` 上限，断言跑完后余额为 0）、`ReportedSections_AreTheStagesThatRun`、`WorkspacePlanTests.CompileTimePredictionUsesTheBandTheExecutorWillPick`。今天又补了 `PlanningTicks` 与 `PlanReport.Execution`，两入口不再出现一个有一个没有。

**不成立**：基线 §13 要求报告回答「预测和实测差异有多大」。`PeakWorkingBytes`（实测）与 `PredictedWorkingBytes`（估算）都在报告里，但**没有任何地方把它们对比、也没有断言比值落在合理区间**。`memestimate` 基准有测，但结论没有进测试门禁。

## 与基线的真实差距（不是缺陷，是未做）

| 基线要求 | 现状 |
|---|---|
| §7 规划预算 `CandidateLimit` / `PlannerDepth` / `MaxPlanningTime` | **全未实现**。候选表最多 2 项，够用；但长链没有上限保护 |
| §5.2 `FastPathEvaluator` | **未实现**。实测表明内存路径的编译只占 3.5% 以下，没有实现的必要 |
| §8 粗成本模型 | **未实现**。选择是「按顺序取第一个可行」，不做代价比较——已被数据否定（见 `architecture.md` 的「一条被数据否定的设计」） |
| §13 预测 vs 实测闭环 | **未实现**。两个数字都在，但没人对比 |

## 一句话

六项里五条成立，「收益驱动」在中小图上有实质缺口——`64 MiB` 这个常量对 1080p 以下的图没有区分能力。其余未实现项（规划预算、成本模型、预测闭环）都不是当前瓶颈：实测规划开销 < 3.5%，而基线自己也说「不为架构完整性增加抽象」。
