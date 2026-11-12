# CodecLoom

> **纯托管图片 I/O + 文档智能工作台** · 仓库 [`iamartinlong/CodecLoom`](https://github.com/iamartinlong/CodecLoom) · 中文别称「解码织台」

基于 [Sdcb.SimdCodecs 2.0](https://www.nuget.org/packages/Sdcb.SimdCodecs) 的**纯托管图片工作台**：一个零原生依赖的编解码核心，同时驱动 Avalonia 桌面客户端、命令行批量工具、ASP.NET Core API 与 Web 测试台；再叠加纯 C# 的[文档照片增强](https://github.com/sdcb/SimdGcdrNet)与[文字识别](https://github.com/sdcb/SimdPaddleOCR)模块，把「解码 → 去阴影 / 透印 → 识别」串成一条链路。

名字取自 *codec* + *loom*（织机）：把 HEIC、TIFF、JPEG 2000、JBIG2、QOI、Netpbm 等不同"线"织成统一的 Bgra32 像素布，再交给显示、服务、CV 或文档智能管线。

目标不是再做一个看图软件，而是验证这套库能否撑起三种产品形态：**本地隐私工具、图片处理服务、OCR / CV 输入层**。所有结论以仓库内代码和真机实跑为准。

## 快速开始

环境：.NET 10 SDK（`dotnet --version` ≥ 10.0.100）。

```bash
# 1) 起服务 + Web 测试台（无前端依赖，单文件 HTML）
dotnet run --project SimdCodecs.Api -c Release -- --urls http://127.0.0.1:5080
# 浏览器打开 http://127.0.0.1:5080/

# 2) 桌面工作台（Avalonia，支持拖放 / 裁剪 / 帧浏览 / 批量预设）
dotnet run --project SimdCodecsViewer -c Release

# 3) 容器（需要运行中的 Docker daemon）
docker compose up --build -d
curl http://127.0.0.1:5080/api/ready

# 4) 文档智能：桌面点顶部「文档智能 ↓」开关，或走 API / CLI
dotnet run --project SimdCodecsViewer -c Release -- --document <输入目录> <输出目录> both compare
# API 侧默认 opt-in，启用需 Document__Enabled=true（见「生产化配置」）
```

一条命令跑完所有回归：

```bash
dotnet build SimdCodecsWorkbench.slnx -c Release
dotnet test SimdCodecs.Tests/SimdCodecs.Tests.csproj -c Release --no-build
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --selftest
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --smoke
dotnet SimdCodecs.Api/bin/Release/net10.0/SimdCodecs.Api.dll --apitest
```

## 项目结构

| 项目 | 职责 | 依赖 |
| --- | --- | --- |
| `SimdCodecs.Core` | 编解码门面、`ICodecService` 抽象、上传闸门、批量引擎、预设 | 仅 Sdcb.SimdCodecs（精确锁 `[2.0.0]`） |
| `SimdCodecs.Processing` | 缩放 / 滤镜 / 几何水印 / Gray8·BGR 布局 / Otsu·行投影 | 仅 BCL，Core 不引用它 |
| `SimdCodecs.Document` | 文档照片增强 + PP-OCRv6 识别、统一闸门 / 并发 / 管线 | 精确锁 GcdrNet `[1.0.0]`、PaddleOCR `[2.0.0]`、tiny 模型 `[1.0.0]`；Core / Processing 不改 |
| `SimdCodecs.Api` | Minimal API、无依赖 Web 测试台、生产化运行时 | Core + Processing + Document |
| `SimdCodecsViewer` | Avalonia 三栏工作台 + CLI 批量 + 文档智能 + 自检 / GUI 冒烟 | Core + Processing + Document |
| `SimdCodecs.Tests` | xUnit 单元 / 集成测试（真实 Kestrel） | 全部项目 |

`docs/` 保存规划方案、实施计划、待办清单与逐轮工作日志；`testdata/` 是真实 HEIC 与 3 页 TIFF 样本，不随发布产物分发。

## API 端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/` | Web 测试台 |
| GET | `/api/health` | 服务信息、能力、限额（免鉴权） |
| GET | `/api/ready` | 就绪探针：HEIF / PNG / 处理层自检（免鉴权） |
| GET | `/api/metrics` | 进程内指标 JSON；`?format=prometheus` 输出 Prometheus 文本 |
| GET | `/api/formats` | 输入格式可解码性 + 导出目标 |
| GET | `/api/presets` | 一键预设列表 |
| POST | `/api/inspect` | 只读文件头，返回尺寸 / 帧数 / EXIF·XMP·ICC 字节数 |
| POST | `/api/convert` | 单张转码，支持缩放 / 裁剪 / 指定帧 |
| POST | `/api/thumbnail` | 缩略图 |
| POST | `/api/batch` | 批量转 zip，支持 `preset=` 与 `_manifest.csv` |
| POST | `/api/frames` | 多帧拆成 zip |
| POST | `/api/process` | 任意尺寸缩放 + 亮度/对比度/灰度/反色 + 几何水印 |
| POST | `/api/pixels` | 直出 Gray8 / Bgr24 / Rgb24 / Bgra32 连续裸像素 + Otsu·行投影分析 |
| POST | `/api/enhance` | 文档照片去阴影 / 透印，返回同尺寸 PNG（alpha 保持） |
| POST | `/api/ocr` | 文字识别 JSON：正文、行框、置信度、文本哈希，可选 `ctc=true` / `enhance=true` |
| POST | `/api/document` | 增强 → 识别一步式，`compare=true` 附增强前后行数 / 字符数对比 |

请求统一 `multipart/form-data`，文件字段 `file`（单张）或 `files`（批量）。响应头带 `X-Source-Format`、`X-Decoded-Width`、`X-Batch-Ok` 等统计字段，以及 `X-Request-Id`；文档端点另有 `X-Document-Backend / -Enhance-Ms / -Ocr-Ms / -Lines / -Chars / -Text-Hash / -Degraded`，**只回统计、绝不把正文写进日志或指标**。

文档端点默认关闭（501），设 `Document__Enabled=true` 开启；只收单帧、≤ 4 MP、长边 ≤ 2400px，超限返回 413 且**不静默缩放**。服务端固定 CPU 后端、tiny 模型；容器镜像里同样固定 CPU。

一键预设：`heic-jpeg`、`heic-png`、`scan-split`（多页扫描件按 `-pNNN` 拆页 + CSV 清单）、`web-optimize`。

```bash
curl -F "files=@IMG_001.HEIC" -F "files=@IMG_002.HEIC" \
     -F "preset=heic-jpeg" http://127.0.0.1:5080/api/batch -o photos.zip
curl -F "file=@a.png" -F "layout=gray8" \
     http://127.0.0.1:5080/api/pixels -o a.gray8.raw
```

## 生产化配置

默认**全部关闭**，本地开发、测试台与既有回归不需要任何配置。生产环境用环境变量（推荐）或命令行开关：

| 配置 | 环境变量 | 命令行 | 默认 |
| --- | --- | --- | --- |
| 开启 API Key | `ApiRuntime__Auth__Enabled=true` | `--ApiRuntime:Auth:Enabled=true` | `false` |
| Key 值 | `ApiRuntime__Auth__Key=<secret>` | `--ApiRuntime:Auth:Key=<secret>` | 空 |
| 自定义 Key 头 | `ApiRuntime__Auth__HeaderName` | 同上（`:` 分层） | `X-Api-Key` |
| 开启限流 | `ApiRuntime__RateLimit__Enabled=true` | `--ApiRuntime:RateLimit:Enabled=true` | `false` |
| 窗口配额 / 秒数 | `...__PermitLimit=60` / `...__WindowSeconds=60` | 同上 | `60` / `60` |
| 结构化日志 | `ApiRuntime__StructuredLog=false` | 同上 | `true` |
| 启用文档智能 | `Document__Enabled=true` | `--Document:Enabled=true` | `false`（opt-in） |
| 后端 / 模型 | `Document__Backend=cpu` / `Document__Model=tiny` | 同上 | `cpu` / `tiny` |
| 像素 / 长边闸门 | `Document__MaxPixels` / `Document__MaxEdge` | 同上 | `4000000` / `2400` |
| 超时 / 并发 | `Document__TimeoutSeconds` / `Document__MaxConcurrency` | 同上 | `60` / `1` |

**隐私默认值**：OCR 输出即合同、证件、试卷正文。生产环境开启文档模块时**必须同时开启 `ApiRuntime__Auth`**；未开鉴权时启动会输出显著告警（可用 `Document__WarnWhenUnauthenticated=false` 关闭）。日志与指标只记行数、字符数、耗时和文本哈希。

- 鉴权保护所有 `/api/*` 业务端点（`/api/health`、`/api/ready`、静态测试台除外）；支持 `X-Api-Key` 与 `Authorization: Bearer`。测试台有 API Key 输入框，Key 只存浏览器 `localStorage`。
- 限流是**按客户端 IP 的固定窗口**，超限返回 429 + `Retry-After`；使用框架内置 RateLimiter，无额外 NuGet 依赖。
- 结构化日志为整行 JSON（JsonConsole），字段含 `requestId / method / path / status / elapsedMs / traceParent`，可直接被采集器索引。
- 命令行同时接受习惯写法 `--ApiRuntime__Auth__Enabled=true`（程序会自动把 `__` 归一化成 `:`）。
- 指标为进程内计数（总请求 / 错误 / 429 / p95 / 按端点与状态码），重启清零；不引入 Prometheus 客户端包，避免破坏 AOT 与零依赖主张。

## 验证状态（2026-11-12）

| 验证 | 结果 |
| --- | --- |
| 解决方案 Release 编译（6 项目） | 0 警告 / 0 错误 |
| `dotnet test`（xUnit，真实 Kestrel） | **97 / 97** |
| 覆盖率（coverlet Cobertura） | line **71.15%** / branch **58.13%**（文档模块另计，待 CI 首跑刷新） |
| `--selftest` / `--smoke` / `--apitest` | **64 / 7 / 46** 全绿（含文档智能：增强 / OCR / 闸门 / 确定性 / 413·422·503 语义） |
| 发布产物 | 框架依赖单文件 exe、API publish（含 `THIRD-PARTY-NOTICES.txt` 与 `LICENSES/Apache-2.0.txt`）、NativeAOT 原生 exe 均复验通过 |
| 文档智能 NativeAOT | 含增强 + OCR 的原生 exe（55.3 MB 单文件）`IL2026`/`IL3050` = **0**，自检 64/64 通过 |
| GitHub Actions 远端首跑 | run #1 `43131782994` 整体 success（文档智能上线前基线）；本轮回归已在本地按 workflow 步骤复现 |
| Docker 镜像 | CI 已用同一份 `Dockerfile` 构建并推送 `ghcr.io/iamartinlong/codecloom:latest` / `:main` / `:sha-35bee0a`（Public）；本机 `docker build` / `compose up` 仍待 daemon 启动 |

## 推送到 GitHub 与 CI/CD

远端仓库：`https://github.com/iamartinlong/CodecLoom.git`（默认分支 `main`，已首次推送）。工作流在 `.github/workflows/ci.yml`，push / PR 自动运行，**不需要配置任何 Secret**（用内置 `GITHUB_TOKEN`）。

```bash
# 已有仓库：直接克隆
git clone https://github.com/iamartinlong/CodecLoom.git
cd CodecLoom

# 若需从本地目录重新初始化（仅空仓库场景）
git init -b main
git add .
git commit -m "Initial commit: CodecLoom"
git remote add origin https://github.com/iamartinlong/CodecLoom.git
git push -u origin main
```

> `.gitignore` 已排除签名私钥 `*.pfx`、MSIX/zip 打包产物与 `msix-src/`（105 MB 自包含目录）。**首次提交前请确认 `git status` 里没有这些文件**；打包产物由 CI 在 Release 中生成，不要进源码仓库。

### CI（每次 push / PR）

- `test`：Ubuntu + Windows 双矩阵 —— 还原、Release 编译、xUnit 97 项 + 覆盖率、`--selftest`、`--smoke`（Linux 走 Xvfb）、`--apitest`（含文档端点 CPU 真实推理）、真实 3 页 TIFF 批量、文档智能 CLI（真实 HEIC 增强 + OCR + 对比）、发布产物可运行与**合规声明存在性**校验。
- `aot`：Windows runner 发布组合 NativeAOT 原生 exe，断言 `IL2026/IL3050 = 0`、自检里真的跑过「增强→OCR」、exe 体积 ≥ 35 MB（证明嵌入模型未被裁剪），再跑批量。

### CD（自动，需回归全绿）

| 触发 | 动作 | 产物 |
| --- | --- | --- |
| push 到 `main` / `master` | 构建并推送容器镜像到 GHCR | `ghcr.io/iamartinlong/codecloom:latest`、`:main`、`:sha-<短 SHA>` |
| push `v*` tag | 同上 + 创建 GitHub Release | `CodecLoom-win-x64-single.zip`、`CodecLoom-win-x64-portable.zip`、`CodecLoom-api.zip`、`CodecLoom-setup-<版本>.exe`（含自动生成的 release notes） |

```bash
# 发布一个版本
git tag v1.0.0 && git push origin v1.0.0

# 拉取镜像（GHCR 包已设为 Public，匿名可直接 pull）
docker run --rm -p 5080:8080 ghcr.io/iamartinlong/codecloom:latest
curl http://127.0.0.1:5080/api/ready
```

PR 与手动 `workflow_dispatch` 只跑 CI，不推镜像、不发 Release。

桌面分发两条路：**免安装**——解压 `CodecLoom-win-x64-portable.zip` 直接运行（无需 .NET 运行时）；**安装版**——`CodecLoom-setup-<版本>.exe` 由 [Inno Setup](https://jrsoftware.org/isinfo.php) 生成（脚本 `installer/CodecLoom.iss`），支持中文向导、用户级安装（无需管理员，装到 `%LOCALAPPDATA%\Programs\CodecLoom`）、开始菜单/可选桌面快捷方式与系统卸载。安装器无代码签名证书，SmartScreen 首次运行会提示「仍要运行」。

MSIX 包只在本地生成（自签证书，**私钥不入库、CI 不产出 MSIX**）；公开发行需正式代码签名证书（见 `docs/03-待办清单.md` X-6）。

## 能力边界与风险

- **AVIF / JPEG XL 只能识别不能解码**；HEIF/HEIC 只支持解码，不支持编码、alpha 与序列 —— 均为上游 SimdCodecs 2.0 限制，不自行修上游。
- **HEVC 专利**：MIT 授权不含专利许可，HEIC 商用前需法务评估。
- .NET 10 是硬门槛；`Stream` 会整读进内存，单数组 2 GB 上限；服务端已用 `ImageGuard` 挡住 32 MB / 80 MP / 帧数上限的图片炸弹。
- JBIG2 / JPEG 2000 的真实样本链路未跑（下载失败，未伪造结果）；水印是几何图案而非文字字形（避免引入字体引擎破坏零原生依赖）。
- 上游库年轻且单人维护：通过精确锁版本（`SimdCodecs [2.0.0]`、`GcdrNet [1.0.0]`、`PaddleOCR [2.0.0]`）+ `ICodecService` / `IDocumentEnhancer` / `IOcrService` 抽象层缓解。
- **文档智能**：模型是嵌入资源，所有产物约 +31 MB；CPU 推理不可被取消（超时只让调用方提前返回）；CTC 逐字符框是估算值；tiny 模型中文精度有限，不做「100% 识别准确」承诺；medium 模型不进默认产物。

详细决策、阶段验收与每轮实跑证据见 [docs/README.md](docs/README.md)；**接手开发 / 运维 / 发版请先读 [docs/05-工作交接.md](docs/05-工作交接.md)**。「文档增强 + 文字识别」的事实核对、串联实测与评估见 [docs/06-模块集成评估-SimdGcdrNet.md](docs/06-模块集成评估-SimdGcdrNet.md) 与 [docs/07-模块集成评估-SimdPaddleOCR.md](docs/07-模块集成评估-SimdPaddleOCR.md)；**两者已按推荐合并实施为 `SimdCodecs.Document` 模块**，落地记录见 `docs/04-工作日志.md` 第十六轮。

源码采用 [MIT 许可](LICENSE)；`Sdcb.SimdCodecs`、`Sdcb.SimdGcdrNet.Models`（GCDRNet 权重）与 Avalonia 同为 MIT；`Sdcb.SimdGcdrNet`、`Sdcb.SimdPaddleOCR` 及其模型包为 **Apache-2.0**（PP-OCRv6 权重归属 PaddlePaddle Authors）。完整归属、论文引用、商标声明与 Apache-2.0 全文分别见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 与 [LICENSES/Apache-2.0.txt](LICENSES/Apache-2.0.txt)，三者随每个二进制产物分发并由 CI 校验。HEVC / HEIF 的专利授权不在 MIT 范围内，商用前另行评估。
