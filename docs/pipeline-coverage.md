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

## 不能进（必须整图，`Global`）

| 算子 | 原因 |
|---|---|
| Equalize / Normalize / CLAHE | 需要整图直方图或分块统计 |
| LocalLaplacian | 金字塔跨尺度，带内没有足够上下文 |
| Vignette | 依赖整图中心，带内取向会错 |
| Pixelate | 块对齐；带边界可切在块中间，**没有常数 halo 能表达** |
| Noise | 随机，逐带不可复现 |
| DistanceTransform | 全局传播 |

## 根本不能

| 算子 | 原因 |
|---|---|
| Resize / Crop / Rotate90 / Transpose / Trim / EntropyCrop | 改变尺寸或布局。`Pipe()` 会在这些处**自动切段**，调用方无需关心 |

## 维护规则

新增算子时，先在 `PlanOps` 里写工厂并**注明 halo 的来源**（哪一行、哪个参数），再把该算子加进
`HaloDeclarationTests.EveryImageOpsFactory_...` 的列表——那条测试会把它在 `bandRows=4` 下与直调逐字节比对。
