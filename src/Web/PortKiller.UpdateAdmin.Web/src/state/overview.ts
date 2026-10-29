import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch, type Ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, errorText } from '../api/http';
import { useAuth } from './auth';
import { OverviewChannel } from './overviewPolling';

export type Monitoring = components['schemas']['MonitoringResponse'];

// One bounded memory cache per authenticated session, independent of the route component.
export function createOverviewStore(request = api, monotonic = () => performance.now()) {
  const monitoring = shallowRef<Monitoring>(), current = shallowRef<components['schemas']['CurrentReleaseResponse']>();
  const history = shallowRef<components['schemas']['PageResponseOfJobListItem']>();
  const monitorError = ref(''), currentError = ref(''), historyError = ref('');
  const ticking = ref(0), refreshing = ref(false), failureCount = ref(0);
  let receivedAt = 0, blockedUntil = 0, active = false, screen = false;
  let timer: ReturnType<typeof setInterval> | undefined;
  const serverNow = computed(() => {
    void ticking.value;
    return monitoring.value ? monitoring.value.servedAtUtcMs + Math.max(0, monotonic() - receivedAt) : Date.now();
  });
  const transport = computed(() => {
    void ticking.value;
    if (!monitoring.value) return monitorError.value ? 'disconnected' : 'loading';
    if (failureCount.value >= 3 || monotonic() - receivedAt >= 25_000) return 'disconnected';
    return monitorError.value ? 'delayed' : 'connected';
  });
  const limited = (until: number) => { blockedUntil = Math.max(blockedUntil, until); };
  const monitorChannel = new OverviewChannel(async signal => {
    refreshing.value = true;
    try {
      const result = await request<Monitoring>('/api/v1/system/monitoring', 'GET', undefined, signal);
      if (!signal.aborted && active) {
        monitoring.value = result; receivedAt = monotonic(); ticking.value++;
        monitorError.value = ''; failureCount.value = 0;
      }
    } finally { if (!signal.aborted || !active) refreshing.value = false; }
  }, 5000, error => { monitorError.value = errorText(error); failureCount.value++; refreshing.value = false; }, limited, monotonic);
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
  function visibility() { if (document.hidden) cancel(); else refresh(false); }
  function deactivate() {
    active = false; clearInterval(timer); timer = undefined; cancel();
    document.removeEventListener('visibilitychange', visibility);
  }
  function activate(display: boolean) {
    screen = display;
    if (active) return;
    active = true; refresh(false); timer = setInterval(() => refresh(false), 1000);
    document.addEventListener('visibilitychange', visibility);
  }
  function setScreen(display: boolean) { screen = display; if (display) historyChannel.cancel(); else refresh(false); }
  function reset() {
    cancel(); monitorChannel.reset(); currentChannel.reset(); historyChannel.reset();
    monitoring.value = current.value = history.value = undefined;
    monitorError.value = currentError.value = historyError.value = '';
    blockedUntil = receivedAt = 0; failureCount.value = 0; ticking.value++;
  }
  return { monitoring, current, history, monitorError, currentError, historyError, serverNow, transport, refreshing,
    refresh, activate, deactivate, reset, setScreen };
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
