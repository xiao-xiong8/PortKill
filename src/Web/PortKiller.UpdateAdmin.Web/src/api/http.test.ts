import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, parseJson, readResponse } from './http';
afterEach(() => vi.unstubAllGlobals());
describe('authenticated management transport', () => {
  it('reads a new CSRF token before every mutation and never retries a mutation', async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(Response.json({ requestToken: 'before-login' }))
      .mockResolvedValueOnce(Response.json({ next: 'verifyMfa' }))
      .mockResolvedValueOnce(Response.json({ requestToken: 'after-login' }))
      .mockResolvedValueOnce(Response.json({ detail: 'denied', code: 'permission_denied' }, { status: 403 }));
    vi.stubGlobal('fetch', fetcher);
    await api('/api/v1/auth/login', 'POST', { userName: 'test', password: 'test' });
    await expect(api('/api/v1/releases/test/publish', 'POST', {})).rejects.toMatchObject({ status: 403 });
    expect(fetcher).toHaveBeenCalledTimes(4);
    expect(fetcher.mock.calls[3][1].headers.get('X-PortKill-CSRF')).toBe('after-login');
    expect(fetcher.mock.calls[3][1].credentials).toBe('same-origin');
  });
  it('preserves exact unsafe int64 values', () => { expect(parseJson<{ sequence: string }>('{"sequence":9223372036854775806}').sequence).toBe('9223372036854775806'); });
  it('retains bounded rate limit information', async () => { await expect(readResponse(new Response('{"code":"rate_limited"}', { status: 429, headers: { 'content-type': 'application/problem+json', 'retry-after': '60' } }))).rejects.toMatchObject({ retryAfter: 60 }); });
  it('adds an idempotency header without replacing the fresh CSRF token', async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(Response.json({ requestToken: 'fresh-token' }))
      .mockResolvedValueOnce(Response.json({ id: 'job-1' }, { status: 202 }));
    vi.stubGlobal('fetch', fetcher);
    await api('/api/v1/releases/draft/publish', 'POST', { challengeId: 'challenge', idempotencyKey: 'request-1' }, undefined, { 'Idempotency-Key': 'request-1' });
    const headers = fetcher.mock.calls[1][1].headers as Headers;
    expect(headers.get('X-PortKill-CSRF')).toBe('fresh-token');
    expect(headers.get('Idempotency-Key')).toBe('request-1');
  });
  it('rejects SPA HTML and surfaces unauthorized protected responses', async () => {
    await expect(readResponse(new Response('<html>'))).rejects.toBeInstanceOf(ApiError);
    const listener = vi.fn(); vi.stubGlobal('window', { dispatchEvent: listener });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ code: 'authentication_required' }, { status: 401 })));
    await expect(api('/api/v1/auth/me')).rejects.toMatchObject({ status: 401 }); expect(listener).toHaveBeenCalledOnce();
  });
});
