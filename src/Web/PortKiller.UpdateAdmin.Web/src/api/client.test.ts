import { afterEach, describe, expect, it, vi } from 'vitest';
import { getSystemStatus } from './client';

afterEach(() => vi.unstubAllGlobals());

describe('management response boundaries', () => {
  it.each([401, 403])('treats HTTP %s as a locked session', async (status) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status })));
    expect(await getSystemStatus()).toEqual({ kind: 'locked' });
  });
  it('does not treat SPA HTML fallback as an authenticated connection', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html></html>', { headers: { 'Content-Type': 'text/html' } })));
    expect(await getSystemStatus()).toEqual({ kind: 'error' });
  });
  it('rejects an incompatible contract version', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json({ application: 'admin', version: '0.1.0', contractVersion: 2 })));
    expect(await getSystemStatus()).toEqual({ kind: 'error' });
  });
  it('accepts a typed response from the supported contract', async () => {
    const status = { application: 'admin', version: '0.1.0', contractVersion: 1 };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json(status)));
    expect(await getSystemStatus()).toEqual({ kind: 'ready', status });
  });
  it('reports transport failures without inventing server state', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline')));
    expect(await getSystemStatus()).toEqual({ kind: 'error' });
  });
});
