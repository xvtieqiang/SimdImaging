# C1 证据：ImageSharp 4.1.3 的 NativeAOT 能力（黑盒实测）

补上 C1 里缺失的参照实现侧证据。探针**只**通过公开 API 调用 NuGet 4.1.3 黑盒二进制，不读取源码。

```bash
dotnet publish benchmarks/SimdImaging.Comparison.AotOracle/SimdImaging.Comparison.AotOracle.csproj -c Release -r win-x64
benchmarks/SimdImaging.Comparison.AotOracle/bin/Release/net10.0/win-x64/publish/SimdImaging.Comparison.AotOracle.exe
```

许可密钥通过 `SixLaborsLicenseFile` **按路径**引用 `docs/sixlabors.lic`（git-ignored），不进入任何文件字面量或日志。

## 两次实测（2026-11-06，win-x64 / .NET 10.0.14）

| 探针内容 | publish | warning | error | 运行 | 产物 |
|---|---|---|---|---|---|
| 内存图 → Resize/Grayscale/GaussianBlur（处理链） | ✅ | 0 | 0 | `aot-oracle-ok 32x32 dynamic-code=False` | 5 535 232 B |
| 加上 **PNG 编码 + `Image.Load<Rgba32>` 解码**（完整链路） | ✅ | 0 | 0 | `aot-oracle-ok png=120B decoded=32x32 dynamic-code=False` | 10 902 016 B |

## 结论修正（重要）

优化前报告把 C1 判成「本库赢」，依据是 4.1 发布说明里「AOT seeds 覆盖 ICO/CUR/QOI」。**这个前提被实测推翻了**：
参照实现 4.1.3 在 NativeAOT 下，含 PNG 编解码的完整链路也能 publish 成功、零警告、正常运行。

因此 **C1 判定从「赢」改为「平」**。剩下的差异只是工程契约，不是能力：

- 本库把 `IsAotCompatible` 作为全库构建门禁，且 `run.sh` 强制发布 **avx2 / SSE2 两条基线**并断言实际 SIMD 档位，防止「测试全绿但 AVX2 内核被 ILC 裁掉」。
- 参照实现 4.1 补的 seeds 说明它在逐格式补齐泛型管线，但常见格式在本探针中并不需要额外配置。

## 未覆盖（不要外推）

- 只测了 **PNG + Rgba32** 一种组合；JPEG / WebP / GIF / TIFF / EXR / ICO / CUR / QOI 与其它像素格式的 AOT 组合未逐个验证。
- 只测了 win-x64；linux-x64 / arm64 / wasm 未测。
- 没测参照实现在 AOT 下的多帧动画、ICC 转换、`ColorProfileHandling` 等路径。

要把它写成「参照实现 AOT 全面可用」同样是没有证据的——目前只能说**这一条链路可用**。
