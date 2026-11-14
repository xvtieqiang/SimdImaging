# ICC LUT 的解析边界：谁解析、谁执行、数值怎么定

**结论：CLUT 由本库自解析自执行。** codec 层（`Sdcb.SimdCodecs`，NuGet，不可改源码）只负责识别
profile 字节并通过 `ImageMetadata.Icc` 透传；`SimdImaging.Processing` 负责 `A2B0`/`B2A0`/`mAB`/`mBA`
的 tag 解析、1D 预整形、3D CLUT 四面体求值与 PCS 处理。本文记录这条边界的成因、数值契约、拒绝面与
验收口径；**未定稿前不动 CLUT 实现**（主计划 §2 M13 的红线）。

机器口径：本机（x64，AVX2，无 AVX-512）。规范引用一律指 ICC.1:2010（与 ICC.1:2022 在这些条款上一致）。

## 1. 边界为什么在这里

`Sdcb.SimdCodecs.Color.Icc` 2.0.0 只暴露四件事：`IccProfiles.SRgbV4`（一个 profile 字节常量）、
`Describe`、`TryGetPrimaries`、`WithCicp`。它**不交出 CLUT 网格**，也不交出 1D 表。因此"复用上游的
解析器"在今天的 API 面上不是一个选项——只有两条路：

| 方案 | 结果 |
|---|---|
| 等上游暴露网格 | 阻塞 M13，且上游无时间表 |
| 本库自解析 tag 字节 | 立即可做；`IccProfile.Parse` 是唯一替换点，将来上游若交出网格，改一个分支即可消费，不构成返工 |

选后者。**边界因此定为：**

```
Sdcb.SimdCodecs   → 识别 JPEG/PNG/…、解出 ICC profile 字节、ImageMetadata.Icc 透传（含写回）
SimdImaging       → 不碰 profile 内部结构，只把 ReadOnlySpan<byte> 交给 Processing
SimdImaging.Processing → header/tag 表校验、tag 选择、1D 表与 CLUT 解析、求值、PCS 缝合
```

`AGENTS.md:34-36` 规定 codec 层是 NuGet 依赖、不可改源码、不可 `ProjectReference` 进别的仓库——本决策
完全在该约束内：**没有一行代码进入 codec 包**。

## 2. 支持面与拒绝面

| 维度 | 支持 | 拒绝（`NotSupportedException`，消息点名原因） |
|---|---|---|
| 设备色彩空间（header 16-19） | `RGB ` | `CMYK`、`GRAY`、`Lab `、`3CLR` …（CLUT 的输入通道数随空间变，CMYK 要 4 输入 3 输出的独立求值路径与配色意图处理，不在本轮） |
| PCS（header 20-23） | `XYZ `、`Lab ` | 其它 |
| Tag 类型 | `mft2`(lut16)、`mft1`(lut8，仅 Lab PCS)、`mAB `、`mBA ` | 其它；`mft1` + `XYZ` PCS 单独拒绝（规范本身写明 8 位 XYZ 未定义，不建议使用） |
| Rendering intent | `A2B0` / `B2A0` | `A2B1/A2B2/B2A1/B2A2`：存在时忽略，只有 1/2 时抛"该 profile 只有 Intent 1/2 的表" |
| 方向 | A2B0 用于 设备→PCS，B2A0 用于 PCS→设备 | 缺哪个 tag，哪个方向就抛异常并点名缺失的 tag |
| 快路径 | 现有 matrix/TRC（`rXYZ/gXYZ/bXYZ` + `curv/para`）行为不变 | — |

矩阵/TRC 与 LUT 同时存在时 **LUT 优先**（CMM 语义：A2B0/B2A0 是 profile 作者给出的实际映射）。

## 3. 数值契约

### 3.1 定点格式

| 环节 | 格式 | 理由 |
|---|---|---|
| 3D CLUT 网格 | `ushort`（ICC 原生 16 位，域 0..65535 均匀） | 用满规范精度；降成 Q15 白丢一位 |
| 插值权重 | **Q15**（`frac >> 1`） | 权重是 `[0,1]` 的分数，Q15 足够且乘加可控 |
| 累加 | `int64` | 角点差可达 ±65535，Q15 权重乘后 4 项求和需要 48 位以上 |
| 输出 | `(acc + (1<<14)) >> 15`，clamp 到 `[0,65535]` | 一次舍入，可复现 |

`mAB`/`mBA` 的 3×3 矩阵级（`s15Fixed16`）用 **double** 计算、单次舍入回 `ushort`：它是实数矩阵，硬塞
定点会丢精度，且这一级不参与下面的 D0 主张。

### 3.2 PCS 16 位编码（**按 tag 类型分两套，易错**）

ICC.1:2010 §6.3.4.2 给出通用 16 位编码，其 NOTE 3 明确：**只有 `lut16Type` 与 `namedColor2Type` 使用
v2 遗留编码**。因此同一个 Lab PCS，`mft2` 与 `mAB` 的解码公式不同：

| 位置 | 字节数 | L* 0→100 | a*/b* −128→0→+127 | 解码 |
|---|---|---|---|---|
| `mAB`/`mBA`（通用） | 16 | `0x0000 → 0xFFFF` | `0x0000 → 0x8080 → 0xFFFF` | `L = v/65535*100`，`a = v/257 − 128` |
| `mft2`（v2 遗留） | 16 | `0x0000 → 0xFF00` | `0x0000 → 0x8000 → 0xFF00` | `L = v/65280*100`，`a = v/256 − 128` |
| `mft1`（v2 遗留） | 8 | `0x00 → 0xFF` | `0x00 → 0x80 → 0xFF` | 先 `×257` 展宽到 16 位再按上行 |
| PCSXYZ（两种 16 位类型共用） | 16 | — | — | **u1Fixed15**：`X = v/32768`，`1.0 → 0x8000`，上限 `1.999969…`（`0xFFFF`） |

**判据（实现前先落常量测试，不许猜）**：`0x8000 → Y = 1.0`、`0xFFFF → 1.999969482421875`、
`mft2 的 0xFF00 → L* = 100`、`mAB 的 0xFFFF → L* = 100`、`mAB 的 0x8080 → a* = 0`。
把 XYZ 因子写成 `v/65535` 会让整条链暗一半——恒等 profile 的 ΔE 测试会立刻红。

### 3.3 缝合

```
device float ──round(clamp(v,0,1)*65535)──▶ u16 ──A2B0──▶ PCS u16
  ──PCS 解码（double）──▶ XYZ D50 ──目标 profile（矩阵/TRC 或编码成目标 PCS 后 B2A0）──▶ device float
```

两端都是 LUT 时中间**仍然**走 XYZ D50 float，不做 PCS u16 直通：省掉一条等价路径，代价 ≤1 个 PCS 码，
由 ΔE 验收覆盖。`Rgba32F` 输入超出 `[0,1]` 一律钳到设备域（profile 的定义域就是 0..1），并写进 xmldoc。

### 3.4 确定性等级

- **CLUT 段**：纯整数（u16 表 + Q15 权重 + int64 累加），跨 SIMD 档必然逐字节一致 ⇒ **D0**。
- **整条 `Transform`**：含 PCS 的 float 缝合与目标端 matrix/TRC 的 float 路径 ⇒ 登记 **D1**（冻结运算序）。
- 不宣称整链 D0。逐像素运算序固定为：A 曲线 3 次 → CLUT → M 曲线 → 矩阵 → B 曲线；禁止改写成可结合形式。

## 4. 加速策略

第一阶段**行带并行**（`RowParallel.ForBands` + `FloatScratch` 行缓冲），逐像素标量核。第二阶段按实测决定
是否补 SIMD：3D CLUT 是 8 角点分散取址 + `int64` 累加，`Avx2.Gather` 延迟高，收益不预设；若 SIMD 版
慢于标量版，就只保留标量并把实测数字写进 `known-gaps.md`（与 EWA 保持纯标量的结论同形）。
两套实现必须共用同一个 `ForceScalar` 孪生开关，且输出逐字节一致。

## 5. 验收口径

1. **构造向量逐字节一致**：手写 tag 字节的 lut16/mAB profile（`tests/Fixtures/SyntheticIccProfiles.cs`），
   期望值手算，0 mismatch。
2. **独立双精度参考**：测试内另写一个不做量化的朴素求值器（三线性加权 + double），与本实现比 ΔE2000
   均值/最大值，阈值实测后回填（设计目标：均值 ≤0.2、最大 ≤1.0）。
3. **锚点**：构造一个 PCSXYZ 走 sRGB 矩阵的 LUT profile，对 sRGB 图应与 matrix/TRC 快路径几乎重合
   （ΔE 小），这条同时抓"XYZ 因子写错 2 倍"。
4. **缺口登记**：无第三方真实扫描仪 profile 的可分发语料，`known-gaps.md` 如实写"构造向量 + 双精度参考"，
   **不写"已对拍真实 profile"**。

## 6. 什么时候该推翻这个决策

- 上游 `Sdcb.SimdCodecs.Color.Icc` 暴露 CLUT 网格/1D 表 → 改为消费上游，删本库解析器（`IccProfile.Parse` 是唯一替换点）。
- 需要 CMYK / Lab 设备空间或 Intent 1/2 → 那是另一套求值路径（N 输入、G 输出、intent 链），应重新评估是否继续自建。
- 需要绝对色度意图（media white 补偿）→ 需要 profile 的 `wtpt`/`chad` 语义，超出 PCS 编解码本身。
