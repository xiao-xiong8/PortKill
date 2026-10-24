# 自动更新本地验收记录

日期：2026-10-24。状态：完整本地流水线退出码 0，result.json 的 Passed=true；补充的最终 10 组更新回归、100 次手动检查及 7 类 AOT/NTFS 用例也均通过。最后一次完整流水线完成于北京时间 2026-10-24 02:00:10。

## 环境与隔离

- Windows，AMD Ryzen 7 5800H，8 核 / 16 线程，.NET SDK 10.0.400。
- 基线提交 acf3bad4f3087eb358acc07e33577a136363cfa4；测试的是本次未提交工作区。流水线 Commit 字段表示基线，发布演练记录另有 Dirty=true。
- 当前为普通用户令牌（非提升管理员）；未请求或自动提升权限。
- HTTP/SSE 服务仅绑定 127.0.0.1，使用每次创建的 RSA 测试密钥、临时安装目录、独立信任文件和测试配置。
- Native AOT 测试宿主是单独的 PortKiller.UpdateProbe，不随桌面程序分发。正式程序没有新增自动确认、模拟崩溃或自定义信任目录命令。
- 没有连接真实发布服务器，没有使用生产私钥，没有替换正在使用的安装，没有提交/推送 Git。

## 自动化入口与记录

完整入口：

```powershell
pwsh -NoProfile -File scripts/build/Test-UpdatePipeline.ps1 `
  -VcVarsAllPath 'C:/Program Files/Microsoft Visual Studio/2022/Professional/VC/Auxiliary/Build/vcvarsall.bat' `
  -OutputDirectory 'C:/自用/项目/PortKill/artifacts/update-validation/final-verified'
```

主要记录：`artifacts/update-validation/final-verified-console.log`、`final-verified/pipeline.log` 和成功后生成的 `final-verified/result.json`。补充记录为 `extra-checks.log`（短退避与不可变包覆盖测试）和 `acl-checks.log`（最终测试宿主及真实 NTFS 权限）。以上产物留在 Git 之外。

## 功能覆盖

| 范围 | 自动验证 |
|---|---|
| 既有功能 | 39 项原后端回归、完整 UI；应用名称列、搜索、导航字体/图标的深浅主题、更新窗口全部状态继续通过 |
| 版本一致 | A Ready 后发现 B、B 收到 304、B 下载失败和恢复、同 ZIP 续签、安装确认租约阻止推进信任 |
| 调度 | 10,000 次信号只保留一次意图、忙时延迟、自动开关、后台执行、长及最后一次短 Retry-After 到期补查 |
| 暂停 | 通知不能恢复暂停下载，用户继续后执行待检查版本；原 Range 回退、错误范围和并发去重继续通过 |
| SSE | CRLF、多行 data、心跳、4KiB 行 / 8KiB 事件上限、畸形事件重连、EOF 重连、404 降级、服务重启恢复 |
| 服务端通知 | 无效清单与低序号不广播；100/500/1000 连接阶梯、初始提示、连接释放归零 |
| 发布事务 | staged / package-published / manifest-activated / receipt-written 四点注入失败；相同产物恢复幂等、CAS 冲突、序号倒退、同版本不同 ZIP、坏包检查、并发排他锁 |
| 发布入口 | 公私钥不配对拒绝、五文件首装包、原始四文件更新包、清单激活后公网检查失败、同一 ReleaseId 恢复/重复恢复不重签、同版本续签、完整下载及真实 Range |
| 构建 | Solution Release、正式桌面 win-x64 Native AOT、两个版本 AOT Probe、linux-x64 自包含发布工具和通知服务 |

安装使用 8.1.0.0 与 8.2.0.0 两个真实 AOT EXE，在临时安装目录调用实际 UpdateInstaller：主进程启动工作副本、退出、替换四文件、新进程启动与健康确认/回滚。每例核对四文件 SHA-256、启动版本、设置保留、事务日志清理。

| 安装场景 | 机制 |
|---|---|
| 正常升级 | 新版写健康标记，提交成功 |
| 新版立即退出 | 未健康即退出，自动回滚并启动旧版 |
| 45 秒健康超时 | 新版保持存活而不确认，实际等待默认 45 秒并回滚 |
| 存储失败 | 首文件替换后注入 IOException，回滚；属于故障模拟，不是实际磁盘写满 |
| 工作进程中断 | 首文件替换后进程直接退出，下次启动恢复 |
| 文件被占用 | 实际 FileShare.Read 锁住 DLL，替换/回滚受阻后释放锁，再启动完成恢复 |
| 目录拒绝写入 | 临时目录对当前用户应用真实 NTFS Deny Write ACL，拒绝进入安装，恢复 ACL 后旧版可启动 |

## 性能实测

以下为本机结果，不代表公网或生产容量。

| 指标 | 条件与结果 |
|---|---|
| 更新 UI Light | 1000 次 Apply + UpdateLayout，P95 0.19ms，最大 6.83ms |
| 更新 UI Dark | 1000 次 Apply + UpdateLayout，P95 0.18ms，最大 7.99ms |
| SSE 发现 | 100 次，P95 1258.0ms，最大 1264.1ms；包含 0–500ms 抖动、后台调度及实际清单验签，达到本地 P95 ≤ 5s 目标 |
| 手动检查 | 100 次真实 loopback 请求，推进虚拟时钟消除 30 秒冷却等待；P95 1.4ms，最大 187.7ms |
| SSE 阶梯 | 补充运行 100 / 500 / 1000 负载连接，另有 1 条验证连接；连接全部成功且释放后归零 |
| 资源 | 上述阶梯总计约 2.65s，CPU 时间约 1.64s，工作集增加约 30.3MiB；1000 阶段整个测试进程 RSS 约 102MiB |

资源数值包含通知服务和负载客户端所在的同一测试进程，不能解释成独立服务端的单连接成本；短阶梯不是长时间稳定性或线上容量证明。UI 数值仅包括快照应用与强制布局，不能代表整个 Windows 合成器或完整用户输入延迟。loopback 无网络延迟注入，未复现公网丢包/代理/带宽。手动冷却与退避本身由独立功能用例验证。

## 现场边界

本地没有 Linux 发行版运行环境，因此只完成 linux-x64 自包含产物构建和 Bash 语法检查，没有声称 Ubuntu、systemd、Nginx 已安装运行或生产 SSE 代理已经验证。正式上线前仍需核实 Linux CPU/发行版、公开地址、证书、SSH/目录权限、代理连接数与缓冲、真实客户端已有公钥和公网 Range。

管理员令牌安装、杀毒软件拦截、真实磁盘写满、物理断电和慢机器启动尚需现场演练；本地注入故障不能替代这些条件。Windows 电源恢复消息分类、协调入口和窗口订阅生命周期已测试，未实际让用户电脑休眠。

已提供可复用 CI 入口和发布脚本，没有绑定外部 CI 平台、安装生产签名密钥或启用生产定时任务。正式发布继续需要用户选择版本并执行明确发布命令。
