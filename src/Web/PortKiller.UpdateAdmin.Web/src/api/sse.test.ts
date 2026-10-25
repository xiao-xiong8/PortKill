import { describe, expect, it } from 'vitest';
import { eventSequence, SseParser, type SseFrame } from './sse';
describe('bounded management SSE parsing', () => {
  it('handles split CRLF frames, multiline data and snapshots without advancing an ID', () => { const events: SseFrame[] = []; const parser = new SseParser(event => events.push(event)); parser.push(': heartbeat\r\nevent: job\r\nid: task:9223372036854775806\r\nda'); parser.push('ta: first\r\ndata: second\r\n\r\nevent: snapshot\ndata: {}\n\n'); expect(events).toEqual([{ event: 'job', id: 'task:9223372036854775806', data: 'first\nsecond' }, { event: 'snapshot', id: undefined, data: '{}' }]); expect(eventSequence(events[0].id!, 'task')).toBe(9223372036854775806n); });
  it('rejects another job cursor and unbounded frames', () => { expect(() => eventSequence('foreign:1', 'task')).toThrow(); expect(() => new SseParser(() => {}).push('x'.repeat(131073))).toThrow(); });
  it('does not dispatch incomplete frames or heartbeat comments', () => { const events: SseFrame[] = []; const parser = new SseParser(event => events.push(event)); parser.push(': heartbeat\n\nevent: job\ndata: partial'); expect(events).toEqual([]); });
});
