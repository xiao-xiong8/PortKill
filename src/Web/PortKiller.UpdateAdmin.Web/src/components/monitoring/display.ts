import type { components } from '../../api/contracts.generated';

type Monitoring = components['schemas']['MonitoringResponse'];
type Alert = components['schemas']['MonitoringAlert'];
export const defaultFreshness = { resourceTtlMs: 20_000, collectorTtlMs: 20_000, dockerTtlMs: 45_000, diskTtlMs: 90_000, probeTtlMs: 90_000 };
export const freshness = (response?: Monitoring) => response?.freshness ?? defaultFreshness;

export function alertObservation(response: Monitoring, alert: Alert, now: number) {
  const snapshot = response.snapshot;
  let observed: number | null | undefined = response.servedAtUtcMs;
  let error: string | null | undefined;
  const policy = freshness(response);
  let ttl = 25_000;
  if (alert.code === 'container_state') {
    observed = snapshot?.docker.observedAtUtcMs; error = snapshot?.docker.error; ttl = policy.dockerTtlMs;
  } else if (alert.code === 'disk_space') {
    const disk = snapshot?.resources.disks.find(disk => disk.label === alert.source);
    observed = disk?.observedAtUtcMs; error = disk?.error; ttl = policy.diskTtlMs;
  } else if (alert.code === 'application_failed' || alert.code === 'notifier_manifest') {
    const probe = snapshot?.appChecks.find(probe => probe.name === alert.source);
    observed = probe?.observedAtUtcMs; error = probe?.error; ttl = policy.probeTtlMs;
  }
  const expired = now - response.servedAtUtcMs >= 25_000
    || observed == null || !!error || now - observed >= ttl;
  return { alert, observedAtUtcMs: observed, expired };
}

export function fresh(observed: number | null | undefined, error: string | null | undefined, ttl: number,
  generated: number | undefined, now: number): boolean {
  return observed != null && generated != null && !error && now - observed < ttl;
}
export function bytes(amount: number | null | undefined): string {
  if (amount == null || !Number.isFinite(amount)) return '未知';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']; let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) { amount /= 1024; unit++; }
  return `${amount.toFixed(unit ? 1 : 0)} ${units[unit]}`;
}
export function label(state: string | null | undefined): string {
  const names: Record<string, string> = { ok: '采集正常', degraded: '部分数据不可用', stale: '采集已断流',
    unconfigured: '未启用宿主机监控', unavailable: '监控不可用', invalid_snapshot: '快照格式异常',
    healthy: '正常', failed: '异常', unknown: '未知', unconfirmed: '发布状态待确认', missing: '未发现',
    running: '运行中', exited: '已停止', dead: '已停止', restarting: '重启中', paused: '已暂停',
    created: '已创建', removing: '移除中', ambiguous: '发现多个实例', unhealthy: '不健康',
    starting: '启动检查中', not_configured: '未配置', unavailable_manifest: '清单不可用', loading: '正在加载监控',
    delayed: '监控更新延迟', disconnected: '监控连接中断' };
  return state ? names[state] ?? state : '未知';
}
