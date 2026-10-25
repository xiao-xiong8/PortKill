import type { components } from '../api/contracts.generated';
import { eventSequence } from '../api/sse';

type Snapshot = components['schemas']['JobSnapshotResponse'];
const terminalPublishStates = ['completed', 'activeUnverified', 'failed', 'conflict', 'superseded'];
const terminalValidationStates = ['readyForApproval', 'rejected'];

export function terminalValidation(state: components['schemas']['ValidationState'], jobState: components['schemas']['PublishJobState']) {
  return terminalValidationStates.includes(state) || terminalPublishStates.includes(jobState);
}

export function terminalPublication(state: components['schemas']['PublishJobState']) {
  return terminalPublishStates.includes(state);
}

export function terminal(snapshot: Snapshot): boolean {
  if (snapshot.validation) return terminalValidation(snapshot.validation.validationState ?? 'pending', snapshot.validation.state);
  return !!snapshot.publication && terminalPublication(snapshot.publication.state);
}

export function newer(current: Snapshot | undefined, next: Snapshot, id: string) {
  if (next.id !== id) return false;
  const nextSequence = eventSequence(next.eventId, id);
  if (!current) return true;
  const previousSequence = eventSequence(current.eventId, id);
  return nextSequence > previousSequence || nextSequence === previousSequence
    && Number(next.observedUtcMs) >= Number(current.observedUtcMs);
}
