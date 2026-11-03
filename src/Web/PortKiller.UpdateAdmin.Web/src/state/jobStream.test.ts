import { describe, expect, it, vi } from 'vitest';
import { advanceSessionEpoch, onSessionEpochChanged } from '../api/sessionEpoch';
import type { components } from '../api/contracts.generated';
import { newer, terminalPublication, terminalValidation } from './jobState';

type Snapshot = components['schemas']['JobSnapshotResponse'];
function publish(id: string, sequence: string, state: components['schemas']['PublishJobState']): Snapshot {
  return {
    id, kind: 'publish', eventId: `${id}:${sequence}`, observedUtcMs: '100', validation: null,
    publication: { id, draftId: 'draft', application: 'PortKill', channel: 'stable', state, version: '1.0.0',
      sequence: '1', manifestSha256: 'A'.repeat(64), packageSha256: 'B'.repeat(64), expectedCurrentManifestSha256: 'none', createdUtcMs: '1' },
  };
}

describe('task stream snapshot ordering', () => {
  it('aborts a subscribed stream on identity change and releases its listener', () => {
    const abort = vi.fn();
    const unsubscribe = onSessionEpochChanged(abort);
    advanceSessionEpoch();
    expect(abort).toHaveBeenCalledTimes(1);
    unsubscribe();
    advanceSessionEpoch();
    expect(abort).toHaveBeenCalledTimes(1);
  });
  it('compares 64-bit event sequences without numeric rounding', () => {
    const current = publish('job', '9223372036854775806', 'publishing');
    expect(newer(current, publish('job', '9223372036854775807', 'completed'), 'job')).toBe(true);
    expect(newer(current, publish('job', '9223372036854775805', 'queued'), 'job')).toBe(false);
    expect(newer(current, publish('other', '9223372036854775807', 'completed'), 'job')).toBe(false);
  });

  it('treats recoverable active states as terminal and leaves queued work connected', () => {
    expect(terminalPublication('activeUnverified')).toBe(true);
    expect(terminalPublication('conflict')).toBe(true);
    expect(terminalPublication('superseded')).toBe(true);
    expect(terminalPublication('publicVerifying')).toBe(false);
    expect(terminalValidation('readyForApproval', 'completed')).toBe(true);
  });
});
