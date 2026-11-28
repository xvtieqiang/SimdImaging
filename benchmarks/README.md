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
| `randomchain <宽x高>` | 随机链按长度分档（短 2–4 / 中 5–9 / 长 10–20）：时间 + 分配 + 一致性 |
| `transcode <宽x高>` | 文件级转码 PNG→PNG：我方流式 / 我方整图 / 对手，时间 + 分配 + **冷进程峰值** |
| `fusionab <宽x高>` | 融合决策 A/B（我方流式）：按 `ShouldFuse` 决策 vs `ForceFusion`，段数 + 时间 + 冷进程峰值 |
| `crossover <宽x高>` | 流式 vs 整图的形状表：5 条链 × 10 个尺寸，时间比 + 峰值比 + 按噪声下限（15%）的判定。回答「该不该流式」而不是「谁快」 |

**本库自测（无对手）**

| 模式 | 内容 |
|---|---|
| `mem` / `memsteady` | 分配与常驻内存 |
| `peakmem` | 峰值 RSS |
| `seamprofile` | 接缝裁剪分段剖析 |
| `fusion` | 融合是否发生（按池租用次数判定） |
| `batch` | 批量场景（多张图连续处理） |
| `icc` | ICC 变换的自洽验证（恒等 / 往返 / 中性轴） |
| `chainab <宽x高>` | 同一条点链的三条路径（融合 / 不融合 / 直调） |
| `cores <宽x高>` | 各算子的有效核数（`gates.sh --cores` 用） |
| `realworld` | 真实照片跑全部算子（`SIMDIMAGING_DUMP_DIR` 可导出对比图） |
| `disposecheck` | 忘记 `using` 的代价 |
| `nlm-ablate` | NLM 内层消融 |
| `nlm-bench <宽x高> <S> <P>` | NLM 端到端秒表 + 输出 SHA 字节锚点 |
| `chaindiag <宽> <高>` | 同一条长链的五条路径（编译器决定 / 强制不融合 / 强制融合 / 仅编译 / 对手）+ 带高扫描 |

**脚本**

| 脚本 | 内容 |
|---|---|
| `bash benchmarks/gates.sh [--aot] [--perf] [--realworld]` | 构建零告警 → 全量测试 → 公开 API 快照 →（可选）AOT、性能基线、真图跑通 |
| `bash benchmarks/simd-tiers.sh [--aot]` | 在 AVX2 / Vector128 / 标量三档下跑全量并用 body SHA 比对 |
| `bash benchmarks/repeat.sh <模式> <宽x高> <轮次>` | 多轮取中位，标出真落后与噪声 |
| `bash benchmarks/check-baseline.sh` | 与 `baselines/*.json` 的记录值比，越阈值即失败 |

## 冷启动

`benchmarks/SimdImaging.ColdStart` 只做一件事（64×64 + 一次高斯），用来量**整个进程**的墙钟。
程序自己打印的 `elapsed=` 只是 in-process 算子时间，不进墙钟账——墙钟必须在外面打点，三次取中位：

```bash
dotnet build benchmarks/SimdImaging.ColdStart -c Release
dotnet publish benchmarks/SimdImaging.ColdStart -c Release -r win-x64 \
    -p:PublishAot=true -p:IlcInstructionSet=avx2 -o /tmp/cs   # avx2 与 AOT 门禁同款，否则 ILC 裁掉 AVX2 内核

# 每项各跑三次，取中位；把下面的命令换成 AOT 产物即为第二组
for i in 1 2 3; do
    start=$(date +%s%3N)
    dotnet benchmarks/SimdImaging.ColdStart/bin/Release/net10.0/SimdImaging.ColdStart.dll >/dev/null
    echo "JIT $(( $(date +%s%3N) - start )) ms"
done
# AOT: /tmp/cs/SimdImaging.ColdStart.exe
```

当前读数与其口径限定见 `docs/measurements.md`「冷启动」节。注意墙钟差包含宿主启动、程序集加载、
类型初始化与 JIT，不能整体记成 JIT 成本；要单独量 JIT，需同一进程连跑两次、用首次减第二次。

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
