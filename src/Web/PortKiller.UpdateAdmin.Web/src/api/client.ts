import type { paths } from './contracts.generated';

type SystemStatus = paths['/api/v1/system/status']['get']['responses'][200]['content']['application/json'];
export type ConnectionResult =
  | { kind: 'ready'; status: SystemStatus }
  | { kind: 'locked' | 'error' };

export async function getSystemStatus(): Promise<ConnectionResult> {
  try {
    const response = await fetch('/api/v1/system/status', { credentials: 'same-origin', redirect: 'error' });
    if (response.status === 401 || response.status === 403) return { kind: 'locked' };
    if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) return { kind: 'error' };
    const data: SystemStatus = await response.json();
    if (typeof data.application !== 'string' || typeof data.version !== 'string' || data.contractVersion !== 1)
      return { kind: 'error' };
    return { kind: 'ready', status: data };
  } catch { return { kind: 'error' }; }
}
