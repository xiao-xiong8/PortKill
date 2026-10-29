import { computed, ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, ApiError, errorText } from '../api/http';

export type Session = components['schemas']['CurrentSessionView'];
const session = ref<Session | null>(null);
const loading = ref(true);
const reauthOpen = ref(false);
const connectionError = ref('');
let strongCompletion: ((value: boolean) => void) | undefined;
let generation = 0;
let restoredAt = -Infinity, retryAt = 0;
let pending: Promise<void> | undefined, controller: AbortController | undefined;

function clear() {
  generation++; controller?.abort(); controller = undefined; pending = undefined;
  session.value = null; loading.value = false; restoredAt = -Infinity; retryAt = 0;
  connectionError.value = ''; finishStrong(false);
}
function hasSession() {
  return session.value != null && (!session.value.expiresUtcMs || Number(session.value.expiresUtcMs) > Date.now());
}
export function restoreSession(force = true): Promise<void> {
  if (pending) return pending;
  const now = performance.now();
  if (!force && hasSession() && (now - restoredAt < 30_000 || now < retryAt)) return Promise.resolve();
  const ownGeneration = generation;
  const own = controller = new AbortController();
  const timeout = setTimeout(() => own.abort(), 8000);
  const work = (async () => {
    try {
      const value = await api<Session>('/api/v1/auth/me', 'GET', undefined, own.signal);
      if (own.signal.aborted) throw new DOMException('Session request timed out', 'AbortError');
      if (ownGeneration === generation) {
        session.value = value; restoredAt = performance.now(); connectionError.value = ''; retryAt = 0;
      }
    } catch (error) {
      if (ownGeneration !== generation) return;
      if (error instanceof ApiError && error.status === 401) { clear(); return; }
      // A transport failure does not revoke a previously authenticated session.
      if (!hasSession()) session.value = null;
      connectionError.value = errorText(error);
      retryAt = performance.now() + (error instanceof ApiError && error.status === 429 ? Math.max(5000, error.retryAfter * 1000) : 5000);
      throw error;
    } finally {
      clearTimeout(timeout);
      if (ownGeneration === generation) { loading.value = false; controller = undefined; pending = undefined; }
    }
  })();
  pending = work;
  return work;
}
async function ensureSession() {
  if (!hasSession()) { if (session.value) clear(); await restoreSession(); }
  else void restoreSession(false).catch(() => { /* connectionError is shown without blocking navigation. */ });
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
  return { session, loading, reauthOpen, connectionError, restore: restoreSession, ensure: ensureSession, clear, logout, requireStrong,
    full: computed(() => session.value?.phase === 'full'),
    can: (permission: string) => session.value?.phase === 'full' && session.value.permissions.includes(permission) };
}
window.addEventListener('admin:unauthorized', clear);
