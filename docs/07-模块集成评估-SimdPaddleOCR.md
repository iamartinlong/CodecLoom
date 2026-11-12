# 07 · 模块集成评估：SimdPaddleOCR（文档 OCR）

> 调研日期：2026-11-12 · 触发：用户给出微信文章 `https://mp.weixin.qq.com/s/ilyrDZitk44ES9y-ZKHx7g`，要求「再评估添加本功能」
> 口径：以 NuGet 2.0.0 实体包与本机实测为准，文章中的性能数据标注为「上游数据」。
> 状态：**评估与 PoC 已完成，仍未改动产品代码**。本文件修订 `06-模块集成评估-SimdGcdrNet.md` 的模块划分建议。

---

## 1. 一句话结论

**能加，而且不建议做成第二个独立模块。** 它和 06 评估的文档增强是同一作者、同一推理引擎、同一像素格式、同一后端策略、同一套闸门与合规问题；上游 `examples/PaddleOCR.Console` 本身就是「先 SimdGcdrNet 增强、再 SimdPaddleOCR 识别」。本机实测把这条链路完整跑通：**原图 OCR 55 行 / 808 字符（含背面透印乱码）→ 增强后 14 行 / 374 字符干净正文**，与文章宣称的 56→14 行一致。

因此建议把 06 的 `SimdCodecs.Enhance` **一次性升级为「文档智能」模块**（下称 `SimdCodecs.Document`），内含 `enhance` / `ocr` 两个 capability 与统一的 `DocumentLimits`、`DocumentGate`、后端选择、合规声明、AOT 配置。分两次做会重复造一遍闸门、限流、CI 与 NOTICE。

产品定位随之自然升级：CodecLoom 从「纯托管图片 I/O 工作台」延伸到「**扫描件 / 手机拍文档预处理 → 识别 → 结构化输出**」，与已有的 `scan-split`、`/api/pixels`、Gray8 / Otsu 行投影串成一条完整链路。

**新增代价**：tiny 模型全套餐约 **9 MB**（远比增强的 21 MB 轻），双模块合计约 31 MB 嵌入资源；组合 NativeAOT 实测单文件 **42.5 MB**，零裁剪警告。

---

## 2. 这个库是什么

文章：《SimdPaddleOCR 2.0：再快15倍！见证纯C#驱动的GPU性能核弹》，周杰DotNet / .NET骚操作，2026-10-09。

| 项 | 事实 | 来源 |
| --- | --- | --- |
| 仓库 | `https://github.com/sdcb/SimdPaddleOCR` | 文章 / NuGet |
| 核心包 | `Sdcb.SimdPaddleOCR` **2.0.0**（NuGet 已有 1.0.0→2.0.0 共 8 个版本，2.0.0 下载约 1778） | flat container 索引 / NuGet |
| 模型包 | **分档**：`Models.ChineseV6Tiny` / `Small` / `Medium` 均 **1.0.0**；另有 `ModelProvider 1.0.0`、`Models.TextLineOrientation 1.0.0`（CLS，被中文包传递引用）。**不存在** `Sdcb.SimdPaddleOCR.Models` 这个包 | flat container 索引 |
| 能力 | 纯 C# 的 PP-OCRv6：文本检测 DET → 行方向分类 CLS → 识别 REC；返回文本行、置信度、检测框；`returnCtcAlignment` 可得逐字符估算框（用于打码 / 按字覆盖） | README / 反射实测 |
| TFM | 核心 `net10.0;netstandard2.0`；模型包 `netstandard2.0`。**GPU 后端仅 `net10.0`** | nuspec / README |
| 依赖 | `net10.0` 组只依赖 `ModelProvider 1.0.0`；`netstandard2.0` 组另有 `Microsoft.Bcl.Numerics` 等 BCL 兼容包。**无 onnxruntime / Paddle Inference / OpenCV**，包内无原生二进制 | nuspec / 包文件清单 |
| 许可 | 核心与全部模型包 **Apache-2.0**；模型资产衍生自 PaddleOCR / PP-OCRv6，上游标记 Apache-2.0，`Copyright (c) 2016 PaddlePaddle Authors` | nuspec / `THIRD-PARTY-NOTICES.md` |
| 后端 | `OcrBackend { Auto, Cpu, Vulkan, Metal }`；DET / CLS / REC 三个阶段可分别指定；`Auto` 在无协作矩阵或实测跑不过 CPU 的设备上自动回退 CPU | 反射 / README |
| 像素 | `ImagePixelFormat { Bgr24, Rgb24, Bgra32, Rgba32 }`，`stride=0` 表示紧密排列；不负责解码、不强制引入任何图像库 | 反射 / README |
| 上游数据 | RTX 3080 Ti + Vulkan：medium 端到端 **36.4 ms**（2.0 CPU 523.2 ms，1.4.2 CPU 545.8 ms，约 15×）；WASM tiny 103 ms / 9.7 img/s；CPU 结果与 1.4.2 逐行相同 | 文章（**非本机**） |

### 两个必须写进实施手册的事实偏差

1. **文章里的 `Run` 写法在 2.0.0 编译不过**。反射得到的真实签名是：
   ```csharp
   PaddleOcrResult Run(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
                       int sourceStride = 0, ImagePixelFormat format = Bgr24);
   PaddleOcrResult Run(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
                       bool returnCtcAlignment, int sourceStride = 0, ImagePixelFormat format = Bgr24);
   ```
   `returnCtcAlignment` 是**第二个重载的第 4 个位置参数**，写成 `ocr.Run(bgr, w, h, returnCtcAlignment: true)` 会触发 `CS1744`。正确写法：`ocr.Run(px, w, h, true, stride, ImagePixelFormat.Bgra32)`。
2. **`PixelBuffer` 没有 `TryCopyTo`**（CodecLoom 的 `CodecService.cs` 里那个 `TryCopyTo` 属于多帧路径的 `IDecodedImage`）。单帧路径是 `buffer.Pixels.ToArray()` + `buffer.Stride`，正好就是 `LoadedImage.Pixels`。所以 OCR 可以直接吃 CodecLoom 现有解码结果，**零格式转换、零拷贝路径一致**。

### 版本与共享引擎

上游 README 说推理引擎已切到纯 C# 的 `Sdcb.SimdOnnx`（与 SimdGcdrNet 共用），但 NuGet 上**没有 `Sdcb.SimdOnnx` 这个包**：2.0.0 的核心 DLL 2.12 MB 内嵌了引擎。也就是说当前 `GcdrNet 1.0.0` 与 `PaddleOCR 2.0.0` **不存在共享包版本冲突**；但 GitHub `main` 已经在做引擎拆分，未来升级 GcdrNet 2.x / PaddleOCR 2.x 时必须统一 `Sdcb.SimdOnnx` 版本并全量回归。锁版本策略沿用 06：`[2.0.0]` / `[1.0.0]` 全精确区间。

---

## 3. 本机实测证据

PoC 建在 `%TEMP%/codecloom-ocr-probe`（**系统临时目录，不入库**），环境同 06：Windows 10 x64 / .NET 10.0.11 / 4 核 / AVX2。示例图取自上游仓库 `examples/sample.jpg`（500×500 中文商品标签）与 `examples/ocr-example.jpg`（1056×1586，背面透印严重的真题册页）。

### 3.1 包体与档位（决定产物体积的关键）

| 包 / 档位 | 解压 DLL | nupkg |
| --- | --- | --- |
| `Sdcb.SimdPaddleOCR 2.0.0`（net10.0 DLL） | 2.12 MB | 2.59 MB（含 netstandard2.0 DLL） |
| `ModelProvider 1.0.0` | 7.5 KB | 0.04 MB |
| **tiny 套餐**（ChineseV6Tiny 6.00 MB + TextLineOrientation 0.98 MB） | **6.98 MB** | 5.49 MB + CLS |
| small 模型包 | — | **24.91 MB** |
| medium 模型包 | — | **95.71 MB** |
| 对照：GcdrNet 引擎 + 模型 | 22.28 MB | — |

结论：**一期只发 tiny**（OCR 侧约 +9 MB）；small 作为可选档（+25 MB）；**medium 绝不进默认产物**（+96 MB）。若一定要支持大模型，走 `PaddleOcrAll.Load(detPath, clsPath, recPath, dictPath)` 的**外置模型文件**方案，用配置指向模型目录，产物零模型体积，代价是部署要额外分发模型并校验完整性。

### 3.2 真实识别（tiny，`OcrBackend.Cpu` 显式）

| 场景 | 实测 |
| --- | --- |
| `PaddleOcrAll.Load(ChineseV6TinyModels.Default, opts)` | **198 ms**，峰值 WS 47 MB |
| 500×500 中文标签 冷 / 热 | **563 ms / 206 ms**，16 行 / 220 字符，峰值 WS 92 MB |
| 识别内容抽样 | `纯臻营养护发素` / `产品信息/参数` / `【品牌】：代加工方式/OEM ODM` 等，中文与符号均正确 |
| `Run(..., true, ...)` CTC | 得到 **205 个逐字符框**，如 `'纯' @ (43,33)-(67,33)`，坐标格式 `X1..Y4` 四边形 |
| `OcrBackend.Auto` 冷启动（本机有 Vulkan） | 1282 ms，识别结果与 CPU 一致（16 行 / 220 字符），但比 CPU 冷启动更慢 |
| 结构化结果字段 | `PaddleOcrResult`: `Lines[] / DetectedCount / Text / PackedText / PackedTextHash`；`PaddleOcrLine`: `Box / Text / RecognitionScore / ClassificationScore / CtcSpans` 等 |
| 内置内存保护 | `PaddleOcrOptions` 含 `MaxCropPixels` / `LineWorkerCount` / `DetIntraOpThreads` / `RecBatchLines`，可直接复用为闸门参数 |

### 3.3 与 06 的增强模块串联（本轮最有价值的验证）

用 `ocr-example.jpg`，**同一个进程**同时加载 GcdrNet 与 PaddleOCR 模型，Bgra32 全程零转换：

```
CodecService 同款解码(1056×1586, Bgra32)
  → ocr.Run(原图)                     → 55 行 / 808 字符（大量背面透印乱码）
  → GcdrEnhancer.Enhance(Bgra32 原地) → 1780 ms
  → ocr.Run(增强图)                   → 14 行 / 374 字符（干净正文）   327 ms
  峰值工作集 419 MB（双模型同进程）
```

原图首行是 `MNoAToebTirts`（透印干扰），增强后首行是 `第三篇历年真题汇编`。这证明两件事：**两个模块的像素契约天然兼容**，以及**增强对 OCR 的价值在本机可复现**，不是文章里的营销数字。

### 3.4 组合 NativeAOT（增强 + OCR 双模块）

`<PublishAot>true</PublishAot>` + `<IlcInstructionSet>avx2</IlcInstructionSet>`（OCR README 推荐写法；06 里 GcdrNet 用的是 `x86-64-v3`，`avx2` 写法在本机两模块均生效），按 `04-工作日志.md` 第六轮注入 D 盘 Windows SDK 库路径：

- 产物 **42.5 MB 单文件原生 PE，无托管 DLL 旁挂**；`IL2026` / `IL3050` **计数 0**。
- 无需额外 `TrimmerRootAssembly` / `rd.xml`：直接引用模型包时，嵌入资源在 ILC 裁剪后完整保留（README 的「请保留核心程序集和所用模型程序集」警告在直接引用场景未触发；改用反射动态加载模型时才需要）。
- 运行：OCR 模型加载 67 ms → 增强模型加载 OK（21973142 B）→ 解码 500×500 → 增强 259 ms + OCR 160 ms → `lines=16 chars=220`，退出码 0。
- 唯一 warning 仍是 `NU1903`（`Microsoft.DotNet.ILCompiler 10.0.11` 自身），与 06 相同，来自 SDK 工具链；Git Bash 控制台把中文首行显示成乱码是终端编码问题，`lines/chars` 与 JIT 版一致，不是识别退化。

---

## 4. 与 CodecLoom 的契合度（比 06 更强）

1. **上游 README 直接推荐 `Sdcb.SimdCodecs`**：快速上手第一段就是 `ImageCodecs.Decode(data, PixelFormat.Bgr24, ...)` → `ocr.Run(image.Pixels, ...)`，并注明需要 `Sdcb.SimdCodecs 2.0+`。CodecLoom 正锁在 `[2.0.0]`。
2. **零转换**：`CodecService.Load` 的单帧路径就是 `buffer.Pixels.ToArray()` + `buffer.Stride` 得到连续 Bgra32；OCR 的 `ImagePixelFormat.Bgra32` 与 `sourceStride` 直接接住，增强模块的 `GcdrPixelFormat.Bgra32` 也是同一块内存。三者共用一块缓冲区即可完成 `decode → enhance → ocr`。
3. **官方端点同名**：上游 `examples/SimdCodecs.AspNetCore` 提供 `POST /api/ocr`（multipart `file` / `model`），与 CodecLoom 既有 `/api/process` / `/api/pixels` 风格几乎一致，可直接照搬请求解析与响应头约定。
4. **补齐闭环**：`/api/pixels` + Otsu / 行投影之前只能证明「字节能被 CV 算子读」，现在能给出真实文本与坐标；`scan-split` 多页 TIFF 拆页后可以逐页 OCR 输出文本清单。
5. **不破坏零原生依赖**：包内只有托管 DLL，GPU 是运行时 P/Invoke 系统 Vulkan / Metal；容器固定 `Cpu` 即可完全不碰。

---

## 5. 模块设计修订：从 `Enhance` 升级为 `Document`

> 这是对 06 第 6 节的**修订**，不是新增第二个项目。若尚未开始 06 的 E1，直接按本节实施，不要先做一个只有增强的模块再重构。

```
SimdCodecs.Core          (不改)   CodecService → 连续 Bgra32
SimdCodecs.Processing    (不改)   纯 BCL 算子
        │
        ▼
SimdCodecs.Document      (新增，替代 06 建议的 SimdCodecs.Enhance)
  ├ IDocumentEnhancer.cs          GcdrEnhancer 封装（Lazy 单例 / 后端回落 / 诊断）
  ├ IOcrService.cs                PaddleOcrAll 封装（模型档位 / CTC 开关 / 结果 DTO）
  ├ DocumentPipeline.cs           enhance → ocr 一步串联，输出像素 + 文本 + 耗时
  ├ DocumentLimits.cs             统一像素·帧数·文本行·超时闸门（替代 EnhanceLimits）
  ├ DocumentOptions.cs            后端、模型档位、enhance 开关、returnCtcAlignment
  ├ DocumentGate.cs               独立并发闸门（与 DecoderGate 分开）
  └ *.csproj                      [Sdcb.SimdGcdrNet 1.0.0] + [Models 1.0.0]
                                  + [Sdcb.SimdPaddleOCR 2.0.0] + [Models.ChineseV6Tiny 1.0.0]
        ▼
Api / Viewer / Tests / CLI（可选引用）
```

统一的理由（都是重复成本）：两个库同作者同引擎、同一 `Bgra32` 契约、同一 `Cpu/Vulkan/Metal` 后端选择、同一「冷启动慢且不可取消」的并发模型、同一 Apache-2.0 合规声明、同一 AOT `IlcInstructionSet`、同一套 CI 冒烟与产物校验。拆成两个项目会得到两套几乎一样的闸门和配置。

### 5.1 挂载点

| 层 | 改动 | 要点 |
| --- | --- | --- |
| API | `ImageEndpoints.cs` 新增 `POST /api/ocr`、`POST /api/document`；`Program.cs` 注册 `IOcrService` / `IDocumentEnhancer` | `ocr` 返回 JSON（`text` / `lines[]`（文本、置信度、检测框、可选逐字符框）/ `enhanced` / `backend` / `elapsedMs`）；`document` 是 `enhance → ocr` 一步式，可顺带返回增强图。沿用 `ReadUploadAsync` / `ImageGuard` / `Safe` / `ProblemDetails`；错误码 413 超限、422 多帧、501 未启用、503 后端失败 |
| 配置 | `appsettings.json` 加 `Document:Enabled` / `Backend` / `OcrModel` / `MaxPixels` / `TimeoutSeconds` | 沿用 `ApiRuntime__X__Y`（命令行 `NormalizeRuntimeArg` 转 `:`）约定；模型档位默认 `tiny` |
| 桌面 | `MainWindow.cs` 处理卡片、`Settings.cs`、`L10n.cs` | 增加「文档增强」「文字识别」两个开关 + 模型/后端下拉 + 结果文本可复制；全部 `Task.Run` 异步、禁用按钮、显示耗时 |
| CLI | `BatchCli.cs` 加 `--ocr` / `--document` | 逐张串行；输出 `.txt` / `.json` 清单，与 `scan-split` 的 `_manifest.csv` 风格一致 |
| 测试 | `SimdCodecs.Tests/DocumentTests.cs` + `ApiIntegrationTests.cs` | 见 5.2 |
| 自检 / CI | `SelfTest.cs` / `ApiSelfTest.cs` / `ci.yml` | 小图 CPU 冒烟（未启用输出 `[SKIP]`）；双矩阵加显式 `Cpu` 冒烟与超时；AOT job 加组合探针；Release/zip/MSIX/镜像加 NOTICE 校验与体积预算 |

### 5.2 测试策略

不做「必须识别出某句话」的脆弱断言（模型精度受图、档位、GPU fp16 抖动影响），断言结构不变量：输出行数/字符数为正、同一输入两次结果一致、`Bgra32` alpha 保持、`PackedTextHash` 稳定、CTC 框坐标有限且数量与字符数同量级、闸门 413/422/501/503 语义、增强前后像素确有变化；再用固定 fixture 做**宽松**的串联断言（如「透印样本增强后行数与字符数显著下降」，阈值留余量）。AOT 探针断言 `IL2026/IL3050=0` 且退出码 0。

---

## 6. 约束与风险

### 6.1 沿用 06 的三条硬约束（合并后统一实现）

- **像素/内存闸门**：`DocumentLimits` 默认 4 MP、长边 ≤ 2400、只收单帧、超限 413 不静默缩放。本机双模型 + 1.67 MP 图峰值 WS **419 MB**；OCR 侧另有 `PaddleOcrOptions.MaxCropPixels` 可二次兜底。80 MP 直连仍会 OOM。
- **并发/超时/后端**：`DocumentGate` 与 `DecoderGate` 分离；CPU 计算不可取消，需请求级超时与 429/503；容器/CI 固定 `OcrBackend.Cpu` + `GcdrBackend.Cpu`（本机 `Auto` 冷启动 1.28 s 反而慢于 CPU 0.56 s）；桌面 `Auto` 需可回落并标记降级。
- **AOT**：统一 `<IlcInstructionSet>avx2</IlcInstructionSet>`，接受「仅支持 AVX2 CPU」或改为不设并接受约 1.3× 降档；组合实测 42.5 MB、零裁剪警告。

### 6.2 本功能新增的四条

1. **模型档位与体积**：tiny 约 +9 MB、small +25 MB、medium +96 MB。一期只发 tiny；medium 不进产物。若要多档可选，用外置模型文件 + 配置目录，别把三个模型包全引用进来。
2. **合规声明（比 06 更容易漏）**：OCR 的 NuGet 包**不带** `THIRD-PARTY-NOTICES`（实测包内只有 `README.md`），必须自行从上游仓库 `THIRD-PARTY-NOTICES.md` 取回并随产物分发。清单需包含：核心与模型包 Apache-2.0 全文、PaddleOCR / PP-OCRv6 模型来源与 `Copyright (c) 2016 PaddlePaddle Authors`、`PaddleOCR` / `PP-OCR` 商标与非官方背书声明、GCDRNet 权重 MIT 版权与论文引用（见 06 §5.3）。**不要**照抄上游示例里的 ImageSharp（Six Labors Split License，非普通 MIT）；CodecLoom 用 `SimdCodecs` 解码，无此问题。
3. **隐私**：OCR 输出就是文档正文（合同、证件、试卷）。`/api/ocr` 默认应强制或强烈建议开启 `ApiRuntime:Auth`，结构化日志与 `/api/metrics` 只记字节数 / 耗时 / 行数，**绝不记文本或文件名原文**；测试台上传提示需说明数据不出本机。
4. **文章与包 API 偏差、精度边界**：CTC 参数须用第二重载的位置参数；GPU fp16 在低置信度下偶有结果翻转；逐字符框是 CTC 估算（适合打码，不适合精确排版还原）；中文 tiny 的 CER 参考上游约 2.30%（WASM 数据）。

### 6.3 风险登记

| 风险 | 等级 | 缓解 |
| --- | --- | --- |
| medium 模型 95.71 MB 撑爆产物 | 高 | 一期仅 tiny；多档走外置模型文件 |
| 文档正文泄露到日志 / 指标 | 高 | 默认开启鉴权、日志脱敏、只记统计 |
| 合规声明遗漏（包内无 NOTICE） | 高 | 从上游仓库取 NOTICE，纳入打包校验断言 |
| 双模块内存 / 冷启动叠加 | 中 | 统一 `DocumentLimits` + `DocumentGate` + `Lazy` 单例 + 预热策略 |
| 上游引擎拆分（GitHub main 已切 `Sdcb.SimdOnnx`） | 中 | 全精确锁版本；升级时统一引擎版本并跑全套回归（同 X-5） |
| `Auto` 在无 GPU / 核显环境行为差异 | 中 | 服务端固定 `Cpu`；桌面 `Auto` 可回落并显式标记 |
| 精度与 fp16 抖动 | 低 | 不做逐字金标准断言；文案不承诺 100% 准确 |
| AOT `IlcInstructionSet` 提高 CPU 门槛 | 中 | 与 06 同一个决策点，二选一写进交接文档 |

**明确不做**：medium 默认内置、WASM 浏览器 OCR（静态测试台无 COOP/COEP，且需下载运行时+模型）、可搜索 PDF / 版面还原（超出当前范围）、第三方 ONNX 模型直连（上游 GPU 融合算子要求规整图，易回退 CPU）。

---

## 7. 分期与待决策

06 的 E0 已完成；本轮新增 F0（已完成）。**E1 起按本文件第 5 节的合并模块实施**，不再单独做 `SimdCodecs.Enhance`。

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| E0 / F0 | 两个库的事实核对、JIT + 组合 NativeAOT PoC、06 / 07 评估文档 | **已完成** |
| F1 | `SimdCodecs.Document` 合并模块 + `DocumentLimits/Gate` + `POST /api/ocr` 与 `/api/document` + 测试 + 合规文件与打包校验 | 待决策 |
| F2 | 桌面双开关 / 结果文本 / 本地化 / 设置持久化 / CLI `--ocr` `--document` / 自检冒烟 | 待决策 |
| F3 | CI 双矩阵 CPU 冒烟与超时、AOT 组合探针、Release/zip/MSIX/镜像 NOTICE 与体积校验、01/02/03/05/README 同步 | 待决策 |

需要拍板（06 的四点仍然有效，新增后三点）：

1. **是否与增强合并**（推荐：合并为 `SimdCodecs.Document`；若坚持分开，须接受两套闸门/配置/CI）。
2. **模型档位**：tiny 默认 / small 可选 / medium 外置（推荐一期 tiny，medium 不进产物）。
3. **隐私默认值**：`/api/ocr` 是否强制 `Auth.Enabled=true`（推荐强制或启动时告警）。
4. 06 遗留：默认开关 opt-in、AOT 是否设 `avx2`、是否暴露 GPU、是否接受双模块合计约 31 MB 模型（AOT exe 约 42–52 MB）。

---

## 8. 复现与参考

```bash
# JIT PoC（系统临时目录，不入库）
dotnet new console --framework net10.0 -o "$TEMP/codecloom-ocr-probe"
dotnet add package Sdcb.SimdPaddleOCR --version 2.0.0
dotnet add package Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny --version 1.0.0
dotnet add package Sdcb.SimdCodecs --version 2.0.0
dotnet add package Sdcb.SimdGcdrNet --version 1.0.0
dotnet add package Sdcb.SimdGcdrNet.Models --version 1.0.0
# 示例图：上游仓库 examples/sample.jpg、SimdGcdrNet examples/ocr-example.jpg
# 关键调用：
#   using PixelBuffer b = ImageCodecs.Decode(data, PixelFormat.Bgra32, new DecodeOptions());
#   byte[] px = b.Pixels.ToArray();              // 与 CodecService.Load 完全一致
#   ocr.Run(px, b.Width, b.Height, true, b.Stride, ImagePixelFormat.Bgra32);  // CTC 位置参数
#   enh.Enhance(px, w, h, stride, dst, stride, GcdrPixelFormat.Bgra32);

# 组合 AOT（Windows，按 04-工作日志 第六轮注入 SDK 库路径）
export LIB='D:\Windows Kits\10\Lib\10.0.26100.0\um\x64;D:\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64'
export WindowsSdkDir='D:\Windows Kits\10\'
export WindowsSDKVersion='10.0.26100.0\'
dotnet publish -c Release -r win-x64 -o aot-out
```

参考：调研文章 `https://mp.weixin.qq.com/s/ilyrDZitk44ES9y-ZKHx7g` · 仓库 `https://github.com/sdcb/SimdPaddleOCR` · NuGet `Sdcb.SimdPaddleOCR/2.0.0` 与 `Models.ChineseV6Tiny/1.0.0` · 上游 `THIRD-PARTY-NOTICES.md` · 项目内 [06-模块集成评估-SimdGcdrNet.md](06-模块集成评估-SimdGcdrNet.md)、[03-待办清单.md](03-待办清单.md) E/F 节、[04-工作日志.md](04-工作日志.md) 第十四/十五轮。
