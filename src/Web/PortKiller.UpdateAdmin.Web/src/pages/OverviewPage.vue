<script setup lang="ts">
import { computed, defineAsyncComponent } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { status, time, value } from '../api/display';
import { useAuth } from '../state/auth';
import { useOverview } from '../state/overview';
import { alertObservation, bytes, fresh, freshness, label } from '../components/monitoring/display';
const ResourceChart = defineAsyncComponent(() => import('../components/monitoring/ResourceChart.vue'));
import JobTable from '../components/JobTable.vue';

const route = useRoute(), router = useRouter(), auth = useAuth();
const screen = computed(() => route.query.display === 'screen');
const { monitoring, current, history, monitorError, currentError, historyError, serverNow, transport, refreshing, refresh } = useOverview(screen);
const snapshot = computed(() => monitoring.value?.snapshot);
const resources = computed(() => snapshot.value?.resources);
const policy = computed(() => freshness(monitoring.value));
const usable = (observed: number | null | undefined, error: string | null | undefined, ttl: number) =>
  fresh(observed, error, ttl, snapshot.value?.generatedAtUtcMs, serverNow.value);
const cpu = computed(() => usable(resources.value?.cpu.observedAtUtcMs, resources.value?.cpu.error, policy.value.resourceTtlMs) ? resources.value?.cpu.percent : null);
const memory = computed(() => usable(resources.value?.memory.observedAtUtcMs, resources.value?.memory.error, policy.value.resourceTtlMs) ? resources.value?.memory : null);
const network = computed(() => usable(resources.value?.network.observedAtUtcMs, resources.value?.network.error, policy.value.resourceTtlMs) ? resources.value?.network : null);
const dockerFresh = computed(() => usable(snapshot.value?.docker.observedAtUtcMs, snapshot.value?.docker.error, policy.value.dockerTtlMs));
const agedSnapshot = computed(() => snapshot.value && serverNow.value - snapshot.value.generatedAtUtcMs >= policy.value.collectorTtlMs);
const overall = computed(() => transport.value !== 'connected' ? transport.value
  : monitoring.value?.status === 'stale' ? 'stale' : agedSnapshot.value ? 'delayed' : monitoring.value?.status);
const chartNow = computed(() => Math.max(Math.floor(serverNow.value / 5000) * 5000, snapshot.value?.series.at(-1)?.timestampUtcMs ?? 0));
const alertRows = computed(() => {
  const response = monitoring.value;
  return response?.alerts.map(alert => alertObservation(response, alert, serverNow.value)) ?? [];
});
const currentAlerts = computed(() => alertRows.value.filter(row => !row.expired));
const previousAlerts = computed(() => alertRows.value.filter(row => row.expired));
const alertTitle = computed(() => currentAlerts.value.some(row => row.alert.severity === 'critical') ? '存在业务异常' : '当前异常提醒');
function container(name: string) { return dockerFresh.value ? snapshot.value?.docker.services.find(service => service.service === name) : undefined; }
function checkedAt(name: string) { return name === 'worker' ? snapshot.value?.docker.observedAtUtcMs
  : snapshot.value?.appChecks.find(probe => probe.name === (name === 'caddy' ? 'download' : name))?.observedAtUtcMs; }
function application(name: string) {
  if (name === 'worker') return '未检查（未配置业务探针）';
  const probe = snapshot.value?.appChecks.find(probe => probe.name === (name === 'caddy' ? 'download' : name));
  if (!probe || !usable(probe.observedAtUtcMs, probe.error, policy.value.probeTtlMs)) return '未知';
  if (name === 'notifier' && probe.status === 'healthy') return `服务正常 · ${probe.manifestValid === true ? '清单有效' : '未加载有效清单'}`;
  if (name === 'caddy' && probe.status === 'healthy') return '清单入口可达（结构有效）';
  return label(probe.status);
}
async function toggleScreen() {
  if (!screen.value) {
    try { await document.documentElement.requestFullscreen?.(); } catch { /* Page layout still works without browser fullscreen. */ }
    await router.replace({ path: '/overview', query: { ...route.query, display: 'screen' } });
  } else {
    if (document.fullscreenElement) { try { await document.exitFullscreen(); } catch { /* Keep navigation usable. */ } }
    const query = { ...route.query }; delete query.display;
    await router.replace({ path: '/overview', query });
  }
}
</script>
<template>
  <div class="monitor-overview">
    <section class="surface monitor-toolbar">
      <div><strong>{{ label(overall) }}</strong><p class="muted">宿主机资源 · 最近采样 {{ time(snapshot?.generatedAtUtcMs) }}</p><p v-if="monitoring" class="muted">最近响应 {{ time(monitoring.servedAtUtcMs) }}<span v-if="refreshing"> · 正在更新</span></p></div>
      <div class="actions"><button :disabled="refreshing" @click="refresh()">刷新总览</button><button @click="toggleScreen">{{ screen ? '退出大屏' : '进入大屏' }}</button></div>
    </section>
    <p v-if="transport === 'disconnected'" class="notice danger" role="alert">与监控接口的连接中断，无法确认最新采集状态。已保留最近结果，过期指标显示未知。</p>
    <p v-else-if="monitorError || overall === 'delayed'" class="notice" role="status">监控更新延迟，正在重试。已保留最近结果，过期指标显示未知。</p>
    <p v-if="overall === 'unconfigured'" class="notice">未启用宿主机监控。正式版本与任务仍可正常使用。</p>
    <p v-else-if="overall === 'stale'" class="notice danger" role="alert">采集器快照已停止更新，上次采样 {{ time(snapshot?.generatedAtUtcMs) }}。各项状态按最近有效检查时间显示。</p>
    <p v-else-if="overall === 'invalid_snapshot' || overall === 'unavailable'" class="notice danger" role="alert">监控快照不可用，请检查采集容器。</p>
    <section v-if="currentAlerts.length" class="surface monitor-alerts" aria-label="当前异常">
      <h2>{{ alertTitle }}</h2><ul><li v-for="row in currentAlerts" :key="row.alert.code + row.alert.source" :class="row.alert.severity">{{ row.alert.message }}</li></ul>
    </section>
    <section v-if="previousAlerts.length" class="surface monitor-alerts" aria-label="历史异常">
      <h2>最近一次异常（数据已过期）</h2><p class="muted">以下为历史观察，不代表当前状态。</p>
      <ul><li v-for="row in previousAlerts" :key="row.alert.code + row.alert.source">{{ row.alert.message }} <span class="muted">上次观察 {{ time(row.observedAtUtcMs) }}</span></li></ul>
    </section>
    <div class="monitor-cards" :class="{ 'monitor-loading': !monitoring && !monitorError }" :aria-busy="!monitoring && !monitorError">
      <section class="surface metric-card"><h2>CPU</h2><strong>{{ cpu == null ? '未知 / 采样中' : cpu.toFixed(1) + '%' }}</strong><p class="muted">宿主机间隔平均使用率</p></section>
      <section class="surface metric-card"><h2>内存</h2><strong>{{ memory?.totalBytes && memory.usedBytes != null ? (memory.usedBytes / memory.totalBytes * 100).toFixed(1) + '%' : '未知' }}</strong><p class="muted">{{ bytes(memory?.usedBytes) }} / {{ bytes(memory?.totalBytes) }}</p></section>
      <section class="surface metric-card"><h2>磁盘可用空间</h2><template v-if="resources?.disks.length"><div v-for="disk in resources.disks" :key="disk.label"><strong>{{ usable(disk.observedAtUtcMs, disk.error, policy.diskTtlMs) ? bytes(disk.freeBytes) : '未知' }}</strong><p class="muted">{{ disk.label }} · 总量 {{ usable(disk.observedAtUtcMs, disk.error, policy.diskTtlMs) ? bytes(disk.totalBytes) : '未知' }}</p></div></template><strong v-else>未知</strong></section>
      <section class="surface metric-card"><h2>网络收发</h2><strong>↓ {{ bytes(network?.rxBytesPerSec) }}/s</strong><p>↑ {{ bytes(network?.txBytesPerSec) }}/s</p><p class="muted">主机网卡 {{ resources?.network.interface ?? '待确认' }}</p></section>
    </div>
    <div class="monitor-charts"><ResourceChart :points="snapshot?.series ?? []" :now="chartNow" /><ResourceChart :points="snapshot?.series ?? []" :now="chartNow" network /></div>
    <section class="surface"><div class="section-heading"><h2>服务状态</h2><span class="muted">Docker检查 {{ time(snapshot?.docker.observedAtUtcMs) }}</span></div>
      <div class="table-scroll"><table><thead><tr><th>服务</th><th>容器</th><th>Docker 健康</th><th>重启次数</th><th>应用检查</th><th>最近有效检查</th></tr></thead><tbody>
        <tr v-for="name in ['api', 'worker', 'notifier', 'caddy']" :key="name"><td class="mono">{{ name }}</td><td>{{ label(container(name)?.state) }}</td><td>{{ label(container(name)?.health) }}</td><td>{{ container(name)?.restartCount ?? '—' }}</td><td>{{ application(name) }}</td><td>{{ time(checkedAt(name)) }}</td></tr>
      </tbody></table></div><p class="muted">容器运行不代表业务正常。Worker 首版仅检查容器；公开入口仅检查清单信封结构，不下载更新包，签名可信性由正式源验签确认。</p>
    </section>
    <section class="surface"><div class="section-heading"><h2>当前正式版本</h2><RouterLink v-if="auth.can('release.upload') && !screen" to="/upload">上传新版本</RouterLink></div>
      <p v-if="currentError" class="notice danger" role="alert">{{ currentError }}<span v-if="current?.release"> 以下为最近成功读取的版本信息，观察时间 {{ time(current.observedUtcMs) }}。</span></p>
      <template v-if="current?.release"><p class="release-version">{{ current.release.version }}</p><dl class="facts"><dt>签名状态</dt><dd><span class="status-label" :class="current.release.expiryStatus">{{ status(current.release.expiryStatus) }}</span></dd><dt>签名有效期至</dt><dd>{{ time(current.release.expiresUtcMs) }}</dd><dt>发布序号</dt><dd>{{ value(current.release.sequence) }}</dd></dl>
        <details><summary>技术详情</summary><dl class="facts"><dt>清单摘要</dt><dd class="mono">{{ current.release.manifestSha256 }}</dd><dt>更新包摘要</dt><dd class="mono">{{ current.release.packageSha256 }}</dd><dt>本次观察时间</dt><dd>{{ time(current.observedUtcMs) }}</dd><dt>后台激活记录</dt><dd>{{ current.release.activationReceipt ? time(current.release.activationReceipt.recordedUtcMs) : '暂无匹配记录' }}</dd><dt>最近下载检查</dt><dd>{{ current.release.downloadCheck?.lastSucceeded == null ? '尚无结果' : current.release.downloadCheck.lastSucceeded ? '通过' : '未通过' }} · {{ time(current.release.downloadCheck?.lastCompletedUtcMs) }}<span v-if="current.release.downloadCheck?.pending"> · {{ current.release.downloadCheck.running ? '正在检查' : '等待检查' }}</span></dd></dl><p class="muted">激活回执与下载结果是历史记录，本次观察不发起公网请求。</p></details>
      </template><p v-else-if="!current && !currentError" role="status" class="muted">正在核对签名清单…</p><div v-else-if="current" class="empty-workspace"><h3>未检测到正式版本</h3><p class="muted">当前分发源没有正式清单；可上传签名清单与更新包进行发布。</p></div>
    </section>
    <section v-if="!screen" class="surface"><div class="section-heading"><h2>最近任务</h2><RouterLink to="/history">全部记录</RouterLink></div><p v-if="historyError" role="alert" class="notice danger">{{ historyError }}</p><p v-if="!history && !historyError" class="muted">正在获取任务…</p><JobTable v-if="history" :items="history.items" /></section>
  </div>
</template>
