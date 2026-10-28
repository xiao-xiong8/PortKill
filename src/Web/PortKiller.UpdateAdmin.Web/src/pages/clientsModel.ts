export function maskInstallationId(id: string) { return id.length > 8 ? `${id.slice(0, 4)}…${id.slice(-4)}` : id; }
export function compareVersion(client: string, current?: string | null) {
  if (!current) return '尚无正式版本';
  const left = client.split('.').map(Number), right = current.split('.').map(Number);
  for (let index = 0; index < 4; index++) {
    const difference = (left[index] ?? 0) - (right[index] ?? 0);
    if (difference < 0) return '低于正式版本';
    if (difference > 0) return '高于正式版本';
  }
  return '与正式版本一致';
}
export function reportResult(result: string) {
  return ({ started: '已启动', installed: '安装成功', rolled_back: '已回滚', failed: '更新启动失败' } as Record<string, string>)[result] ?? result;
}
