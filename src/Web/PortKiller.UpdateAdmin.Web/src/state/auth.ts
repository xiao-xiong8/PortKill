import { computed, ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, ApiError } from '../api/http';

export type Session = components['schemas']['CurrentSessionView'];
const session = ref<Session | null>(null);
const loading = ref(true);
const reauthOpen = ref(false);
let strongCompletion: ((value: boolean) => void) | undefined;
let generation = 0;

function clear() { generation++; session.value = null; finishStrong(false); }
export async function restoreSession() {
  const ownGeneration = generation;
  try {
    const value = await api<Session>('/api/v1/auth/me');
    if (ownGeneration === generation) session.value = value;
  } catch (error) {
    if (ownGeneration === generation) session.value = null;
    if (!(error instanceof ApiError && error.status === 401)) throw error;
  } finally { loading.value = false; }
}
async function logout() { try { await api('/api/v1/auth/logout', 'POST', {}); } finally { clear(); } }
export function finishStrong(value: boolean) { reauthOpen.value = false; const completion = strongCompletion; strongCompletion = undefined; completion?.(value); }
async function requireStrong() {
  if (Number(session.value?.strongAuthenticationExpiresUtcMs) > Date.now() + 5000) return true;
  if (reauthOpen.value) return false;
  reauthOpen.value = true;
  return new Promise<boolean>((resolve) => { strongCompletion = resolve; });
}
export function useAuth() {
  return { session, loading, reauthOpen, restore: restoreSession, clear, logout, requireStrong,
    full: computed(() => session.value?.phase === 'full'),
    can: (permission: string) => session.value?.phase === 'full' && session.value.permissions.includes(permission) };
}
window.addEventListener('admin:unauthorized', clear);
