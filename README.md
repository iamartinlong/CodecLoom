# CodecLoom

> **纯托管图片 I/O 工作台** · 仓库 [`iamartinlong/CodecLoom`](https://github.com/iamartinlong/CodecLoom) · 中文别称「解码织台」

基于 [Sdcb.SimdCodecs 2.0](https://www.nuget.org/packages/Sdcb.SimdCodecs) 的**纯托管图片 I/O 工作台**：一个零原生依赖的编解码核心，同时驱动 Avalonia 桌面客户端、命令行批量工具、ASP.NET Core API 与 Web 测试台。

名字取自 *codec* + *loom*（织机）：把 HEIC、TIFF、JPEG 2000、JBIG2、QOI、Netpbm 等不同"线"织成统一的 Bgra32 像素布，再交给显示、服务或 CV 管线。

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
| `SimdCodecs.Api` | Minimal API、无依赖 Web 测试台、生产化运行时 | Core + Processing |
| `SimdCodecsViewer` | Avalonia 三栏工作台 + CLI 批量 + 自检 / GUI 冒烟 | Core + Processing |
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

请求统一 `multipart/form-data`，文件字段 `file`（单张）或 `files`（批量）。响应头带 `X-Source-Format`、`X-Decoded-Width`、`X-Batch-Ok` 等统计字段，以及 `X-Request-Id`。

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

- 鉴权保护所有 `/api/*` 业务端点（`/api/health`、`/api/ready`、静态测试台除外）；支持 `X-Api-Key` 与 `Authorization: Bearer`。测试台有 API Key 输入框，Key 只存浏览器 `localStorage`。
- 限流是**按客户端 IP 的固定窗口**，超限返回 429 + `Retry-After`；使用框架内置 RateLimiter，无额外 NuGet 依赖。
- 结构化日志为整行 JSON（JsonConsole），字段含 `requestId / method / path / status / elapsedMs / traceParent`，可直接被采集器索引。
- 命令行同时接受习惯写法 `--ApiRuntime__Auth__Enabled=true`（程序会自动把 `__` 归一化成 `:`）。
- 指标为进程内计数（总请求 / 错误 / 429 / p95 / 按端点与状态码），重启清零；不引入 Prometheus 客户端包，避免破坏 AOT 与零依赖主张。

## 验证状态（2026-11-12）

| 验证 | 结果 |
| --- | --- |
| 解决方案 Release 编译（5 项目） | 0 警告 / 0 错误 |
| `dotnet test`（xUnit，真实 Kestrel） | **74 / 74** |
| 覆盖率（coverlet Cobertura） | line **71.15%** / branch **58.13%** |
| `--selftest` / `--smoke` / `--apitest` | **58 / 6 / 41** 全绿 |
| 发布产物 | 框架依赖单文件 exe（12 MB）、API publish、23 MB NativeAOT 原生 exe 均复验通过 |
| GitHub Actions 远端首跑 | **run #1 `43131782994` 整体 success**：Ubuntu/Windows 双矩阵回归 + NativeAOT + GHCR 镜像 4 个 job 全绿，`release` 因无 `v*` tag 按预期跳过，详见 `docs/04-工作日志.md` 第十二轮 |
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

- `test`：Ubuntu + Windows 双矩阵 —— 还原、Release 编译、xUnit 74 项 + 覆盖率、`--selftest`、`--smoke`（Linux 走 Xvfb）、`--apitest`、真实 3 页 TIFF 批量、发布产物可运行校验。
- `aot`：Windows runner 发布 NativeAOT 原生 exe 并跑自检与批量。

### CD（自动，需回归全绿）

| 触发 | 动作 | 产物 |
| --- | --- | --- |
| push 到 `main` / `master` | 构建并推送容器镜像到 GHCR | `ghcr.io/iamartinlong/codecloom:latest`、`:main`、`:sha-<短 SHA>` |
| push `v*` tag | 同上 + 创建 GitHub Release | `CodecLoom-win-x64-single.zip`、`CodecLoom-win-x64-portable.zip`、`CodecLoom-api.zip`（含自动生成的 release notes） |

```bash
# 发布一个版本
git tag v1.0.0 && git push origin v1.0.0

# 拉取镜像（GHCR 包已设为 Public，匿名可直接 pull）
docker run --rm -p 5080:8080 ghcr.io/iamartinlong/codecloom:latest
curl http://127.0.0.1:5080/api/ready
```

PR 与手动 `workflow_dispatch` 只跑 CI，不推镜像、不发 Release。

MSIX 用本地自签证书签名，**私钥不入库、CI 不产出 MSIX**；公开发行需正式代码签名证书（见 `docs/03-待办清单.md` X-6）。CI 分发的桌面产物是可直接解压运行的便携 zip。

## 能力边界与风险

- **AVIF / JPEG XL 只能识别不能解码**；HEIF/HEIC 只支持解码，不支持编码、alpha 与序列 —— 均为上游 SimdCodecs 2.0 限制，不自行修上游。
- **HEVC 专利**：MIT 授权不含专利许可，HEIC 商用前需法务评估。
- .NET 10 是硬门槛；`Stream` 会整读进内存，单数组 2 GB 上限；服务端已用 `ImageGuard` 挡住 32 MB / 80 MP / 帧数上限的图片炸弹。
- JBIG2 / JPEG 2000 的真实样本链路未跑（下载失败，未伪造结果）；水印是几何图案而非文字字形（避免引入字体引擎破坏零原生依赖）。
- 上游库年轻且单人维护：通过精确锁版本 `[2.0.0]` + `ICodecService` 抽象层缓解。

详细决策、阶段验收与每轮实跑证据见 [docs/README.md](docs/README.md)；**接手开发 / 运维 / 发版请先读 [docs/05-工作交接.md](docs/05-工作交接.md)**。候选功能「文档增强 + 文字识别」的可行性评估、串联实测与组合 NativeAOT 验证见 [docs/06-模块集成评估-SimdGcdrNet.md](docs/06-模块集成评估-SimdGcdrNet.md) 与 [docs/07-模块集成评估-SimdPaddleOCR.md](docs/07-模块集成评估-SimdPaddleOCR.md)（**均尚未集成进产品，建议合并为一个模块**）。

源码采用 [MIT 许可](LICENSE)；已引入的依赖 `Sdcb.SimdCodecs` 与 Avalonia 同为 MIT。HEVC / HEIF 的专利授权不在 MIT 范围内，商用前另行评估。若后续启用候选的文档智能模块：SimdGcdrNet 引擎为 Apache-2.0、模型 MIT（GCDRNet 权重），SimdPaddleOCR 核心与模型包均为 Apache-2.0（PP-OCRv6 权重来自 PaddleOCR）；OCR 的 NuGet 包不带 NOTICE 文件，需按 `docs/06` 第 5.3 节与 `docs/07` 第 6.2 节补齐第三方声明、归属与商标说明后再分发。
