# 06 · 模块集成评估：SimdGcdrNet（文档图像增强）

> 调研日期：2026-11-12 · 触发：用户给出微信文章 `https://mp.weixin.qq.com/s/n3v0YuSBvVKiTIHzyTY2IQ`
> 结论口径：所有数字以本机实测或包内元数据为准，文章中的性能数据一律标注为「上游数据」。
> 状态：**评估与 PoC 已完成，尚未改动产品代码**。落地范围需产品决策，见第 8 节。

---

## 1. 一句话结论

**能集成，而且契合度是本项目至今遇到的第三方库里最高的一档**：同一个作者（sdcb）、同一套技术底座（`net10.0` 纯托管）、同一个像素格式（`Bgra32`）、同一条 `SimdCodecs` 解码链路，上游甚至已经提供了 `SimdCodecs.AspNetCore` + `SkiaSharp.Avalonia` 两个示例，恰好对应 CodecLoom 的 API 端与桌面端。

推荐做成**独立可选模块 `SimdCodecs.Enhance`**（新项目 + 接口隔离），而不是塞进 `Core` 或 `Processing`；通过开关挂到 `/api/enhance`、桌面处理面板与 CLI 批量。PoC 已证明 JIT 与 NativeAOT 两条链路都能真实跑通。

**但在动代码之前必须先解决三件事**，否则会直接破坏现有基线：

| # | 硬约束 | 不处理的后果 |
| --- | --- | --- |
| 1 | 独立的像素 / 内存闸门 | 现有 `ImageLimits` 允许 80 MP，模型 CPU arena 约 96 B/px，理论峰值 7.7 GB → 服务 OOM |
| 2 | 并发闸门 + 超时 + 后端固定策略 | 本机 4 核 CPU 单张 1600×1152 约 1.4 s，冷启动另加 0.4～3.6 s；无闸门会拖垮 API 与 UI 线程 |
| 3 | Apache-2.0 + MIT 合规声明 | 引擎是 Apache-2.0（不是 MIT），模型要求保留 GCDRNet 版权并引用论文；漏声明属于分发合规问题 |

另外要有心理准备：**21 MB 嵌入模型会进入每一个交付物**（AOT exe、便携 zip、MSIX、Docker 镜像、Release 三份 zip）。

---

## 2. 这个库是什么

文章：《SimdGcdrNet：纯 C# 的文档照片去阴影，3080 Ti 上 9.6 ms 一张，浏览器里也能跑》，周杰DotNet / .NET骚操作，2026-10-15。

| 项 | 事实 | 来源 |
| --- | --- | --- |
| 仓库 | `https://github.com/sdcb/SimdGcdrNet` | 文章 / NuGet |
| NuGet | `Sdcb.SimdGcdrNet` **1.0.0**、`Sdcb.SimdGcdrNet.Models` **1.0.0** | NuGet flat container 索引：两个包**均只有 1.0.0** |
| 模型 | GCDRNet（GC-Net 估计光照图 → 除法校正 → DR-Net 细节恢复），IEEE TAI 2023 | 论文 / 包内 NOTICE |
| 解决什么 | 手机拍文档的**光照不均、阴影、背面透印**；作为 OCR 前处理（文章示例：OCR 从 56 行乱码降到 14 行正文） | 文章 |
| TFM | 仅 `net10.0`，无 `netstandard2.0` | nuspec |
| 依赖 | **零 NuGet 传递依赖**（`<group targetFramework="net10.0" />` 为空组），无 onnxruntime、无第三方推理框架 | nuspec / `project.assets.json` |
| 原生二进制 | 包内**只有两个托管 DLL**，无 `.so` / `.dylib` / native `.dll`；GPU 通过 P/Invoke 调系统 Vulkan / Metal | 实测 `find` 包目录 |
| 许可 | 引擎 **Apache-2.0**；模型包 **MIT**（随上游 GCDRNet 权重，Copyright (c) 2024 Jiaxin Zhang）；两包各带 `THIRD-PARTY-NOTICES.txt` | nuspec / NOTICE 全文 |
| 包体 | 引擎 DLL 1.32 MB；模型 DLL 20.96 MB（`GcNet` 5.68 MB + `DrNet` 15.27 MB，嵌入资源，零拷贝 `ReadOnlySpan<byte>`） | 实测 |
| 像素格式 | `Bgr24` / `Rgb24` / `Bgra32` / `Rgba32`，任意 stride，输出同格式、alpha 原样拷回、支持原地 | 反射实测 |
| 后端 | `Cpu`（默认）/ `Vulkan` / `Auto` / `Metal`；`GcdrEnhancer` 线程安全，内置按尺寸 plan 池与 session 池 | 反射 / 上游源码 |
| 已知限制 | 仅 `net10.0`；**不支持批量推理 N>1**；无 fp16/int8；浏览器无 GPU 且多线程 WASM 需 COOP/COEP；模型 PSNR 均值 24.65 dB、个别图仅约 11 dB | 文章 / README |

### 版本陷阱（必须写进锁版本策略）

NuGet 上只有 `1.0.0`（下载量约 135，发布约 1 个月），但 GitHub `main` 的 README 与源码**已经是 2.0 开发中**：推理引擎类型迁移到 `Sdcb.SimdOnnx.*`，并说明「2.0 起 `InferenceSession` / `CompiledModel` / `VkInferenceSession` 等不再位于 `Sdcb.SimdGcdrNet.*`，但 `GcdrEnhancer` 的 API 保持不变」。

因此集成必须沿用项目既有约定 —— **精确区间锁 `[1.0.0]`**，与 `Core` 锁 `Sdcb.SimdCodecs [2.0.0]` 的做法一致；升级前重跑全套回归，并用 `IDocumentEnhancer` 接口把上游类型挡在模块内部。这也和 `X-5`（上游年轻、单人维护）的缓解策略一致，属于新增的同类风险，不是新问题。

---

## 3. 本机实测证据

PoC 建在系统临时目录 `%TEMP%/codecloom-gcdr-probe`（**不在仓库内、不入库**），`dotnet new console --framework net10.0` + 两个包，Release 运行。

环境：Windows 10 x64 (19045) · .NET **10.0.11** · 4 逻辑核 · `AVX2=True` / `AVX512F=False`。

### 3.1 精确 API（反射自 1.0.0 程序集，非文章转述）

```csharp
public enum GcdrBackend { Cpu, Vulkan, Auto, Metal }
public enum GcdrPixelFormat { Bgr24, Rgb24, Bgra32, Rgba32 }

public sealed class GcdrEnhancer : IDisposable
{
    public static GcdrEnhancer FromFiles(string gcnetOnnxPath, string drnetOnnxPath, GcdrBackend backend = GcdrBackend.Cpu);
    public static GcdrEnhancer FromBytes(ReadOnlySpan<byte> gcnetOnnx, ReadOnlySpan<byte> drnetOnnx, GcdrBackend backend = GcdrBackend.Cpu);
    public static GcdrEnhancer FromStreams(Stream gcnetOnnx, Stream drnetOnnx, GcdrBackend backend = GcdrBackend.Cpu);

    // 关键：stride 重载，返回 void，写进调用方 dst；可直接吃 CodecLoom 的 Bgra32 缓冲
    public void Enhance(ReadOnlySpan<byte> src, int width, int height, int srcStride,
                        Span<byte> dst, int dstStride, GcdrPixelFormat format = GcdrPixelFormat.Bgr24);

    // 便捷重载：紧密排列，返回新数组
    public byte[] Enhance(byte[] src, int width, int height, GcdrPixelFormat format = GcdrPixelFormat.Bgr24);

    public (long pools, long workers, long evicted) PoolStats { get; }
    public void Dispose();
}
```

### 3.2 运行数据（合成文档图：白纸 + 文字 + 大面积渐变阴影，Bgra32）

| 场景 | 实测 | 说明 |
| --- | --- | --- |
| `FromBytes` 构造 | **49～56 ms**，峰值 WS 67 MB | 模型零拷贝，不解压、不写临时文件 |
| 640×480 首次 | **497～646 ms**，分配 86 MB | 含该尺寸的 plan 编译；峰值 WS 179 MB |
| 640×480 二次（热） | **181～274 ms**，分配 1.0 MB | 复用池，托管分配近乎归零 |
| 1600×1152 首次 | **1397～1432 ms**，峰值 WS **336 MB** | 新尺寸重新编译 plan；本机仅 4 核 |
| 320×240 紧密 BGR24 | 59～101 ms | 便捷重载，输出 230400 B 正确 |
| `Auto` 首次（本机选到 Vulkan） | **3635 ms**，峰值 WS 538 MB | 含 GPU 探测 / 设备初始化 / 管线编译 |
| `Vulkan` 二次 | 246 ms，峰值 WS 609 MB | 热路径，但显存/工作集明显高于 CPU |
| 结构不变量 | `alphaKept=True`、输出长度正确、像素确有改变（meanDelta 79/255） | 合成大阴影图，变化符合预期 |
| 非法输入 | 短缓冲抛 `ArgumentException: src holds 10 bytes...` | 库自身有参数校验，外层仍要闸门 |

对照上游数据（**非本机，仅供预期管理**）：RTX 3080 Ti Vulkan 端到端约 9.6 ms / 1600×1152；49 张 RealDAE 语料 Vulkan 均值 28 ms、CPU AVX2 722 ms（16 线程 5800X 单图推理约 365 ms）。本机 4 核无 AVX-512，慢属正常。

### 3.3 NativeAOT 实测（关键，决定能否保住现有交付链路）

独立最小探针 `aotcheck`：`<PublishAot>true</PublishAot>` + `<IlcInstructionSet>x86-64-v3</IlcInstructionSet>`，按 `04-工作日志.md` 第六轮注入 D 盘 Windows SDK 库路径后发布。

- 产物：**25.8 MB 单文件原生 PE，无托管 DLL 旁挂**。
- 编译告警：`IL2026` / `IL3050` **计数 0**。唯一 warning 是 `NU1903`（`Microsoft.DotNet.ILCompiler 10.0.11` 自身已知高危漏洞），来自 SDK 工具链，**不是新库引入**，但应作为现有 AOT 链路的独立待办记录。
- 运行：`FromBytes OK: 50 ms, models=21973142 bytes` → `AOT Enhance OK: 1228800 bytes in 295 ms, changed=True, alpha=255`，退出码 0。
- 构建期 `reg.exe` 被沙箱拦截是已知现象，不影响链接与产物。

**结论：21 MB 嵌入资源在 ILC 裁剪后仍完整可用，且不产生裁剪/AOT 警告。**

---

## 4. 为什么适合 CodecLoom

1. **同一生态**：CodecLoom 的 `Sdcb.SimdCodecs 2.0` 与 `Sdcb.SimdGcdrNet` 同为 sdcb 作品；上游仓库 `examples/SimdCodecs.AspNetCore` 就是「`ImageCodecs.Decode` 到 Rgba32 → 原地 Enhance → `POST /api/enhance`」，`examples/SkiaSharp.Avalonia` 就是 Avalonia 并排预览 + 后端切换。等于官方已经替我们验证了架构组合。
2. **像素链路零转换**：`CodecService.Load(...)` 产出连续 `Bgra32`（stride = w*4），正好是 `GcdrPixelFormat.Bgra32`；`Enhance(src, w, h, stride, dst, stride, Bgra32)` 输出仍是 `Bgra32`，可直接送 `WriteableBitmap(Bgra8888, Unpremul)` 或 `Codec.EncodeBytes`。不需要引入 ImageSharp / SkiaSharp / OpenCvSharp。
3. **不破坏「零原生依赖」主线**：包内无原生二进制；GPU 是运行时 P/Invoke 系统 API，容器里显式选 `Cpu` 即可完全不碰。
4. **补齐产品能力缺口**：现有处理层是纯几何/色调算子（缩放、亮度对比度、灰度、反色、水印、Otsu、行投影），对「手机拍文档」这个高频场景没有语义级增强；加上它之后，CodecLoom 从「图片 I/O 工作台」自然延伸到「扫描件 / 文档预处理」，且与已有的 `Gray8/Bgr24` OCR 输入层、`scan-split` 多页预设串成一条完整故事线。
5. **挂点现成**：`/api/process` 与 `/api/pixels` 的请求解析、`ImageGuard`、`DecoderGate`、响应头风格、`ProblemDetails`；`MainWindow.cs` 第 133 行起的处理卡片、`BatchCli`、`SelfTest` 第 286 行的处理层用例，都是可直接复用的模式。

### 定位边界

它是**单帧、单张、彩色文档的增强**，不是通用滤镜，也不替代现有 `ImageProcessor`：增强应作为处理管线的**可选第一步**（`enhance → resize → 色调 → 水印 → 布局`），且不能用于多帧 GIF/APNG 或多页 TIFF 的整体处理（需按帧逐个、显式选择）。

---

## 5. 三个必须先解决的工程约束

### 5.1 像素 / 内存闸门（最高优先级）

`SimdCodecs.Core/ImageGuard.cs` 的 `ImageLimits` 默认 `MaxBytes=32 MB`、`MaxPixels=80_000_000`、`MaxFrames=200`，`/api/process` 的 `EnsureProcessLimits` 也只卡这个上限。上游实测 5.1 MP 图 CPU arena 约 492 MB（96 B/px），`main` 分支源码里 `CpuArenaBudget = 4 GB`；按 80 MP 线性外推约 7.7 GB，**必然 OOM 或被容器 OOM killer 杀掉**。

建议新增 `EnhanceLimits`（独立于 `ImageLimits`，不要改动现有闸门语义）：

- `MaxPixels` 默认 **4 MP**（约 400 MB 峰值，本机 1.8 MP 实测 336 MB），可由配置上调到 8 MP 硬上限；
- 长边默认 ≤ 2400，宽高各 ≥ 32（内部会 pad 到 32 倍数）；
- **只接受单帧**：`FrameCount > 1` 返回 422，并提示用 `/api/frames` 先拆页；
- 超限返回 **413 + 明确原因**，**不静默降采样**（静默改尺寸会破坏「所见即所得」和后续 OCR 坐标）；若将来要做自动缩放，必须是显式 `downscale=true` 参数并在响应头标明实际尺寸；
- 桌面端同样走这套闸门，避免 UI 点一下大图把进程拖死。

### 5.2 并发、超时与后端策略

- `GcdrEnhancer` 线程安全且自带池 → **进程内单例 + `Lazy<T>` 懒构造**（首次点击 / 首次请求才付 50 ms + plan 编译）。
- 增强是**CPU 密集且不可被 `CancellationToken` 中断**（SIMD 内核内部不检查取消）。因此需要：独立的 `EnhanceGate`（建议 `SemaphoreSlim(1)` 或按内存预算设 1～2），**与现有 `DecoderGate` 分开**，避免一个慢请求同时占住解码与增强；请求级超时返回 503/429，并复用 `ApiRuntime` 的限流与 `/api/metrics` 埋点。
- 桌面必须在 `Task.Run` 中执行并禁用按钮 / 显示耗时，不能落在 UI 线程（本机 1.4 s 会明显卡窗）。
- **容器与 CI 一律显式 `GcdrBackend.Cpu`**：chiseled 镜像无 `libvulkan`，`Auto` 的探测既有冷启动代价（本机 3.6 s）也有平台差异；GPU 仅作为桌面可选项（`cpu` / `auto` 下拉），且 `Auto`/`Vulkan` 失败时回落 `Cpu` 并在响应头/状态栏标明降级。
- **AOT 指令集权衡**：上游要求 x64 可执行项目设 `<IlcInstructionSet>x86-64-v3</IlcInstructionSet>`，否则 `Avx2.IsSupported` 被 ILC 折叠为 false，CPU 静默降到 128 位 `Vector<float>`（慢约 1.3 倍）；但设置了之后，**AOT 产物将拒绝在不支持 AVX2 的老 CPU 上启动**。这直接影响 CodecLoom 现有 AOT 产物的兼容面，是第 8 节必须拍板的决策点。

### 5.3 交付物体积与许可证合规

体积（按 21 MB 嵌入资源估算，压缩后略小）：

| 产物 | 现状 | 集成后估计 |
| --- | --- | --- |
| NativeAOT `SimdCodecsViewer.exe` | 23 MB | 约 44–45 MB（PoC 最小程序 25.8 MB 含 21 MB 模型） |
| 便携 zip / MSIX | 46 / 47 MB | 约 67 / 68 MB |
| `publish-single` 框架依赖 exe | 12 MB | 约 33 MB |
| Docker chiseled 镜像 / Release 三份 zip | — | 各 +21 MB（未压缩） |

注意区分：**磁盘/下载体积必然增加，运行时内存增量有限** —— 模型是程序集嵌入资源，映射后零拷贝读取，实测构造前峰值 WS 仅 67 MB；真正的内存峰值来自推理 arena，由 5.1 的像素闸门控制。

合规动作（Apache-2.0 与 MIT 都要做，不能只写一句「用了某某库」）：

1. 新增根级 `THIRD-PARTY-NOTICES.txt`：收录 `Sdcb.SimdGcdrNet`（Apache-2.0，Copyright (c) sdcb）、`Sdcb.SimdGcdrNet.Models` 与 GCDRNet 权重（MIT，Copyright (c) 2024 Jiaxin Zhang），保留两包 NOTICE 的原文要点。
2. 新增 `LICENSES/Apache-2.0.txt` 全文；**CodecLoom 自身 `LICENSE` 保持 MIT 不变**，只是分发时附带第三方声明。
3. README 增加致谢与论文引用（模型包明确要求 cite）：Zhang et al., *Appearance Enhancement for Camera-captured Document Images in the Wild*, IEEE TAI, 2023。
4. 所有分发产物（便携 zip、MSIX、Release zip、Docker 镜像）都要带上 1～3 的文件；在 csproj / Dockerfile / workflow 打包步骤里补 copy 与校验断言。
5. Apache-2.0 的专利与免责条款需随 LICENSE 全文保留；若未来修改上游源码（当前不计划），还要标注变更。

---

## 6. 推荐模块设计

### 6.1 分层与依赖方向

```
SimdCodecs.Core          (不改)   ICodecService / CodecService / ImageGuard / BatchService
SimdCodecs.Processing    (不改)   纯 BCL 算子，保持「零第三方依赖」定位
        │                              │
        └──────────┬───────────────────┘
                   ▼
SimdCodecs.Enhance      (新增)   唯一引用两个 NuGet 包的地方
  ├ IDocumentEnhancer.cs        接口：Enhance(ReadOnlySpan<byte> bgra, int w, int h, EnhanceOptions) → EnhanceResult
  ├ GcdrDocumentEnhancer.cs     实现：Lazy 单例 / 后端选择与回落 / EnhanceGate / 诊断
  ├ EnhanceLimits.cs            像素·帧数·后端白名单闸门
  ├ EnhanceOptions.cs / EnhanceResult.cs   后端、耗时、是否降级、实际尺寸
  └ SimdCodecs.Enhance.csproj   [1.0.0] 精确锁，IsAotCompatible=true
                   ▼
SimdCodecs.Api / SimdCodecsViewer / SimdCodecs.Tests   (可选引用)
```

**为什么不放进 `Processing`**：该项目的契约是「只依赖 BCL、不引用任何 UI/Web/第三方库」，也是 WASM / AOT 轻量假设的载体；塞进 21 MB 模型与 Apache-2.0 依赖会直接改掉它的定位（`docs/03-待办清单.md` 的「选型变更」一节有过同样的推理）。**为什么不放进 `Core`**：Core 必须保持「文件字节 ↔ 像素」的单一职责，且不能让 API/桌面之外的消费者被迫携带模型。

### 6.2 挂载点（精确到现有文件）

| 层 | 改动 | 说明 |
| --- | --- | --- |
| API | `SimdCodecs.Api/ImageEndpoints.cs` 新增 `POST /api/enhance`；`Program.cs` 注册 `IDocumentEnhancer` | 复用 `ReadUploadAsync` / `ReadConversion` / `ImageGuard` / `Safe` / `Codec.EncodeBytes`；响应头 `X-Enhance-Backend` / `X-Enhance-Ms` / `X-Enhance-Degraded` / `X-Output-Width|Height`；错误码 413 超限、422 多帧、501 未启用、503 后端失败；端点列表与 `/api/health` 同步 |
| 配置 | `appsettings.json` 增加 `Enhance:Enabled` / `Backend` / `MaxPixels` / `TimeoutSeconds` | 沿用 `ApiRuntime__X__Y`（命令行需 `NormalizeRuntimeArg` 转 `:`）的既有约定 |
| 桌面 | `MainWindow.cs` 处理卡片（约 133 行、422 行、1033 行三处）、`Settings.cs`、`L10n.cs` | 增加「文档增强」勾选 + 后端下拉 + 耗时状态；异步执行、按钮禁用；设置持久化新增字段并兼容旧 JSON |
| CLI | `BatchCli.cs` 增加 `--enhance [cpu\|auto]` | 模型不支持 N>1，批量必须**串行**逐张；结果写入现有 manifest 风格 |
| 测试 | `SimdCodecs.Tests/EnhanceTests.cs` + `ApiIntegrationTests.cs` | 见 6.3 |
| 自检 | `SelfTest.cs` / `ApiSelfTest.cs` | 增加小图（如 320×240）CPU 冒烟，缺模型/未启用时输出 `[SKIP]` 而非失败（沿用 `testdata/` 约定） |
| CI | `.github/workflows/ci.yml` | `test` 双矩阵加显式 CPU 增强冒烟（设超时）；`aot` job 加增强探针并决定是否设 `IlcInstructionSet`；`release` 三份 zip 加 NOTICE 校验；镜像体积预算 +21 MB |

建议的呼叫链：`上传 → ImageGuard.Validate → EnhanceLimits.Validate(单帧/像素) → Codec.Load(Bgra32) → EnhanceGate.WaitAsync → IDocumentEnhancer.Enhance(原地/双缓冲) → 可选 ImageProcessor.Apply 后续算子 → Codec.EncodeBytes → 响应`。

### 6.3 测试策略（避免脆弱断言）

模型输出质量本身**不做像素级金标准**（上游自己声明个别图 PSNR 仅 11 dB，且 CPU / GPU 允许 ≤2 的 u8 差异）。断言不变量：

- 输出尺寸、stride、长度不变；`Bgra32` 的 alpha 逐字节保持；
- 同一输入 + 同一后端连续两次结果一致（确定性）；
- 增强确实改变了像素（合成阴影图的平均亮度/方差方向合理，用宽松阈值）；
- 闸门：超 `MaxPixels` 返回 413、多帧返回 422、未启用返回 501、`Auto` 不可用时降级且 `X-Enhance-Degraded=true`；
- 并发：同时 4 个请求不互相破坏（各自输出独立），且不超过 `EnhanceGate` 并发度；
- AOT 探针：原生 exe 内跑一次小图增强，退出码 0，`IL2026/IL3050=0`。

真实文档效果验证建议用一张固定 fixture（可用 RealDAE 语料，需单独确认再分发许可），只做人工对比截图，不进 CI 断言。

---

## 7. 风险登记

| 风险 | 等级 | 缓解 |
| --- | --- | --- |
| 80 MP 闸门直连导致 OOM | **高** | 独立 `EnhanceLimits`，默认 4 MP，拒绝而非隐式缩放（§5.1） |
| 长耗时请求拖垮 API / 卡 UI | **高** | `EnhanceGate` + 超时/限流 + `Task.Run` + 冷启动预热策略（§5.2） |
| Apache-2.0 声明遗漏 | **高** | NOTICE / LICENSE 全文 / README 引用 / 打包校验（§5.3） |
| AOT 设 `x86-64-v3` 提高 CPU 门槛 | 中 | 二选一并在文档写明：接受 AVX2 门槛，或不设并接受 CPU 降档约 1.3× |
| 上游 1.0.0 极新（下载约 135）、单人维护、main 已 2.0 重构 | 中 | 精确锁 `[1.0.0]` + `IDocumentEnhancer` 隔离 + 升级前全回归；与 X-5 同类 |
| Vulkan / Metal 平台差异、容器无 `libvulkan` | 中 | 容器与 CI 固定 `Cpu`；桌面 `Auto` 可回落并显式标记 |
| 交付物普遍 +21 MB | 中 | 一期接受；若体积敏感，二期改用 MSBuild `EnableDocumentEnhance` 条件引用（代价：两套构建/回归） |
| 模型能力边界（PSNR 均值 24.65 dB、个别 11 dB） | 低 | UI 文案明确「文档照片增强」，不承诺扫描仪效果；只做单帧、不做批量推理 |
| `NU1903`：`Microsoft.DotNet.ILCompiler 10.0.11` 已知高危漏洞 | 低（非本库引入） | 单独登记为 AOT 工具链待办，等 SDK 修复版本后重发产物 |

**明确不做（建议登记为不排期）**：浏览器 WASM 增强 —— CodecLoom 的 Web 测试台是无服务端头的静态页，多线程 WASM 需要 COOP/COEP，且要下载约 33 MB 运行时+模型；fp16/int8 量化、批量推理（N>1）上游尚不支持；HEVC 专利问题与本模块无关（见 X-3）。

---

## 8. 落地分期与待决策

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| **E0** | 调研、事实核对、JIT + AOT PoC、本评估文档 | **已完成（本轮）** |
| E1 | `SimdCodecs.Enhance` 模块 + `EnhanceLimits` + `POST /api/enhance` + `EnhanceTests` + 合规文件与打包校验 | 待决策 |
| E2 | 桌面按钮 / 后端选择 / 本地化 / 设置持久化 / CLI `--enhance` / 自检冒烟 | 待决策 |
| E3 | CI 双矩阵增强冒烟 + AOT 探针 + 镜像/Release 体积与 NOTICE 校验 + 文档（01/02/03/05/README）同步 | 待决策 |

需要产品拍板的四点：

1. **默认开关**：`Enhance:Enabled` 默认 `false`（opt-in，产物仍 +21 MB 但零运行时代价）还是 `true`（开箱可用，桌面首点付费约 0.5 s）。推荐一期 opt-in。
2. **AOT 指令集**：是否接受「仅支持 AVX2 CPU」以换取 CPU 后端满速；还是保持通用基线、接受约 1.3× 降档。
3. **GPU 暴露**：桌面是否给用户 `auto` 开关（体验更快但冷启动 3.6 s、显存更高），还是一期只给 `cpu`。
4. **体积**：接受全部产物 +21 MB，还是一期就做条件引用（默认产物不变，构建矩阵变复杂）。

---

## 9. 复现与参考

```bash
# JIT PoC（系统临时目录，不入库）
dotnet new console --framework net10.0 -o "$TEMP/codecloom-gcdr-probe"
dotnet add package Sdcb.SimdGcdrNet --version 1.0.0
dotnet add package Sdcb.SimdGcdrNet.Models --version 1.0.0
# Program.cs：FromBytes(GcdrModels.GcNet, GcdrModels.DrNet, GcdrBackend.Cpu)
#            Enhance(src, w, h, w*4, dst, w*4, GcdrPixelFormat.Bgra32)

# AOT 探针（Windows，按 docs/04-工作日志.md 第六轮注入 SDK 库路径）
export LIB='D:\Windows Kits\10\Lib\10.0.26100.0\um\x64;D:\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64'
export WindowsSdkDir='D:\Windows Kits\10\'
export WindowsSDKVersion='10.0.26100.0\'
dotnet publish -c Release -r win-x64 -o aot-out
```

参考链接：

- 调研文章：`https://mp.weixin.qq.com/s/n3v0YuSBvVKiTIHzyTY2IQ`
- 仓库与示例：`https://github.com/sdcb/SimdGcdrNet`
- NuGet：`https://www.nuget.org/packages/Sdcb.SimdGcdrNet/1.0.0`、`https://www.nuget.org/packages/Sdcb.SimdGcdrNet.Models/1.0.0`
- 上游模型与论文：`https://github.com/ZZZHANG-jx/GCDRNet`、IEEE TAI 2023
- 项目内对照：[01-规划方案.md](01-规划方案.md) 第 8 节回归口径、[03-待办清单.md](03-待办清单.md) P1-3 选型变更与 E 节、[04-工作日志.md](04-工作日志.md) 第六轮 AOT、[05-工作交接.md](05-工作交接.md) 验证体系与 CI/CD 章节
