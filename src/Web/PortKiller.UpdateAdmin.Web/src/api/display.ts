export function time(value: unknown): string { const number = Number(value); return value != null && Number.isFinite(number) ? new Date(number).toLocaleString('zh-CN', { hour12: false }) : '—'; }
export function value(value: unknown): string { return typeof value === 'string' || typeof value === 'number' ? String(value) : '—'; }
export function status(state: string | null | undefined): string {
  const names: Record<string, string> = { queued: '等待处理', pending: '等待校验', publishing: '正在发布', validating: '正在校验', readyForApproval: '可确认发布', rejected: '校验拒绝', manifestActivated: '清单已生效', publicVerifying: '正在检查下载', completed: '发布完成', activeUnverified: '已生效，下载检查未通过', failed: '处理失败', conflict: '发布冲突', superseded: '已被更新版本取代', healthy: '签名有效', expiring: '签名即将到期', critical: '签名临近到期', expired: '签名已过期' };
  return state ? names[state] ?? state : '—';
}
export function openJob(id: string) { window.location.hash = 'job/' + encodeURIComponent(id); }
