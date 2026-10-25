<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, errorText } from '../api/http';
import { time } from '../api/display';
import PageControls from '../components/PageControls.vue';
import { redact } from './securityModel';

type AuditPageResponse = components['schemas']['PageResponseOfAuditEvent'];
const page = ref(1), action = ref(''), appliedAction = ref('');
const data = ref<AuditPageResponse>(), busy = ref(false), error = ref('');
let controller: AbortController | undefined;

function requestPath() {
  const query = new URLSearchParams({ page: String(page.value), pageSize: '20' });
  if (appliedAction.value) query.set('action', appliedAction.value);
  return `/api/v1/audit?${query.toString()}`;
}

async function load() {
  controller?.abort(); const own = controller = new AbortController(); busy.value = true; error.value = '';
  try {
    const result = await api<AuditPageResponse>(requestPath(), 'GET', undefined, own.signal);
    if (!own.signal.aborted) data.value = result;
  } catch (cause) { if (!own.signal.aborted) { data.value = undefined; error.value = errorText(cause); } }
  finally { if (!own.signal.aborted) busy.value = false; }
}

function applyFilter() { appliedAction.value = action.value.trim(); page.value = 1; void load(); }
function changePage(value: number) { page.value = value; void load(); }
onMounted(load);
onBeforeUnmount(() => controller?.abort());
</script>

<template>
  <section class="surface" :aria-busy="busy">
    <div class="section-heading"><div><h2>审计记录</h2><p class="muted">只展示安全操作的必要元数据；请求正文、查询参数、密码、动态码和恢复码不会进入审计记录。</p></div><button :disabled="busy" @click="load">刷新</button></div>
    <form class="inline-fields audit-filter" @submit.prevent="applyFilter"><label>操作筛选<span class="muted">可填写 action 标识，例如 account.roles_changed</span><input v-model="action" maxlength="80" pattern="[A-Za-z0-9._-]{1,80}" placeholder="全部操作"></label><button class="primary" :disabled="busy">筛选</button><button type="button" :disabled="busy && !data" @click="action = ''; applyFilter()">清空</button></form>
    <p v-if="error" class="notice danger" role="alert">{{ error }}</p><p v-else-if="busy" class="muted" role="status">正在加载审计记录…</p>
    <div v-else-if="data?.items.length" class="table-scroll"><table><thead><tr><th>时间</th><th>操作</th><th>结果</th><th>操作者</th><th>资源</th><th>关联 ID</th></tr></thead><tbody><tr v-for="event in data.items" :key="String(event.id)"><td>{{ time(event.createdUtcMs) }}</td><td class="mono">{{ event.action ?? '—' }}</td><td>{{ event.result ?? '—' }}</td><td class="mono">{{ redact(event.actorUserId) }}</td><td class="mono">{{ redact(event.resourceId) }}</td><td class="mono">{{ redact(event.correlationId) }}</td></tr></tbody></table></div>
    <div v-else-if="!busy" class="empty-workspace"><h3>暂无审计记录</h3><p class="muted">安全操作完成后，必要的结果元数据会显示在这里。</p></div>
    <PageControls :page="page" :total="data?.total" :busy="busy" @change="changePage" />
  </section>
</template>
