import { afterEach, describe, expect, it, vi } from 'vitest';
import { OverviewChannel } from './overviewPolling';
import { ApiError } from '../api/http';

afterEach(() => vi.useRealTimers());
describe('overview GET channels', () => {
  it('does not overlap requests or poll before the due time', async () => {
    vi.useFakeTimers(); vi.setSystemTime(1000);
    let complete: (() => void) | undefined;
    const loader = vi.fn(() => new Promise<void>(resolve => { complete = resolve; }));
    const channel = new OverviewChannel(loader, 10_000, vi.fn(), vi.fn(), Date.now);
    const running = channel.run(1000);
    await channel.run(1000, true);
    expect(loader).toHaveBeenCalledTimes(1);
    complete?.(); await running;
    await channel.run(10_999);
    expect(loader).toHaveBeenCalledTimes(1);
  });
  it('cancels on leave and uses at least the polling interval for unspecified 429', async () => {
    vi.useFakeTimers(); vi.setSystemTime(1000);
    const limited = vi.fn();
    const rate = new OverviewChannel(async () => { throw new ApiError(429, 'limited', 'limited'); }, 10_000, vi.fn(), limited, Date.now);
    await rate.run(1000);
    expect(limited).toHaveBeenCalledWith(11_000);
    let observed: AbortSignal | undefined;
    const channel = new OverviewChannel(async signal => { observed = signal; }, 10_000, vi.fn(), vi.fn());
    const running = channel.run(1000); channel.cancel(); await running;
    expect(observed?.aborted).toBe(true);
  });
  it('schedules from request start instead of adding response time to every interval', async () => {
    vi.useFakeTimers(); vi.setSystemTime(1000);
    const loader = vi.fn(async () => { vi.setSystemTime(3000); });
    const channel = new OverviewChannel(loader, 5000, vi.fn(), vi.fn(), Date.now);
    await channel.run(1000);
    expect(channel.nextAt).toBe(6000);
  });
  it('does not turn an intentional cancellation into an error and ignores late completion', async () => {
    vi.useFakeTimers();
    const failed = vi.fn();
    const channel = new OverviewChannel(signal => new Promise((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError')))), 5000, failed, vi.fn());
    const running = channel.run(0); channel.cancel(); await running;
    expect(failed).not.toHaveBeenCalled();
  });
  it('enforces retry-after even for forced manual refresh', async () => {
    vi.useFakeTimers(); vi.setSystemTime(1000);
    const load = vi.fn(async () => { throw new ApiError(429, 'limited', 'limited', 60); });
    const channel = new OverviewChannel(load, 5000, vi.fn(), vi.fn(), Date.now);
    await channel.run(1000); vi.setSystemTime(30_000); await channel.run(30_000, true);
    expect(load).toHaveBeenCalledTimes(1);
    vi.setSystemTime(61_000); await channel.run(61_000, true); expect(load).toHaveBeenCalledTimes(2);
  });
  it('ignores a late timeout rejection after the session channel has been reset', async () => {
    vi.useFakeTimers(); vi.setSystemTime(1000);
    let reject!: (error: unknown) => void;
    const failed = vi.fn();
    const channel = new OverviewChannel(() => new Promise((_, fail) => { reject = fail; }), 5000, failed, vi.fn(), Date.now);
    const running = channel.run(1000); await vi.advanceTimersByTimeAsync(8000);
    channel.reset(); reject(new DOMException('', 'AbortError')); await running;
    expect(failed).not.toHaveBeenCalled(); expect(channel.nextAt).toBe(0);
  });
  it('reports timeouts and backs off without overlapping the next request', async () => {
    vi.useFakeTimers(); vi.setSystemTime(1000);
    const failed = vi.fn();
    const channel = new OverviewChannel(signal => new Promise((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError')))), 5000, failed, vi.fn(), Date.now);
    const running = channel.run(1000); await vi.advanceTimersByTimeAsync(8000); await running;
    expect(failed).toHaveBeenCalledWith(expect.objectContaining({ code: 'timeout' }));
    await channel.run(10_000, true); expect(failed).toHaveBeenCalledTimes(1);
  });
});
