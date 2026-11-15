# 版本化性能基线

与 git-ignored 的 `benchmarks/SimdImaging.Comparison/results/` 不同，**这个目录随仓库走**：它是 `gates.sh` 第 5 步判定「明显回退」的对照物。原始测量报告仍只留本机，权威数字仍写在 `benchmarks/README.md` 与 `docs/known-gaps.md`。

## 文件命名

`<machine>-<milestone>.json`，例如 `windows-10.0.19045-x64-16-m15.json`。

`machine` 由 `BaselineJson.Machine` 生成，格式固定为 `OSDescription|ProcessArchitecture|ProcessorCount`——**刻意粗**：它只用来挡住"换了一台机器"的比较，真正的可比性还要求 `binarySha256` 相同。两台同配置的机器会撞 id，这是已知的、可接受的近似（见 `docs/known-gaps.md`）。

## Schema

```json
{
  "machine": "Microsoft Windows 10.0.19045|X64|16",
  "date": "2026-11-15",
  "gitSha": "<commit>",
  "binarySha256": "<sha of SimdImaging.Processing.dll>:<sha of SimdImaging.Abstractions.dll>",
  "protocol": "best-of-N warm single process; SimdImaging.Benchmarks; slow=0; norm=max(mem,alu)",
  "repeats": 3,
  "calibration": { "memory": 10.262, "alu": 92.815 },
  "entries": [
    {
      "scenario": "resize 4K->1080p bicubic",
      "size": "as named",
      "metric": "milliseconds",
      "value": 65.8,
      "unit": "ms",
      "thresholdPercent": 38.0,
      "megapixelsPerSecond": 0.0,
      "allocatedBytes": 0
    }
  ]
}
```

`calibration` 是同一进程里两个**不调用本库**的探针读数（毫秒）：`memory` 是 256 MB 并行流式求和，`alu` 是 32M 次 xorshift 取和。判定时用 `run_probe / baseline_probe` 中最差的那一个把场景读数除回来，见下节。

`thresholdPercent: null` 表示该场景**不设门槛**。`gates.sh` 只对同机器、同协议、同 `binarySha256` 口径可比、且 `thresholdPercent` 非 null 的条目判定回退。

## 为什么判定的是「校正后的读数」而不是毫秒

2026-11-15 实测：同一台机器、同一份源码，04:00 与 07:00 两个时点，**黑盒参照（ImageSharp 4.1.3，不受本库代码影响）
的 resize-bicubic 从 12.5 ms 涨到 19.3 ms（1.54×）**，我方同项 4.97 → 7.91 ms（1.59×），29 个场景整表慢 1.2–1.8×。
参照都慢了同样的比例，说明变的是机器，不是代码。绝对毫秒在这种漂移下无法分辨「回退」与「机器忙」，只会要么误报、
要么把真回退埋在膨胀的基线里。

所以判定用 `got_raw / degradation` 对 `baseline.value`，其中
`degradation = max(run.memory / base.memory, run.alu / base.alu)`——取两个探针里恶化更重的那个：哪种资源被抢，
校正就覆盖哪种。校正只会让门槛在机器退化时更宽松，**不会凭空制造回退**：探针不动时 `degradation = 1`，场景按自己的
movement 判定。

已验证（2026-11-15，本机）：人为抢占 4 核后，`alu` 探针报 **x1.59**，门禁按 x1.59 校正，24 条设闸场景**最差 -1.2%、全部通过**；
同一负载下未校正的原始读数均值 +40%。

## 门槛纪律

- 只对**公平、可重复**的场景设门槛：1080p / 4K / 8K 的单算子与多算子链，3 次最好值 + 区间，读区间不读最好值。
- **单次读数、跨机器、跨会话、无对等实现的场景不设门槛**（`benchmarks/README.md` 的既有纪律）。
- **阈值怎么定**：同一 binary 连跑**三次**，每次的读数先按各自的 calibration 归一化，再看三次的最大相对偏差 `S`。
  阈值取 `max(15, ceil(2.5 × S))`，即「门槛至少是实测噪声的 2.5 倍」；`S > 20%` 的场景留 `null`，因为它的噪声已经
  大到无论设多少都测不出有意义的东西。2026-11-15 的 29 条里 24 条设了闸（15–48%），null 的 5 条是 crop / fill path /
  clip path / icc grid=33 / icc matrix-trc。
- **不要为了让某个场景进门而降低这个倍数**：门槛低于噪声就是随机红灯，这正是 11-14 那版「同一对跑 ≤10% 才设 15%」的
  缺陷——它只用了**一对**记录，把当天恰好安静的 15 个场景当成了稳定场景，换一天就翻车。
- 自检：`bash benchmarks/check-baseline.sh --self-test` 把所有阈值压到 0 并要求**失败**——一个只会在正常时通过的比较脚本等于没写。
- 性能门禁是 opt-in：`bash benchmarks/gates.sh --perf`；默认 `gates.sh` 仍保持约 1 分钟。

## 与语料、向量的关系

性能场景的输入必须来自 `tests/Fixtures/corpus.tsv`（真实照片）或 `SyntheticCorpus`（确定性合成图），不得用临时随机图；否则基准输入漂移会让回退判定失真。像素正确性由 `tests/Fixtures/vectors/` 单独保证，两者互不替代。

## 二进制指纹与「故意跳过」

`binarySha256` 锁 `SimdImaging.Processing.dll` + `SimdImaging.Abstractions.dll`。改到这两个 dll 的产品改动会让指纹变化，
`check-baseline.sh` 判「binary differs」→ `comparable-baselines=0` → 打印一行「no baseline matches」后 PASS。
**这是设计，不是漏洞**：拿不同 binary 的 stopwatch 读数比较是噪声、不是门禁（脚本头注释）。此时要重录一份带新指纹的
基线，门禁才重新「有牙」——重录是刻意的、需复核的动作，不能在负载里顺手做。重录协议：同一 binary 连跑三次
`--no-build`（每次都写 `calibration`），合并时 `value` 取三次均值、`calibration` 取三次均值、阈值按上节规则从
归一化后的三次偏差推出，`date` 取**本地**日期（生成器写的是本地日，不是 UTC——凌晨录制曾把 11-15 写成 11-14）。

- **不要为了"让绿灯"去改基线值或强行对齐指纹**。2026-11-15 的教训：一次 nearest 内层改动（见 `docs/known-gaps.md`）
  后曾把 `binarySha256` 强改成新 dll 值，结果拿「机器满载时的 fresh run」去比「空闲时测的 M15 值」，15 个场景
  +30~98% 全线 FAIL。把 nearest 还原成旧写法重测，同一批场景同样 +10~70%、甚至更慢（resize-bicubic 旧 +70% vs 新 +40%）——
  证明那不是代码回归，而是连续 AOT publish / benchmark 累积的机器负载。随后把指纹改回原值，让门禁按设计跳过。
- 由此重申：跨会话、单次、受负载污染的读数**一律不进基线**；「同机器」只挡换机器，挡不住本机热节流与后台负载。
  判断有没有真回归，要在机器空闲时、同一 binary 下连跑两次读区间，或做"改动前/后 binary 同时段 A/B"对照。
