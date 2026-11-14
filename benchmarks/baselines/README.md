# 版本化性能基线

与 git-ignored 的 `benchmarks/SimdImaging.Comparison/results/` 不同，**这个目录随仓库走**：它是 `gates.sh` 第 5 步判定「明显回退」的对照物。原始测量报告仍只留本机，权威数字仍写在 `benchmarks/README.md` 与 `docs/known-gaps.md`。

## 文件命名

`<machine>-<milestone>.json`，例如 `windows-10.0.19045-x64-16-m15.json`。

`machine` 由 `BaselineJson.Machine` 生成，格式固定为 `OSDescription|ProcessArchitecture|ProcessorCount`——**刻意粗**：它只用来挡住"换了一台机器"的比较，真正的可比性还要求 `binarySha256` 相同。两台同配置的机器会撞 id，这是已知的、可接受的近似（见 `docs/known-gaps.md`）。

## Schema

```json
{
  "machine": "Microsoft Windows 10.0.19045|X64|16",
  "date": "2026-11-14",
  "gitSha": "<commit>",
  "binarySha256": "<sha of SimdImaging.Processing.dll>:<sha of SimdImaging.Abstractions.dll>",
  "protocol": "best-of-N warm single process; SimdImaging.Benchmarks; slow=0",
  "repeats": 3,
  "entries": [
    {
      "scenario": "resize-bicubic-1080p",
      "size": "1920x1080",
      "metric": "throughput | latency | allocated-bytes | peak-workingset",
      "value": 0.0,
      "unit": "MPix/s | ms | KiB | MiB",
      "thresholdPercent": 15.0,
      "megapixelsPerSecond": 0.0,
      "allocatedBytes": 0
    }
  ]
}
```

`thresholdPercent: null` 表示该场景**不设门槛**。`gates.sh` 只对同机器、同协议、同 `binarySha256` 口径可比、且 `thresholdPercent` 非 null 的条目判定回退。

## 门槛纪律

- 只对**公平、可重复**的场景设门槛：1080p / 4K / 8K 的单算子与多算子链，3 次最好值 + 区间，读区间不读最好值。
- **单次读数、跨机器、跨会话、无对等实现的场景不设门槛**（`benchmarks/README.md` 的既有纪律）。
- **阈值怎么定**：同一 binary 连跑两次，只有**每一对**记录差都 ≤10% 的场景才设 `15%` 门槛（给噪声留 5 个点），其余保持 `null`。实测（2026-11-14，三对记录）：量化/滤镜/NLM/LL 这类 ≥150 ms 的场景差 ≤7.7%；而 stroke 23%、gaussian 15.6%、bilateral 21.8%、ICC 19.6%、crop 20.5%、lanczos 25.9%、dt 21.7%，以及更早一对里的 rl +29%、seam +25%、icc grid=2 +37% 由分层编译与调度噪声主导，给它们设门槛只会做出一个随机失败的绿灯。
- 自检：`bash benchmarks/check-baseline.sh --self-test` 把所有阈值压到 0 并要求**失败**——一个只会在正常时通过的比较脚本等于没写。
- 性能门禁是 opt-in：`bash benchmarks/gates.sh --perf`；默认 `gates.sh` 仍保持约 1 分钟。

## 与语料、向量的关系

性能场景的输入必须来自 `tests/Fixtures/corpus.tsv`（真实照片）或 `SyntheticCorpus`（确定性合成图），不得用临时随机图；否则基准输入漂移会让回退判定失真。像素正确性由 `tests/Fixtures/vectors/` 单独保证，两者互不替代。

## 二进制指纹与「故意跳过」

`binarySha256` 锁 `SimdImaging.Processing.dll` + `SimdImaging.Abstractions.dll`。改到这两个 dll 的产品改动会让指纹变化，
`check-baseline.sh` 判「binary differs」→ `comparable-baselines=0` → 打印一行「no baseline matches」后 PASS。
**这是设计，不是漏洞**：拿不同 binary 的 stopwatch 读数比较是噪声、不是门禁（脚本头注释）。此时要在**机器空闲**时用
两次 best-of-3 重录一份带新指纹的基线，门禁才重新「有牙」——重录是刻意的、需复核的动作，不能在负载里顺手做。

- **不要为了"让绿灯"去改基线值或强行对齐指纹**。2026-11-15 的教训：一次 nearest 内层改动（见 `docs/known-gaps.md`）
  后曾把 `binarySha256` 强改成新 dll 值，结果拿「机器满载时的 fresh run」去比「空闲时测的 M15 值」，15 个场景
  +30~98% 全线 FAIL。把 nearest 还原成旧写法重测，同一批场景同样 +10~70%、甚至更慢（resize-bicubic 旧 +70% vs 新 +40%）——
  证明那不是代码回归，而是连续 AOT publish / benchmark 累积的机器负载。随后把指纹改回原值，让门禁按设计跳过。
- 由此重申：跨会话、单次、受负载污染的读数**一律不进基线**；「同机器」只挡换机器，挡不住本机热节流与后台负载。
  判断有没有真回归，要在机器空闲时、同一 binary 下连跑两次读区间，或做"改动前/后 binary 同时段 A/B"对照。
