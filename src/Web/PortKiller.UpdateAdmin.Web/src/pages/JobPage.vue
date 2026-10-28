<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, errorText } from '../api/http';
import { openJob, status, time, value } from '../api/display';
import { useAuth } from '../state/auth';
import { useJobStream } from '../state/jobStream';

const props = defineProps<{ id: string }>();
const auth = useAuth();
const stream = useJobStream(props.id);
const { snapshot, events, connection, gap, error, busy, load } = stream;
type Challenge = components['schemas']['PublishChallengeResponse'];
type PublishJob = components['schemas']['PublishJobResponse'];
type PendingPublishRequest = { draftId: string; challengeId: string; key: string };

const challenge = ref<Challenge>();
const requestStorageKey = `portkill.publish-request.${props.id}`;
const pendingRequest = ref(readStoredRequest());
const idempotencyKey = ref(pendingRequest.value?.key ?? '');
const now = ref(Date.now());
const expiryTimer = setInterval(() => { now.value = Date.now(); }, 1000);
const publishBusy = ref(false);
const publishError = ref('');
const retryBusy = ref(false);

const validation = computed(() => snapshot.value?.validation);
const publication = computed(() => snapshot.value?.publication);
const result = computed(() => publication.value ?? validation.value);
const state = computed(() => {
  if (validation.value) {
    return validation.value.validationState === 'pending' && ['failed', 'conflict', 'superseded'].includes(validation.value.state)
      ? validation.value.state : validation.value.validationState;
  }
  return publication.value?.state;
});
const challengeExpired = computed(() => challenge.value != null && Number(challenge.value.expiresUtcMs) <= now.value);
const canCreateChallenge = computed(() => snapshot.value?.kind === 'validate'
  && validation.value?.validationState === 'readyForApproval' && auth.can('release.publish')
  && !pendingRequest.value
  && (!challenge.value || challengeExpired.value));
const canRetryPublicCheck = computed(() => publication.value?.state === 'activeUnverified' && auth.can('release.publish'));

function newIdempotencyKey() {
  return globalThis.crypto.randomUUID().replaceAll('-', '');
}

function readStoredRequest(): PendingPublishRequest | undefined {
  try {
    const raw = sessionStorage.getItem(requestStorageKey);
    if (!raw) return undefined;
    const value = JSON.parse(raw) as Partial<PendingPublishRequest>;
    if (typeof value.draftId === 'string' && /^[a-f0-9]{32}$/i.test(value.draftId)
      && typeof value.challengeId === 'string' && /^[a-f0-9]{32}$/i.test(value.challengeId)
      && typeof value.key === 'string' && /^[\x21-\x7e]{1,128}$/.test(value.key))
      return { draftId: value.draftId, challengeId: value.challengeId, key: value.key };
    sessionStorage.removeItem(requestStorageKey);
  } catch { /* Optional storage is never used as authority. */ }
  return undefined;
}

function storeRequest(request: PendingPublishRequest) {
  pendingRequest.value = request;
  idempotencyKey.value = request.key;
  try { sessionStorage.setItem(requestStorageKey, JSON.stringify(request)); } catch { /* Same-page recovery still works without optional storage. */ }
}

function clearRequestKey() {
  pendingRequest.value = undefined;
  idempotencyKey.value = '';
  try { sessionStorage.removeItem(requestStorageKey); } catch { /* The server remains authoritative. */ }
}

async function sendPublishRequest(request: PendingPublishRequest) {
  const job = await api<PublishJob>(`/api/v1/releases/${encodeURIComponent(request.draftId)}/publish`, 'POST',
    { challengeId: request.challengeId, idempotencyKey: request.key }, undefined, { 'Idempotency-Key': request.key });
  clearRequestKey();
  openJob(job.id);
}

async function createChallenge() {
  if (!canCreateChallenge.value || publishBusy.value) return;
  publishBusy.value = true; publishError.value = '';
  try {
    if (!await auth.requireStrong()) return;
    challenge.value = await api<Challenge>(`/api/v1/releases/${encodeURIComponent(validation.value!.draftId)}/publish/challenge`, 'POST');
    clearRequestKey();
  } catch (cause) { publishError.value = errorText(cause); }
  finally { publishBusy.value = false; }
}

async function confirmPublish() {
  if (!challenge.value || challengeExpired.value || publishBusy.value) return;
  publishBusy.value = true; publishError.value = '';
  try {
    if (!await auth.requireStrong()) return;
    const request = pendingRequest.value ?? { draftId: challenge.value.draftId, challengeId: challenge.value.id, key: newIdempotencyKey() };
    storeRequest(request);
    await sendPublishRequest(request);
  } catch (cause) {
    publishError.value = errorText(cause);
  } finally { publishBusy.value = false; }
}

async function recoverPublish() {
  if (!pendingRequest.value || publishBusy.value) return;
  publishBusy.value = true; publishError.value = '';
  try {
    const job = await api<PublishJob>(`/api/v1/publish-requests/${encodeURIComponent(pendingRequest.value.key)}`);
    clearRequestKey();
    openJob(job.id);
  } catch (cause) { publishError.value = errorText(cause); }
  finally { publishBusy.value = false; }
}

async function retryStoredPublish() {
  if (!pendingRequest.value || publishBusy.value) return;
  publishBusy.value = true; publishError.value = '';
  try {
    if (!await auth.requireStrong()) return;
    await sendPublishRequest(pendingRequest.value);
  } catch (cause) { publishError.value = errorText(cause); }
  finally { publishBusy.value = false; }
}

async function retryPublicCheck() {
  if (!canRetryPublicCheck.value || retryBusy.value) return;
  retryBusy.value = true; publishError.value = '';
  try {
    if (!await auth.requireStrong()) return;
    await api<PublishJob>(`/api/v1/publish-jobs/${encodeURIComponent(props.id)}/public-check/retry`, 'POST', {});
    await load();
  } catch (cause) { publishError.value = errorText(cause); }
  finally { retryBusy.value = false; }
}

onBeforeUnmount(() => clearInterval(expiryTimer));
</script>

<template>
  <section class="surface" :aria-busy="busy || publishBusy || retryBusy">
    <div class="section-heading">
      <h2>{{ snapshot?.kind === 'publish' ? '发布任务' : '校验任务' }}</h2>
      <div class="actions"><RouterLink to="/history">返回历史</RouterLink><button :disabled="busy" @click="load">刷新任务</button></div>
    </div>
    <p v-if="error" class="notice danger" role="alert">{{ error }}</p>
    <p v-else-if="busy" class="muted" role="status">正在获取任务…</p>
    <template v-else-if="snapshot && result">
      <p class="muted" role="status">{{ connection }}</p>
      <p v-if="gap" class="notice">部分早期事件已清理，当前状态以任务快照为准。</p>
      <p v-if="publishError" class="notice danger" role="alert">{{ publishError }}</p>
      <p class="status-label" :class="state">{{ status(state) }}</p>
      <dl class="facts">
        <dt>任务标识</dt><dd class="mono">{{ snapshot.id }}</dd>
        <dt>版本 / 序号</dt><dd>{{ result.version ?? '待校验' }} / {{ value(result.sequence) }}</dd>
        <dt>清单摘要</dt><dd class="mono">{{ result.manifestSha256 ?? '—' }}</dd>
        <dt>更新包摘要</dt><dd class="mono">{{ result.packageSha256 ?? '—' }}</dd>
        <dt>创建时间</dt><dd>{{ time(result.createdUtcMs) }}</dd>
        <dt>错误代码</dt><dd class="mono">{{ result.errorCode ?? '—' }}</dd>
      </dl>

      <template v-if="validation">
        <h3>签名中的更新说明</h3>
        <pre class="log-lines">{{ validation.releaseNotes?.join('\n') || '暂无说明' }}</pre>
        <p class="muted">签名有效期至 {{ time(validation.manifestExpiresUtcMs) }}</p>
        <div v-if="auth.can('release.publish') && (canCreateChallenge || challenge || pendingRequest)" class="publish-panel">
          <h3>确认发布</h3>
          <p class="muted">发布前会再次核对当前正式清单和已验证产物。确认请求只会创建一个持久发布任务。</p>
          <template v-if="pendingRequest">
            <p class="notice">检测到上次发布请求尚未完成页面确认。查询结果或使用同一个确认请求重试，不会创建第二个发布任务。</p>
            <div class="actions"><button :disabled="publishBusy" @click="recoverPublish">查询确认结果</button><button class="primary" :disabled="publishBusy" @click="retryStoredPublish">{{ publishBusy ? '正在恢复…' : '重试同一确认请求' }}</button></div>
          </template>
          <p v-if="challengeExpired" class="notice danger">发布确认已过期，请重新生成。</p>
          <button v-if="canCreateChallenge" class="primary" :disabled="publishBusy" @click="createChallenge">
            {{ publishBusy ? '正在准备…' : challenge ? '重新生成发布确认' : '生成发布确认' }}
          </button>
          <template v-if="challenge && !challengeExpired">
            <dl class="facts challenge-facts">
              <dt>确认有效期</dt><dd>{{ time(challenge.expiresUtcMs) }}</dd>
              <dt>当前正式清单</dt><dd class="mono">{{ challenge.expectedCurrentManifestSha256 }}</dd>
              <dt>待发布清单</dt><dd class="mono">{{ challenge.manifestSha256 }}</dd>
              <dt>待发布更新包</dt><dd class="mono">{{ challenge.packageSha256 }}</dd>
            </dl>
            <pre class="log-lines">{{ challenge.releaseNotes.join('\n') || '暂无说明' }}</pre>
            <div class="actions">
              <button class="primary" :disabled="publishBusy" @click="confirmPublish">{{ publishBusy ? '正在确认…' : '确认并创建发布任务' }}</button>
            </div>
            <p v-if="idempotencyKey" class="muted">确认请求已生成幂等键；网络中断时可以查询同一请求，不会重复发布。</p>
          </template>
        </div>
      </template>

      <template v-if="publication">
        <p class="muted">下载检查：{{ publication.downloadCheck?.lastSucceeded == null ? '尚无结果' : publication.downloadCheck.lastSucceeded ? '通过' : '未通过' }} · {{ time(publication.downloadCheck?.lastCompletedUtcMs) }}<span v-if="publication.downloadCheck?.pending"> · {{ publication.downloadCheck.running ? '正在检查' : '等待检查' }}</span><span v-if="publication.downloadCheck?.lastErrorCode"> · {{ publication.downloadCheck.lastErrorCode }}</span></p>
        <div v-if="canRetryPublicCheck" class="actions"><button class="primary" :disabled="retryBusy" @click="retryPublicCheck">{{ retryBusy ? '正在重新检查…' : '重试下载检查' }}</button></div>
        <p v-if="state === 'completed'" class="notice">正式清单已生效，公网下载检查通过。客户端下载和安装由用户决定。</p>
        <p v-else-if="state === 'activeUnverified'" class="notice danger">清单已经生效，但下载检查未通过；修复分发服务后可以重试检查。</p>
        <p v-else-if="state === 'conflict'" class="notice danger">发布与当前正式版本发生冲突，系统没有覆盖现有清单。</p>
        <p v-else-if="state === 'superseded'" class="notice">此任务已被更新的正式版本取代，不能再次激活。</p>
      </template>

      <h3>任务阶段</h3>
      <ol class="event-list"><li v-for="event in events" :key="event.id"><time>{{ time(event.createdUtcMs) }}</time>{{ event.stage }} · {{ event.code }}</li></ol>
    </template>
  </section>
</template>
