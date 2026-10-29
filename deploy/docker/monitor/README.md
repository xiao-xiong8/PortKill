# Docker 运行总览监控

适配已核实的 `PortKill-Docker-Kit-20261026`：API 在 `api_proxy` 网络，Notifier 在 `portkill` 网络，Web 从 `server-bundle/web` 提供静态文件，数据分别为 `data/admin`、`data/public`。不修改原包的账号、密钥、域名或业务数据。

## 配置与启动

1. 使用与本次代码匹配的新 API、Web 产物。仅加入 monitor 而继续使用旧 API，不会出现新总览接口。
2. 将本目录放到部署包 `docker/monitor`，将示例中的新增服务、API配置/挂载和卷合入**原主** `docker-compose.yml`。不要将示例单独当作完整部署文件。
3. 在服务器原部署目录执行 `docker compose ls` 或检查现有容器的 `com.docker.compose.project` 标签。将**已有项目名**写入 `.env` 的 `MONITOR_COMPOSE_PROJECT`；已有 `COMPOSE_PROJECT_NAME` 时直接沿用。不能为监控新建另一个项目。
4. 推荐在 `.env` 填写 `MONITOR_INTERFACE=eth0`（按服务器实际主网卡替换）。留空时尝试 `/host/proc/1/net/route` 自动选卡；权限受限会显示网络未知，不能拿容器网卡替代。
5. 首次准备好镜像：`docker compose build api monitor`。普通后续启动：`docker compose up -d`。API 使用独立的新镜像标签，保留原 Worker/Notifier 镜像不变。
6. 检查 `docker compose logs --tail=50 monitor`，登录运行总览，对照主机同窗口计数和业务磁盘。monitor 不开放公网端口，不是 API 启动依赖。

API内部请求使用 `MONITOR_API_HOST=${ADMIN_DOMAIN}`，以符合原包 AllowedHosts。公开探针从服务器请求 HTTPS 清单，按现有 .NET 协议检查 `Payload`/`Signature` 类型、Base64 编码及 Payload JSON 对象结构；普通错误 JSON 不算成功。此项只表示入口可达且信封结构有效，签名可信性与有效期仍由 API 对正式源验签确认，不表示公网响应已验签或与正式源一致。不下载ZIP，也不证明所有外部客户端都能访问。

采集器只读主机 `/proc`、`/sys`、两个业务目录，仅向快照共享卷写JSON。API以原有 UID 10001 只读挂载整个快照目录。快照在monitor重启后重建历史，API重启不清空采集器的历史。快照文件过期后显示未知。

Docker socket只给monitor。`:ro`只是挂载属性，**不是Docker API只读授权**；采集器只使用固定白名单查询，不允许网页配置命令、容器或探针。

## 更新与回退

更换Compose、API/Web前，备份原主Compose、API/Web目录并记录正在运行的API镜像ID。先构建后切换：

```bash
docker compose config --quiet
docker compose build api monitor
docker compose up -d --no-deps api monitor
```

Caddy的Web目录为宿主机绑定挂载，替换Web内容时保留根目录，不通过重命名整个Web目录更新，以免容器仍绑定旧目录；按完整交付包更新静态资源，避免新旧入口/资源错配。

原有 notifier/worker 更新脚本继续读同一个主Compose；新增服务和API挂载不能被脚本或旧配置覆盖。业务镜像均来自同一个Dockerfile，但API的新标签不与旧Worker/Notifier共用，避免更新API时改变其镜像引用。

仅暂停监控：`docker compose stop monitor`。整版回退：停止monitor，恢复备份的Compose与API/Web，恢复原API镜像引用，再执行原项目的 `docker compose up -d` 并检查登录、正式版本、发布和下载。快照卷可保留。

不要执行 `docker compose down -v`；不要删除数据库、证书、发布目录或签名材料。

## 验证边界

Python单测、后台快照/权限测试、前端类型/lint/单测/浏览器测试分别验证对应行为。生产Linux还需实际验证宿主机资源口径、网络读取、非root API读取、Docker socket访问、容器/整机重启和回退；模拟Docker不能代替这些检查。
