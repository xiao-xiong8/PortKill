import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { api, ApiError } from '../api/http';
vi.mock('../api/http', async original => ({ ...await original<typeof import('../api/http')>(), api: vi.fn() }));
let auth: ReturnType<typeof import('./auth')['useAuth']>;
const value = { userId: 'u1', currentSessionId: 's1', userName: 'reader', phase: 'full' as const, roles: [], permissions: ['release.read'], expiresUtcMs: Date.now() + 86400_000, strongAuthenticationExpiresUtcMs: 0 };
beforeAll(async () => { vi.stubGlobal('window', new EventTarget()); auth = (await import('./auth')).useAuth(); });
afterEach(() => { auth.clear(); vi.mocked(api).mockReset(); vi.useRealTimers(); });
describe('non-blocking authenticated navigation', () => {
  it('deduplicates bootstrap requests and reuses a valid session for navigation', async () => {
    let resolve!: (value: unknown) => void;
    vi.mocked(api).mockImplementation(() => new Promise(done => { resolve = done; }));
    const first = auth.restore(), second = auth.ensure();
    expect(api).toHaveBeenCalledTimes(1); resolve(value); await first; await second;
    await auth.ensure(); expect(api).toHaveBeenCalledTimes(1); expect(auth.full.value).toBe(true);
  });
  it('keeps authenticated identity on 503 but clears it on 401', async () => {
    vi.mocked(api).mockResolvedValueOnce(value); await auth.restore();
    vi.mocked(api).mockRejectedValueOnce(new ApiError(503, 'unavailable', 'unavailable'));
    await expect(auth.restore()).rejects.toMatchObject({ status: 503 });
    expect(auth.session.value?.userId).toBe('u1');
    vi.mocked(api).mockRejectedValueOnce(new ApiError(401, 'expired', 'expired')); await auth.restore();
    expect(auth.session.value).toBeNull();
  });
  it('does not let a delayed session response restore identity after logout', async () => {
    let resolve!: (value: unknown) => void;
    vi.mocked(api).mockImplementation(() => new Promise(done => { resolve = done; }));
    const first = auth.restore(); auth.clear(); resolve(value); await first;
    expect(auth.session.value).toBeNull();
  });
  it('navigation does not await a due background session recheck', async () => {
    vi.useFakeTimers();
    vi.mocked(api).mockResolvedValueOnce(value); await auth.restore();
    await vi.advanceTimersByTimeAsync(30_001);
    let resolve!: (value: unknown) => void;
    vi.mocked(api).mockImplementation(() => new Promise(done => { resolve = done; }));
    await auth.ensure(); expect(api).toHaveBeenCalledTimes(2); expect(auth.full.value).toBe(true);
    resolve(value); await Promise.resolve();
  });
});
