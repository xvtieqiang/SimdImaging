# SimdImaging.Lab · 实验台

把 `SimdImaging` 与 `ImageSharp 4.1.3` 放在同一张图上对比：选图 → 选算子 → 调参数 → 一次运行拿到**时间、
内存**和倍数。结果用滑块做前后对照，左图右表。全部在本机进程内完成，不上传任何地方。

页面顶部有两个视图，它们回答的问题不一样，谁也不替代谁：

| 视图 | 回答的问题 | 背后 |
|---|---|---|
| **单算子** | 这一个内核多快、准不准、和 ImageSharp 差多少 | `/api/run`，一个算子一次 |
| **场景链** | 六步串起来之后画面还在不在、时间花在哪、内存峰值多少 | `/api/chain`，`ProcessingPlan` 一次跑完整条 |

单算子测不出两种失效：**误差累积**（每步 ±1 LSB 被后面的对比度/锐化逐级放大）和**内存峰值**（链路每步
租一张整图）。场景链就是为这两件事存在的。

**混合模式（18 种）和 Porter-Duff（12 式）是单算子目录里的两组普通条目**，不是单独的视图——拿两张图
怎么叠，也是"一个算子"，只是多一个输入。选中它们时参数区会多出"叠加图"下拉和缩放滑条，说明文案写的
是**这个模式干什么活**（压印水印、加光斑、上色、橡皮擦、蒙版），公式在后面。

叠加图**默认是同模式配套的合成演示层**，不是当前照片——这一点是经实测后才改的：

| 演示层 | 长什么样 | 用在哪 |
|---|---|---|
| **光斑层** | 黑底 + 三团彩色柔光 | 滤色 / 线性减淡 / 颜色减淡 / 变亮 / 柔光 / 强光 / 排除 —— 黑底不参与，只剩光 |
| **水印层** | 白底 + 黑线条框与圆环 | 正片叠底 / 变暗 / 颜色加深 / 减法 —— 白底消失，只剩线条压印 |
| **透明彩条** | 六色竖条 + 径向 alpha | 色相 / 饱和度 / 颜色 / 明度，以及全部 Porter-Duff（语义要有透明区才看得见） |

一开始默认用当前照片当叠加图，结果**变暗对同一张图逐通道取小等于原图**（`min(x,x)=x`），正片叠底、
滤色、变亮全部退化成"什么都没变"，输出与输入一致。问题出在默认叠加图的选择上，而不是功能本身。

它们暂无 ImageSharp 对照：逐个模式核对参照实现语义是独立任务，名字对得上不代表语义对得上，硬给倍数没有意义。

## 跑起来

```bash
dotnet run -c Release --project samples/SimdImaging.Lab
# 然后打开 http://localhost:5000（或控制台打印的地址）
```

页面里有 **3 张真实 1920×1080 照片**（`wwwroot/samples/photo-{landscape,portrait,detail}.jpg`）、
2 张彩条测试图（带透明的那张用来验 alpha 路径）、4 张合成图，也可以直接拖自己的文件进来。

## 示例图片的来源

| 文件 | 来源 | 说明 |
|---|---|---|
| `photo-{landscape,portrait,detail}.jpg` | [Lorem Picsum](https://picsum.photos)（图为 Unsplash 摄影，Unsplash License） | 各约 200–300 KB，1920×1080，固定 seed 取的三张真实照片 |

### 示例照片

`wwwroot/samples/` 下**三张 720p jpg 是入库的**（共 388 KB），克隆后开箱即用：

| 文件 | 内容 | 适合看 |
|---|---|---|
| `photo-face.jpg` | 柔光人像 | 双边、锐化、辉光 |
| `photo-portrait.jpg` | 雨林栈桥（纹理密） | 高斯/方框/中值模糊、油画 |
| `photo-mist.jpg` | 雾景（低反差） | 直方图均衡、CLAHE、色调映射 |

它们不是测试夹具——`tests/` 里所有像素都由程序生成，符合"测试图按 URL + SHA-256 下载、不入库"的规则。
这三张是**演示素材**，体积压到 720p 就是为了让仓库能直接带走。

来源：Lorem Picsum（Unsplash License），固定 seed 可复现：

```bash
cd samples/SimdImaging.Lab/wwwroot/samples
curl -sL -o photo-face.jpg     "https://picsum.photos/seed/simd3-texture/1280/720"
curl -sL -o photo-portrait.jpg "https://picsum.photos/seed/simd2-portrait/1280/720"
curl -sL -o photo-mist.jpg     "https://picsum.photos/seed/simd3-contrast/1280/720"
```

「合成 · 带透明」不依赖任何文件——彩条与 alpha 渐变由 `/api/sample` 现算。

三张 jpg 只是演示素材，换掉不影响任何逻辑：删掉文件、改 `index.html` 里 `photos` 数组即可。
如果要进公开仓库，建议换成自摄或明确 CC0 的素材。

## 为什么要单独一个工程，且不在 `SimdImaging.slnx` 里

工程引用了 `SixLabors.ImageSharp`，而它**在构建期校验授权密钥**（Release 下缺密钥直接失败）。密钥文件
`docs/sixlabors.lic` 已被忽略、不入库，所以这个工程和 `benchmarks/SimdImaging.Comparison` 一样**不进入
主解决方案**——保证交付用的 `SimdImaging.slnx` 在没有密钥的机器上仍能构建。

密钥只按路径引用：

```xml
<SixLaborsLicenseFile>$(MSBuildThisFileDirectory)..\..\docs\sixlabors.lic</SixLaborsLicenseFile>
```

## 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/info` | 版本、.NET、SIMD 宽度、核心数、参照实现版本 |
| GET | `/api/memory` | 内存构成：WorkingSet、托管堆、强制 GC 后的存活量、两个缓存各占多少；`?compact=true` 先压缩 |
| GET | `/api/ops` | 算子目录（分组、参数范围、默认值） |
| POST | `/api/open` | `multipart/form-data` 上传，返回 id 与尺寸 |
| POST | `/api/sample` | `{width,height}` 生成确定性合成图，返回同 `/api/open` |
| POST | `/api/run` | `{id, op, params, overlayId?, scale?}` → 两边耗时、倍数、输出 id；需要第二张图的算子带 `overlayId` |
| GET | `/api/chains` | 配方目录（步骤名、类别、halo、参数、注意事项） |
| POST | `/api/chain` | `{id, chain, params}` → 总耗时、分配、**每步的耗时与中间图 id** |
| POST | `/api/compare` | `{id}` → 全部配方各跑一次，返回并排对比用的结果 id |
| POST | `/api/batch` | `{ids[], op\|chain, params, concurrency, encode}` → 逐张耗时、总吞吐；`encode:false` 只跑算子不出图 |
| GET | `/api/source/{id}` / `/api/output/{id}` | 原图 / 结果（PNG） |

## 规模

90 个算子，分六组：点算子 20 / 滤波 18 / 几何 12 / 色调 10 / 混合 18 / Porter-Duff 12。其中 **19 个有 ImageSharp 对等实现**，给出时间与分配的
倍数；其余只展示效果，并写明为什么没有对等实现（多数是 4.1.3 根本没有该扩展方法，由编译器确认，不是猜测）。

另有 **6 条配方**（复古胶片 6 步 / 线稿化 5 步 / 黑白胶片 4 步 / 风光增强 3 步 / 雾蓝冷调 5 步 /
品牌水印 3 步），走 `ProcessingPlan` 一次执行，产出每步的中间图。「场景链」分组里还有一个
**▶ 全部跑一遍**，把六条配方同时对当前图跑一次，出并排对比。

**品牌水印是唯一需要第二张图的配方**：水印层按底图尺寸生成，正片叠底全幅压印，再提对比、轻微锐化。
它验证的是"合成之后继续处理"这条路，同时标出 `ProcessingPlan` 的一条边界——合成步骤只能以
`Global` 整图执行，切带没有意义（`Blend` 要读整幅叠加层）。

> 库里的算子编号有 148 个（见 `docs/operators.md`），实验台覆盖 60。差额主要是需要第二张输入图的
> 合成类（18 种混合模式、12 式 Porter-Duff、水印、图层合成）和需要色彩配置文件上下文的（ICC、色彩空间
> 转换），这些不是"加一行 Catalog"能接的，得先有第二张图和配置入口。

## 口径（重要）

- 计时只包住**算子本身**：解码在计时外，结果编码在计时外（但 `oursBytes` 是编码后大小）。
- **空间两项**：`分配` 是 `GC.GetTotalAllocatedBytes` 在计时区间内的进程级差值（含线程池线程，两侧同法）；
  `工作集` 是跑完后的驻留快照（`Environment.WorkingSet`），是footprint 而不是峰值曲线。
- 2 次预热 + 7 次采样，报**最快一次**。用最快而不是中位数，是因为 band scratch 是 `[ThreadStatic]`，
  而一个 HTTP 请求落在哪个线程池线程不确定；取中位数会把冷线程的分配算进来（实测 sigma 3 会从 16 ms
  变成 47 ms）。
- **这是演示工具，不是测量仪器。** 权威数字看 `benchmarks/SimdImaging.Comparison` 的同进程 A/B，
  那里两个实现共享同一套热身与交错采样。

## 批量

图片卡片下半部分可以多选若干张图，用当前选中的**算子或配方**一次跑完，出汇总表（逐张耗时 + 总吞吐）。
并发度固定 4，**默认跳过编码**——这是量出来的，不是猜的。

1080p `detail` × 8 张，`gaussian` σ3，同一台机器：

| 并发 | 只跑算子 | 含编码出图 |
|---|---|---|
| 1 | 37.62 张/秒 | 4.56 张/秒 |
| 4 | **60.58** 张/秒 | 13.11 张/秒 |
| 8 | 62.82 张/秒 | 16.71 张/秒 |

两条结论：

1. **编码是瓶颈，不是算子。** 同一批活儿，加上"每张都编码出图"后吞吐掉 **8 倍**。批量场景通常只要数字，
   所以界面默认不出图；要看效果就单张跑。
2. **并发 4 就基本饱和**（60.6 对 8 并发的 62.8，只差 3.6%），再往上只是多占线程。每个 `Execute` 内部
   本来就有行带并行，所以并发不是越大越好。

配方也能批量跑：复古胶片 6 步，8 张 1080p 共 569 ms，**14.05 张/秒**（并发 4、不编码，单张约 260 ms）。

## 内存

`GET /api/memory` 给出进程当前持有的东西；`?compact=true` 会先 `Trim()` 池再强制压缩一次 LOH。
一次 4K 六步链的实测（同一进程）：

| 阶段 | WorkingSet | 托管堆 |
|---|---|---|
| 刚启动 | 50 MiB | 2 MiB |
| 连跑六轮 4K 链（未等待） | 712 MiB | 379 MiB |
| 静置 6 秒（空闲压缩后） | **211 MiB** | 147 MiB |
| `?compact=true` 手动压缩 | **149 MiB** | 89 MiB |

三条规则让这个数字可控：

1. **池不按 2 的幂取整**。4K 一帧 33 MiB，`ArrayPool.Shared` 会塞进 64 MiB 的桶，一帧当两帧用。
   `ArrayPixelPool` 对大于 4 MiB 的请求走 1 MiB 粒度、每尺寸最多留 2 块。
2. **结果缓存按字节封顶**，不按张数。48 张 4K PNG 是半个 GB，48 张 1080p JPEG 是 4 MiB。
3. **中间图缩到 1600 宽**再编码。它只用来回答"哪一步坏了"，不需要 4K。

`WorkingSet` 高于 `managedHeap` 的部分是已回收但未归还的 committed 页，不是泄漏——判断依据是强制 GC 后的
`liveAfterFullGcMiB`，它跨多轮运行保持稳定。

## 语义陷阱

`brightness` 故意设成**没有倍数**：本库是加性偏移（0.2 = +0.2×满量程），参照实现同名 API 是另一套语义
（对拍 PSNR 仅 4.23 dB），界面上会出现一个"看起来能比、其实不是一个东西"的倍数，所以只展示效果。
`hue` / `gamma` / `posterize` / `sobel` / `median` 同理：ImageSharp 4.1.3 里没有对应扩展方法或参数语义不同。
`sepia` 只提供全强度——两边都只调无参重载。

## 加一个算子

在 `Program.cs` 的 `Ops.Catalog` 加一行，并在 `ApplyOurs` / `ApplyTheirs` 各加一个分支。两边的实现
故意放在同一个文件里紧挨着，语义不一致会立刻看出来。
