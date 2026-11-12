# CodecLoom 文档索引

本目录沉淀本项目的规划、实施、待办与工作日志。所有结论以仓库内代码和真机实跑结果为准，不以调研文章的宣传口径为准。

| 文档 | 职责 | 更新时机 |
| --- | --- | --- |
| [01-规划方案.md](01-规划方案.md) | 定位、目标、架构、技术选型、能力边界、风险登记 | 架构或选型变更时 |
| [02-实施计划.md](02-实施计划.md) | 阶段划分、交付物、验收标准、依赖关系 | 新增阶段或调整优先级时 |
| [03-待办清单.md](03-待办清单.md) | 可执行待办，含优先级、状态、验收标准 | 每轮推进前后 |
| [04-工作日志.md](04-工作日志.md) | 按轮次记录实跑数据、踩坑与决策 | 每完成一轮工作 |
| [05-工作交接.md](05-工作交接.md) | **接手人入口**：资产与权限、系统全景、运行、验证、CI/CD 发版、风险与排障 | 架构 / 远端 / 基线 / 权限变化时 |
| [06-模块集成评估-SimdGcdrNet.md](06-模块集成评估-SimdGcdrNet.md) | 候选模块评估：文档图像增强库的事实核对、JIT/AOT 实测、模块设计（已按实施轮修订为合并模块） | 评估结论或落地范围变化时 |
| [07-模块集成评估-SimdPaddleOCR.md](07-模块集成评估-SimdPaddleOCR.md) | 候选模块评估：文档 OCR 的事实核对、增强→OCR 串联实测、组合 AOT、**修订 06 的模块划分**（已实施） | 评估结论或落地范围变化时 |

另有本地智能体工作区记忆（`.workbuddy/memory/`，已被 `.gitignore` 排除，不入库）；`docs/04-工作日志.md` 是它的对外可读版本，两者事实应保持一致。**新接手的人请先读 `05-工作交接.md`。**

## 当前状态速览

- 解决方案：`SimdCodecsWorkbench.slnx`，**六个项目**，目标框架统一 `net10.0`：
  - `SimdCodecs.Core` — 编解码门面、`ICodecService` 抽象层、批量引擎、预设、上传闸门（零 UI / 零原生依赖）
  - `SimdCodecs.Processing` — 可选处理层，只依赖 BCL：缩放 / 滤镜 / 水印 / Gray8·BGR 布局 / Otsu·行投影
  - `SimdCodecs.Document` — **文档智能模块**（可选）：去阴影/透印增强（GcdrDocumentEnhancer）+ OCR（SimdOcrService，PP-OCRv6 tiny）+ 串联管线 + `DocumentLimits`/`DocumentGate`；opt-in，GPU 失败自动降级 CPU
  - `SimdCodecsViewer` — Avalonia 三栏工作台 + 命令行批量 + 文档 CLI（`--document`）+ 自检 / GUI 冒烟
  - `SimdCodecs.Api` — ASP.NET Core 图片处理 API + 无依赖 Web 测试台 + 生产化管线（`ApiRuntime.cs`）
  - `SimdCodecs.Tests` — 正式 xUnit 测试工程（Core / Guard / Batch / Processing / Document / API 集成，含 401/429/413/422）
- API 端点：`GET /api/health`、`/api/formats`、`/api/presets`、`/api/ready`（就绪探针）、`/api/metrics`（JSON + `?format=prometheus`）；`POST /api/inspect`、`/convert`、`/thumbnail`、`/batch`(zip)、`/frames`(zip)、`/process`、`/pixels`；`POST /api/enhance`、`/api/ocr`、`/api/document`（文档智能，`Document__Enabled=true` 启用；OCR 默认强制鉴权）。
- API 生产化（`ApiRuntime`，默认全关，不破坏本地开发）：可选 API Key（`X-Api-Key` / `Bearer`，固定时间比较）、按 IP 固定窗口限流（429 + Retry-After）、单行 JSON 请求日志、进程内指标。开启示例：`ApiRuntime__Auth__Enabled=true ApiRuntime__Auth__Key=<key>` 环境变量或 `--ApiRuntime:Auth:Key=<key>` 参数。
- 一键预设：`heic-jpeg`、`heic-png`、`scan-split`（多页扫描件按 `-pNNN` 拆页 + `_manifest.csv`）、`web-optimize`；桌面 / CLI / API / 测试台均可调用。
- 验证入口：`--selftest`（端到端 64 项，含文档智能 6 项）、`--smoke`（不弹窗 GUI 冒烟 7 项，含文档面板挂载）、`--batch`（命令行批量，第三个参数可为预设 ID）、`--document`（文档智能批量，`enhance/ocr/both` + `compare`）、`--apitest`（真实 Kestrel HTTP 集成 46 项）、`dotnet test`（xUnit 97 项，`--collect:"XPlat Code Coverage"` 产 Cobertura）。
- 运行中的本地服务：`SimdCodecs.Api/bin/Release/net10.0/SimdCodecs.Api.dll --urls http://127.0.0.1:5080`，测试台首页即 `http://127.0.0.1:5080/`。
- 测试资产：`testdata/` 存真实 HEIC（libheif / Nokia）与 3 页 TIFF（Pillow），不随发布产物分发；缺失时自检输出 `[SKIP]` 而非误判失败。
- 发布产物：
  - `SimdCodecsViewer-portable-win-x64.zip`（46 MB，win-x64 自包含便携版，解压即用）；
  - `SimdCodecsViewer.msix`（47 MB，已签名可安装；安装前需把 `SimdCodecsDev.pfx` 中的证书导入 `LocalMachine\TrustedPeople`）；
  - `SimdCodecsViewer/publish-single/SimdCodecsViewer.exe`（12 MB 框架依赖单文件）、`SimdCodecs.Api/publish/SimdCodecs.Api.exe`、`aot-probe/SimdCodecsViewer.exe`（23 MB NativeAOT 原生 exe，0 条 IL 裁剪警告）。
- 桌面设置持久化：窗口尺寸 / 输出目录 / 预设 / 处理参数存 `%APPDATA%/SimdCodecsViewer/settings.json`，损坏自动回默认；界面语言中/英可切换（重启生效）。
- 最新回归：**xUnit 97/97 + selftest 64/64 / GUI 冒烟 7/7 / API 集成 46/46 全绿**（含文档智能），Release 编译 0 警告 0 错误；组合 NativeAOT 55.3 MB 原生 exe 自检 64/64、`IL2026`/`IL3050`=0（详见 `04-工作日志.md` 第十六轮）。
- 远端 CI/CD 首跑：Actions run #1 `43131782994` **整体 success**（双矩阵回归 + AOT + GHCR 4 job 全绿），镜像 `ghcr.io/iamartinlong/codecloom:sha-35bee0a` 已公开；`release` 无 tag 跳过（详见 `04-工作日志.md` 第十二轮）。
- 工程化：`.github/workflows/ci.yml`（Windows/Ubuntu 矩阵 + Xvfb + GHCR 镜像 + tag Release）、`Dockerfile`（chiseled）+ `docker-compose.yml` + 根 `README.md` 已落地；**远端 CI 已用同一 Dockerfile 构建成功**；本机 `docker build/run` 实测仍待 Docker daemon 启动后补跑。
- 产品名 **CodecLoom**，远端仓库 [`iamartinlong/CodecLoom`](https://github.com/iamartinlong/CodecLoom)（默认分支 `main`，已首次推送）：对外标识已统一，项目名 / 命名空间 / 解决方案文件名保留技术名不变；推送与 CI/CD 步骤见根 `README.md`，首跑证据见 `04-工作日志.md` 第十二轮。

## 快速命令

```bash
dotnet build SimdCodecsWorkbench.slnx -c Release

# 验证
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --selftest
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --smoke
dotnet SimdCodecs.Api/bin/Release/net10.0/SimdCodecs.Api.dll --apitest
dotnet test SimdCodecs.Tests/SimdCodecs.Tests.csproj -c Release

# 命令行批量（支持预设）
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --batch <输入目录> <输出目录> WebP
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --batch <输入目录> <输出目录> scan-split
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --batch <输入目录> <输出目录> heic-jpeg

# 文档智能批量（增强/OCR/两者 + 原图对比，产 CSV manifest）
dotnet SimdCodecsViewer/bin/Release/net10.0/SimdCodecsViewer.dll --document <输入目录> <输出目录> both compare

# 启动
dotnet run --project SimdCodecs.Api -c Release -- --urls http://127.0.0.1:5080
dotnet run --project SimdCodecsViewer -c Release
```

## NativeAOT 复现（Windows）

```bash
export LIB='D:\Windows Kits\10\Lib\10.0.26100.0\um\x64;D:\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64'
export WindowsSdkDir='D:\Windows Kits\10\'
export WindowsSDKVersion='10.0.26100.0\'
dotnet publish SimdCodecsViewer/SimdCodecsViewer.csproj -c Release -r win-x64 \
  -p:PublishAot=true -p:StripSymbols=true -o aot-probe
```

> 重新发布前必须先停掉正在运行的 `SimdCodecs.Api.exe` / `SimdCodecsViewer.exe`，否则输出目录被锁（MSB4018 / MSB3027）。

## 下一步

P0–P2 与文档智能模块（E1–E3 / F1–F3）全部收口，剩余均为外部条件项：
- **文档智能模块已上线**：`SimdCodecs.Document` 六项目就绪，三个新端点 opt-in，桌面 / CLI / 测试台可调用；合规声明 `THIRD-PARTY-NOTICES.txt` + `LICENSES/Apache-2.0.txt` 随产物分发。推送后远端 CI 将跑含文档智能冒烟的新工作流。
- 本机 docker build / run 实测（待 Docker daemon 启动；远端 CI 已构建成功）。
- MSIX 安装验证（需管理员导入证书后 `Add-AppxPackage`）；便携 zip / MSIX 需按新基线重新打包（体积约 +22 MB）。
- 首个 `v*` tag 触发的 GitHub Release 产物链路待验证（run #1 无 tag，按预期跳过）。
- 上游限制（AVIF / JPEG XL 解码、HEVC 专利、.NET 10 门槛、`Stream` 整读内存、2 GB 上限、OCR small/medium 模型档位）不排期或待决策。详见 `03-待办清单.md`。
