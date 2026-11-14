# 对外契约（30 条）

每条契约都要有一个**存在于测试程序集里的**测试类作锚点；`ContractsTests` 反射校验这张表与程序集一致
（数量、类名、无遗漏），所以"文档里写了"和"真的测了"不能各说各话。

等级沿用仓库口径：**D0** 整数逐字节精确；**D1** 浮点冻结运算序、单次舍入、跨档逐字节一致；**D2** 近似。

| # | 契约 | 等级 | 锚点测试类 |
|---|---|---|---|
| 1 | 整数管线跨 SIMD 档逐字节一致 | D0 | `RowMathTests` |
| 2 | 浮点管线冻结运算序，不用 `Vector.Sum` 归约 | D1 | `FiniteContractTests` |
| 3 | 缓冲所有权：`Rent` 与 `Dispose` 一一对应，异常路径不泄漏 | D0 | `ProcessingPlanLeakTests` |
| 4 | 融合执行器与逐步执行逐字节相同 | D1 | `FusedExecutorEquivalenceTests` |
| 5 | 连续点 LUT 折叠等价于逐步应用 | D0 | `PointLutCompositionTests` |
| 6 | 采样契约：`SampleOptions` 的域/曲线/alpha/边界语义 | D1 | `SampleOptionsContractTests` |
| 7 | 几何选项契约：`WithOptions` 与 legacy 的等价与差异边界 | D1 | `GeometryOptionsContractTests` |
| 8 | EWA 椭圆足迹：2σ 窗口、退化回退、预算不越界 | D1 | `EwaPrecisionTests` |
| 9 | `TapPlan` 是唯一的权重生成器（三链统一） | D1 | `MappingTapPlanEquivalenceTests` |
| 10 | Rgba8 / Rgba16 / Rgba32F 三格式语义一致，16 位不塌 8 位 | D0/D1 | `FilterRgba16Tests` |
| 11 | HDR：Rgba32F 的 >1、负值、超范围不钳位（除物理非负） | D1 | `HdrTests` |
| 12 | 色调映射曲线端点正确（含 BT.2390 的 PQ 天花板不除零） | D1 | `HdrTests` |
| 13 | 增益图重建：headroom 1 退回线性底图，满容量施加作者增益 | D1 | `HdrTests` |
| 14 | 增益图成对几何：配对结果 == 独立变换 + 派生参数 | D0 | `GainMappedImageTests` |
| 15 | Ultra HDR 往返：Compute→Assemble→读→重建 保持峰值 | D1 | `GainMapTests` |
| 16 | ICC matrix/TRC：sRGB 往返 ΔE 有界 | D1 | `IccTests` |
| 17 | ICC LUT：PCS 三套编码按 tag 类型解码，端点常量正确 | D0 | `IccTests` |
| 18 | ICC CLUT 求值：四面体、Q15 权重、u16 网格、int64 累加 | D0 | `IccClutTests` |
| 19 | ΔE2000 与 Sharma 34 组参考一致（含 180° 色相不连续） | D1 | `ColorDifferenceTests` |
| 20 | 非 RGB 设备空间 / lut8+XYZ / Intent 1-2 / 无方向 tag 显式拒绝 | — | `IccTests` |
| 21 | 伪输入抛 `InvalidDataException`，调用方错误抛 `ArgumentException`，禁静默回退 | — | `IccTests` |
| 22 | NLM 精确无剪枝，双精度朴素参考逐字节一致 | D1 | `NonLocalMeansTests` |
| 23 | Local Laplacian：α=β=1 恒等、LUT == 直算 | D1 | `LocalLaplacianTests` |
| 24 | Richardson-Lucy：PSNR 单调改善，alpha 逐字节不变 | D1 | `RichardsonLucyTests` |
| 25 | Seam Carving：平局规则确定、横向 == 转置竖向 | D0 | `SeamCarvingTests` |
| 26 | 内存预算：改动预算不改变任何输出字节 | D0 | `MemoryBudgetTests` |
| 27 | 内存预估：不得少报执行器实际租借的峰值 | D0 | `MemoryEstimateTests` |
| 28 | 公开面由快照锁定，只允许新增 | — | `PublicApiTests` |
| 29 | 元数据透传与替换：Exif / XMP / ICC 往返 | — | `MetadataOpsTests` |
| 30 | 距离变换：平方欧氏距离场与 O(n²) 定义逐位一致，2²⁴ 内为精确整数 | D0 | `DistanceTransformTests` |

## 为什么是这 30 条

它们覆盖三类风险：**跨档一致性**（1、2、10、11）、**资源与所有权**（3、26、27）、**边界与拒绝面**
（20、21、28）。算法正确性由各自里程碑的测试承担，这里只收"对外承诺且能机械判定"的部分。

新增契约时：先加测试，再在上表加一行，`ContractsTests` 的数量断言会提醒你改。
