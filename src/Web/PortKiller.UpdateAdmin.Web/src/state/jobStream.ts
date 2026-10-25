import { onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import type { components } from '../api/contracts.generated';
import { ApiError, errorText, parseJson, readResponse, reportAuthentication } from '../api/http';
import { eventSequence, SseParser } from '../api/sse';
import { newer, terminal } from './jobState';
import { useResource } from './resource';
import { api } from '../api/http';
import { restoreSession } from './auth';
type Snapshot = components['schemas']['JobSnapshotResponse'];
type Event = components['schemas']['JobEventResponse'];
function delay(milliseconds: number, signal: AbortSignal) { return new Promise<void>(resolve => { const finish = () => { clearTimeout(timer); signal.removeEventListener('abort', finish); resolve(); }; const timer = setTimeout(finish, milliseconds); signal.addEventListener('abort', finish, { once: true }); if (signal.aborted) finish(); }); }
export function useJobStream(id: string) {
  const page = useResource(signal => api<components['schemas']['JobEventPageResponse']>(`/api/v1/jobs/${encodeURIComponent(id)}/events?after=${encodeURIComponent(id + ':0')}`, 'GET', undefined, signal));
  const snapshot = shallowRef<Snapshot>(), events = ref<Event[]>([]), connection = ref('正在连接任务'), gap = ref(false);
  let controller: AbortController | undefined;
  watch(page.data, data => {
    controller?.abort(); if (!data) { snapshot.value = undefined; events.value = []; gap.value = false; return; }
    if (newer(snapshot.value, data.snapshot, id)) snapshot.value = data.snapshot;
    const priorEvents = data.reset ? [] : events.value;
    const merged = new Map([...priorEvents, ...data.events].map(item => [item.id, item]));
    events.value = [...merged.values()].sort((left, right) => {
      const a = eventSequence(left.id, id), b = eventSequence(right.id, id);
      return a < b ? -1 : a > b ? 1 : 0;
    }).slice(-500);
    gap.value ||= data.reset;
    const own = controller = new AbortController(); void follow(data.cursor, own.signal);
  });
  async function refresh() { controller?.abort(); await page.load(); }
  async function follow(initial: string, signal: AbortSignal) {
    let cursor = initial, failures = 0;
    while (!signal.aborted) {
      if (snapshot.value && terminal(snapshot.value)) { connection.value = '任务已结束'; return; }
      const attempt = new AbortController(), abort = () => attempt.abort(); signal.addEventListener('abort', abort, { once: true });
      let idle = setTimeout(abort, 15000), reader: ReadableStreamDefaultReader<Uint8Array> | undefined, closed = false;
      let retry = Math.min(30000, 3000 * 2 ** failures);
      try {
        const path = `/api/v1/jobs/${encodeURIComponent(id)}/events/stream`;
        const response = await fetch(path, { credentials: 'same-origin', redirect: 'error', cache: 'no-store', headers: { 'Last-Event-ID': cursor }, signal: attempt.signal });
        if (!response.ok) await readResponse(response);
        if (!response.headers.get('content-type')?.includes('text/event-stream') || !response.body) throw new ApiError(0, 'invalid_stream', '任务连接返回了无法识别的内容。');
        connection.value = '实时连接中'; reader = response.body.getReader(); const decoder = new TextDecoder();
        const parser = new SseParser(frame => {
          if (signal.aborted) return;
          if (frame.event === 'closed') { closed = true; connection.value = '会话或访问权限已变化，请重新确认'; return; }
          if (frame.id) eventSequence(frame.id, id);
          if (frame.event === 'job') {
            const item = parseJson<Event>(frame.data); eventSequence(item.id, id);
            if (item.id !== frame.id || typeof item.stage !== 'string' || typeof item.code !== 'string') throw new Error('Invalid task event.');
            if (!events.value.some(row => row.id === item.id)) events.value = [...events.value, item].slice(-500);
            cursor = item.id;
            failures = 0;
          }
          if (frame.event === 'snapshot' || frame.event === 'reset') {
            const next = parseJson<Snapshot>(frame.data);
            if (newer(snapshot.value, next, id)) snapshot.value = next;
            if (frame.event === 'reset') { events.value = []; gap.value = true; }
            if (frame.id) cursor = frame.id;
            failures = 0;
          }
        });
        while (!signal.aborted && !closed) {
          clearTimeout(idle); idle = setTimeout(abort, 35000);
          const chunk = await reader.read(); if (chunk.done) break;
          parser.push(decoder.decode(chunk.value, { stream: true }));
          if (snapshot.value && terminal(snapshot.value)) break;
        }
        if (closed) { try { await restoreSession(); } catch { /* Explicit retry remains available. */ } return; }
      } catch (cause) {
        if (signal.aborted) return;
        if (cause instanceof ApiError) {
          reportAuthentication(cause, '/api/v1/jobs/' + id);
          if ([400, 401, 403, 404].includes(cause.status)) { connection.value = errorText(cause); return; }
          retry = Math.max(retry, cause.retryAfter * 1000);
          connection.value = cause.retryAfter > 0
            ? `连接受限，${cause.retryAfter} 秒后重试`
            : '连接中断，稍后重连';
        } else {
          connection.value = '连接中断，稍后重连';
        }
      } finally { clearTimeout(idle); abort(); signal.removeEventListener('abort', abort); try { await reader?.cancel(); } catch { /* The transport may already be closed. */ } }
      if (snapshot.value && terminal(snapshot.value)) { connection.value = '任务已结束'; return; }
      if (++failures >= 6) { connection.value = '连接多次失败，请手动刷新任务'; return; }
      await delay(retry, signal);
    }
  }
  onBeforeUnmount(() => controller?.abort());
  return { ...page, load: refresh, snapshot, events, connection, gap };
}
