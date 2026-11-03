<script setup lang="ts">
import { computed, onActivated, onDeactivated, onMounted, onBeforeUnmount } from 'vue';
import { useAuth } from '../state/auth';
import { useServerUpdate } from '../state/serverUpdate';
const auth = useAuth(), store = useServerUpdate();
const { summary, task, logs, busy, sent, total, error, connection } = store;
const active = computed(() => summary.value?.tasks.some(item => !item.terminal || item.state === 'recoveryRequired') ?? false);
let timer: ReturnType<typeof setTimeout> | undefined;
let polling = false;
let pollGeneration = 0;
function start() { if (polling) return; polling = true; void tick(++pollGeneration); }
async function tick(own: number) { await store.refresh(); if (polling && own === pollGeneration) timer = setTimeout(() => { void tick(own); }, 2000); }
function stop() { polling = false; pollGeneration++; clearTimeout(timer); timer = undefined; }
onMounted(start); onActivated(start); onDeactivated(stop); onBeforeUnmount(stop);
function changed(event: Event) { const input = event.target as HTMLInputElement; const file = input.files?.[0]; if (file) void pick(file); input.value = ''; }
async function pick(file: File) { if (await auth.requireStrong()) await store.upload(file); }
function drop(event: DragEvent) { if (!busy.value && !active.value) { const file = event.dataTransfer?.files[0]; if (file) void pick(file); } }
async function apply(id: string) {
  if (!window.confirm('将短暂重建 API、Worker 和 Notifier。确认更新服务器？')) return;
  if (await auth.requireStrong()) await store.action(`/api/v1/server-updates/packages/${id}/apply`);
}
async function rollback(id: string) {
  if (!window.confirm('恢复此任务更新前的应用镜像和 Web，数据库保留当前内容。确认回退？')) return;
  if (await auth.requireStrong()) await store.action(`/api/v1/server-updates/tasks/${id}/rollback`);
}
async function remove(id: string) { if (await auth.requireStrong()) await store.action(`/api/v1/server-updates/packages/${id}`, 'DELETE'); }
const size = (bytes: number) => (Number(bytes) / 1024 / 1024).toFixed(1) + ' MiB';
const stateLabels: Record<string, string> = { starting: '准备中', running: '执行中', succeeded: '已完成', failed: '失败', rolledBack: '已恢复旧版', recoveryRequired: '需要人工恢复' };
const stateLabel = (state: string) => stateLabels[state] ?? '状态未知';
</script>
<template>
  <section class="surface server-update" :aria-busy="busy">
    <div class="section-heading"><div><h2>服务器更新</h2><p class="muted">服务器版本 {{ summary?.currentVersion ?? '正在读取' }} · 与桌面版本发布独立</p></div><button @click="store.refresh">刷新状态</button></div>
    <p v-if="connection" class="notice" role="status">{{ connection }}</p><p v-if="error" class="notice danger" role="alert">{{ error }}</p>
    <p v-if="summary && !summary.enabled" class="notice">尚未启用服务器更新。请按部署说明执行一次性接入脚本。</p>
    <template v-if="summary?.enabled">
      <p class="muted">支持签名的 Linux x64 服务器 ZIP；更新前自动备份，失败自动回退。API 重启期间日志暂时暂停，恢复后续读。</p>
      <div class="server-upload" @dragover.prevent @drop.prevent="drop">
        <label>拖入或选择服务器 ZIP（最大 {{ size(summary.maximumPackageBytes) }}）<input type="file" accept=".zip" :disabled="busy || active" @change="changed"></label>
        <template v-if="busy && total"><progress :value="sent" :max="total" /> <span>{{ size(sent) }} / {{ size(total) }}{{ sent >= total ? ' · 正在校验签名与内容' : '' }}</span></template>
      </div>
      <div v-if="summary.packages.length" class="table-scroll"><table><thead><tr><th>已验证的服务器包</th><th>体积</th><th>操作</th></tr></thead><tbody><tr v-for="item in summary.packages" :key="item.id"><td>{{ item.version }}<small class="muted">{{ item.applicationImage }}</small></td><td>{{ size(item.bytes) }}</td><td><div class="actions"><button class="primary" :disabled="busy || active" @click="apply(item.id)">一键更新</button><button :disabled="busy || active" @click="remove(item.id)">删除暂存包</button></div></td></tr></tbody></table></div>
      <div v-if="task" class="server-task"><h3>{{ task.stage }}</h3><p>版本 {{ task.version }} · {{ stateLabel(task.state) }} · {{ new Date(Number(task.updatedUtcMs)).toLocaleString() }}</p><p v-if="task.error" class="notice danger">错误：{{ task.error }}</p><p v-if="task.state === 'recoveryRequired'" class="notice danger">请通过 SSH 检查更新任务及恢复记录。为保护数据，更新屏障尚未解除。</p><button v-if="task.state === 'succeeded' && !task.rollbackOf" :disabled="busy || active" @click="rollback(task.id)">回退到更新前版本</button><pre class="server-log" aria-label="更新日志"><template v-for="entry in logs" :key="entry.seq">{{ new Date(Number(entry.atUtcMs)).toLocaleTimeString() }} {{ entry.message }}{{ '\n' }}</template></pre></div>
      <details v-if="summary.tasks.length"><summary>最近更新记录</summary><ul><li v-for="item in summary.tasks" :key="item.id">{{ item.version }} · {{ item.stage }} · {{ item.id }}</li></ul></details>
    </template>
  </section>
</template>
<style scoped>
.server-update { display: grid; gap: 18px; }
.server-upload { padding: 24px; border: 1px dashed var(--line); border-radius: 12px; }
.server-upload label { display: grid; gap: 12px; }
.server-upload progress { width: 100%; margin-top: 16px; }
.server-log { max-height: 360px; min-height: 100px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; padding: 16px; background: var(--surface-muted); border-radius: 8px; font-size: 13px; }
small { display: block; overflow-wrap: anywhere; }
</style>
