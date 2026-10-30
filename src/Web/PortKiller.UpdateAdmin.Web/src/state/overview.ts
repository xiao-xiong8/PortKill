import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch, type Ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, ApiError, errorText } from '../api/http';
import { useAuth } from './auth';
import { OverviewChannel } from './overviewPolling';

export type Monitoring = components['schemas']['MonitoringResponse'];

// One bounded memory cache per authenticated session, independent of the route component.
export function createOverviewStore(request = api, monotonic = () => performance.now()) {
  const monitoring = shallowRef<Monitoring>(), current = shallowRef<components['schemas']['CurrentReleaseResponse']>();
  const history = shallowRef<components['schemas']['PageResponseOfJobListItem']>();
  const monitorError = ref(''), currentError = ref(''), historyError = ref('');
  const ticking = ref(0), refreshing = ref(false), failureCount = ref(0);
  const paused = ref(false), forbidden = ref(false);
  const diagnostic = shallowRef<{ at: number; elapsedMs: number; status: number; code: string }>();
  let receivedAt = 0, blockedUntil = 0, active = false, screen = false;
  let timer: ReturnType<typeof setInterval> | undefined;
  const serverNow = computed(() => {
    void ticking.value;
    return monitoring.value ? monitoring.value.servedAtUtcMs + Math.max(0, monotonic() - receivedAt) : Date.now();
  });
  const transport = computed(() => {
    void ticking.value;
    if (paused.value) return 'paused';
    if (monotonic() < blockedUntil) return 'rate_limited';
    if (forbidden.value) return 'forbidden';
    if (failureCount.value >= 3) return 'disconnected';
    if (!monitoring.value) return monitorError.value ? 'delayed' : 'loading';
    if (!monitorError.value && monotonic() - receivedAt >= 25_000) return 'refreshing';
    return monitorError.value ? 'delayed' : 'connected';
  });
  const limited = (until: number) => { blockedUntil = Math.max(blockedUntil, until); ticking.value++; };
  const retrySeconds = computed(() => {
    void ticking.value;
    return Math.max(0, Math.ceil((Math.max(blockedUntil, monitorChannel.retryAt) - monotonic()) / 1000));
  });
  let startedAt = 0;
  const monitorChannel = new OverviewChannel(async signal => {
    refreshing.value = true; startedAt = monotonic();
    try {
      const result = await request<Monitoring>('/api/v1/system/monitoring', 'GET', undefined, signal);
      if (!signal.aborted && active) {
        monitoring.value = result; receivedAt = monotonic(); ticking.value++;
        monitorError.value = ''; failureCount.value = 0;
        forbidden.value = false;
        diagnostic.value = { at: Date.now(), elapsedMs: monotonic() - startedAt, status: 200, code: '' };
      }
    } finally { if (!signal.aborted || !active) refreshing.value = false; }
  }, 5000, error => {
    monitorError.value = errorText(error); refreshing.value = false;
    const status = error instanceof ApiError ? error.status : 0;
    forbidden.value = status === 401 || status === 403;
    if (status !== 429 && !forbidden.value) failureCount.value++;
    diagnostic.value = { at: Date.now(), elapsedMs: monotonic() - startedAt, status,
      code: error instanceof ApiError ? error.code : 'network_error' };
  }, limited, monotonic);
  const currentChannel = new OverviewChannel(async signal => {
    const result = await request<components['schemas']['CurrentReleaseResponse']>('/api/v1/releases/current', 'GET', undefined, signal);
    if (!signal.aborted && active) { current.value = result; currentError.value = ''; }
  }, 60_000, error => { currentError.value = errorText(error); }, limited, monotonic);
  const historyChannel = new OverviewChannel(async signal => {
    const result = await request<components['schemas']['PageResponseOfJobListItem']>('/api/v1/jobs?pageSize=5', 'GET', undefined, signal);
    if (!signal.aborted && active && !screen) { history.value = result; historyError.value = ''; }
  }, 60_000, error => { historyError.value = errorText(error); }, limited, monotonic);
  function refresh(force = true) {
    ticking.value++;
    const now = monotonic();
    if (!active || document.hidden || now < blockedUntil) return;
    void monitorChannel.run(now, force); void currentChannel.run(now, force);
    if (!screen) void historyChannel.run(now, force);
  }
  function cancel() { monitorChannel.cancel(); currentChannel.cancel(); historyChannel.cancel(); refreshing.value = false; }
  function visibility() { paused.value = document.hidden; if (document.hidden) cancel(); else refresh(false); }
  function deactivate() {
    active = false; paused.value = true; clearInterval(timer); timer = undefined; cancel();
    document.removeEventListener('visibilitychange', visibility);
  }
  function activate(display: boolean) {
    screen = display;
    if (active) return;
    active = true; paused.value = document.hidden; refresh(false); timer = setInterval(() => refresh(false), 1000);
    document.addEventListener('visibilitychange', visibility);
  }
  function setScreen(display: boolean) { screen = display; if (display) historyChannel.cancel(); else refresh(false); }
  function reset() {
    cancel(); monitorChannel.reset(); currentChannel.reset(); historyChannel.reset();
    monitoring.value = current.value = history.value = undefined;
    monitorError.value = currentError.value = historyError.value = '';
    blockedUntil = receivedAt = 0; failureCount.value = 0; paused.value = forbidden.value = false;
    diagnostic.value = undefined; ticking.value++;
  }
  return { monitoring, current, history, monitorError, currentError, historyError, serverNow, transport, refreshing,
    diagnostic, retrySeconds, refresh, activate, deactivate, reset, setScreen };
}

const store = createOverviewStore();
const auth = useAuth();
watch(() => {
  const value = auth.session.value;
  return value?.phase === 'full' ? JSON.stringify([value.userId, value.currentSessionId, value.permissions]) : '';
}, () => store.reset(), { flush: 'sync' });

export function useOverview(screen: Ref<boolean>) {
  onMounted(() => store.activate(screen.value));
  onBeforeUnmount(store.deactivate);
  watch(screen, store.setScreen);
  return store;
}
