# Oracle 契约 — ImageSharp 4.1.3

对标基线锁定 **SixLabors.ImageSharp 4.1.3**（net8.0，Six Labors Split License 1.0）。
本项目只做**功能与行为的黑盒对比**，不参考源码。

## 固定版本与哈希

| 项 | 值 |
|---|---|
| NuGet 包 | `SixLabors.ImageSharp` **4.1.3** |
| 下载 URL | `https://api.nuget.org/v3-flatcontainer/sixlabors.imagesharp/4.1.3/sixlabors.imagesharp.4.1.3.nupkg` |
| SHA-256 | `2997ca222c529e851f9e31b2926cc09b224c1f016b5e4f75c6dad17ee817aae2` |
| 包大小 | 1 377 514 B（2026-11-06 实测） |
| 公开文档快照日期 | 2026-11-06（sixlabors.com 产品页 / 4.1.0 发布说明 / NuGet 包页） |
| 程序集信息版本 | 由 `benchmarks/SimdImaging.Comparison` 运行时反射打印，写进结果文件 |

版本由根目录 `Directory.Packages.props` 集中锁定。**换版本必须同时改哈希与快照日期**，并在报告里注明，
绝不拿 3.1.x 的数字冒充 4.x。

## Clean-room 规则（不可谈判）

- 允许：NuGet 黑盒二进制、公开 API 文档、编译后程序集的**公开类型 / 方法签名**（等价于 API 参考）。
- 禁止：读取、反编译、反汇编或复述 ImageSharp / SkiaSharp / ImageMagick / OpenCV 的源码与实现。
- 算法只来自规范、论文与 CPU 手册。功能面的「命中」只代表**公开方法名存在**，名称不同不等于功能缺失，
  报告中必须保留这句限定。
- 测试语料只用 PD / CC0 或**自生成确定性图案**；本对比工程用的是自生成图案，不下载、不提交任何第三方素材。

## 许可密钥

ImageSharp 3.2.0 / 4.x 在**构建期**校验 Six Labors 许可密钥：Debug 只警告，Release 失败。
密钥文件位于 `docs/sixlabors.lic`。`.gitignore` 第 21/22 行写有 `*.lic` 与 `docs/sixlabors.lic` 两条规则。
⚠ 当前工作区**没有 `.git` 目录**，规则尚未由 git 实际生效；首次用 VS 接入或初始化仓库时，必须先确认该 lic 文件没被纳入版本控制。

- `benchmarks/SimdImaging.Comparison` 通过 MSBuild 属性 `SixLaborsLicenseFile` **按路径**引用它。
- 密钥内容**绝不**进入 csproj 字面量、报告、日志、CI 输出或任何 diff。若出现在 diff 里，那是泄漏。
- 该对比工程**不进 `SimdImaging.slnx`**：主解决方案必须能在没有密钥的机器上 Release 构建通过。

## 运行

```bash
dotnet run -c Release --project benchmarks/SimdImaging.Comparison -- all 1920x1080
# 或只跑某一组： surface | parity | perf
```

结果写到 `benchmarks/SimdImaging.Comparison/results/<日期>.raw.md`，含机器、版本、维度矩阵与原始数字。
人工复核与结论写进 `docs/compare-4.1.3-<日期>.md`。

计时口径照 `SimdCodecs/benchmarks/.../Infrastructure.Timing`：同进程交错、每选手热身 ≥300 ms、
7 轮 × ≥30 ms 采样取中位数；分配用 `GC.GetAllocatedBytesForCurrentThread`；PSNR 只算 RGB、忽略 alpha。
对手固定 `MaxDegreeOfParallelism = 1`，两边都从共享源**产生一张新图**（对手 `Clone(ctx => ...)`）。
