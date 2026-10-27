# PortKill

<div align="center">

**Windows 端口占用排查与释放工具**

轻量、快速、可回滚的端口诊断工具，使用 Avalonia + .NET 10 + Native AOT 构建。

[![最新版本](https://img.shields.io/github/v/release/xiao-xiong8/PortKill?display_name=tag&sort=semver&label=最新版本)](https://github.com/xiao-xiong8/PortKill/releases/latest)
[![下载量](https://img.shields.io/github/downloads/xiao-xiong8/PortKill/total?label=下载量)](https://github.com/xiao-xiong8/PortKill/releases)
[![更新渠道](https://img.shields.io/badge/更新渠道-签名校验-2563eb)](docs/AUTO-UPDATE.md)

[下载最新版本](https://github.com/xiao-xiong8/PortKill/releases/latest) · [自动更新说明](docs/AUTO-UPDATE.md) · [架构说明](docs/ARCHITECTURE.md) · [提交问题](https://github.com/xiao-xiong8/PortKill/issues)

</div>

## 项目简介

PortKill 面向 Windows 桌面用户和开发者，用于快速回答三个问题：哪个进程占用了端口、这个连接属于哪个应用、是否可以安全释放。它提供实时端口快照、进程与应用信息、连接释放、进程终止和 HTTP.sys 诊断，并在需要时保留进程树和系统保护提示。

桌面程序为 `PortKiller.exe`。正式发布使用 Windows x64 Native AOT，包含运行所需的原生依赖，解压后即可运行；项目不依赖系统中预装的 .NET 运行时。

## 主要功能

| 功能 | 说明 |
| --- | --- |
| 端口监控 | 实时查看 TCP/UDP 端口、连接状态、本地与远端地址、PID 和进程名 |
| 连接释放 | 对选中的连接执行释放并进行结果复检，避免只更新界面而未改变系统状态 |
| 进程诊断 | 查看进程树、文件描述、产品名称和应用名称，支持按应用或进程信息搜索 |
| HTTP.sys 诊断 | 识别 Windows HTTP 服务占用及相关监听信息 |
| 桌面体验 | 支持浅色、深色和跟随系统主题，提供托盘、HUD、快捷操作和多显示器 DPI 适配 |
| 安全更新 | 更新清单和压缩包使用 RSA 签名校验，支持实时通知、轮询、下载进度、暂停续传、失败恢复和重启确认 |
| 更新清理 | 更新成功后清理下载包、备份和工作副本；文件暂时被占用时会在下次启动继续处理 |

## 下载与安装

1. 打开 [Releases](https://github.com/xiao-xiong8/PortKill/releases/latest)，下载 `PortKiller-<版本>-win-x64.zip`。
2. 将 ZIP 解压到有写入权限的目录，例如 `C:\Tools\PortKill`。
3. 双击 `PortKiller.exe` 启动。

建议将程序放在独立目录中，不要直接覆盖正在运行的文件。首次启动后，应用会在用户配置目录保存设置；配置不会被更新包覆盖。

当前稳定版本：**0.1.3**。如果 GitHub 页面显示的最新版本与此处不同，以 Release 页面为准。

## 使用方式

### 查看端口占用

启动后进入端口页面，使用搜索框按端口、地址、PID、进程名或应用名称筛选。选中连接后可查看详情、复制信息或执行释放操作；高风险操作会显示确认提示。

### 处理更新

当检测到新版本时，主界面版本区域会显示更新提示，托盘菜单也可直接打开更新操作。点击更新徽章即可查看版本说明、下载进度和校验结果；下载完成后由用户确认重启，程序再完成替换和清理。

设置中的“应用更新”页面用于查看更新源、自动检查开关、当前状态和历史错误，适合需要手动检查或排查网络问题时使用。更新过程不需要管理后台账号，桌面端只读取经过签名验证的公开清单。

## 系统要求

- Windows 10/11 x64。
- 首次使用端口释放、进程终止或 HTTP.sys 诊断时，可能需要管理员权限；普通查看不要求管理员权限。
- 正式 ZIP 已包含 Native AOT 运行时文件，不要求安装 .NET Runtime。

## 从源码构建

开发环境建议使用 Windows、PowerShell 7 和 .NET SDK 10.0.400（版本约束见 [`global.json`](global.json)）。要构建 Native AOT，需要 Visual Studio 的“使用 C++ 的桌面开发”工作负载和 Windows SDK。

```powershell
# 构建解决方案
pwsh -File scripts/build/Build.ps1

# 构建并执行业务、架构和更新回归
pwsh -File scripts/build/Build.ps1 -RunRegression

# 运行桌面程序
dotnet run --project src/PortKiller.Desktop/PortKiller.Desktop.csproj

# 发布 Windows x64 Native AOT
pwsh -File scripts/build/Publish-Aot.ps1 `
  -VcVarsAllPath 'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvarsall.bat'
```

默认构建产物位于 `.local/artifacts/publish/win-x64`。`.local/`、`aotpush/`、`updatepush/`、`bin/` 和 `obj/` 均为本地产物，不应提交到 Git。

### 管理后台和前端

更新管理后台由独立的 API、Worker、Notifier、SQLite 和 Vue 前端组成。需要开发这些组件时：

```powershell
. ./scripts/dev/Initialize-Dev.ps1 -InstallWebTools
pwsh -File scripts/build/Build-Desktop.ps1
pwsh -File scripts/build/Build-Admin.ps1
pwsh -File scripts/build/Build-Web.ps1
```

后台配置、密钥和数据库只应放在本机或受控服务器。示例配置位于 [`deploy/examples/`](deploy/examples/)，生产部署流程见 [`docs/AUTO-UPDATE.md`](docs/AUTO-UPDATE.md)。

## 项目结构

| 路径 | 说明 |
| --- | --- |
| `src/PortKiller.Desktop/` | Avalonia 桌面界面、Shell、页面、配置和资源 |
| `src/PortKiller.Core/` | 共享模型、日志和保护策略 |
| `src/PortKiller.Infrastructure.Windows/` | Windows API、权限、进程元数据和系统偏好 |
| `src/Modules/PortKiller.Modules.Ports/` | 端口采集、查询、连接释放和进程操作 |
| `src/Modules/PortKiller.Updates/` | 更新检测、下载、安装、恢复和清理 |
| `src/Modules/PortKiller.UpdateProtocol/` | 客户端与服务端共用的签名、清单和包验证协议 |
| `src/Tools/PortKiller.UpdateTool/` | 服务端发布、原子激活、恢复和公网检查工具 |
| `src/Services/PortKiller.UpdateNotifier/` | 公开的只读实时更新通知服务 |
| `src/Web/PortKiller.UpdateAdmin.Web/` | 更新管理后台前端 |
| `src/Services/PortKiller.UpdateAdmin.Api/` | 更新管理 API |
| `src/Services/PortKiller.UpdatePublisher.Worker/` | 发布验证、激活和下载检查 Worker |
| `tests/` | 业务、UI、更新、发布和 Native AOT 回归测试 |
| `scripts/` | 构建、发布、检查和诊断脚本 |
| `deploy/` | Linux、Nginx、systemd、Docker 和配置示例 |
| `docs/` | 面向维护者的架构、更新和发布文档 |

## 自动更新设计

桌面端使用内置的稳定更新源和公钥。客户端先验证签名清单，再校验包大小、SHA-256、文件集合和 PE 版本；下载和安装不会覆盖用户配置。更新流程具备以下保护：

- SSE 实时通知与定时轮询互为兜底，后台检查失败不会阻塞主界面。
- 下载支持暂停、续传、超时和失败恢复。
- 安装前保留必要的回滚材料，启动健康检查确认成功后再清理。
- 旧版文本回执和新版结构化回执均可识别，历史安装器可以连续升级。
- 下载包、临时目录、备份和 Worker 副本在成功后清理；占用文件会延迟到下一次启动处理。

完整协议、密钥准备、服务器部署、恢复和验收步骤见 [`docs/AUTO-UPDATE.md`](docs/AUTO-UPDATE.md)。

## 发布与验证

正式发布前应在 Windows 上完成完整验收，不能只修改版本号。常用入口如下：

```powershell
# 完整桌面更新、真实 AOT 安装/恢复和历史版本兼容验收
pwsh -File scripts/build/Test-UpdatePipeline.ps1 `
  -VcVarsAllPath 'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvarsall.bat'

# 发布前检查源码范围、契约、部署模板和服务端包
pwsh -File scripts/checks/Test-ReleaseReadiness.ps1
```

制作签名 AOT 更新包时，签名私钥必须位于仓库之外的受控路径：

```powershell
pwsh -File scripts/release/Publish-Update.ps1 -PrepareOnly `
  -Version 0.1.3 `
  -PublicBaseUrl https://xiaoxiongupdate.duckdns.org/stable/ `
  -SigningKeyPath C:/private/update-private.pem `
  -PublicKeyPath C:/private/update-public.pem `
  -ReleaseNotesPath deploy/examples/release-notes.txt `
  -VcVarsAllPath 'C:/Program Files/Microsoft Visual Studio/2022/Professional/VC/Auxiliary/Build/vcvarsall.bat'
```

不要将签名私钥、服务器凭据、数据库、真实配置、发布 ZIP 或本地验证日志提交到 Git。GitHub Release 只发布经过校验的公开 ZIP 和对应说明。

## 文档

- [`docs/AUTO-UPDATE.md`](docs/AUTO-UPDATE.md)：更新协议、发布、恢复、服务器和验收流程。
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)：模块边界、数据流、UI 和 Native AOT 约束。
- [`docs/RELEASE-NOTES.txt`](docs/RELEASE-NOTES.txt)：当前发布说明源文件。
- [`deploy/`](deploy/)：Linux、Docker、Nginx、systemd 和公开配置示例。

## 隐私与安全

PortKill 的桌面端只为端口诊断和更新检查访问网络；更新检查读取公开清单，不上传端口列表、进程信息或用户配置。管理后台是独立部署组件，生产环境应使用 HTTPS、最小权限服务账号、MFA 和受控的签名密钥。

发现安全问题时，请不要在公开 Issue 中粘贴密钥、令牌、服务器地址或用户数据。请先通过 GitHub 私下联系维护者，并提供可复现步骤、受影响版本和必要的最小日志。

## 参与贡献

欢迎提交 Issue、改进文档和 Pull Request。提交前请：

1. 保持改动聚焦，并说明用户可观察到的行为变化。
2. 运行与改动相关的构建和回归脚本。
3. 不提交 `.local/`、发布包、私钥、数据库、日志和个人路径。
4. 涉及更新协议或安装器回执时，保留已有历史基线并补充兼容测试。

## 许可证

当前仓库未附带 `LICENSE` 文件，也未声明可自由再分发的开源许可证。代码的使用、分发和二次开发请先联系项目作者确认授权范围。
