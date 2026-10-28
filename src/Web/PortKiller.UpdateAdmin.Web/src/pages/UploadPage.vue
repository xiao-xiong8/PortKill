<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import type { components } from '../api/contracts.generated';
import { api, errorText } from '../api/http';
import { uploadPart } from '../api/upload';
import { openJob, time } from '../api/display';

const props = defineProps<{ id?: string }>();
const router = useRouter();
const manifest = ref<File>(), packageFile = ref<File>(), upload = ref<components['schemas']['UploadSessionResponse']>();
const manifestSent = ref(0), packageSent = ref(0), busy = ref(false), error = ref(''), stage = ref('选择两份签名产物');
const manifestInput = ref<HTMLInputElement>(), packageInput = ref<HTMLInputElement>();
let controller: AbortController | undefined;

function pick(event: Event, part: 'manifest' | 'package') {
  const file = (event.target as HTMLInputElement).files?.[0];
  if (part === 'manifest') manifest.value = file;
  else packageFile.value = file;
}

function formatSize(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1048576).toFixed(2)} MiB`;
}

async function readSession(id: string, signal?: AbortSignal) {
  upload.value = await api(`/api/v1/uploads/${encodeURIComponent(id)}`, 'GET', undefined, signal);
  manifestSent.value = Number(upload.value?.manifestReceivedBytes);
  packageSent.value = Number(upload.value?.packageReceivedBytes);
}

function clearSelectedFiles() {
  manifest.value = undefined;
  packageFile.value = undefined;
  manifestSent.value = 0;
  packageSent.value = 0;
}

async function restoreUpload(id: string) {
  controller?.abort();
  const own = controller = new AbortController();
  busy.value = true;
  error.value = '';
  clearSelectedFiles();
  stage.value = '正在读取上传记录…';
  try {
    await readSession(id, own.signal);
    if (!own.signal.aborted) stage.value = '已恢复上传记录，请重新选择原文件';
  } catch (cause) {
    if (!own.signal.aborted) error.value = errorText(cause);
  } finally {
    if (!own.signal.aborted) busy.value = false;
  }
}

async function start() {
  if (busy.value) return;
  busy.value = true;
  error.value = '';
  const own = controller = new AbortController();
  try {
    if (upload.value) await readSession(upload.value.id, own.signal);
    if (upload.value?.state !== 'uploaded') {
      if (!manifest.value || !packageFile.value) throw new Error('Select files');
      if (manifest.value.size < 1 || manifest.value.size > 65536 || packageFile.value.size < 1 || packageFile.value.size > 268435456) {
        error.value = '清单须为 1–64 KiB，更新包须为 1 byte–256 MiB。';
        return;
      }
      if (!upload.value) {
        upload.value = await api('/api/v1/uploads/', 'POST', { manifestBytes: manifest.value.size, packageBytes: packageFile.value.size }, own.signal);
        await router.replace({ name: 'upload', params: { id: upload.value!.id } });
      }
      if (Number(upload.value!.manifestExpectedBytes) !== manifest.value.size || Number(upload.value!.packageExpectedBytes) !== packageFile.value.size) {
        error.value = '重新选择的文件大小与原上传不符，请选择原文件或取消后新建上传。';
        return;
      }
      if (Number(upload.value!.manifestReceivedBytes) === 0) {
        stage.value = '正在传输签名清单';
        upload.value = await uploadPart(upload.value!.id, 'manifest', manifest.value, own.signal, bytes => { manifestSent.value = bytes; });
      }
      if (Number(upload.value!.packageReceivedBytes) === 0) {
        stage.value = '正在传输更新包';
        upload.value = await uploadPart(upload.value!.id, 'package', packageFile.value, own.signal, bytes => { packageSent.value = bytes; });
      }
    }
    stage.value = '提交服务器校验';
    const result = await api<components['schemas']['UploadCompletionResponse']>(`/api/v1/uploads/${upload.value!.id}/complete`, 'POST', {}, own.signal);
    openJob(result.jobId);
  } catch (cause) {
    error.value = errorText(cause);
    stage.value = '传输未完成，可整文件重传未确认的文件';
  } finally {
    busy.value = false;
  }
}

async function cancel() {
  if (!upload.value || busy.value) return;
  error.value = '';
  try {
    await api(`/api/v1/uploads/${upload.value.id}`, 'DELETE');
    upload.value = undefined;
    stage.value = '本次上传已取消';
    manifestSent.value = 0;
    packageSent.value = 0;
    await router.replace('/upload');
  } catch (cause) {
    error.value = errorText(cause);
  }
}

onMounted(async () => {
  if (props.id) await restoreUpload(props.id);
});
watch(() => props.id, id => {
  // The upload route keeps one component instance for /upload and /upload/:id.
  // Do not reload the session created by this page; reload only when navigating
  // to a different existing upload session.
  if (id && upload.value?.id !== id) {
    void restoreUpload(id);
    return;
  }
  if (!id && upload.value) {
    controller?.abort();
    upload.value = undefined;
    clearSelectedFiles();
    error.value = '';
    stage.value = '选择两份签名产物';
  }
});
onBeforeUnmount(() => controller?.abort());
</script>

<template>
  <section class="surface">
    <div class="section-heading">
      <h2>上传签名产物</h2>
      <span class="muted">PortKill · stable · win-x64</span>
    </div>

    <div class="split-workspace upload-workspace">
      <form class="form-stack upload-form" @submit.prevent="start">
        <p v-if="error" class="notice danger" role="alert">{{ error }}</p>

        <!-- 顶部传输阶段与会话状态条 -->
        <div class="upload-status-bar">
          <p role="status" class="upload-stage-text">{{ stage }}</p>
          <p v-if="upload" class="upload-session-meta mono">
            上传会话 {{ upload.id }} · 到期 {{ time(upload.expiresUtcMs) }}
          </p>
        </div>

        <!-- 产物 1：签名清单（manifest.json） -->
        <div class="upload-file-card" :class="{ selected: Boolean(manifest), disabled: busy }">
          <div class="upload-file-header">
            <div>
              <label for="upload-manifest-input" class="upload-file-label">签名清单（manifest.json）</label>
              <span class="upload-file-spec">JSON 签名清单 · 限制 1 B – 64 KiB</span>
            </div>
            <span class="status-label" :class="manifest ? 'healthy' : 'superseded'">
              {{ manifest ? formatSize(manifest.size) : '待选择' }}
            </span>
          </div>

          <div class="file-picker-box">
            <input
              id="upload-manifest-input"
              ref="manifestInput"
              type="file"
              accept=".json"
              class="sr-only-file-input"
              :disabled="busy"
              @change="pick($event, 'manifest')"
            >
            <button
              type="button"
              class="file-trigger-btn"
              :disabled="busy"
              @click="manifestInput?.click()"
            >
              {{ manifest ? '重新选择' : '选择文件' }}
            </button>
            <div class="file-selected-meta" @click="!busy && manifestInput?.click()">
              <template v-if="manifest">
                <strong class="file-selected-name mono">{{ manifest.name }}</strong>
                <span class="file-selected-bytes mono">({{ manifest.size.toLocaleString() }} 字节)</span>
              </template>
              <span v-else class="file-placeholder">尚未选择文件，点击按钮选择本地 manifest.json</span>
            </div>
          </div>

          <div class="upload-progress-block">
            <div class="file-summary">
              <span>传输进度</span>
              <span class="mono">已传输 {{ manifestSent.toLocaleString() }} / {{ (manifest?.size ?? Number(upload?.manifestExpectedBytes ?? 0)).toLocaleString() }} 字节</span>
            </div>
            <progress
              v-if="manifest || upload"
              :value="manifestSent"
              :max="manifest?.size ?? Number(upload?.manifestExpectedBytes)"
              aria-label="清单传输进度"
            />
          </div>
        </div>

        <!-- 产物 2：四文件更新包（ZIP） -->
        <div class="upload-file-card" :class="{ selected: Boolean(packageFile), disabled: busy }">
          <div class="upload-file-header">
            <div>
              <label for="upload-package-input" class="upload-file-label">四文件更新包（ZIP）</label>
              <span class="upload-file-spec">ZIP 归档产物 · 限制 1 B – 256 MiB</span>
            </div>
            <span class="status-label" :class="packageFile ? 'healthy' : 'superseded'">
              {{ packageFile ? formatSize(packageFile.size) : '待选择' }}
            </span>
          </div>

          <div class="file-picker-box">
            <input
              id="upload-package-input"
              ref="packageInput"
              type="file"
              accept=".zip"
              class="sr-only-file-input"
              :disabled="busy"
              @change="pick($event, 'package')"
            >
            <button
              type="button"
              class="file-trigger-btn"
              :disabled="busy"
              @click="packageInput?.click()"
            >
              {{ packageFile ? '重新选择' : '选择文件' }}
            </button>
            <div class="file-selected-meta" @click="!busy && packageInput?.click()">
              <template v-if="packageFile">
                <strong class="file-selected-name mono">{{ packageFile.name }}</strong>
                <span class="file-selected-bytes mono">({{ packageFile.size.toLocaleString() }} 字节)</span>
              </template>
              <span v-else class="file-placeholder">尚未选择文件，点击按钮选择本地更新包 .zip</span>
            </div>
          </div>

          <div class="upload-progress-block">
            <div class="file-summary">
              <span>传输进度</span>
              <span class="mono">已传输 {{ packageSent.toLocaleString() }} / {{ (packageFile?.size ?? Number(upload?.packageExpectedBytes ?? 0)).toLocaleString() }} 字节</span>
            </div>
            <progress
              v-if="packageFile || upload"
              :value="packageSent"
              :max="packageFile?.size ?? Number(upload?.packageExpectedBytes)"
              aria-label="更新包传输进度"
            />
          </div>
        </div>

        <!-- 底部操作按钮栏 -->
        <div class="actions upload-actions-bar">
          <button
            class="primary"
            :disabled="busy || (upload?.state !== 'uploaded' && (!manifest || !packageFile))"
          >
            {{ upload?.state === 'uploaded' ? '查看校验任务' : '上传并校验' }}
          </button>
          <button v-if="busy" type="button" @click="controller?.abort()">停止传输</button>
          <button v-if="upload && !busy && upload.state === 'uploading'" type="button" class="danger-button" @click="cancel">取消本次上传</button>
        </div>
      </form>

      <!-- 右侧操作规范与重传说明 -->
      <aside class="upload-aside">
        <div class="upload-aside-block">
          <h3>从 Windows 制作环境选择文件</h3>
          <p class="muted">上传已签名的清单和对应更新 ZIP。首装包和私钥不用于此流程。</p>
        </div>
        <div class="upload-aside-block">
          <h3>中断后重传</h3>
          <p class="muted">可以刷新页面恢复上传记录，重新选择原文件。服务器已经确认的文件会保留，未完成的文件从头传输。</p>
          <p class="muted">上传完成后由服务器验签、验包，再决定是否允许确认发布；传输完成不代表版本已发布。</p>
        </div>
        <div class="upload-aside-footer">
          <RouterLink to="/history" class="aside-history-link">查看已有任务</RouterLink>
        </div>
      </aside>
    </div>
  </section>
</template>
