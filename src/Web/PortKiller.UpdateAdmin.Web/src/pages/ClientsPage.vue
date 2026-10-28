<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, errorText } from '../api/http';
import { time } from '../api/display';
import { useAuth } from '../state/auth';
import PageControls from '../components/PageControls.vue';
import { compareVersion, reportResult } from './clientsModel';

type Client = components['schemas']['ClientInstallationResponse'];
type ClientPage = components['schemas']['PageResponseOfClientInstallationResponse'];
const auth = useAuth();
const page = ref(1), machineName = ref(''), version = ref(''), result = ref(''), revoked = ref('');
const reportedAfter = ref(''), reportedBefore = ref('');
const data = ref<ClientPage>(), currentVersion = ref<string>(), busy = ref(false), error = ref('');
const confirming = ref<Client>();
let controller: AbortController | undefined;
let applied = new URLSearchParams();

async function load() {
  controller?.abort(); const own = controller = new AbortController(); busy.value = true; error.value = '';
  const query = new URLSearchParams(applied); query.set('page', String(page.value)); query.set('pageSize', '20');
  try {
    const [clients, current] = await Promise.all([
      api<ClientPage>(`/api/v1/client-installations?${query}`, 'GET', undefined, own.signal),
      api<components['schemas']['CurrentReleaseResponse']>('/api/v1/releases/current', 'GET', undefined, own.signal),
    ]);
    if (!own.signal.aborted) { data.value = clients; currentVersion.value = current.release?.version; }
  } catch (cause) { if (!own.signal.aborted) { data.value = undefined; error.value = errorText(cause); } }
  finally { if (!own.signal.aborted) busy.value = false; }
}
function applyFilter() {
  applied = new URLSearchParams({ includeRevoked: 'true' });
  for (const [key, value] of Object.entries({ machineName: machineName.value.trim(), version: version.value.trim(), result: result.value, revoked: revoked.value }))
    if (value) applied.set(key, value);
  if (reportedAfter.value) applied.set('reportedAfterUtcMs', String(new Date(reportedAfter.value).getTime()));
  if (reportedBefore.value) applied.set('reportedBeforeUtcMs', String(new Date(reportedBefore.value).getTime()));
  page.value = 1; void load();
}
function reset() { machineName.value = version.value = result.value = revoked.value = reportedAfter.value = reportedBefore.value = ''; applyFilter(); }
function changePage(value: number) { page.value = value; void load(); }
async function revoke() {
  const item = confirming.value;
  if (!item || !await auth.requireStrong()) return;
  busy.value = true; error.value = '';
  try { await api(`/api/v1/client-installations/${encodeURIComponent(item.revocationId)}/revoke`, 'POST', {}); confirming.value = undefined; await load(); }
  catch (cause) { error.value = errorText(cause); }
  finally { busy.value = false; }
}
onMounted(applyFilter);
onBeforeUnmount(() => controller?.abort());
</script>

<template>
  <section class="surface" :aria-busy="busy">
    <div class="section-heading"><div><h2>客户端版本</h2><p class="muted">显示每个安装实例最近一次上报的版本。客户端在启动和更新完成后上报。</p><p class="muted">当前正式版本：{{ currentVersion ?? '尚未发布' }}</p></div><button :disabled="busy" @click="load">刷新</button></div>
    <form class="inline-fields" @submit.prevent="applyFilter">
      <label>机器名<input v-model="machineName" maxlength="128" placeholder="包含机器名"></label>
      <label>客户端版本<input v-model="version" maxlength="32" pattern="[0-9]+\.[0-9]+(\.[0-9]+){0,2}" placeholder="例如 1.2.3"></label>
      <label>最近结果<select v-model="result"><option value="">全部</option><option value="started">已启动</option><option value="installed">安装成功</option><option value="rolled_back">已回滚</option><option value="failed">更新启动失败</option></select></label>
      <label>撤销状态<select v-model="revoked"><option value="">全部</option><option value="false">未撤销</option><option value="true">已撤销</option></select></label>
      <label>上报时间从<input v-model="reportedAfter" type="datetime-local"></label><label>至<input v-model="reportedBefore" type="datetime-local"></label>
      <button class="primary" :disabled="busy">筛选</button><button type="button" :disabled="busy" @click="reset">清空</button>
    </form>
    <p v-if="error" class="notice danger" role="alert">{{ error }}</p><p v-else-if="busy" class="muted" role="status">正在读取最近一次上报…</p>
    <div v-else-if="data?.items.length" class="table-scroll"><table aria-label="客户端最近一次上报"><thead><tr><th scope="col">机器名</th><th scope="col">安装实例</th><th scope="col">当前版本</th><th scope="col">最近一次上报</th><th scope="col">最近更新结果</th><th scope="col">与正式版本比较</th><th scope="col">凭据状态</th><th v-if="auth.can('accounts.manage')" scope="col">操作</th></tr></thead><tbody>
      <tr v-for="item in data.items" :key="item.revocationId"><td>{{ item.machineName }}</td><td class="mono">{{ item.installationId }}</td><td>{{ item.clientVersion }}</td><td>{{ time(item.lastReportedUtcMs) }}</td><td>{{ reportResult(item.lastReportResult) }}</td><td>{{ compareVersion(item.clientVersion, currentVersion) }}</td><td>{{ item.isRevoked ? '已撤销' : '未撤销' }}</td><td v-if="auth.can('accounts.manage')"><button :disabled="item.isRevoked || busy" @click="confirming = item">撤销凭据</button></td></tr>
    </tbody></table></div>
    <p v-else-if="!busy" class="empty-workspace">暂无符合条件的最近上报记录。</p>
    <PageControls :page="page" :total="data?.total" :busy="busy" @change="changePage" />
    <div v-if="confirming" class="notice" role="alert"><p>确认撤销 {{ confirming.machineName }}（{{ confirming.installationId }}）的凭据？客户端下次上报会自动登记为新的安装实例。</p><button :disabled="busy" @click="revoke">确认撤销</button><button :disabled="busy" @click="confirming = undefined">取消</button></div>
  </section>
</template>
