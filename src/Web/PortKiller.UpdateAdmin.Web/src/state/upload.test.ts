import { beforeAll, describe, expect, it, vi } from 'vitest';
import type { api } from '../api/http';
import type { uploadPart } from '../api/upload';

let createStore: typeof import('./upload')['createUploadStore'];
beforeAll(async () => {
  vi.stubGlobal('window', new EventTarget());
  createStore = (await import('./upload')).createUploadStore;
});
const session = (manifest = 0, packageBytes = 0) => ({ id: 'task-1', state: 'uploading' as const,
  manifestExpectedBytes: 2, packageExpectedBytes: 4, manifestReceivedBytes: manifest,
  packageReceivedBytes: packageBytes, expiresUtcMs: Date.now() + 60000 });
function fixture() {
  const request = vi.fn<(path: string, method?: string, body?: unknown, signal?: AbortSignal) => Promise<unknown>>(
    async path => path.endsWith('/complete') ? { jobId: 'job-1' } : session());
  const send = vi.fn<typeof uploadPart>(async (_, part) => session(2, part === 'package' ? 4 : 0));
  const store = createStore(request as typeof api, send);
  store.pick(new File(['{}'], 'manifest.json'), 'manifest');
  store.pick(new File(['test'], 'package.zip'), 'package');
  return { request, send, store };
}
describe('upload task ownership', () => {
  it('keeps one task and ignores restore attempts while a file is in flight', async () => {
    const { request, send, store } = fixture();
    let complete!: () => void;
    send.mockImplementation(async (_, part) => {
      if (part === 'package') await new Promise<void>(resolve => { complete = resolve; });
      return session(2, part === 'package' ? 4 : 0);
    });
    const running = store.start();
    await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(2));
    await store.restore('other-task'); await store.start();
    expect(store.packageFile.value?.name).toBe('package.zip');
    expect(store.upload.value?.id).toBe('task-1');
    complete(); await running;
    expect(request.mock.calls.filter(([path]) => path.endsWith('/complete'))).toHaveLength(1);
    expect(store.completedJob.value).toBe('job-1');
    expect(store.packageFile.value).toBeUndefined();
  });
  it('stopping transport retains the session without deleting it', async () => {
    const { request, send, store } = fixture();
    send.mockImplementation((_, __, ___, signal) => new Promise((__, reject) =>
      signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError')))));
    const running = store.start(); await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(1));
    store.stop(); await running;
    expect(store.upload.value?.id).toBe('task-1'); expect(store.busy.value).toBe(false);
    expect(store.manifest.value?.name).toBe('manifest.json');
    expect(request.mock.calls.some(([, method]) => method === 'DELETE')).toBe(false);
    await store.cancel();
    expect(request.mock.calls.some(([, method]) => method === 'DELETE')).toBe(true);
    expect(store.upload.value).toBeUndefined();
  });
  it('session reset rejects late callbacks from the old upload', async () => {
    const { request, send, store } = fixture();
    let complete!: () => void;
    send.mockImplementation(() => new Promise(resolve => { complete = () => resolve(session(2)); }));
    const running = store.start(); await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(1));
    store.reset(); complete(); await running;
    expect(store.hasTask.value).toBe(false); expect(store.completedJob.value).toBe('');
    expect(request.mock.calls.some(([path]) => path.endsWith('/complete'))).toBe(false);
  });
  it('restored sessions only require the file not yet confirmed by the server', async () => {
    const { request, send, store } = fixture(); store.reset();
    request.mockResolvedValue(session(2)); await store.restore('task-1');
    store.pick(new File(['test'], 'package.zip'), 'package');
    expect(store.canStart.value).toBe(true);
    request.mockImplementation(async path => path.endsWith('/complete') ? { jobId: 'job-1' } : session(2));
    await store.start();
    expect(send).toHaveBeenCalledTimes(1); expect(send.mock.calls[0]![1]).toBe('package');
  });
  it('closed sessions cannot restart and can be explicitly cleared for a new task', async () => {
    const { request, store } = fixture(); store.reset();
    request.mockResolvedValue({ ...session(), state: 'expired' }); await store.restore('task-1');
    store.pick(new File(['{}'], 'manifest.json'), 'manifest');
    store.pick(new File(['test'], 'package.zip'), 'package');
    expect(store.canStart.value).toBe(false);
    expect(store.stage.value).toContain('已关闭');
    store.reset(); expect(store.hasTask.value).toBe(false);
  });
});
