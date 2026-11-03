import { describe, expect, it, vi } from 'vitest';
import { createServerUpdateStore } from './serverUpdate';
import { api } from '../api/http';
const task = { id: 'abc', packageId: 'pkg', version: '1.2.3', state: 'running', stage: '导入镜像', updatedUtcMs: 1, terminal: false };
const summary = { enabled: true, maximumPackageBytes: 100, currentVersion: '1.0.0', packages: [], tasks: [task] };
describe('server update continuity', () => {
  it('retains the last known task during API downtime and resumes logs by cursor', async () => {
    let offline = false;
    const request = vi.fn(async (path: string) => {
      if (offline) throw new Error('offline');
      if (path.endsWith('/server-updates/')) return summary;
      if (path.endsWith('after=0')) return [{ seq: 1, atUtcMs: 1, message: 'first' }];
      return [{ seq: 1, atUtcMs: 1, message: 'duplicate' }, { seq: 2, atUtcMs: 2, message: 'second' }];
    });
    const store = createServerUpdateStore(request as typeof api);
    await store.refresh(); expect(store.task.value?.id).toBe('abc');
    offline = true; await store.refresh(); expect(store.connection.value).toContain('连接中断'); expect(store.task.value?.state).toBe('running');
    offline = false; await store.refresh(); expect(store.connection.value).toBe(''); expect(store.logs.value.map(l => l.seq)).toEqual([1, 2]);
    expect(request).toHaveBeenLastCalledWith('/api/v1/server-updates/tasks/abc/logs?after=1', 'GET', undefined, expect.any(AbortSignal));
  });
  it('keeps route-independent upload state and does not restore another session after reset', async () => {
    let complete!: (value: never) => void;
    const request = vi.fn(async () => ({ ...summary, tasks: [] }));
    const send = vi.fn(() => new Promise<never>(resolve => { complete = resolve; }));
    const store = createServerUpdateStore(request as typeof api, send);
    await store.refresh();
    const work = store.upload({ name: 'release.zip', size: 10 } as File);
    expect(store.busy.value).toBe(true); expect(send).toHaveBeenCalledTimes(1);
    store.reset(); complete(undefined as never); await work;
    expect(store.summary.value).toBeUndefined(); expect(store.busy.value).toBe(false);
  });
  it('rejects wrong files before starting a transfer', async () => {
    const request = vi.fn(async () => summary), send = vi.fn();
    const store = createServerUpdateStore(request as typeof api, send); await store.refresh();
    await store.upload({ name: 'wrong.exe', size: 10 } as File); expect(send).not.toHaveBeenCalled();
    await store.upload({ name: 'large.zip', size: 101 } as File); expect(send).not.toHaveBeenCalled();
  });
  it('replaces an old success with a newer failure after navigation and restarts the log cursor', async () => {
    const old = { ...task, id: 'old', state: 'succeeded', terminal: true, updatedUtcMs: 1 };
    const latest = { ...task, id: 'new', state: 'failed', terminal: true, updatedUtcMs: 2 };
    let returned = [old];
    const request = vi.fn(async (path: string) => path.endsWith('/server-updates/')
      ? { ...summary, tasks: returned } : [{ seq: 1, atUtcMs: 1, message: path.includes('/old/') ? 'old success' : 'new failure' }]);
    const store = createServerUpdateStore(request as typeof api);
    await store.refresh(); expect(store.logs.value[0]?.message).toBe('old success');
    returned = [old, latest]; await store.refresh();
    expect(store.task.value?.id).toBe('new'); expect(store.task.value?.state).toBe('failed');
    expect(store.logs.value.map(item => item.message)).toEqual(['new failure']);
    expect(request).toHaveBeenLastCalledWith('/api/v1/server-updates/tasks/new/logs?after=0', 'GET', undefined, expect.any(AbortSignal));
  });
  it('selects the returned apply task immediately even when the follow-up summary is offline', async () => {
    let offline = false;
    const request = vi.fn(async (path: string) => {
      if (path.endsWith('/apply')) { offline = true; return { ...task, id: 'new', updatedUtcMs: 2 }; }
      if (offline) throw new Error('API restarting');
      return path.endsWith('/server-updates/') ? { ...summary, tasks: [{ ...task, id: 'old', terminal: true, state: 'succeeded' }] } : [];
    });
    const store = createServerUpdateStore(request as typeof api); await store.refresh();
    await store.action('/api/v1/server-updates/packages/pkg/apply');
    expect(store.task.value?.id).toBe('new'); expect(store.task.value?.state).toBe('running');
    expect(store.connection.value).toContain('连接中断');
  });
  it('discards an old task log response when an action changes the selected task', async () => {
    let finish!: (value: { seq: number; atUtcMs: number; message: string }[]) => void;
    const request = vi.fn(async (path: string) => {
      if (path.endsWith('/apply')) return { ...task, id: 'new', updatedUtcMs: 2 };
      if (path.endsWith('/server-updates/')) return { ...summary, tasks: [{ ...task, id: 'old' }] };
      return new Promise(resolve => { finish = resolve; });
    });
    const store = createServerUpdateStore(request as typeof api);
    const pending = store.refresh();
    await vi.waitFor(() => expect(finish).toBeDefined());
    await store.action('/api/v1/server-updates/packages/pkg/apply');
    finish([{ seq: 1, atUtcMs: 1, message: 'old task only' }]); await pending;
    expect(store.task.value?.id).toBe('new'); expect(store.logs.value).toEqual([]);
  });
});
