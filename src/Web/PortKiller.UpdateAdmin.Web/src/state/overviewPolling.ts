import { ApiError } from '../api/http';

/** A bounded GET channel. All deadlines use the same monotonic clock. */
export class OverviewChannel {
  private controller?: AbortController;
  retryAt = 0;
  private failures = 0;
  private lastStarted = -Infinity;
  nextAt = 0;
  constructor(private load: (signal: AbortSignal) => Promise<void>, private interval: number,
    private failed: (error: unknown) => void, private rateLimited: (until: number) => void,
    private clock = () => performance.now()) {}
  async run(now: number, force = false) {
    if (this.controller || now < this.retryAt || now - this.lastStarted < 1000 || !force && now < this.nextAt) return;
    this.lastStarted = now;
    const controller = this.controller = new AbortController();
    const timeout = setTimeout(() => controller.abort('timeout'), 8000);
    try {
      await this.load(controller.signal);
      if (!controller.signal.aborted) { this.failures = 0; this.retryAt = 0; }
    } catch (error) {
      if (this.controller !== controller) return;
      if (controller.signal.aborted && controller.signal.reason !== 'timeout') return;
      this.failures++;
      this.retryAt = this.clock() + Math.min(30_000, 5000 * 2 ** Math.min(3, this.failures - 1));
      if (error instanceof ApiError && error.status === 429) {
        this.retryAt = this.clock() + Math.max(this.interval, error.retryAfter * 1000);
        this.rateLimited(this.retryAt);
      }
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) this.retryAt = Infinity;
      this.failed(controller.signal.reason === 'timeout' ? new ApiError(0, 'timeout', '监控请求超时，正在重试。') : error);
    } finally {
      clearTimeout(timeout);
      if (this.controller === controller) {
        this.nextAt = Math.max(now + this.interval, this.clock());
        this.controller = undefined;
      }
    }
  }
  cancel() { this.controller?.abort('inactive'); this.controller = undefined; }
  reset() { this.cancel(); this.nextAt = this.retryAt = this.failures = 0; this.lastStarted = -Infinity; }
}
