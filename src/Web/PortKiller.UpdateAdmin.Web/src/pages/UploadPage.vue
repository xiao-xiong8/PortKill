<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { openJob, time } from '../api/display';
import { useUpload } from '../state/upload';

const props = defineProps<{ id?: string }>();
const route = useRoute(), router = useRouter(), task = useUpload();
const { manifest, packageFile, upload, manifestSent, packageSent, busy, error, stage, completedJob, canStart } = task;
// The navigation entry starts a new upload after the previous transfer was submitted.
// Active/paused uploads retain their files and session when returning through that entry.
if (!props.id && completedJob.value) task.reset();
const manifestInput = ref<HTMLInputElement>(), packageInput = ref<HTMLInputElement>();
const conflict = computed(() => props.id && upload.value && props.id !== upload.value.id);
function pick(event: Event, part: 'manifest' | 'package') {
  task.pick((event.target as HTMLInputElement).files?.[0], part);
}
function formatSize(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1048576).toFixed(2)} MiB`;
}
async function cancel() { await task.cancel(); if (!upload.value) await router.replace('/upload'); }
async function newUpload() { task.reset(); await router.replace('/upload'); }
watch(() => props.id, id => { if (id) void task.restore(id); }, { immediate: true });
watch(() => upload.value?.id, id => {
  if (id && !props.id) void router.replace({ name: 'upload', params: { id } });
}, { immediate: true });
watch(completedJob, id => {
  if (id && route.name === 'upload' && !conflict.value) openJob(id);
});
</script>

<template>
  <section class="surface">
    <div class="section-heading">
      <h2>上传签名产物</h2>
      <span class="muted">PortKill · stable · win-x64</span>
    </div>

    <p v-if="conflict" class="notice" role="status">当前保留了另一项上传，以下展示该任务。<RouterLink :to="'/upload/' + upload!.id">返回当前上传</RouterLink></p>
    <p v-if="completedJob" class="notice" role="status">上传完成，已提交校验。<RouterLink :to="'/job/' + completedJob">查看校验任务</RouterLink> <button type="button" @click="newUpload">新建上传</button></p>
    <p v-else-if="upload?.state === 'expired' || upload?.state === 'cancelled'" class="notice" role="status">本次上传已关闭。<button type="button" @click="newUpload">新建上传</button></p>
    <div class="split-workspace upload-workspace">
      <form class="form-stack upload-form" @submit.prevent="task.start">
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
              :disabled="busy || Boolean(completedJob)"
              @change="pick($event, 'manifest')"
            >
            <button
              type="button"
              class="file-trigger-btn"
              :disabled="busy || Boolean(completedJob)"
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
              :disabled="busy || Boolean(completedJob)"
              @change="pick($event, 'package')"
            >
            <button
              type="button"
              class="file-trigger-btn"
              :disabled="busy || Boolean(completedJob)"
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
            :disabled="!canStart"
          >
            {{ upload?.state === 'uploaded' ? '查看校验任务' : '上传并校验' }}
          </button>
          <button v-if="busy" type="button" @click="task.stop">停止传输</button>
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
          <p class="muted">切换页面后上传会继续，返回时保留进度。刷新页面后可恢复上传记录，重新选择尚未确认的原文件；未完成的文件从头传输。</p>
          <p class="muted">上传完成后由服务器验签、验包，再决定是否允许确认发布；传输完成不代表版本已发布。</p>
        </div>
        <div class="upload-aside-footer">
          <RouterLink to="/history" class="aside-history-link">查看已有任务</RouterLink>
        </div>
      </aside>
    </div>
  </section>
</template>
