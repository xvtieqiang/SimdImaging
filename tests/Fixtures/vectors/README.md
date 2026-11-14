# 公开参考向量包

这里放**固定输入 + 固定参数 + 独立推导的期望输出 + SHA-256**，作为 Warp 精度、融合一致性、SIMD 档位与 AOT 一致性的统一度量衡。

## 规则

- `generate-vectors.py` **不得**导入、读取或调用 `SimdImaging`。期望值来自独立的朴素高精度推导，不是对当前实现的快照。
- 每条向量 `determinism` 只能是 `D0` / `D1` / `D2`（口径见 `docs/operators.md`）。声明 `D2` 时 `tolerance` 必填，且必须与 `Metrics.Grade` 的既有分级对齐（`每通道 ≤1 LSB` 或 `PSNR ≥ 40`），不许另造尺子。
- **新增向量允许；为了让测试通过而改动已有 `expected` 禁止**（`AGENTS.md` 的 golden 红线）。需要改语义时，新建一个带新 id 的向量，并在 `docs/known-gaps.md` 记录原因。
- `<case>.sha256` 是期望字节的完整 SHA-256（大写 hex，单行），由生成脚本写入，`VectorTests` 会独立重算比对。

## 格式

`<case>.json` 字段：

| 字段 | 含义 |
|---|---|
| `id` | 唯一标识，进测试名、进基准用例名 |
| `generator` | 生成脚本名，便于复现 |
| `determinism` | `D0` 整数定点 / `D1` 浮点固定序+FMA / `D2` 顺序依赖 |
| `tolerance` | 仅 `D2` 必填；`null` 表示要求逐字节一致 |
| `input` | `width` / `height` / `format` / `bytes`(hex) / `sha256` |
| `params` | 算子与全部参数，含相位、边界、采样域、Alpha 模式 |
| `expected` | `bytes`(hex) / `sha256` |

## 复现

```bash
python tests/Fixtures/vectors/generate-vectors.py
dotnet test tests/SimdImaging.Tests/SimdImaging.Tests.csproj -c Release --filter "FullyQualifiedName~VectorTests"
```

初始三条 `warp-identity-*` 是 M7（P0 Warp 精度修复）的高精度参考：Rgba16 的样本刻意跨 8-bit 边界（验证低 8 位不再被 `/257` 丢掉），Rgba32F 含 `4.0` / `-0.25` / `12.5`（验证 HDR、负值与超范围不再被钳位）。恒等映射在像素中心相位下只有一个权重为 1.0 的 tap，所以三种格式的期望输出都等于输入字节本身。

第四条 `sample-options-encoded-midpoint` 是 M8 显式采样契约的独立推导参考：2×1 的黑白 Rgba8 在 encoded 域双线性中点为 `0.5`，round-half-up 后是 `128`（无浮点歧义）。它由 `generate-vectors.py` 的 `midpoint_vector()` 生成，与被测库无耦合。

## 覆盖矩阵（随里程碑推进）

| 维度 | 落地里程碑 |
|---|---|
| Warp 三格式精度 / HDR | M7（`warp-identity-*` 已落地） |
| 显式 `SampleOptions` 契约（5 核 / LinearLight / Premultiplied / OutOfGamut） | M8 进行中（`sample-options-encoded-midpoint` 已落地，其余待补） |
| 融合 / 非融合逐字节一致 | M10 |
| 四档 SIMD + JIT / AOT | M6 脚本，M15 全量 |
| EWA / 自适应 AA | M9 |
| NLM / Local Laplacian / RL / Seam | M11–M12 |
| GainMap / ICC CLUT | M13 |
