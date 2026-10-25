export interface SseFrame { event: string; id?: string; data: string }
export class SseParser {
  private buffer = ''; private event = 'message'; private id?: string; private lines: string[] = []; private size = 0;
  constructor(private receive: (frame: SseFrame) => void) {}
  push(chunk: string) {
    this.buffer += chunk;
    if (this.buffer.length + this.size > 131072) throw new Error('Event frame exceeds its limit.');
    let end: number;
    while ((end = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, end).replace(/\r$/, ''); this.buffer = this.buffer.slice(end + 1);
      if (!line) {
        if (this.lines.length) this.receive({ event: this.event, id: this.id, data: this.lines.join('\n') });
        this.event = 'message'; this.id = undefined; this.lines = []; this.size = 0; continue;
      }
      if (line.startsWith(':')) continue;
      const separator = line.indexOf(':'), field = separator < 0 ? line : line.slice(0, separator);
      const value = separator < 0 ? '' : line.slice(separator + 1).replace(/^ /, '');
      this.size += line.length;
      if (this.size > 131072) throw new Error('Event frame exceeds its limit.');
      if (field === 'event') this.event = value;
      if (field === 'id' && !value.includes('\0')) this.id = value;
      if (field === 'data') this.lines.push(value);
    }
  }
}

export function eventSequence(id: string, job: string): bigint {
  if (!id.startsWith(job + ':') || !/^\d{1,19}$/.test(id.slice(job.length + 1))) throw new Error('Invalid task event identifier.');
  return BigInt(id.slice(job.length + 1));
}
