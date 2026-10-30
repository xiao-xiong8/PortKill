<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, ApiError, errorText } from '../api/http';
import { time } from '../api/display';
import { useAuth } from '../state/auth';
import PageControls from '../components/PageControls.vue';
import { compareVersion, reportResult } from './clientsModel';

type Client = components['schemas']['ClientInstallationResponse'];
type ClientPage = components['schemas']['PageResponseOfClientInstallationResponse'];
const auth = useAuth();
const page = ref(1), machineName = ref(''), version = ref(''), result = ref(''), revoked = ref('');
const reportedAfter = ref(''), reportedBefore = ref('');
const data = ref<ClientPage>(), currentVersion = ref<string>(), loading = ref(false), revoking = ref(false), error = ref('');
const currentError = ref(''), lastLoaded = ref(0);
const busy = computed(() => loading.value || revoking.value);
const confirming = ref<Client>();
let controller: AbortController | undefined, currentController: AbortController | undefined;
let timer: ReturnType<typeof setInterval> | undefined;
let applied = new URLSearchParams();
let nextAt = 0, currentAt = 0, blockedUntil = 0, failures = 0, disposed = false;
function delayFor(cause: unknown) {
  if (cause instanceof ApiError && cause.status === 429)
    blockedUntil = Math.max(blockedUntil, performance.now() + Math.max(5, cause.retryAfter) * 1000);
  if (cause instanceof ApiError && (cause.status === 401 || cause.status === 403)) blockedUntil = Infinity;
}
async function loadCurrent(force = false) {
  if (currentController || disposed || document.hidden || performance.now() < blockedUntil || !force && performance.now() < currentAt) return;
  const own = currentController = new AbortController();
  const timeout = setTimeout(() => own.abort('timeout'), 8000);
  try {
    const result = await api<components['schemas']['CurrentReleaseResponse']>('/api/v1/releases/current', 'GET', undefined, own.signal);
    if (currentController === own && !own.signal.aborted) { currentVersion.value = result.release?.version; currentError.value = ''; }
  } catch (cause) {
    if (currentController === own && (!own.signal.aborted || own.signal.reason === 'timeout')) {
      currentError.value = errorText(cause); delayFor(cause);
    }
  } finally {
    clearTimeout(timeout);
    if (currentController === own) { currentController = undefined; currentAt = performance.now() + 60_000; }
  }
}
async function load(replace = false) {
  if (disposed || document.hidden || revoking.value || performance.now() < blockedUntil) return;
  if (controller && !replace) return;
  controller?.abort('replaced');
  const own = controller = new AbortController(); loading.value = true;
  const timeout = setTimeout(() => own.abort('timeout'), 8000);
  const query = new URLSearchParams(applied); query.set('page', String(page.value)); query.set('pageSize', '20');
  void loadCurrent();
  try {
    const clients = await api<ClientPage>(`/api/v1/client-installations?${query}`, 'GET', undefined, own.signal);
    if (controller === own && !own.signal.aborted) {
      data.value = clients; error.value = ''; lastLoaded.value = Date.now(); failures = 0;
    }
  } catch (cause) {
    if (controller === own && (!own.signal.aborted || own.signal.reason === 'timeout')) {
      error.value = own.signal.reason === 'timeout' ? '客户端列表读取超时，正在重试。' : errorText(cause);
      failures++; delayFor(cause);
    }
  } finally {
    clearTimeout(timeout);
    if (controller === own) {
      controller = undefined; loading.value = false;
      nextAt = performance.now() + Math.min(30_000, 5000 * 2 ** Math.min(3, failures));
    }
  }
}
function refresh() { void load(); void loadCurrent(true); }
function cancelReads() {
  controller?.abort('inactive'); currentController?.abort('inactive');
  controller = currentController = undefined; loading.value = false;
}
function visibility() { if (document.hidden) cancelReads(); else refresh(); }
function applyFilter() {
  applied = new URLSearchParams({ includeRevoked: 'true' });
  for (const [key, value] of Object.entries({ machineName: machineName.value.trim(), version: version.value.trim(), result: result.value, revoked: revoked.value }))
    if (value) applied.set(key, value);
  if (reportedAfter.value) applied.set('reportedAfterUtcMs', String(new Date(reportedAfter.value).getTime()));
  if (reportedBefore.value) applied.set('reportedBeforeUtcMs', String(new Date(reportedBefore.value).getTime()));
  page.value = 1; data.value = undefined; void load(true);
}
function reset() { machineName.value = version.value = result.value = revoked.value = reportedAfter.value = reportedBefore.value = ''; applyFilter(); }
function changePage(value: number) { page.value = value; data.value = undefined; void load(true); }
async function revoke() {
  const item = confirming.value;
  if (!item || !await auth.requireStrong()) return;
  revoking.value = true; error.value = '';
  try { await api(`/api/v1/client-installations/${encodeURIComponent(item.revocationId)}/revoke`, 'POST', {}); confirming.value = undefined; revoking.value = false; await load(true); }
  catch (cause) { error.value = errorText(cause); }
  finally { revoking.value = false; }
}
onMounted(() => {
  applyFilter();
  timer = setInterval(() => { if (performance.now() >= nextAt) void load(); }, 1000);
  document.addEventListener('visibilitychange', visibility); window.addEventListener('focus', visibility);
});
onBeforeUnmount(() => {
  disposed = true; clearInterval(timer); cancelReads();
  document.removeEventListener('visibilitychange', visibility); window.removeEventListener('focus', visibility);
});
</script>

<template>
  <section class="surface" :aria-busy="busy">
    <div class="section-heading"><div><h2>最近上报记录</h2><p class="muted">显示每个安装实例最近一次上报的版本。客户端在启动和更新完成后上报。</p><p class="muted">当前正式版本：{{ currentError ? '暂不可用' : currentVersion ?? '尚未发布' }}</p></div><button :disabled="busy" @click="refresh">刷新</button></div>
    <form class="inline-fields" @submit.prevent="applyFilter">
      <label>机器名<input v-model="machineName" maxlength="128" placeholder="包含机器名"></label>
      <label>客户端版本<input v-model="version" maxlength="32" pattern="[0-9]+\.[0-9]+(\.[0-9]+){0,2}" placeholder="例如 1.2.3"></label>
      <label>最近结果<select v-model="result"><option value="">全部</option><option value="started">已启动</option><option value="installed">安装成功</option><option value="rolled_back">已回滚</option><option value="failed">更新启动失败</option></select></label>
      <label>撤销状态<select v-model="revoked"><option value="">全部</option><option value="false">未撤销</option><option value="true">已撤销</option></select></label>
      <label>上报时间从<input v-model="reportedAfter" type="datetime-local"></label><label>至<input v-model="reportedBefore" type="datetime-local"></label>
      <button class="primary" :disabled="busy">筛选</button><button type="button" :disabled="busy" @click="reset">清空</button>
    </form>
    <p v-if="currentError" class="notice" role="alert">正式版本暂不可用：{{ currentError }}；客户端记录独立更新。</p>
    <p v-if="error" class="notice danger" role="alert">{{ error }}<span v-if="data"> 已保留最近成功读取的记录。</span></p>
    <p v-if="busy" class="muted" role="status">正在读取最近一次上报…</p>
    <p v-if="lastLoaded" class="muted">最近刷新 {{ time(lastLoaded) }} · 页面可见时每 5 秒自动更新</p>
    <div v-if="data?.items.length" class="table-scroll"><table aria-label="客户端最近一次上报"><thead><tr><th scope="col">机器名</th><th scope="col">安装实例</th><th scope="col">当前版本</th><th scope="col">最近一次上报</th><th scope="col">最近更新结果</th><th scope="col">与正式版本比较</th><th scope="col">凭据状态</th><th v-if="auth.can('accounts.manage')" scope="col">操作</th></tr></thead><tbody>
      <tr v-for="item in data.items" :key="item.revocationId"><td>{{ item.machineName }}</td><td class="mono">{{ item.installationId }}</td><td>{{ item.clientVersion }}</td><td>{{ time(item.lastReportedUtcMs) }}</td><td>{{ reportResult(item.lastReportResult) }}</td><td>{{ currentError ? '未知（正式版本不可用）' : compareVersion(item.clientVersion, currentVersion) }}</td><td>{{ item.isRevoked ? '已撤销' : '未撤销' }}</td><td v-if="auth.can('accounts.manage')"><button :disabled="item.isRevoked || busy" @click="confirming = item">撤销凭据</button></td></tr>
    </tbody></table></div>
    <p v-else-if="!busy && !error" class="empty-workspace">暂无符合条件的最近上报记录。</p>
    <PageControls :page="page" :total="data?.total" :busy="busy" @change="changePage" />
    <div v-if="confirming" class="notice" role="alert"><p>确认撤销 {{ confirming.machineName }}（{{ confirming.installationId }}）的凭据？客户端下次上报会自动登记为新的安装实例。</p><button :disabled="busy" @click="revoke">确认撤销</button><button :disabled="busy" @click="confirming = undefined">取消</button></div>
  </section>
</template>
