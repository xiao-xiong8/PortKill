import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/http';
import type { api } from '../api/http';
import type { Monitoring } from './overview';

let createStore: typeof import('./overview')['createOverviewStore'];
let store: ReturnType<typeof createStore>;
let documentTarget: EventTarget & { hidden: boolean };
const result: Monitoring = { status: 'ok', servedAtUtcMs: 1_000_000, snapshot: null, alerts: [],
  manifest: { status: 'missing', version: null, daysRemaining: null } };
const request = vi.fn<typeof api>();
const settle = async () => { for (let index = 0; index < 10; index++) await Promise.resolve(); };

beforeAll(async () => {
  vi.stubGlobal('window', new EventTarget());
  createStore = (await import('./overview')).createOverviewStore;
});
beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime(1000);
  documentTarget = Object.assign(new EventTarget(), { hidden: false }); vi.stubGlobal('document', documentTarget);
  request.mockReset(); request.mockResolvedValue(result);
  store = createStore(request as typeof api, Date.now);
});
afterEach(() => { store.deactivate(); store.reset(); vi.useRealTimers(); });

describe('overview session memory and lifecycle', () => {
  it('shows the previous response immediately on return and keeps its true age', async () => {
    store.activate(false); await settle();
    expect(request).toHaveBeenCalledTimes(3);
    const previous = store.monitoring.value;
    store.deactivate(); await vi.advanceTimersByTimeAsync(6000);
    expect(request).toHaveBeenCalledTimes(3);
    request.mockImplementation(() => new Promise(() => {}));
    store.activate(false);
    expect(store.monitoring.value).toBe(previous);
    expect(store.serverNow.value).toBe(result.servedAtUtcMs + 6000);
    expect(request).toHaveBeenCalledTimes(4);
  });
  it('stops hidden requests and ignores a response completed after cancellation', async () => {
    let complete!: (value: unknown) => void;
    request.mockImplementation(url => url.endsWith('/monitoring') ? new Promise(resolve => { complete = resolve; }) : Promise.resolve(result));
    store.activate(true);
    documentTarget.hidden = true; documentTarget.dispatchEvent(new Event('visibilitychange'));
    complete(result); await settle(); await vi.advanceTimersByTimeAsync(10_000);
    expect(store.monitoring.value).toBeUndefined();
    expect(store.monitorError.value).toBe(''); expect(request).toHaveBeenCalledTimes(2);
    request.mockResolvedValue(result);
    documentTarget.hidden = false; documentTarget.dispatchEvent(new Event('visibilitychange')); await settle();
    expect(store.monitoring.value).toEqual(result);
    expect(request.mock.calls.some(([url]) => url.includes('/jobs'))).toBe(false);
  });
  it('clears all cached session data and cannot repopulate it with an old response', async () => {
    store.activate(false); await settle();
    let complete!: (value: unknown) => void;
    request.mockImplementation(url => url.endsWith('/monitoring') ? new Promise(resolve => { complete = resolve; }) : Promise.resolve(result));
    vi.setSystemTime(7000); store.refresh(); store.reset(); complete(result); await settle();
    expect(store.monitoring.value).toBeUndefined(); expect(store.current.value).toBeUndefined(); expect(store.history.value).toBeUndefined();
    expect(store.transport.value).toBe('loading');
    request.mockResolvedValue(result); store.refresh(); await settle();
    expect(store.monitoring.value).toEqual(result);
  });
  it('distinguishes failed transport from sample health without refreshing cached timestamps', async () => {
    store.activate(true); await settle(); request.mockRejectedValue(new ApiError(503, 'unavailable', '接口不可用'));
    await vi.advanceTimersByTimeAsync(5000); expect(store.transport.value).toBe('delayed');
    await vi.advanceTimersByTimeAsync(20_000); expect(store.transport.value).toBe('disconnected');
    expect(store.monitoring.value?.servedAtUtcMs).toBe(result.servedAtUtcMs);
    expect(store.serverNow.value).toBe(result.servedAtUtcMs + 25_000);
  });
  it('does not bypass a shared Retry-After deadline through manual refresh', async () => {
    request.mockRejectedValue(new ApiError(429, 'limited', 'limited', 60));
    store.activate(false); await settle(); expect(request).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(30_000); store.refresh(); await settle(); expect(request).toHaveBeenCalledTimes(3);
    expect(store.transport.value).toBe('rate_limited');
    await vi.advanceTimersByTimeAsync(30_000); await settle(); expect(request).toHaveBeenCalledTimes(6);
  });
  it('never classifies a paused cache as failed transport on return', async () => {
    store.activate(false); await settle(); store.deactivate();
    await vi.advanceTimersByTimeAsync(30_000);
    request.mockImplementation(() => new Promise(() => {})); store.activate(false);
    expect(store.transport.value).toBe('refreshing');
    expect(store.serverNow.value).toBe(result.servedAtUtcMs + 30_000);
  });
  it('keeps shared rate limiting distinct from monitor failures and sample age', async () => {
    request.mockImplementation(url => url.includes('/releases/current')
      ? Promise.reject(new ApiError(429, 'limited', 'limited', 60)) : Promise.resolve(result));
    store.activate(false); await settle(); await vi.advanceTimersByTimeAsync(26_000);
    expect(store.transport.value).toBe('rate_limited');
    expect(store.monitorError.value).toBe(''); expect(store.retrySeconds.value).toBe(34);
    expect(store.monitoring.value).toEqual(result);
  });
});
