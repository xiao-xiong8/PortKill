<script setup lang="ts">
import type { components } from '../api/contracts.generated';
import { api } from '../api/http';
import { status, time, value } from '../api/display';
import { useResource } from '../state/resource';
import { useAuth } from '../state/auth';
import JobTable from '../components/JobTable.vue';
const auth = useAuth();
const { data: current, error, busy, load } = useResource(signal => api<components['schemas']['CurrentReleaseResponse']>('/api/v1/releases/current', 'GET', undefined, signal));
const history = useResource(signal => api<components['schemas']['PageResponseOfJobListItem']>('/api/v1/jobs?pageSize=5', 'GET', undefined, signal));
</script>
<template>
  <section class="surface" :aria-busy="busy"><div class="section-heading"><h2>当前正式版本</h2><div class="actions"><a v-if="auth.can('release.upload')" href="#upload">上传新版本</a><button :disabled="busy" @click="load">刷新</button></div></div>
    <p v-if="error" class="notice danger" role="alert">{{ error }}</p><p v-else-if="busy" role="status" class="muted">正在核对签名清单…</p>
    <template v-else-if="current?.release"><p class="release-version">{{ current.release.version }}</p><dl class="facts"><dt>签名状态</dt><dd><span class="status-label" :class="current.release.expiryStatus">{{ status(current.release.expiryStatus) }}</span></dd><dt>签名有效期至</dt><dd>{{ time(current.release.expiresUtcMs) }}</dd><dt>发布序号</dt><dd>{{ value(current.release.sequence) }}</dd><dt>清单摘要</dt><dd class="mono">{{ current.release.manifestSha256 }}</dd><dt>更新包摘要</dt><dd class="mono">{{ current.release.packageSha256 }}</dd><dt>本次观察时间</dt><dd>{{ time(current.observedUtcMs) }}</dd><dt>后台激活记录</dt><dd>{{ current.release.activationReceipt ? time(current.release.activationReceipt.recordedUtcMs) : '暂无匹配记录' }}</dd><dt>最近下载检查</dt><dd>{{ current.release.downloadCheck?.lastSucceeded == null ? '尚无结果' : current.release.downloadCheck.lastSucceeded ? '通过' : '未通过' }} · {{ time(current.release.downloadCheck?.lastCompletedUtcMs) }}<span v-if="current.release.downloadCheck?.pending"> · {{ current.release.downloadCheck.running ? '正在检查' : '等待检查' }}</span></dd></dl><p class="muted">激活回执与下载结果是历史记录，本次观察不发起公网请求。</p></template>
    <div v-else class="empty-workspace"><h3>尚未发布正式版本</h3><p class="muted">选择已在 Windows 签名的清单与更新包，完成校验后确认发布。</p></div>
  </section>
  <section class="surface"><div class="section-heading"><h2>最近任务</h2><a href="#history">全部记录</a></div><p v-if="history.error.value" role="alert" class="notice danger">{{ history.error.value }}</p><p v-else-if="history.busy.value" class="muted">正在获取任务…</p><JobTable v-else-if="history.data.value" :items="history.data.value.items" /></section>
</template>
