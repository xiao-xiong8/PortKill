# 自动更新实现与操作

2026-10-26。项目依赖与目录职责见 [架构说明](ARCHITECTURE.md)。桌面端默认接入真实更新站；新客户端需要安装包含线上配置的完整 AOT 包，服务端发布签名清单后才会出现可下载的新版本。内部实施计划及历史验证记录保存在本地 `.local/docs/`，不随源码分发。

## 客户端流程

启动完成后延迟 10–15 秒检查，保留 30 秒后台静默轮询。第一次检查后建立同源 `/stable/events` SSE；事件仅是提示，重新拉取并验证清单后才接受版本。支持网络恢复、Windows 电源恢复消息、长时间挂起恢复及重新打开自动检查时补查。自动检查关闭会取消事件连接，手动检查继续可用。

`UpdateCoordinator` 后台合并检查意图，最多保留一次；推送增加 0–500ms 抖动，自动检查最小间隔 2 秒。手动检查不设本地冷却，进行中合并重复点击，仍遵守服务器 Retry-After。检查时保留现有页面，独立显示至少 600ms 的检查动画；后台检查不发布临时 Checking 状态，重复结果不刷新整页，系统设置保留稳定的结果文字。检查、验签、缓存 hash、信任记录及阶段日志都在后台执行。下载通过独立可取消任务运行，暂停立即取消传输，保留 Range 半成品；暂停时不由通知恢复下载，用户继续后处理待检查版本。退出取消并观察协调任务，解除网络与窗口消息订阅。

发现只提醒，用户点击后才下载，再次确认后才重启。当前更新窗口和深浅主题保留。UI 快照与主窗口状态分别只排队一个最新回调，不随网络事件数量增长。更新包只替换四文件：`PortKiller.exe`、`av_libglesv2.dll`、`libHarfBuzzSharp.dll`、`libSkiaSharp.dll`，不覆盖本机更新配置和用户设置。

主界面版本号可打开更新窗口，右侧矢量徽章显示“新版本”、下载百分比、“继续下载”、“校验中”、“重启更新”或“更新重试”。点击“新版本”会打开同一更新窗口并立即下载；点击“重启更新”仍需明确确认。托盘菜单和更新 Toast 复用此入口。设置页保留自动检查偏好、手动检查和完整更新管理。发现新版本及下载完成各按目标版本在本次运行内提醒一次，Toast 消失后徽章仍在。

后台联网检查遇到临时网络或服务端故障时，保留已下载且清单仍有效的 Ready 目标及安装入口，并在更新窗口记录最近一次检查错误；发布撤回、版本变更、签名和有效期校验仍按原规则处理。

`PreparedUpdate` 将清单、包路径和版本身份一起发布。身份包含归一化版本、序号、payload 摘要和 ZIP 摘要。B 取代 A 时清除旧 Ready；304 不能恢复旧 A。同版本同 ZIP 更高序号续签会复用缓存并刷新身份。安装确认前取得租约，确认期间不能推进本机信任序号；取消或失败释放租约。安装器仍复检签名、有效期和 ZIP。

工作副本等待主进程退出，完整备份四文件，写持久日志后逐文件原子替换。新版最多 45 秒确认初始化成功，否则恢复旧版本；替换中断时下次启动由独立恢复副本还原。开发运行不替换文件，只支持 win-x64 Native AOT。

### 安装结果与清理

安装完成或恢复后在 `.portkiller-update/result.json` 原子写入结构化回执，关联事务、原版本、目标版本、结果、时间、包摘要及工作目录，同时保留兼容 `result.txt`。新版启动等待安装锁释放后读取；当前用户的已读标记位于更新缓存，避免每次启动重复提示。结构化回执不匹配当前版本时不会改读旧文本结果。安装器已经健康提交后的回执写入失败只记录日志，下次启动完成收尾，不终止健康的新程序。

清理必须在无 `transaction.json` 且取得安装锁后执行。删除本次结束的 worker/recovery、旧备份、四文件对应的替换临时文件和健康标记；成功安装额外删除当前用户缓存中与回执摘要一致的 ZIP、清单和半成品。失败/恢复保留缓存包以便重试，配置、公钥和防回放信任记录保留。文件占用或权限错误只记日志，下次启动重试；逐层跳过重解析点。没有匹配回执的 GUID 工作目录保留 7 天后再清理。

旧版安装器先于新程序运行，可能只有 `result.txt`，不能假定升级第一跳就能生成 `result.json`。读取端识别已知旧格式，成功版本必须与当前版本一致；明确的回滚/中断终态也可清理备份及回执写入前创建的 GUID 工作目录。未知文本、错配版本和损坏/不支持的结构化回执不授权旧格式清理。保留目录本身、锁和最新回执是正常行为，大体积备份、ZIP 和工作副本应在确认终态后清除。

工作目录中的签名清单经同源、公钥、版本验证后，可定位已安装或已被后续版本取代的缓存包；清理已完成更新时允许清单过期，不赋予过期清单新的安装权限。未来版本候选不删除。缓存或安装器副本被占用时保留工作清单，同一次启动异步重试 3 次（100/200/400 毫秒），仍被占用则下次启动继续，提示已读不会跳过清理；后续结构化回执覆盖旧文本也不影响对历史副本的清理。

新候选校验成功后保护当前包，额外保留最多一个 7 天内的旧 ZIP，清理多余/过期 ZIP、关联清单，以及超过 7 天的孤立清单和半成品。启动清理不对未知待更新包执行无保护的全缓存扫描。

下载前检查缓存和安装磁盘空间，同一磁盘合并预算。清单没有解压大小，下载前按允许的最大解压大小保守估算，并预留 64 MiB 安全空间；安装前按 ZIP 实际大小再次检查。空间不足在创建下载半成品或退出当前程序前报告所需/可用 MB，清理磁盘后可以重试。

## 共享验证与配置

`PortKiller.UpdateProtocol` 供客户端、发布工具、通知服务共用。清单上限 64 KiB；RSA ≥ 2048，SHA-256；有效期最多 180 天，默认签名 90 天；包最多 256 MiB。schema 2 有序号、版本和有效期约束。schema 1 仅保留旧 HTTPS 客户端兼容；新服务端激活要求 schema 2。

默认发布源定义在 `src/Modules/PortKiller.Updates/Configuration/production-feed.json`，以嵌入资源随更新模块分发，同时复制为桌面构建和 AOT 发布目录的 `update-settings.json`。默认启用 HTTPS 更新、SSE、30 秒轮询及直连；可信 RSA 公钥由既有部署公钥固定，不能从网络获取或替换。真实清单地址为 `https://xiaoxiongupdate.duckdns.org/stable/manifest.json`，公钥 SPKI SHA-256 为 `02066F8B6F5CFAFA51A4B34ECE9DDCEA19A10F014F6A0266B3210CB5CD104CE5`。AOT 脚本会复核产物中的默认配置与该资源一致。自动更新保留外部配置：原线上源的默认 300 秒配置在内存中迁为 30 秒，不写回文件，自定义更新源的间隔仍保留。

运行目录存在外部 `update-settings.json` 时优先使用它，以兼容自定义源与显式禁用；配置无效会报错，不静默改用其他来源。外部配置缺失时加载内置线上配置。`RealtimeEnabled=true` 和可选同源 `EventsUrl` 控制事件连接，未填写时从清单目录派生 `events`。完整自定义样例在 `deploy/examples/update-settings.example.json`，占位公钥必须换成已有可信公钥。清单及包禁止凭据、查询、片段和重定向，必须同源。HTTP 需要显式允许且每次重新验签，不使用 ETag；HTTPS 在清单有效期内可复用 ETag。

清单返回 404 时仍以独立的 `NoRelease` 状态保留空源身份，界面统一显示“当前版本为最新”，不记录为网络失败；保留实时监听和轮询。此时清理内存中的旧清单、ETag 及就绪版本，磁盘缓存和防回放信任记录保留；后续发布重新验签后才显示新版本与更新说明。真实公网检查可显式执行 `dotnet run --project tests/PortKiller.UpdateRegression -c Release -- --production-check <desktop-directory>`，只读取清单和 SSE 首包，不上传、下载包或安装。

自定义配置未填写 `UseSystemProxy` 时仍默认 `true`，兼容既有系统代理行为；本产品的线上默认配置明确设为 `false`，避免已确认的本机代理 TLS 中断。更新清单、下载和 SSE 共用所选连接池，不更改系统代理，也不在失败后自动绕过代理。两种方式均保留 TLS 证书验证和签名校验，各自复用连接池。

本地信任记录位于 `%LocalAppData%/PortKiller/update-trust.json`，拒绝较低版本/序号及同序号不同内容。ZIP 缓存和阶段日志在其 `updates` 子目录，阶段日志 `operations.jsonl` 约 1 MiB 轮转并只保留一个旧文件，不逐条记录进度。现有错误日志继续由宿主管理。没有默认上传诊断数据。

阶段日志 Phase 表示到达的新阶段，DurationMs 是距上一条阶段边界的耗时；按相邻记录关联上一个阶段，不作为新阶段已执行完成的耗时。缓存命中仍会在后台复核 ZIP 摘要，未采用仅凭时间戳跳过完整性验证的缓存。

HTTP 不提供传输加密或服务器身份，签名不能阻止持续断网；迁移 HTTPS、域名和公钥需兼容老客户端。私钥不进入客户端、服务器静态目录或 Git。安装目录本身的权限和管理员运行应在正式环境单独演练。

## 构建与服务端部署

```powershell
pwsh -File scripts/build/Publish-UpdateServer.ps1 -Runtime linux-x64
```

生成 `.local/artifacts/update-server/linux-x64/api`、`worker`、`notifier`、`tool` 和 `web`，均为自包含非 AOT 服务端程序，无需服务器安装 SDK。按服务器 CPU 选择 linux-arm64 或 linux-x64。

上传完整产物目录与含真实公钥的验证配置，在 Ubuntu 上以管理员执行：

```bash
sudo bash deploy/ubuntu/install-update-server.sh <existing-ssh-user> \
  /path/to/server-bundle /path/to/verification.json
```

脚本安装 Nginx、三个独立的 systemd 服务账号和受限服务单元。API 使用 `portkiller-update-api`，Worker 使用 `portkiller-update-worker`，通知服务使用 `portkiller-update-notifier`；账号均为 `nologin`，程序目录由 root 只读拥有。API 和 Worker 只通过 `portkiller-admin` 组共享 SQLite、WAL/SHM 和上传暂存目录，封存区由 Worker 独占；API 没有封存区或正式目录的写权限，Notifier 只有正式目录读权限。旧 SSH 发布用户仅加入 `portkiller-publish`，不获得管理数据库权限。

工具位于 `/opt/portkiller-updates/tool/PortKiller.UpdateTool`，服务端程序和前端位于 `/opt/portkiller-updates/{api,worker,notifier,web}`，验证配置位于 `/etc/portkiller-updates/verification.json`，SQLite、Data Protection 密钥和非公开封存位于 `/var/lib/portkiller-updates/admin`，静态根目录位于 `/srv/portkiller-updates/stable`。服务单元使用 `ProtectSystem=strict`、`NoNewPrivileges`、私有临时目录、固定回环 API 监听和受限读写路径。已有服务在显式安装新 bundle 时先停止，再启用新进程。真实公钥必须与已分发客户端一致。

本阶段安装的 Nginx 模板先保持旧客户端使用的静态下载和 `/stable/events` 路径；管理网页/API 的 HTTPS 代理和独立管理域名在 P7-03 配置，不能把当前 `8080` 下载入口当作管理登录入口。

P7-03 的 Nginx 模板位于 `deploy/nginx/portkiller-admin.conf.example` 和 `deploy/nginx/portkiller-download-tls.conf.example`。管理域名和更新域名分开填写，证书可用同一台服务器上的免费 Let's Encrypt 证书；模板不包含真实域名、证书或私钥。启用前替换 `server_name` 及证书路径，先执行 `nginx -t` 再 reload。管理站点将 `/api/v1/` 和管理 SSE 代理到回环 API，关闭请求/响应缓冲并允许 257 MiB 流式请求；API 路径没有 SPA 回退，Cookie 仍由 HTTPS 同源管理域名承载。下载站点只公开签名清单、版本 ZIP 和 `/stable/events`，保留 Range、不可变 ZIP 缓存及清单 `no-cache`，其他路径返回 404。

旧客户端的 HTTP 更新源不要通过 301/308 强制迁移；客户端明确拒绝重定向时，继续保留 `portkiller-updates.conf` 的旧静态入口，待客户端配置逐步切换到 TLS 下载域名后再评估下线旧入口。

管理 API 默认只信任回环反向代理。Docker 中 Caddy/Nginx 使用独立容器地址时，通过 `ReverseProxy__KnownProxies__0`（多个代理继续使用 `__1`、`__2`）指定可信代理的固定 IP，并通过 `AllowedHosts` 配置管理域名。代理需转发 `X-Forwarded-Proto` 和 `X-Forwarded-For`。API 保持一跳转发限制、HTTPS 安全 Cookie 和 CSRF 校验；容器地址需由独立代理网络固定，API 不发布宿主机端口。否则 HTTPS 在代理终止后，API 仍可能将请求视为 HTTP，导致 CSRF 令牌生成失败。未配置此项时，现有回环 Nginx/systemd 部署行为保持不变。

首次部署或迁移版本后，先停止 API 与 Worker，再运行维护迁移；服务不会在启动时自动迁移：

```bash
sudo systemctl stop portkiller-update-api.service portkiller-update-worker.service
sudo -u portkiller-update-api /bin/sh -c 'set -a; . /etc/portkiller-updates/api.env; set +a; exec /opt/portkiller-updates/api/PortKiller.UpdateAdmin.Api db migrate'
sudo systemctl start portkiller-update-api.service portkiller-update-worker.service
```

随后在受控终端初始化首位管理员，密码只通过交互输入：

```bash
sudo -u portkiller-update-api /bin/sh -c 'set -a; . /etc/portkiller-updates/api.env; set +a; exec /opt/portkiller-updates/api/PortKiller.UpdateAdmin.Api admin init --user <admin>'
```

P7-04 提供完整的停机协调入口。备份会停止 API、Worker、Notifier，使用 SQLite Online Backup API 保存数据库和认证密钥，再复制 `/var/lib/portkiller-updates/admin/{archive,staging}`、`/srv/portkiller-updates/{stable,.stable-update-state}` 和 `/etc/portkiller-updates`，最后写入覆盖全部文件的 `files.sha256`：

```bash
sudo bash deploy/ubuntu/backup-update-server.sh /private-backups/portkiller-update-20261025
```

升级入口会先创建上述完整备份，再安装新的服务端 bundle、以 API 服务账号执行迁移、启动服务并轮询回环健康检查；失败时保留备份，不自动回滚或删除旧目录：

```bash
sudo bash deploy/ubuntu/upgrade-update-server.sh <existing-ssh-user> \
  /path/to/server-bundle /path/to/verification.json \
  /private-backups/portkiller-update-20261025
```

恢复前必须停止 API、Worker 和 Notifier。恢复脚本先校验所有摘要，以停机状态恢复数据库、认证状态、封存/暂存、正式清单/包、UpdateTool 回执状态和配置；旧目录保留为 `.pre-restore-*`，恢复后由 Worker 账号执行 `inspect` 核对当前清单及包：

```bash
sudo bash deploy/ubuntu/restore-update-server.sh /private-backups/portkiller-update-20261025
sudo systemctl start portkiller-update-api.service portkiller-update-worker.service
```

服务程序本身不从备份覆盖；升级或恢复前必须先部署与数据库迁移版本匹配的已验证 bundle。恢复不会把旧版本清单写回正式目录，签名清单仍是提交事实；若清单/包与回执不一致，保持服务停机并按 `UpdateTool inspect/recover` 检查后再启动。备份目录包含认证材料和发布产物，必须存放在权限为 `0700` 的本地磁盘并另行复制到受控离线介质。

P7-05 增加一个不依赖第三方平台的本机监测 timer：`portkiller-update-monitor.timer` 每 5 分钟运行一次 `monitor-update-server.py`，通过回环 `/health/ready`、Notifier `/health` 和本机旧下载入口检查管理 API、SQLite 就绪状态、验证/发布队列、磁盘容量、正式清单有效期以及静态分发可用性。API、Worker、Notifier 和监测器输出 JSON 结构化日志到 journald；监测账号只读，认证密钥和上传内容不进入日志；结果以原子 JSON 快照保存在 `/run/portkiller-update-monitor/status.json`。可用 `journalctl -u portkiller-update-monitor.service` 查看告警，阈值由 `/etc/portkiller-updates/monitor.env` 调整。API 或 Worker 停止时，只要 Nginx 静态目录和 Notifier 仍健康，监测会标记 `distribution_only`，桌面端继续使用既有下载/通知链；静态清单或只读通知不可用则单独告警。备份和恢复会同时暂停 monitor timer，避免维护期间读取中间状态。

通知服务仅监听 `127.0.0.1:8091`，由 Nginx 精确路由 `/stable/events` 代理，关闭缓冲、缓存和压缩，读超时 90 秒。连接建立后立即发送并刷新 SSE 注释首包，未发布或无有效清单时也不等待第一条 25 秒心跳，以满足客户端 8 秒响应头超时。后续心跳 25 秒，客户端闲置超时 75 秒，单行 4 KiB、单事件 8 KiB；首包和后续帧均有 5 秒写超时。客户端在 404/405/501、401/403 时约 30 分钟后再探测，继续轮询；其他断开指数退避并遵守 Retry-After。首次尚未发布时清单地址可以返回 404，客户端按没有可用更新展示。

服务端只读取原子激活的清单，FileSystemWatcher 加 1 秒文件身份检查兜底；内容未变不重复验签。每连接只保留最新一条提示，慢连接写超时 5 秒。重启从当前有效清单恢复，坏清单/序号倒退不广播。`/health` 仅 loopback 可读，区分运行状态、清单有效状态、序号、连接数和剩余有效天数。生产前检查发行版、CPU、端口、权限、Nginx worker_connections 和证书；本地 Kestrel 连接压测不等于 Nginx 生产容量。

## Docker 通知服务覆盖更新

现有 Docker 部署可用 `scripts/release/New-DockerNotifierUpdate.ps1` 生成通知服务更新 ZIP。`-KitDirectory` 指向原完整部署包，`-ArchivePath` 指定 ZIP；加 `-ApplyToKit` 会同时将载荷覆盖到本机原部署包。脚本交叉编译自包含的 Linux x64 通知程序、更新组件摘要，并校验原 Compose、Dockerfile、Caddyfile、`.env` 和验签配置没有改变。ZIP 包含通知程序、对应变更源码、校验清单及固定更新脚本，不包含业务数据或部署凭据。

将 ZIP 上传到原服务器部署目录，执行 `unzip -o PortKill-Docker-Update.zip`，再执行 `sudo bash docker/update.sh`。此脚本沿用原 Compose 的镜像名与构建路径，只构建并重建 `notifier`，通过 Docker 内网验证即时 SSE 首包；未发布版本时也可验证。构建前先匹配原容器实际镜像，兼容 containerd 的镜像索引与平台清单 ID，并以固定临时标签保护旧镜像；不能把容器 `.Image` 字段直接当作可打标签的镜像 ID，也不能将已移动的 Compose 标签当作旧镜像。匹配或保护失败时在构建前停止。构建失败保留运行容器，启动或首包检查失败会尝试恢复更新前镜像。`sudo bash docker/update.sh --rollback` 可手动恢复固定标签 `portkill-notifier:previous`；恢复运行镜像不会回退磁盘中的更新文件。临时标签 `portkill-notifier:update-base` 在正常结束后移除，自动恢复失败时保留以供处理。更新不执行 `docker-setup.sh`，也不重建 Caddy、API、Worker 或变更数据卷。Docker 自身仍可能保留构建缓存和无标签镜像，脚本不自动清理。

旧镜像清单内容确实缺失时，`docker commit` 也可能失败。原容器文件系统仍可读取时，按 `deploy/docker/UPDATE.txt` 的恢复命令通过 `docker export/import` 创建快照；挂载的数据卷不进入快照。修订 3 可以识别 `portkill-notifier:update-base` 或 `portkill-notifier:latest` 的恢复镜像，要求 `io.portkill.recovery.container` 标签匹配原容器完整 ID、运行账号及工作目录一致，并以无网络、无业务挂载的临时容器确认旧程序可启动，才允许用该镜像备份和回滚。快照属于恢复材料，占用完整根文件系统空间，不自动创建任意容器快照。

部署要求 Linux x64、Docker Compose v2、Python 3、`sha256sum` 与 `flock`。`scripts/checks/Test-DockerNotifierUpdate.py` 使用模拟 Docker 验证更新与失败恢复分支，并通过真实本地 HTTP 验证首包探针；这不代替真实 Linux Docker 构建、容器启动和公网代理链路验收。

Docker 管理发布还需要私有状态挂载：`data/state:/srv/portkill/.stable-update-state` 只挂到 Worker，宿主目录由 `10001:10001` 拥有且权限为 `0700`。状态目录保存发布锁、事务和回执，不能与公开 `data/public` 共用。旧部署若 `inspect` 报该目录无访问权限，运行补丁包中的 `sudo bash docker/fix-publish-state.sh`；脚本会备份并幂等更新 Compose、创建权限正确的目录并只重建 Worker。原任务继续重试，不需要重复上传；若仍在 `operation_conflict` 和 `recovery_pending` 间循环，需要继续核对工具进程身份，目录检查成功不代表发布成功。

Linux Worker 与工具的父进程身份使用 `/proc/<pid>/stat` 的第 22 字段 `starttime`，同一 PID 命名空间内精确比较；Windows 继续比较 UTC 启动 ticks。Linux 的 `Process.StartTime` 依赖每个进程独立估算的系统启动时刻，不适合跨进程严格相等比较。Worker 和工具需要同步更新，保留父进程退出检测、执行期限和看门狗。Docker 补丁通过 `sudo bash docker/update-publisher.sh` 构建后，在断网临时容器中运行 `--self-check-tool-owner`，通过后仅切换 Worker；自检不访问正式发布目录或数据库。r2 自检在读取后台配置前执行，仅绑定工具配置，并在结果中输出 `SelfCheckSchema:2`，因此不依赖临时容器中的 Administration 数据目录；正式 Worker 仍加载并校验原后台配置。原发布任务自动恢复，不重新上传更新包；最终成功以任务激活及公网下载验证完成为准。

## 发布、恢复与续签

网站上传模式先在 Windows 本机制作产物，无需 SSH 凭据：

```powershell
pwsh -File scripts/release/Publish-Update.ps1 -PrepareOnly -Version 0.2.0 `
  -PublicBaseUrl https://updates.example.com/stable/ `
  -SigningKeyPath C:/private/update-private.pem -PublicKeyPath C:/private/update-public.pem `
  -ReleaseNotesPath C:/release/notes.txt -VcVarsAllPath C:/path/to/vcvarsall.bat
```

PrepareOnly 在独立 ReleaseId 目录生成已验签的 `manifest.json`、四文件更新 ZIP 和本地记录；将前两项上传管理网站。此入口在服务端查询、SSH/scp、激活和公网检查之前返回，不生成首装 ZIP，不读取 SSH 主机配置。公开 URL 必须明确提供；离线准备时服务器基线未知，由网站确认接口重新核对。`-PrepareOnly -Resume <ReleaseId>` 仅复核现有签名产物，不能把准备记录隐式转为 SSH 发布；准备模式与 SSH/LocalRoot 参数组合会被拒绝。

多台电脑分发同一更新地址和公钥的首装客户端，后续一次网站发布即可供各客户端自行比较版本、下载和确认安装；客户端无需管理账号或私钥。签名私钥由发布者保管，Docker 部署仅挂载 `verification.json` 公钥配置，不能从部署包恢复私钥。同机多个 Windows 用户建议各自使用独立安装目录；当前安装器不负责协调共享目录内多个账号的进程和写入权限。已有客户端使用旧版时，本次更新的界面行为要在下载并重启到新版后生效。

制作记录 schema 2 保存制作模式、Git 提交及脏状态、公钥指纹、签名清单的版本/序号/有效期、两个文件的大小和摘要，以及 ZIP 内实际 EXE 的文件版本和摘要。`NativeAot` 由 win-x64 PE 结构和 Native AOT 运行时导出检查得出，普通托管 apphost 即使删去配套 DLL 也会被拒绝。准备模式记录 `BaselineKnown=false`、`Expected=null`。私钥及其路径不写入制作记录；网站始终从签名清单和实际更新包取得权威信息，不接收本地记录代替验签。

发布脚本生成的客户端配置也明确使用直连，避免首装包再次落回系统代理。需要系统代理的自定义部署可传 `-UseSystemProxy`；恢复已有发布记录继续使用该记录保留的配置。

新发布默认要求干净 Git 工作区，CI 使用明确版本。本地隔离演练可以显式 `-AllowDirty`，记录会保留 Dirty 状态。版本未指定时基于项目和服务端已激活版本递增；不修改项目源文件版本。

本地发布记录、锁和签名产物保存在 `.local/artifacts/releases/`，恢复和续签继续从此目录读取，不应作为可随意清空的缓存。本地兼容配置读取 `.local/config/publisher.local.json`；非敏感配置示例位于 `deploy/examples/`。签名私钥仍使用原有受保护位置，不因项目目录整理迁入仓库。

```powershell
pwsh -File scripts/release/Publish-Update.ps1 -Version 0.2.0 `
  -SshUser updatepublisher -ServerIp updates.example.com `
  -PublicBaseUrl https://updates.example.com/stable/ `
  -SigningKeyPath C:/private/update-private.pem -PublicKeyPath C:/private/update-public.pem `
  -ReleaseNotesPath C:/release/notes.txt -VcVarsAllPath C:/path/to/vcvarsall.bat
```

SSH 主机和公网源独立配置，可用 `-SshConfigPath C:/private/publisher-ssh.conf` 为 ssh/scp 选择专用客户端配置；主机密钥应提前核对并写入可信 known_hosts。旧 ServerIp/HttpPort 兼容模式继续生成显式 HTTP 配置。不要将示例域名直接用于发布。构建前校验公私钥配对。每次发布独立 ReleaseId 目录，包含原始签名清单、四文件更新 ZIP、五文件首装 ZIP（含 update-settings）、发布记录和回执；记录 Git 提交、脏状态、公钥指纹、版本、序号和摘要。预构建 CI 产物可用 `-PreparedPublishDirectory`，仍复核 EXE 版本、四文件结构、长度和摘要。

本地排他锁保护发布和序号。服务端工具持排他锁，校验预期当前 manifest 摘要（CAS），拒绝序号/版本倒退及同版本不同 ZIP；已存在同摘要版本包可复用，不同摘要拒绝覆盖。包先公开，清单原子切换是提交点。事务和回执在静态目录之外；提交后丢失回执可从签名清单重建。

```powershell
pwsh -File scripts/release/Publish-Update.ps1 -Resume <ReleaseId> -SshUser updatepublisher -ServerIp updates.example.com
```

恢复同一 ReleaseId 不重新编译、分配序号或签名，先核对保留的两个摘要，再调用服务端 `recover` 读取提交事实并修复回执。原清单已经生效时不重复上传或激活，只重新检查下载；网站或其他 CLI 已发布更新版本时，将旧记录标记为 Superseded 并拒绝恢复。公网检测失败时保留 ManifestActivated 状态。全量公网检测包括清单签名与期望摘要、整包 hash/长度/EXE 版本及 Range 的首字节和范围。过期产物不能直接恢复，应续签或创建新发布。

同版本续签通过 `-Version <原版本> -ResignRelease <原ReleaseId>`，其他地址、密钥等参数与新发布一致。复用原 ZIP，只更新序号和有效期并签名；增加 `-PrepareOnly` 可制作待网站上传的续签产物。`Renew-UpdateManifest.ps1` 也可单独生成原签名有效、更高序号的新清单。网站由 Worker 调用同一发布工具，旧入口通过 SSH 调用；两者共用服务端提交锁和 CAS。只读通知服务不负责管理或签名。

工具命令：`verify`、`inspect`、`activate`、`recover`、`public-check`，参数帮助运行不带参数查看。`public-check` JSON 输出 Warning（少于14天）与 Critical（少于7天），签名/网络/摘要错误返回非零；运维监控应按 Critical 字段升级处理。

## 自动化验收入口

正式交付前可运行一次上线准备检查。它会重新生成并审查服务端 `linux-x64` 交付包，核对源码凭据与调试旁路、API 契约、架构边界、迁移、前端锁文件和许可证、systemd/Nginx/备份恢复/监测模板以及 Git 交付范围：

```powershell
pwsh -File scripts/checks/Test-ReleaseReadiness.ps1
```

结果保存在 `.local/artifacts/admin-execution/P8-05/result.json`。检查器不会推送或部署；缺少真实 Authenticode 首装信任、旧客户端公钥轮换迁移、Linux/systemd 运行、生产 Nginx/TLS 或公网路径时会明确报告 `blocked`，不能把交叉构建和回环测试当作生产上线验收。

Windows 预签名及网站发布闭环验收：

```powershell
pwsh -File scripts/checks/Test-ReleaseP5.ps1 -VcVarsAllPath C:/path/to/vcvarsall.bat

# 复用已构建的真实桌面 AOT 目录
pwsh -File scripts/checks/Test-ReleaseP5.ps1 -PreparedPublishDirectory C:/build/portkiller-win-x64
```

该入口检查实际 Native AOT EXE、准备模式的交付隔离、制作记录、原 SSH/scp 和 LocalRoot 发布、Resume 与续签，以及新旧入口的版本竞争。随后使用真实 HTTPS 管理 API、测试签名密钥和生产 Worker/工具进程发布 AOT 包，由现有客户端更新模块发现并在显式下载后验包；同时检查篡改包和落后序号被拒绝。测试不覆盖安装用户当前程序，客户端安装和恢复沿用下面的完整更新验收入口。

SSH 测试使用本机 OpenSSH 客户端连接回环 SSHv2/SFTP 服务，独立生成测试密钥并启用严格主机密钥校验，不安装系统服务或修改全局 SSH 配置。仅测试所需的 Python 依赖固定在 `tests/PortKiller.Release.Tests/ssh-requirements.txt`，自动安装到 `.local/cache/p5-ssh/`；可用 `-PythonExecutable` 指定解释器，不属于生产依赖。部署路径映射到隔离 Windows 目录，真实 Linux 权限、Nginx、生产证书和公网仍需部署验收。结果保存在 `.local/artifacts/admin-execution/P5-04/`，包含独立的后台、前端和旧更新回归证据。

原有完整桌面更新及安装恢复验收：

```powershell
pwsh -File scripts/build/Test-UpdatePipeline.ps1 -VcVarsAllPath C:/path/to/vcvarsall.bat
```

一次执行 solution 构建、41 项后端回归、完整深浅主题 UI、跨平台更新回归、真实两个 AOT 版本安装/回滚/恢复、历史 AOT 安装器到当前实现再到下一版本的连续升级、临时密钥的 loopback 发布/恢复/续签、100 次实时发现测量、1000 连接阶梯及 linux-x64 服务端构建。日志和机器/SDK/提交记录默认保存在 `.local/artifacts/update-validation/run-时间戳/`，可用 `-OutputDirectory` 指定其他位置。Linux 流水线可执行 `-ServerOnly`；`-SkipPerformance` 只供快速开发，完整验收不使用。

后续每次客户端发布都必须执行完整 Windows 验收，不能只用当前源码改两个版本号替代兼容检查。`tests/PortKiller.UpdateRegression/published-update-baselines.json` 固定实际分发过的源码：0.1.1 对应提交 `28d19c7be5aedccab5f25b2bd5907f7646356c8e`（文本回执），0.1.2 对应提交 `eb8a9f4104919b24f3d26879fe7331dbc488fa6c`（结构化回执）。流水线通过 `Publish-LegacyUpdateProbe.ps1` 分别提取历史源码，构建隔离的 8.0.0/8.0.1 AOT 宿主，执行每个历史基线到 `8.1.0 → 8.2.0` 的连续升级。版本仅用于隔离测试，不访问生产更新源和用户缓存。缺少历史提交时验收失败，需获取完整历史，不能静默跳过。

每条链路都检查结果格式、全部四文件摘要、配置保留、缓存/备份/工作副本清理及每次完成只提示一次。修改安装协议或回执格式时保留旧基线，并在上述 JSON 中增加新的已发布源码提交、隔离版本和回执格式，不能用新基线覆盖旧基线；不支持的未来格式应保留恢复材料，不能回退解析成旧成功记录。

这是可复用 CI 入口，未绑定外部 CI 平台，也未创建生产自动发布任务。上线顺序：先部署服务端工具和只读通知服务，再经旧轮询链发布新客户端，最后依据公网与普通/管理员安装演练结果启用运维监测。
