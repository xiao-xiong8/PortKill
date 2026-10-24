# 自动更新实现与操作

2026-10-24。设计依据见 [架构规划](AUTO-UPDATE-ARCHITECTURE-PLAN.md)，实际本地测试及外部边界见 [验证记录](AUTO-UPDATE-VALIDATION.md)。新增能力需要部署通知服务并发布新客户端，代码完成不等于线上已经生效。

## 客户端流程

启动完成后延迟 10–15 秒检查，保留 300–330 秒轮询。第一次检查后建立同源 `/stable/events` SSE；事件仅是提示，重新拉取并验证清单后才接受版本。支持网络恢复、Windows 电源恢复消息、长时间挂起恢复及重新打开自动检查时补查。自动检查关闭会取消事件连接，手动检查继续可用。

`UpdateCoordinator` 后台合并检查意图，最多保留一次；推送增加 0–500ms 抖动，自动检查最小间隔 2 秒，手动冷却 30 秒，所有检查遵守服务器退避。检查、验签、缓存 hash、信任记录及阶段日志都在后台执行。下载通过独立可取消任务运行，暂停立即取消传输，保留 Range 半成品；暂停时不由通知恢复下载，用户继续后处理待检查版本。退出取消并观察协调任务，解除网络与窗口消息订阅。

发现只提醒，用户点击后才下载，再次确认后才重启。当前更新窗口和深浅主题保留。UI 快照与主窗口状态分别只排队一个最新回调，不随网络事件数量增长。更新包只替换四文件：`PortKiller.exe`、`av_libglesv2.dll`、`libHarfBuzzSharp.dll`、`libSkiaSharp.dll`，不覆盖本机更新配置和用户设置。

`PreparedUpdate` 将清单、包路径和版本身份一起发布。身份包含归一化版本、序号、payload 摘要和 ZIP 摘要。B 取代 A 时清除旧 Ready；304 不能恢复旧 A。同版本同 ZIP 更高序号续签会复用缓存并刷新身份。安装确认前取得租约，确认期间不能推进本机信任序号；取消或失败释放租约。安装器仍复检签名、有效期和 ZIP。

工作副本等待主进程退出，完整备份四文件，写持久日志后逐文件原子替换。新版最多 45 秒确认初始化成功，否则恢复旧版本；替换中断时下次启动由独立恢复副本还原。开发运行不替换文件，只支持 win-x64 Native AOT。

## 共享验证与配置

`PortKiller.UpdateProtocol` 供客户端、发布工具、通知服务共用。清单上限 64 KiB；RSA ≥ 2048，SHA-256；有效期最多 180 天，默认签名 90 天；包最多 256 MiB。schema 2 有序号、版本和有效期约束。schema 1 仅保留旧 HTTPS 客户端兼容；新服务端激活要求 schema 2。

只从运行目录 `update-settings.json` 读取地址和公钥；缺失或 Disabled 不联网。新增 `RealtimeEnabled=true` 和可选同源 `EventsUrl`，未填写时从清单目录派生 `events`。完整样例在 `deploy/examples/update-settings.example.json`，占位公钥必须换成已有可信公钥。清单及包禁止凭据、查询、片段和重定向，必须同源。HTTP 需要显式允许且每次重新验签，不使用 ETag；HTTPS 在清单有效期内可复用 ETag。

本地信任记录位于 `%LocalAppData%/PortKiller/update-trust.json`，拒绝较低版本/序号及同序号不同内容。ZIP 缓存和阶段日志在其 `updates` 子目录，阶段日志 `operations.jsonl` 约 1 MiB 轮转并只保留一个旧文件，不逐条记录进度。现有错误日志继续由宿主管理。没有默认上传诊断数据。

阶段日志 Phase 表示到达的新阶段，DurationMs 是距上一条阶段边界的耗时；按相邻记录关联上一个阶段，不作为新阶段已执行完成的耗时。缓存命中仍会在后台复核 ZIP 摘要，未采用仅凭时间戳跳过完整性验证的缓存。

HTTP 不提供传输加密或服务器身份，签名不能阻止持续断网；迁移 HTTPS、域名和公钥需兼容老客户端。私钥不进入客户端、服务器静态目录或 Git。安装目录本身的权限和管理员运行应在正式环境单独演练。

## 构建与服务端部署

```powershell
pwsh -File scripts/build/Publish-UpdateServer.ps1 -Runtime linux-x64
```

生成 `artifacts/update-server/linux-x64/tool` 和 `notifier`，均为自包含非 AOT 服务端程序，无需服务器安装 SDK。按服务器 CPU 选择 linux-arm64 或 linux-x64。

上传产物目录与含真实公钥的验证配置，在 Ubuntu 上以管理员执行：

```bash
sudo bash deploy/ubuntu/install-update-server.sh updatepublisher /path/to/server-bundle /path/to/verification.json
```

脚本安装 Nginx、只读低权限通知服务和目录权限。工具位于 `/opt/portkiller-updates/tool/PortKiller.UpdateTool`，配置 `/etc/portkiller-updates/verification.json`，静态根目录 `/srv/portkiller-updates/stable`。已有服务在显式安装新 bundle 时先停止，再启用新进程。真实公钥必须与已分发客户端一致。

通知服务仅监听 `127.0.0.1:8091`，由 Nginx 精确路由 `/stable/events` 代理，关闭缓冲、缓存和压缩，读超时 90 秒。心跳 25 秒，客户端闲置超时 75 秒，单行 4 KiB、单事件 8 KiB。客户端在 404/405/501、401/403 时约 30 分钟后再探测，继续轮询；其他断开指数退避并遵守 Retry-After。

服务端只读取原子激活的清单，FileSystemWatcher 加 1 秒文件身份检查兜底；内容未变不重复验签。每连接只保留最新一条提示，慢连接写超时 5 秒。重启从当前有效清单恢复，坏清单/序号倒退不广播。`/health` 仅 loopback 可读，区分运行状态、清单有效状态、序号、连接数和剩余有效天数。生产前检查发行版、CPU、端口、权限、Nginx worker_connections 和证书；本地 Kestrel 连接压测不等于 Nginx 生产容量。

## 发布、恢复与续签

新发布默认要求干净 Git 工作区，CI 使用明确版本。本地隔离演练可以显式 `-AllowDirty`，记录会保留 Dirty 状态。版本未指定时基于项目和服务端已激活版本递增；不修改项目源文件版本。

```powershell
pwsh -File scripts/release/Publish-Update.ps1 -Version 0.2.0 `
  -SshUser updatepublisher -ServerIp updates.example.com `
  -PublicBaseUrl https://updates.example.com/stable/ `
  -SigningKeyPath C:/private/update-private.pem -PublicKeyPath C:/private/update-public.pem `
  -ReleaseNotesPath C:/release/notes.txt -VcVarsAllPath C:/path/to/vcvarsall.bat
```

SSH 主机和公网源独立配置。旧 ServerIp/HttpPort 兼容模式继续生成显式 HTTP 配置。不要将示例域名直接用于发布。构建前校验公私钥配对。每次发布独立 ReleaseId 目录，包含原始签名清单、四文件更新 ZIP、五文件首装 ZIP（含 update-settings）、发布记录和回执；记录 Git 提交、脏状态、公钥指纹、版本、序号和摘要。预构建 CI 产物可用 `-PreparedPublishDirectory`，仍复核 EXE 版本、四文件结构、长度和摘要。

本地排他锁保护发布和序号。服务端工具持排他锁，校验预期当前 manifest 摘要（CAS），拒绝序号/版本倒退及同版本不同 ZIP；已存在同摘要版本包可复用，不同摘要拒绝覆盖。包先公开，清单原子切换是提交点。事务和回执在静态目录之外；提交后丢失回执可从签名清单重建。

```powershell
pwsh -File scripts/release/Publish-Update.ps1 -Resume <ReleaseId> -SshUser updatepublisher -ServerIp updates.example.com
```

恢复同一 ReleaseId 不重新编译、分配序号或签名，先核对保留的两个摘要。公网检测失败时保留 ManifestActivated 状态；恢复重新核对激活结果和公网，不换回旧清单。全量公网检测包括清单签名与期望摘要、整包 hash/长度/EXE 版本及 Range 的首字节和范围。过期产物不能直接恢复，应续签或创建新发布。

同版本续签通过 `-Version <原版本> -ResignRelease <原ReleaseId>`，其他地址、密钥等参数与新发布一致。复用原 ZIP，只更新序号和有效期并签名。`Renew-UpdateManifest.ps1` 也可单独生成原签名有效、更高序号的新清单。通知服务无需管理 HTTP API，激活始终通过 SSH 工具。

工具命令：`verify`、`inspect`、`activate`、`recover`、`public-check`，参数帮助运行不带参数查看。`public-check` JSON 输出 Warning（少于14天）与 Critical（少于7天），签名/网络/摘要错误返回非零；运维监控应按 Critical 字段升级处理。

## 自动化验收入口

```powershell
pwsh -File scripts/build/Test-UpdatePipeline.ps1 -VcVarsAllPath C:/path/to/vcvarsall.bat
```

一次执行 solution 构建、39 项原后端回归、完整深浅主题 UI、跨平台更新回归、真实两个 AOT 版本安装/回滚/恢复、临时密钥的 loopback 发布/恢复/续签、100 次实时发现测量、1000 连接阶梯及 linux-x64 服务端构建。日志和机器/SDK/提交记录保存在独立产物目录。Linux 流水线可执行 `-ServerOnly`；`-SkipPerformance` 只供快速开发，完整验收不使用。

这是可复用 CI 入口，未绑定外部 CI 平台，也未创建生产自动发布任务。上线顺序：先部署服务端工具和只读通知服务，再经旧轮询链发布新客户端，最后依据公网与普通/管理员安装演练结果启用运维监测。
