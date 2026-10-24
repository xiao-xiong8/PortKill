# PortKill

用于 Windows 的端口占用排查与释放工具，基于 .NET 10、Avalonia 和 Native AOT。仓库及解决方案名为 PortKill，桌面程序名为 `PortKiller.exe`。

## 功能

- 实时查看端口与连接，支持连接释放、进程终止、进程树和 HTTP.sys 诊断。
- 独立显示应用名称，支持按应用名称、文件描述和产品名称搜索。
- 提供 HUD、托盘及浅色、深色和跟随系统主题；导航选中项文字和图标变蓝。
- 自动更新支持签名验证、SSE 实时通知与轮询、更新说明、下载进度、暂停续传、重启确认及失败恢复。

## 开发环境

- Windows x64、PowerShell 7。
- .NET SDK 10.0.400，版本约束见 [global.json](global.json)。
- Avalonia 12.1.0、CommunityToolkit.Mvvm 8.4.2，由 NuGet 还原。
- 发布 Native AOT 需要 Visual Studio 的“使用 C++ 的桌面开发”工具链及 Windows SDK。

从仓库根目录执行：

```powershell
# 构建解决方案
pwsh -File scripts/build/Build.ps1

# 构建并执行业务、架构和更新回归
pwsh -File scripts/build/Build.ps1 -RunRegression

# 包含窗口与深浅主题回归，需要可交互的 Windows 桌面
pwsh -File scripts/build/Build.ps1 -IncludeUi

# 开发运行
dotnet run --project src/PortKiller.Desktop/PortKiller.Desktop.csproj

# 正式 AOT 发布，默认输出 .local/artifacts/publish/win-x64
pwsh -File scripts/build/Publish-Aot.ps1
```

管理后台与桌面分别构建和运行。后台版本、数据库和运行配置独立；生产接口默认要求认证，桌面端不使用管理账号。

```powershell
# 当前终端启用本地 SDK；可选择安装固定版本的前端工具
. ./scripts/dev/Initialize-Dev.ps1 -InstallWebTools
pwsh -File scripts/build/Build-Desktop.ps1
pwsh -File scripts/build/Build-Admin.ps1
pwsh -File scripts/build/Build-Web.ps1

# 在独立终端运行对应进程
pwsh -File scripts/dev/Start-Admin.ps1 -Service Api
pwsh -File scripts/dev/Start-Admin.ps1 -Service Worker
pwsh -File scripts/dev/Start-Admin.ps1 -Service Web
```

开发配置生成于 `.local/config/admin.local.json`，通过 `PORTKILL_ADMIN_CONFIG` 指定；可用 `Administration__DataRoot` 覆盖后台数据目录。默认开发数据位于用户本地数据目录的 `PortKill.UpdateAdmin.Development`，与桌面 `PortKiller` 设置隔离。API 默认仅监听 `127.0.0.1:5081`，前端开发入口为 `127.0.0.1:5173`。Cookie 始终使用 Secure；登录流程需要 HTTPS 管理入口。

实际接口契约位于 `deploy/contracts/admin-api.v1.json`。接口变更后运行 `scripts/checks/Test-AdminContract.ps1 -Update`，再在前端目录执行 `pnpm generate:api`；验证时使用不带 `-Update` 的契约检查及 `pnpm check:api`。

若需显式初始化 MSVC，向发布脚本传入 `-VcVarsAllPath C:/path/to/vcvarsall.bat`。回归项目是可执行程序，以上脚本会实际运行检查；仅执行 `dotnet test` 不能替代这些回归。

## 代码结构

| 路径 | 职责 |
| --- | --- |
| `PortKill.slnx` | 解决方案入口 |
| `src/PortKiller.Desktop/` | 桌面 UI、Shell、功能页面、配置与装配 |
| `src/PortKiller.Core/` | 共享模型、保护策略、日志与抽象 |
| `src/PortKiller.Infrastructure.Windows/` | Windows API、权限、进程元数据与系统偏好 |
| `src/Modules/PortKiller.Modules.Ports/` | 端口采集、查询、释放与进程操作 |
| `src/Modules/PortKiller.Updates/` | 客户端更新检测、下载、安装与恢复 |
| `src/Modules/PortKiller.UpdateProtocol/` | 客户端和服务端共享的签名与包验证协议 |
| `src/Tools/PortKiller.UpdateTool/` | 服务端发布验证、原子激活、恢复与公网检查 |
| `src/Services/PortKiller.UpdateNotifier/` | 只读实时更新通知服务 |
| `tests/` | 业务、UI、更新及 AOT 测试宿主 |
| `scripts/` | 构建、发布、字体资源与诊断脚本 |
| `deploy/` | 非敏感配置示例及 Linux、Nginx、systemd 部署文件 |
| `docs/` | 正式架构、更新操作说明与发布说明 |
| `.local/` | 仅本地使用的文档、缓存、构建产物、配置及临时文件，整目录忽略提交 |

业务模块不依赖桌面 UI；新工具通过静态注册装配，保持 Native AOT 兼容。详细约束见[架构说明](docs/ARCHITECTURE.md)。

本地工作资料按用途分类：

| 路径 | 内容 |
| --- | --- |
| `.local/docs/plans/` | 实施计划、功能设计与分析 |
| `.local/docs/reports/` | 回归记录、问题排查、迁移及历史调查 |
| `.local/docs/references/` | 字体、部署及其他参考资料 |
| `.local/cache/` | 下载和工具缓存，字体源文件位于 `font-sources/` |
| `.local/artifacts/` | 构建包、发布记录、测试输出及诊断采样 |
| `.local/config/` | 本地发布配置，例如 `publisher.local.json` |
| `.local/tmp/` | 临时文件和预览图片 |

项目内的 `bin/obj`、`.vs` 及前端 `node_modules` 保留开发工具默认位置，统一忽略提交。发布记录与本地配置应保留，需要清理缓存时按具体子目录处理。

## 自动更新

客户端仅从程序目录的 `update-settings.json` 读取更新地址和可信公钥。缺失或禁用时不会联网；配置格式见[示例](deploy/examples/update-settings.example.json)，使用前需替换地址和占位公钥。用户设置保存在 `%LocalAppData%/PortKiller/config.json`。

发布前编辑 [docs/RELEASE-NOTES.txt](docs/RELEASE-NOTES.txt)，每行一项。现有发布流程会将这些说明加入签名清单，客户端显示对应版本的实际内容。更新由用户确认下载和重启，更新包不会覆盖本机配置。

目前使用本地签名和 SSH 发布至 Linux，服务端由 Nginx 提供静态文件与只读 SSE 通知。部署配置、密钥准备、发布、恢复和续签步骤见[自动更新操作说明](docs/AUTO-UPDATE.md)。

完整本地更新验收入口：

```powershell
pwsh -File scripts/build/Test-UpdatePipeline.ps1 -VcVarsAllPath C:/path/to/vcvarsall.bat
```

该入口包含 UI、真实 AOT 安装与恢复、隔离发布演练、性能检查及 Linux 服务端构建。结果写入 `.local/artifacts/update-validation/`。真实 Linux、Nginx、TLS 和公网部署仍需单独验收；过程记录保存在本地，不随源码分发。

网页发布管理后台已提供独立的 Vue 前端、net10.0 API/Worker、SQLite + EF Core 10 认证权限审计基础，以及两文件流式上传 API。上传文件进入私有随机暂存区并按实际字节数计量；验证任务、完整发布页面和 Linux 部署仍在后续阶段实施。实施计划位于本地 `.local/docs/plans/UPDATE-ADMIN-IMPLEMENTATION-PLAN.md`。

```powershell
# 后台测试、实际 OpenAPI 比对、前端检查与构建
pwsh -File scripts/checks/Test-Admin.ps1

# 首次浏览器验证先在前端目录运行 pnpm exec playwright install chromium
pwsh -File scripts/checks/Test-Admin.ps1 -IncludeBrowser
```

浏览器默认使用 Playwright Chromium；若使用本机 Chrome，可在当前终端设置 `$env:PORTKILL_TEST_BROWSER_CHANNEL='chrome'`，测试仍使用独立临时浏览器配置。

首次启用管理认证时，先在受控终端执行数据库迁移，再初始化首位管理员。密码由终端隐藏输入或标准输入读取，不通过命令参数或环境变量提供；未完成 MFA 的登录只能进入认证流程。

管理接口使用持久可撤销会话，密码验证后必须完成验证器 MFA 和恢复码确认。Viewer 只读，Uploader 可上传和验证，Publisher 可发布，Administrator 管理全部权限；发布及账号管理要求五分钟内的强认证。修改密码、角色、账号状态或 MFA 会撤销受影响的会话。所有修改请求（含登录）先读取 `/api/v1/auth/csrf`，携带返回的 `requestToken` 到 `X-PortKill-CSRF` 请求头，身份变化后重新读取；Cookie 仅限 HTTPS。代理只信任本机回环地址的一跳转发头，认证入口限流且返回 JSON 错误。

```powershell
dotnet run --project src/Services/PortKiller.UpdateAdmin.Api -c Release -- db migrate
dotnet run --project src/Services/PortKiller.UpdateAdmin.Api -c Release -- admin init --user <管理员账号>
```

API 与 Worker 启动前都检查迁移版本，启动不会自动迁移。二者运行时持有共享维护锁；迁移、恢复、初始化和离线 MFA 重置要求排他锁，因此需先停止这两个服务。在线备份可以在服务运行期间执行：

```powershell
dotnet run --project src/Services/PortKiller.UpdateAdmin.Api -c Release -- db backup --output D:/private-backups/admin-20261024

# 停止 API 和 Worker 后恢复；已有数据库会保留为 admin.sqlite.pre-restore-<随机ID>
dotnet run --project src/Services/PortKiller.UpdateAdmin.Api -c Release -- db restore --input D:/private-backups/admin-20261024

# 所有 MFA 设备和恢复码均丢失时，由本机运维终端停机恢复指定账号
dotnet run --project src/Services/PortKiller.UpdateAdmin.Api -c Release -- admin reset-mfa --user <管理员账号>
```

上述命令可附加 `--data-root <绝对目录>`，应与服务使用同一配置。备份使用 SQLite Online Backup API，完成后才写入 `backup.json`；包含数据库、迁移版本、认证密钥和 SHA-256 清单。恢复校验复制后的数据库、外键、迁移版本和密钥摘要，保留现有密钥，撤销所有恢复出的会话，随后需重新登录并完成 MFA。备份目录包含认证材料，必须按凭据保管；Linux 新数据目录默认仅属主可访问，已有目录需去掉 other 权限，API/Worker 共享组和 umask 在部署阶段配置。仅使用支持文件锁的本地磁盘，维护时停止所有数据库写入者。此备份只覆盖管理数据库和认证密钥，正式包及公开清单的全量恢复与发布状态核对属于后续部署阶段。

专用 Linux 环境使用 `bash scripts/checks/test-admin-linux.sh` 执行真实服务端测试。Windows 上的测试和 linux-x64 构建不能代替该运行结果；实际 Windows AOT 包在 Linux 上的验包测试属于 P3。

## 提交与发布文件

Git 跟踪源码、测试、构建配置、脚本、必要运行资源、部署示例和少量正式文档；`.gitignore` 按明确范围收录文件。

- `.local/`、`bin/`、`obj/`、发布包、日志、数据库及其伴随文件、IDE 配置和个人资料不进入仓库。
- 真实更新配置、服务器配置、密码、令牌、SSH 凭据和签名私钥不进入仓库。
- 内嵌字体、图标及对应字体许可证是必要运行资源，需要随源码保留。
- 新增正式文档需在 `.gitignore` 中加入准确例外，不开放整个资料目录。
- 安装包应另行发布至受控更新服务器或 GitHub Releases，不提交到源码分支。

项目内嵌 Inter 和 Noto Sans SC 字体，字体授权见 [Inter OFL](src/PortKiller.Desktop/Assets/Fonts/Inter-OFL.txt) 和 [Noto Sans SC OFL](src/PortKiller.Desktop/Assets/Fonts/NotoSansSC-OFL.txt)。
