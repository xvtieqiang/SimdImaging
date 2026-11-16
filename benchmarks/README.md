# Benchmarks

同进程 A/B 的秒表，不是 BenchmarkDotNet。它只回答一个问题：**端到端这次操作有没有变快**。
内核隔离的数字不记在这里——曾经有一个更快的内核让真实流水线慢了 42%（详见 SimdCodecs 的基准教训）。

```bash
dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- <模式> [宽高]
dotnet run -c Release --project benchmarks/SimdImaging.Benchmarks -- [轮次]
```

## 口径

| 规矩 | 原因 |
|---|---|
| 同进程交错计时，取中位 | 分段跑会让机器漂移只落在一边（曾把对手读数拉到 3.5 倍区间） |
| 热身 ≥300 ms | 首帧付 JIT、池增长与一次性建表，不能摊到稳态里 |
| 单次读数不引用 | 同一格在不同轮次能差 2–3 倍时必须标成噪声，`repeat.sh` 会自动标 |
| 先正确性后计时 | 每个算子先与对手比对字节/PSNR，再谈毫秒 |
| 机器随结果记录 | 换机器、换电源策略都会动数字，结论必须带上下文 |

## 模式

**算子对拍（对 ImageSharp 4.1.3）**

| 模式 | 内容 |
|---|---|
| `all` / 默认 | 全部：表面 + 一致性 + 性能 + 尺寸矩阵 |
| `surface` | 算子表面覆盖对拍 |
| `parity` | 输出一致性（字节 / PSNR） |
| `perf <宽x高>` | 19 项算子端到端，`Rgba8` |
| `perf16 <宽x高>` | 7 项，`Rgba16` |
| `perf32 <宽x高>` | 8 项，`Rgba32F` |
| `matrix` | 四尺寸 × 三格式，全量 |
| `perfpipe` | 处理链场景（融合 vs 原地链） |

**本库自测（无对手）**

| 模式 | 内容 |
|---|---|
| `mem` / `memsteady` | 分配与常驻内存 |
| `peakmem` | 峰值 RSS |
| `seamprofile` | 接缝裁剪分段剖析 |
| `fusion` | 融合是否发生（按池租用次数判定） |
| `batch` | 批量场景（多张图连续处理） |
| `icc` | ICC 变换的自洽验证（恒等 / 往返 / 中性轴） |
| `realworld` | 真实照片跑全部算子（`SIMDIMAGING_DUMP_DIR` 可导出对比图） |
| `disposecheck` | 忘记 `using` 的代价 |
| `nlm-ablate` | NLM 内层消融 |
| `nlm-bench <宽x高> <S> <P>` | NLM 端到端秒表 + 输出 SHA 字节锚点 |

**脚本**

| 脚本 | 内容 |
|---|---|
| `bash benchmarks/gates.sh [--aot] [--perf]` | 构建零告警 → 全量测试 → 公开 API 快照 →（可选）AOT 与性能基线 |
| `bash benchmarks/simd-tiers.sh [--aot]` | 在 AVX2 / Vector128 / 标量三档下跑全量并用 body SHA 比对 |
| `bash benchmarks/repeat.sh <模式> <宽x高> <轮次>` | 多轮取中位，标出真落后与噪声 |
| `bash benchmarks/check-baseline.sh` | 与 `baselines/*.json` 的记录值比，越阈值即失败 |

## ICC 素材

`icc` 模式要两个 RGB profile，放在 `tests/Fixtures/.cache/icc/`（**不入库**，与语料同规矩）。
来自 [Compact ICC Profiles](https://github.com/saucecontrol/Compact-ICC-Profiles)（CC0）：

```bash
mkdir -p tests/Fixtures/.cache/icc
for n in srgb-v4 DisplayP3-v4 AdobeCompat-v2; do
  curl -sSL -o "tests/Fixtures/.cache/icc/$n.icc" \
    "https://raw.githubusercontent.com/saucecontrol/Compact-ICC-Profiles/master/profiles/$n.icc"
done
sha256sum tests/Fixtures/.cache/icc/*.icc
# srgb-v4.icc        c56e1685d888f5edb92fe07f2750f387f8fe8e91b32ff8fb0b56bfbbb9458353
# DisplayP3-v4.icc   cb51de38e482ee974c0c76b9689e16aad04bad16e226fed2f30c842d15ff3a3d
# AdobeCompat-v2.icc 60fb2adecacf82132db0b1c09b303316f3bbd9e2823e7ba096d01627d12d57c9
```

## 结果去哪

| 位置 | 内容 |
|---|---|
| `results/` | 每次运行的原始报告（自动生成，带日期与模式） |
| [`docs/measurements.md`](../docs/measurements.md) | 各模式的结论与关键数字（人工整理） |
| [`docs/measurements.md`](../docs/measurements.md) | 按日期的工作与实测报告 |
| [`docs/known-gaps.md`](../docs/known-gaps.md) | 未达标项、已否决方向、方法论教训 |

## 新增实验的工作流

1. **先量后改**：动手前先做消融，确认主因（seam 的 Energy 看着像大头，实测只占 17%；NLM 的指数看着像大头，
   实测只占 19%，真正的 78.7% 是 patch 扫描）。
2. **字节锚点**：改动必须能证明输出未变——要么逐字节比对，要么给一个 SHA 记在测试里。
3. **负结论也留**：否决一个方向同样要有数字，写进 `known-gaps.md`，避免下一个人重做。
