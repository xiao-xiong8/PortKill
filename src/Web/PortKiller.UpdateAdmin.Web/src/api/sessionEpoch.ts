// Guards all transports against responses from a previous browser authentication state.
let epoch = 0;
const listeners = new Set<() => void>();
export function sessionEpoch() { return epoch; }
export function advanceSessionEpoch() {
  epoch++;
  for (const listener of [...listeners]) listener();
  return epoch;
}
export function onSessionEpochChanged(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}
const channel = typeof window !== 'undefined' && window.location && typeof BroadcastChannel !== 'undefined'
  ? new BroadcastChannel('portkill-admin-session') : undefined;
export function authenticationChanged() {
  advanceSessionEpoch();
  if (typeof window !== 'undefined') window.dispatchEvent(new Event('admin:authentication-changed'));
  channel?.postMessage('changed'); // No identity, cookie, or token crosses the channel.
}
channel?.addEventListener('message', event => {
  if (event.data !== 'changed') return;
  advanceSessionEpoch();
  window.dispatchEvent(new Event('admin:authentication-remote'));
});
