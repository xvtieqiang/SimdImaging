# 哪些算子能进流水线（按源码判定）

判定依据是**实现**，不是运行时试探。`Pipe()` 支持的算子列在 `PlanOps`，每一条的 halo 都来自算子自己的读法；
下面「不能进」的每一条也注明原因，避免下一个人重新猜一遍。

## 能进（halo 来自源码）

| 算子 | halo | 来源 |
|---|---|---|
| 全部点算子（Brightness / Contrast / Gamma / Invert / Grayscale / Sepia / Saturation / Posterize / Levels / Tint …） | 0 | 每个输出像素只看同位置的输入像素 |
| GaussianBlur(σ) / Sharpen(σ) / GaussianSharpen(σ) / Glow(σ) | `ceil(3σ)` | 高斯核的支撑 |
| BoxBlur(r) / Bilateral(r) / BokehBlur(r) / GuidedFilter(r) / OilPaint(r) | `r` | 各自的窗口半径 |
| Median3x3 / Erode3x3 / Dilate3x3 / Emboss / Sobel | 1 | 3×3 核 |
| Open3x3 / Close3x3 | 2 | 两次 3×3 |
| MotionBlur(len) | `ceil(len/2)` | 偏移表跨 ±len/2 |
| NonLocalMeans(search, patch) | `search + patch` | 搜索窗 + patch 半径（**不是两者之一**） |
| AdaptiveThreshold(block) | ≈ `block/2` | 局部门槛窗 |

## 按整图执行的（`Global`）——**不是不能进**

这些算子要求整图上下文，流水线会把它们**当成独立的一段整体执行**：多一张中间图（和直接调用它一样），
其余步骤照旧共享同一遍行带。**调用方的写法完全不变**，`Pipe()` 里一样能写。

| 算子 | 为什么要整图 |
|---|---|
| Equalize / Normalize / CLAHE | 整图直方图或分块统计 |
| LocalLaplacian | 金字塔跨尺度 |
| Vignette | 按到**整图中心**的距离衰减，带内会算错中心 |
| Pixelate | 块对齐；带边界可切在块中间，**没有常数 halo 能表达** |
| AdaptiveThreshold | 局部门槛窗远宽于任何值得携带的带 |
| DistanceTransform | 全局传播 |
| Noise | 随机，逐带不可复现 |

## 根本不能

| 算子 | 原因 |
|---|---|
| Resize / Crop / Rotate90 | 改变尺寸或布局。`Pipe()` 在这些处**自动切段**，调用方无需关心 |

## 维护规则

新增算子时，先在 `PlanOps` 里写工厂并**注明 halo 的来源**（哪一行、哪个参数），再把该算子加进
`HaloDeclarationTests.EveryImageOpsFactory_...` 的列表——那条测试会把它在 `bandRows=4` 下与直调逐字节比对。
