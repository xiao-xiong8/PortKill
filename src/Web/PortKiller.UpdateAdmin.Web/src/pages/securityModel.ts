export const SECURITY_ROLES = ['Administrator', 'Publisher', 'Uploader', 'Viewer'] as const;
export type SecurityRole = typeof SECURITY_ROLES[number];

const ROLE_LABELS: Record<SecurityRole, string> = {
  Administrator: '管理员',
  Publisher: '发布者',
  Uploader: '上传者',
  Viewer: '查看者',
};

export function roleLabel(role: string): string {
  return ROLE_LABELS[role as SecurityRole] ?? role;
}

export function toggleRole(roles: readonly string[], role: string): string[] {
  if (!SECURITY_ROLES.includes(role as SecurityRole)) return [...roles];
  const next = roles.includes(role) ? roles.filter(value => value !== role) : [...roles, role];
  return next.length ? next : [...roles];
}

export function redact(value: string | null | undefined): string {
  if (!value) return '—';
  return value.length > 14 ? `${value.slice(0, 8)}…${value.slice(-4)}` : value;
}
